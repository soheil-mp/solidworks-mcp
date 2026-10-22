using System.Collections.Concurrent;
using System.Text;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Per-operation state shared between the STA thread (the running tool) and the dialog watchdog thread:
/// the dialog rules the tool expects, the dialogs that were auto-handled, and warnings for the result.
/// </summary>
internal sealed class OperationContext
{
    private readonly ConcurrentQueue<HandledDialog> _handled = new();
    private readonly ConcurrentDictionary<IntPtr, DialogInfo> _seen = new();
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly ConcurrentDictionary<IntPtr, byte> _preExisting = new();

    /// <summary>Dialogs already visible when the operation started: never auto-answered (they aren't ours).</summary>
    public void MarkPreExisting(IEnumerable<DialogInfo> dialogs)
    {
        foreach (var d in dialogs) _preExisting[d.Handle] = 0;
    }

    public bool IsPreExisting(IntPtr handle) => _preExisting.ContainsKey(handle);

    public OperationContext(string name, IReadOnlyList<DialogRule>? rules)
    {
        Name = name;
        Rules = rules ?? [];
    }

    public string Name { get; }
    public IReadOnlyList<DialogRule> Rules { get; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Dialogs the watchdog answered automatically during this operation (in order).</summary>
    public IReadOnlyList<HandledDialog> HandledDialogs => _handled.ToArray();

    /// <summary>Dialogs the watchdog saw but left alone (not covered by a rule).</summary>
    public IReadOnlyCollection<DialogInfo> SeenDialogs => _seen.Values.ToArray();

    public IReadOnlyList<string> Warnings => _warnings.ToArray();

    public void AddWarning(string warning) => _warnings.Enqueue(warning);

    internal void RecordHandled(HandledDialog handled) => _handled.Enqueue(handled);

    internal void RecordSeen(DialogInfo dialog) => _seen[dialog.Handle] = dialog;

    /// <summary>The first handled dialog whose rule is flagged <see cref="DialogRule.IsError"/>, if any.</summary>
    public HandledDialog? FirstErrorDialog => _handled.FirstOrDefault(h => h.Rule.IsError);

    public string DescribeSeenDialogs()
    {
        var seen = SeenDialogs;
        if (seen.Count == 0) return "";
        var sb = new StringBuilder(" Dialogs seen: ");
        foreach (var d in seen) sb.Append(d.Describe()).Append("; ");
        return sb.ToString().TrimEnd(' ', ';') + ".";
    }
}
