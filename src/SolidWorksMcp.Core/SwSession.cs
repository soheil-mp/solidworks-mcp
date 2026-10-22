using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Files;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core;

/// <summary>Per-call options for <see cref="SwSession.RunAsync{T}"/>.</summary>
public sealed record RunOptions
{
    /// <summary>Time limit for this call (queue wait included). Null uses <see cref="SwOptions.DefaultTimeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Dialogs the watchdog may answer automatically while this call runs.</summary>
    public IReadOnlyList<DialogRule>? DialogRules { get; init; }

    /// <summary>Which document the call needs, and whether it changes it (ownership is enforced for writes).</summary>
    public DocAccess Access { get; init; } = DocAccess.None;

    /// <summary>Explicit target document (handle "D2", title, file name or path); null = the session's target.</summary>
    public string? Doc { get; init; }

    /// <summary>With <see cref="DocAccess.Read"/>: proceed with no document instead of failing NO_ACTIVE_DOC.</summary>
    public bool OptionalDoc { get; init; }

    public static RunOptions Read(string? doc = null) => new() { Access = DocAccess.Read, Doc = doc };
    public static RunOptions Write(string? doc = null) => new() { Access = DocAccess.Write, Doc = doc };
}

/// <summary>Result of attaching to (or launching) SOLIDWORKS.</summary>
public sealed record ConnectInfo(bool Connected, bool Launched, string Revision, int ProcessId, string? ExecutablePath,
    IReadOnlyList<string> RecoveredPreferences);

/// <summary>
/// The connection to one running SOLIDWORKS instance and everything that must be shared by all tools: the STA
/// dispatcher, the dialog watchdog, the document and entity registries, the preference journal and the path policy.
/// Thread-safe: all COM work is marshalled to the dispatcher thread.
/// </summary>
public sealed class SwSession : IDisposable
{
    private readonly ILogger _log;
    private ISldWorks? _app; // STA thread only
    private volatile int _pid;
    private long _mainHwnd;
    private volatile string? _revision;
    private volatile bool _attached;
    private bool _everAttached;
    private IReadOnlyList<string> _lastRecovered = [];

    public SwSession(SwOptions? options = null, ILoggerFactory? loggerFactory = null)
    {
        Options = options ?? SwOptions.FromEnvironment();
        _log = loggerFactory?.CreateLogger("SolidWorksMcp.Session") ?? NullLogger.Instance;
        Dispatcher = new StaDispatcher { MaxQueueWait = Options.MaxQueueWait };
        Journal = new PreferenceJournal(Options.DataDir);
        Paths = PathPolicy.FromEnvironment(Options.DataDir);
        Watchdog = new DialogWatchdog(Dispatcher, () => ProcessId, loggerFactory?.CreateLogger("SolidWorksMcp.Watchdog"));
        // Unexpected modal dialogs (not open before the call, not covered by its rules) end the wait early.
        Dispatcher.BlockingDialogProbe = op =>
        {
            if (!IsUiBlocked || ProcessId is not int pid) return [];
            return Dialogs.List(pid).Where(d => !op.IsPreExisting(d.Handle) && !op.Rules.Any(r => r.Matches(d))).ToList();
        };
    }

    public SwOptions Options { get; }
    public StaDispatcher Dispatcher { get; }
    public PreferenceJournal Journal { get; }
    public PathPolicy Paths { get; }
    public DialogWatchdog Watchdog { get; }
    internal DocumentRegistry Documents { get; } = new();
    internal EntityRegistry Entities { get; } = new();
    internal ILogger Log => _log;

    /// <summary>True after a successful attach, until a disconnect is detected.</summary>
    public bool IsAttached => _attached;

    /// <summary>The SOLIDWORKS revision ("33.1.2" = 2025 SP1.2) once attached.</summary>
    public string? Revision => _revision;

    /// <summary>The SLDWORKS.exe process id: from the attached instance, or by process lookup (works without COM).</summary>
    public int? ProcessId
    {
        get
        {
            if (_pid != 0) return _pid;
            var ids = SolidWorksProcessIds();
            return ids.Count > 0 ? ids[0] : null;
        }
    }

