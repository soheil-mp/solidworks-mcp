using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SolidWorksMcp.Core.Com;

namespace SolidWorksMcp.Core.Win32;

/// <summary>
/// While an operation runs on the STA thread, polls the SOLIDWORKS process for modal dialogs every 250 ms.
/// Dialogs matching one of the operation's <see cref="DialogRule"/>s are answered automatically and recorded;
/// all others are only recorded (they show up in TIMEOUT errors and in sw_status). Runs on a thread-pool timer and
/// never touches COM.
/// </summary>
public sealed class DialogWatchdog : IDisposable
{
    private readonly StaDispatcher _dispatcher;
    private readonly Func<int?> _processId;
    private readonly ILogger _log;
    private readonly Timer _timer;
    private readonly HashSet<IntPtr> _answered = [];
    private int _busy;

    /// <summary>Operations shorter than this are never inspected (most calls finish well within it).</summary>
    public static TimeSpan Grace { get; set; } = TimeSpan.FromMilliseconds(400);

    public DialogWatchdog(StaDispatcher dispatcher, Func<int?> processId, ILogger? log = null)
    {
        _dispatcher = dispatcher;
        _processId = processId;
        _log = log ?? NullLogger.Instance;
        _timer = new Timer(_ => Tick(), null, 250, 250);
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var ctx = _dispatcher.CurrentContext;
            if (ctx is null)
            {
                lock (_answered) _answered.Clear();
                return;
            }
            if (DateTime.UtcNow - ctx.StartedUtc < Grace) return;
            if (_processId() is not int pid) return;

            foreach (var dialog in Dialogs.List(pid))
            {
                // Provenance: a dialog that was already open when the operation started belongs to the user.
                var rule = ctx.IsPreExisting(dialog.Handle) ? null : ctx.Rules.FirstOrDefault(r => r.Matches(dialog));
                if (rule is null || rule.Button.Length == 0) // an empty Button only tolerates the dialog: never pressed
                {
                    ctx.RecordSeen(dialog);
                    continue;
                }
                lock (_answered)
                {
                    if (!_answered.Add(dialog.Handle)) continue; // already answered; give it time to close
                }
                try
                {
                    string pressed = Dialogs.Press(dialog, rule.Button);
                    ctx.RecordHandled(new HandledDialog(dialog, rule, pressed, DateTime.UtcNow));
                    _log.LogInformation("Watchdog answered dialog {Dialog} with '{Button}' during {Operation} ({Reason})",
                        dialog.Describe(), pressed, ctx.Name, rule.Reason);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Watchdog could not answer dialog {Dialog}", dialog.Describe());
                    ctx.RecordSeen(dialog);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Dialog watchdog tick failed");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
