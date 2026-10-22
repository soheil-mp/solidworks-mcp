using System.Diagnostics;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

public sealed class DialogRow
{
    public required string Handle { get; init; }
    public required string Title { get; init; }
    public string? Text { get; init; }
    public required IReadOnlyList<string> Buttons { get; init; }
}

public sealed class DialogListResult : ToolResult
{
    public required IReadOnlyList<DialogRow> Dialogs { get; init; }
    public bool UiBlocked { get; init; }
    public string? RunningOperation { get; init; }
}

public sealed class DialogRespondResult : ToolResult
{
    public required string Dialog { get; init; }
    public required string Pressed { get; init; }
    public bool Closed { get; init; }
}

public sealed class StatusResult : ToolResult
{
    public required string ServerVersion { get; init; }
    public required bool Attached { get; init; }
    public string? Revision { get; init; }
    public int? SolidWorksPid { get; init; }
    public bool UiBlocked { get; init; }
    public IReadOnlyList<DialogRow>? Dialogs { get; init; }
    public string? RunningOperation { get; init; }
    public IReadOnlyList<LastOperation>? RecentOps { get; init; }
    public IReadOnlyList<LateCompletion>? LateCompletions { get; init; }
    public string? ActiveDocument { get; init; }
    public string? TargetDocument { get; init; }
    public IReadOnlyList<DocInfo>? Documents { get; init; }
    public string? OpenSketch { get; init; }
    public IReadOnlyList<JournalRow>? PendingPreferenceRestores { get; init; }
    public IReadOnlyList<int>? OtherServerPids { get; init; }
    public bool VbaEnabled { get; init; }
    public string? Note { get; init; }
}

public sealed class JournalRow
{
    public required string Preference { get; init; }
    public required string Original { get; init; }
    public required string Operation { get; init; }
}

public sealed class ConnectResult : ToolResult
{
    public required string Revision { get; init; }
    public int ProcessId { get; init; }
    public bool Launched { get; init; }
    public string? ExecutablePath { get; init; }
    public IReadOnlyList<string>? RecoveredPreferences { get; init; }
}