    private static List<int> SolidWorksProcessIds()
    {
        try
        {
            var procs = Process.GetProcessesByName("SLDWORKS");
            try { return procs.Select(p => p.Id).ToList(); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return []; }
    }

    /// <summary>Visible SOLIDWORKS dialogs (Win32 only; never blocked by the STA thread).</summary>
    public IReadOnlyList<DialogInfo> ListDialogs() => ProcessId is int pid ? Dialogs.List(pid) : [];

    /// <summary>
    /// True when the SOLIDWORKS main window is disabled, i.e. a modal dialog is up (Win32 only, no COM).
    /// </summary>
    public bool IsUiBlocked
    {
        get
        {
            long h = Interlocked.Read(ref _mainHwnd);
            return h != 0 && NativeMethods.IsWindow((IntPtr)h) && !NativeMethods.IsWindowEnabled((IntPtr)h);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the STA thread with a live connection and (per <paramref name="options"/>) a
    /// checked target document. Refuses up front while a modal dialog blocks SOLIDWORKS. COM disconnects become
    /// NOT_CONNECTED (the next call re-attaches); calls rejected for &gt;30 s become BUSY.
    /// </summary>
    internal Task<T> RunAsync<T>(string operation, Func<SwContext, T> work, RunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RunOptions();
        if (IsUiBlocked)
        {
            var dialogs = ListDialogs();
            throw new SwException(ErrorCodes.DialogBlocking,
                "A modal dialog is open in SOLIDWORKS, so no command can run. " +
                (dialogs.Count == 0 ? "" : "Visible: " + string.Join("; ", dialogs.Select(d => $"{d.HandleHex} {d.Describe()}")) + "."),
                "Tell the user what the dialog says and ask how to answer it (or answer it with dialog_respond only if your own last call caused it).");
        }

        return Dispatcher.InvokeAsync(operation, op =>
        {
            try
            {
                var app = Attach(launch: false);
                if (ProcessId is int pid) op.MarkPreExisting(Dialogs.List(pid));
                var ctx = new SwContext(this, app, op);
                ctx.Bind(options);
                var result = work(ctx);
                ctx.AfterWrite();
                if (result is Model.ToolResult tr)
                {
                    var warnings = op.Warnings;
                    if (warnings.Count > 0) tr.Warnings = [.. tr.Warnings ?? [], .. warnings];
                    var handled = op.HandledDialogs;
                    if (handled.Count > 0)
                        tr.DialogsHandled = handled.Select(h => $"'{h.Dialog.Title}' → {h.Pressed} ({h.Rule.Reason})").ToList();
                }
                return result;
            }
            catch (Exception ex) when (ComArgs.IsDisconnect(ex))
            {
                // A stale proxy (e.g. of a closed document) also raises disconnect errors: only a failed probe of
                // the application object means SOLIDWORKS itself is gone.
                if (_app is null || !IsAlive(_app))
                {
                    Detach();
                    throw new SwException(ErrorCodes.NotConnected, "Lost the connection to SOLIDWORKS (it closed or crashed).",
                        "If SOLIDWORKS is running again, the next call re-attaches automatically.", ex);
                }
                throw new SwException(ErrorCodes.EntityStale, "An object used by this call no longer exists (its document was closed or rebuilt).",
                    "List again (doc_list / face_list / feature_tree) and retry with current references.", ex);
            }
            catch (Exception ex) when (ComArgs.IsRejected(ex))
            {
                throw new SwException(ErrorCodes.Busy, "SOLIDWORKS kept rejecting the call because it is busy.",
                    "Wait for SOLIDWORKS to finish (check dialog_list for a modal dialog), then retry.", ex);
            }
        }, options.Timeout ?? Options.DefaultTimeout, options.DialogRules, cancellationToken);
    }

    /// <summary>Attaches to the running SOLIDWORKS, or launches it when <paramref name="launch"/> is true.</summary>
    public Task<ConnectInfo> ConnectAsync(bool launch, CancellationToken cancellationToken = default) =>
        Dispatcher.InvokeAsync("sw_connect", _ =>
        {
            bool alreadyRunning = SolidWorksProcessIds().Count > 0;
            var app = Attach(launch);
            bool launched = launch && !alreadyRunning;
            var recovered = _lastRecovered;
            _lastRecovered = [];
            string? exe = null;
            try { exe = app.GetExecutablePath(); } catch { /* optional */ }
            return new ConnectInfo(true, launched, _revision ?? "", _pid, exe, recovered);
        }, launch ? TimeSpan.FromSeconds(Math.Max(200, Options.DefaultTimeout.TotalSeconds)) : Options.DefaultTimeout,
            null, cancellationToken);

    /// <summary>STA thread only: returns a live application object, attaching (or launching) as needed.</summary>
    internal ISldWorks Attach(bool launch)
    {
        if (_app is not null)
        {
            if (IsAlive(_app)) return _app;
            Detach();
        }

        var app = TryGetRunning();
        if (app is null)
        {
            var running = SolidWorksProcessIds();
            if (running.Count > 0)
                throw new SwException(ErrorCodes.NotConnected,
                    $"SOLIDWORKS is running (pid {string.Join(", ", running)}) but can't be reached over COM: it may still be starting, " +
                    "or it runs with different privileges than this server.",
                    "Wait until SOLIDWORKS has finished starting and retry. If it was started 'as administrator' (or the MCP client was), " +
                    "restart one of them so both run at the same level. A second SOLIDWORKS is never launched.");
            // Auto-launch only on the very first attach of this process; after a disconnect only sw_connect may launch.
            bool mayLaunch = launch || (Options.AutoLaunch && !_everAttached);
            if (!mayLaunch)
                throw new SwException(ErrorCodes.NotConnected, "SOLIDWORKS is not running.",
                    "Ask the user to start SOLIDWORKS, or call sw_connect with launch=true (this starts it and takes a license seat).");
            app = Launch();
        }

        _app = app;
        _pid = app.GetProcessID();
        _revision = app.RevisionNumber();
        try { Interlocked.Exchange(ref _mainHwnd, ((IFrame)(object)app.Frame()).GetHWndx64()); }
        catch { Interlocked.Exchange(ref _mainHwnd, 0); }
        _attached = true;
        _everAttached = true;
        _log.LogInformation("Attached to SOLIDWORKS {Revision} (pid {Pid})", _revision, _pid);
        _lastRecovered = RecoverJournal(app);
        try
        {
            using var p = Process.GetProcessById(_pid);
            Documents.Load(Options.DataDir, _pid, p.StartTime.ToUniversalTime(), app.Documents());
        }
        catch (Exception ex) { _log.LogDebug(ex, "Document registry not restored"); }
        return app;
    }

    /// <summary>False only when the instance is gone (disconnect errors); "busy" rejections propagate to the caller.</summary>
    private static bool IsAlive(ISldWorks app)
    {
        try
        {
            _ = app.RevisionNumber();
            return true;
        }
        catch (Exception ex) when (ComArgs.IsDisconnect(ex))
        {
            return false;
        }
    }

    /// <summary>Forgets the instance and every COM object obtained from it (they died with it).</summary>
    private void Detach()
    {
        if (_app is not null) _log.LogWarning("Detached from SOLIDWORKS (pid {Pid})", _pid);
        _app = null;
        _pid = 0;
        Interlocked.Exchange(ref _mainHwnd, 0);
        _attached = false;
        Documents.Clear();
        Entities.Clear();
    }

    private static ISldWorks? TryGetRunning()
    {
        if (NativeMethods.CLSIDFromProgID("SldWorks.Application", out var clsid) != 0) return null;
        int hr = NativeMethods.GetActiveObject(ref clsid, IntPtr.Zero, out var obj);
        return hr == 0 ? obj as ISldWorks : null;
    }

    private ISldWorks Launch()
    {
        _log.LogInformation("Launching SOLIDWORKS");
        var type = Type.GetTypeFromProgID("SldWorks.Application")
                   ?? throw new SwException(ErrorCodes.NotConnected, "SOLIDWORKS is not installed (ProgID SldWorks.Application not registered).");
        var app = (ISldWorks)(Activator.CreateInstance(type)
                              ?? throw new SwException(ErrorCodes.NotConnected, "Could not start SOLIDWORKS."));
        app.Visible = true;
        app.UserControl = true; // the user owns this instance: it stays open when the server exits
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (app.StartupProcessCompleted) return app;
            }
            catch (COMException) { /* still starting */ }
            Thread.Sleep(500);
        }
        throw new SwException(ErrorCodes.Timeout, "SOLIDWORKS started but did not finish its startup within 180 s.",
            "Check for a startup dialog (license, what's new) with dialog_list and ask the user to answer it, then call sw_connect again.");
    }

    /// <summary>
    /// Replays journals of server processes that died while holding system preferences (compare-and-swap, same
    /// SOLIDWORKS major version only). Runs on every attach.
    /// </summary>
    private IReadOnlyList<string> RecoverJournal(ISldWorks app)
    {
        var report = new List<string>();
        string? major = _revision?.Split('.')[0];
        foreach (var (file, entries) in Journal.Orphans())
        {
            var remaining = new List<JournalEntry>();
            foreach (var e in entries)
            {
                if (e.SwRevision is not null && major is not null && e.SwRevision.Split('.')[0] != major)
                {
                    remaining.Add(e); // written for another SOLIDWORKS release: never replay into this one
                    continue;
                }
                try
                {
                    var outcome = PreferenceScope.Restore(app, e);
                    if (outcome == PreferenceScope.RestoreOutcome.Failed) remaining.Add(e);
                    if (outcome is PreferenceScope.RestoreOutcome.Restored or PreferenceScope.RestoreOutcome.ChangedExternally)
                    {
                        string what = outcome == PreferenceScope.RestoreOutcome.Restored
                            ? $"{e.Kind} {e.Pref} restored to {e.OldValue} (left at {e.NewValue} by '{e.Operation}' of a crashed server)"
                            : $"{e.Kind} {e.Pref} was changed by someone else after a crash; left as is";
                        report.Add(what);
                        _log.LogWarning("Preference recovery: {What}", what);
                    }
                }
                catch (Exception ex)
                {
                    remaining.Add(e);
                    _log.LogWarning(ex, "Could not restore journaled preference {Kind} {Pref}", e.Kind, e.Pref);
                }
            }
            Journal.ReplaceOrphan(file, remaining);
        }
        return report;
    }

    public void Dispose()
    {
        Watchdog.Dispose();
        Dispatcher.Dispose();
    }
}