/// <summary>Connection, status and dialog tools. The dialog parts are Win32-only and never wait for the STA thread.</summary>
public sealed class SessionService(SwSession session)
{
    public static string ServerVersion { get; } =
        typeof(SessionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>
    /// Status that never hangs: the Win32 part (dialogs, UI blocked, running operation) is read directly; the COM part
    /// is skipped with a note when SOLIDWORKS is blocked or busy.
    /// </summary>
    public async Task<StatusResult> StatusAsync(CancellationToken ct = default)
    {
        var dialogs = Rows(session.ListDialogs());
        bool blocked = session.IsUiBlocked;
        var current = session.Dispatcher.Current;
        var others = OtherServers();
        var pending = session.Journal.Entries.Select(e => new JournalRow
        {
            Preference = $"{e.Kind} {e.Pref}", Original = e.OldValue, Operation = e.Operation,
        }).ToList();

        StatusResult Partial(string note) => new()
        {
            ServerVersion = ServerVersion,
            Attached = session.IsAttached,
            Revision = session.Revision,
            SolidWorksPid = session.ProcessId,
            UiBlocked = blocked,
            Dialogs = dialogs.Count == 0 ? null : dialogs,
            RunningOperation = current is null ? null : $"{current.Name} ({current.ElapsedS:0.0} s)",
            RecentOps = session.Dispatcher.Recent is { Count: > 0 } r ? r : null,
            LateCompletions = session.Dispatcher.LateCompletions is { Count: > 0 } l ? l : null,
            PendingPreferenceRestores = pending.Count == 0 ? null : pending,
            OtherServerPids = others.Count == 0 ? null : others,
            VbaEnabled = session.Options.EnableVba,
            Note = note,
        };

        if (blocked) return Partial("A modal dialog blocks SOLIDWORKS; document details are unavailable until it is answered.");
        if (current is not null && current.ElapsedS > 2)
            return Partial($"SOLIDWORKS is busy with '{current.Name}'; document details are skipped.");
        if (session.ProcessId is null && !session.IsAttached)
            return Partial("SOLIDWORKS is not running. Ask the user to start it, or call sw_connect with launch=true.");

        try
        {
            return await session.RunAsync("sw_status", ctx =>
            {
                var docs = ctx.App.Documents();
                ctx.Documents.Prune(docs);
                var active = ctx.ActiveDocOrNull;
                var infos = docs.Select(x => DocumentService.Info(ctx, x, active)).ToList();
                var target = ctx.Documents.Target;
                string? sketch = null;
                if (active?.SketchManager?.ActiveSketch is ISketch s) sketch = ((IFeature)s).Name;
                var st = Partial(null!);
                return new StatusResult
                {
                    ServerVersion = st.ServerVersion,
                    Attached = true,
                    Revision = session.Revision,
                    SolidWorksPid = session.ProcessId,
                    UiBlocked = st.UiBlocked,
                    Dialogs = st.Dialogs,
                    RunningOperation = st.RunningOperation,
                    RecentOps = st.RecentOps,
                    LateCompletions = st.LateCompletions,
                    ActiveDocument = active is null ? null : ctx.Title(active),
                    TargetDocument = target is null ? null : ctx.Title(target),
                    Documents = infos,
                    OpenSketch = sketch,
                    PendingPreferenceRestores = st.PendingPreferenceRestores,
                    OtherServerPids = st.OtherServerPids,
                    VbaEnabled = st.VbaEnabled,
                    Note = target is null && infos.Count > 0
                        ? "No target document yet: doc_new creates one; doc_activate picks an open one (allowEdit=true only if the user asked you to change it)."
                        : null,
                };
            }, new RunOptions { Timeout = TimeSpan.FromSeconds(15) }, ct).ConfigureAwait(false);
        }
        catch (SwException ex) when (ex.Code is ErrorCodes.NotConnected or ErrorCodes.Busy or ErrorCodes.Timeout or ErrorCodes.DialogBlocking)
        {
            return Partial(ex.ToAgentText());
        }
    }

    public async Task<ConnectResult> ConnectAsync(bool launch, CancellationToken ct = default)
    {
        var info = await session.ConnectAsync(launch, ct).ConfigureAwait(false);
        var r = new ConnectResult
        {
            Revision = info.Revision,
            ProcessId = info.ProcessId,
            Launched = info.Launched,
            ExecutablePath = info.ExecutablePath,
            RecoveredPreferences = info.RecoveredPreferences.Count == 0 ? null : info.RecoveredPreferences,
        };
        string major = info.Revision.Split('.')[0];
        if (major != "33") r.Warnings = [$"SOLIDWORKS revision {info.Revision} is untested (tested: 33.x = 2025). Please report problems."];
        return r;
    }

    public DialogListResult ListDialogs()
    {
        var cur = session.Dispatcher.Current;
        return new DialogListResult
        {
            Dialogs = Rows(session.ListDialogs()),
            UiBlocked = session.IsUiBlocked,
            RunningOperation = cur is null ? null : $"{cur.Name} ({cur.ElapsedS:0.0} s)",
        };
    }

    private static readonly string[] Destructive = ["don't save", "dont save", "do not save", "discard", "delete", "replace", "overwrite", "no to all"];

    /// <summary>Presses a button on a dialog by handle. Destructive answers need <paramref name="confirm"/>.</summary>
    public async Task<DialogRespondResult> RespondAsync(string handle, string button, bool confirm, CancellationToken ct = default)
    {
        if (session.ProcessId is not int pid) throw new SwException(ErrorCodes.NotConnected, "SOLIDWORKS is not running.");
        var dialogs = Dialogs.List(pid);
        var target = dialogs.FirstOrDefault(x => string.Equals(x.HandleHex, handle.Trim(), StringComparison.OrdinalIgnoreCase))
                     ?? throw new SwException(ErrorCodes.EntityNotFound, $"No visible SOLIDWORKS dialog with handle {handle}.",
                         dialogs.Count == 0 ? "No dialogs are open." : $"Open dialogs: {string.Join("; ", dialogs.Select(x => $"{x.HandleHex} '{x.Title}'"))}.");
        string want = button.Replace("&", "").Trim().ToLowerInvariant();
        bool isDestructive = Destructive.Any(want.Contains) ||
                             (want is "no" && (target.Text.Contains("save", StringComparison.OrdinalIgnoreCase)));
        if (isDestructive && !confirm)
            throw new SwException(ErrorCodes.InvalidArgument,
                $"'{button}' on '{target.Title}' can discard the user's work (dialog says: \"{Clip(target.Text, 200)}\").",
                "Ask the user first; pass confirm=true only with their approval.");
        string pressed = Dialogs.Press(target, button);
        for (int i = 0; i < 20 && Dialogs.IsOpen(target.Handle); i++) await Task.Delay(100, ct).ConfigureAwait(false);
        return new DialogRespondResult { Dialog = target.Title, Pressed = pressed, Closed = !Dialogs.IsOpen(target.Handle) };
    }

    private static List<DialogRow> Rows(IEnumerable<DialogInfo> dialogs) => dialogs.Select(d => new DialogRow
    {
        Handle = d.HandleHex,
        Title = d.Title,
        Text = d.Text.Length == 0 ? null : Clip(d.Text, 600),
        Buttons = d.Buttons.Select(b => b.Caption).ToList(),
    }).ToList();

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static List<int> OtherServers()
    {
        try
        {
            var me = Environment.ProcessId;
            var procs = Process.GetProcessesByName("solidworks-mcp");
            try { return procs.Select(p => p.Id).Where(id => id != me).ToList(); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return []; }
    }
}
