using System.Globalization;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Prefs;

/// <summary>
/// Temporarily changes SOLIDWORKS <em>system</em> preferences and restores them on dispose: reverse order, verified by
/// read-back, and compare-and-swap (a value someone else changed meanwhile is left alone and reported). Preferences
/// SOLIDWORKS keeps consistent with each other (<see cref="SystemPrefIds.Coupled"/>, e.g. STL quality with its
/// deviation and angle) are snapshotted before any of them is written, re-read after every write (their side-effect
/// changes are the server's) and restored together in the group's order. Every change is journaled first, so a crash
/// can't leave the user's settings modified. Only ids in <see cref="SystemPrefIds"/> are accepted.
/// </summary>
/// <example><code>
/// using var prefs = ctx.SystemPrefs();
/// prefs.Toggle(SystemPrefIds.InputDimValOnCreate, false);
/// </code></example>
internal sealed class PreferenceScope : IDisposable
{
    private readonly ISldWorks _app;
    private readonly PreferenceJournal _journal;
    private readonly string _operation;
    private readonly string? _revision;
    private readonly Action<string>? _warn;
    private readonly List<Change> _changes = [];
    private bool _disposed;

    /// <summary>A preference this scope holds: the value before the scope, and the last value it left.</summary>
    private sealed class Change(PrefKind kind, int pref, string old, bool journaled)
    {
        public PrefKind Kind { get; } = kind;
        public int Pref { get; } = pref;
        public string Old { get; } = old;
        public string New { get; set; } = old;
        /// <summary>False when an outer scope of this process already journals it (that entry keeps the user's value).</summary>
        public bool Journaled { get; } = journaled;
    }

    public PreferenceScope(ISldWorks app, PreferenceJournal journal, string operation, string? revision, Action<string>? warn = null)
    {
        _app = app;
        _journal = journal;
        _operation = operation;
        _revision = revision;
        _warn = warn;
    }

    public PreferenceScope Toggle(int pref, bool value) => Set(PrefKind.Toggle, pref, Fmt(value));
    public PreferenceScope Integer(int pref, int value) => Set(PrefKind.Integer, pref, Fmt(value));
    public PreferenceScope Double(int pref, double value) => Set(PrefKind.Double, pref, Fmt(value));
    public PreferenceScope String(int pref, string value) => Set(PrefKind.String, pref, value);

    private PreferenceScope Set(PrefKind kind, int pref, string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!SystemPrefIds.IsAllowed(kind, pref))
            throw new SwException(ErrorCodes.Internal,
                $"System preference {kind} {pref} is not on the allow-list (Prefs/SystemPrefIds.cs); refusing to write it.");
        if (Same(kind, Read(_app, kind, pref), value)) return this;
        // A write can change the other members of a coupled group: hold (and journal) all of them first.
        var unit = SystemPrefIds.GroupOf(kind, pref) ?? [(kind, pref)];
        foreach (var (k, p) in unit) Track(k, p, k == kind && p == pref ? value : null);
        Write(_app, kind, pref, value);
        foreach (var (k, p) in unit) Observe(k, p);
        string now = Read(_app, kind, pref);
        if (!Same(kind, now, value))
            _warn?.Invoke($"System preference {kind} {pref} was set to '{value}' but reads '{now}'.");
        return this;
    }

    private void Track(PrefKind kind, int pref, string? intended)
    {
        if (Find(kind, pref) is not null) return;
        string old = Read(_app, kind, pref);
        bool journaled = _journal.Add(new JournalEntry(kind, pref, old, intended ?? old, DateTime.UtcNow, Environment.ProcessId, _operation, _revision));
        _changes.Add(new Change(kind, pref, old, journaled));
    }

    /// <summary>Records the value a write left (also for an outer scope's journal entry: crash recovery must see it as ours).</summary>
    private void Observe(PrefKind kind, int pref)
    {
        var c = Find(kind, pref)!;
        c.New = Read(_app, kind, pref);
        _journal.SetNewValue(kind, pref, c.New);
    }

    private Change? Find(PrefKind kind, int pref) => _changes.FirstOrDefault(c => c.Kind == kind && c.Pref == pref);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Units in reverse order of their first change; a coupled group is restored as one, in its own order.
        var handled = new HashSet<(PrefKind, int)>();
        for (int i = _changes.Count - 1; i >= 0; i--)
        {
            var c = _changes[i];
            if (handled.Contains((c.Kind, c.Pref))) continue;
            var group = SystemPrefIds.GroupOf(c.Kind, c.Pref);
            var unit = group is null ? [c] : group.Select(g => Find(g.Kind, g.Pref)).OfType<Change>().ToList();
            foreach (var u in unit) handled.Add((u.Kind, u.Pref));
            RestoreUnit(unit);
        }
    }

    private void RestoreUnit(List<Change> unit)
    {
        string names = string.Join(", ", unit.Select(u => $"{u.Kind} {u.Pref}"));
        PrefGroupRestore.Outcome outcome;
        try
        {
            outcome = PrefGroupRestore.Restore(unit.Select(u => new PrefValue(u.Kind, u.Pref, u.Old, u.New)).ToList(),
                (k, p) => Read(_app, k, p), (k, p, v) => Write(_app, k, p, v));
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"System preference(s) {names} could not be restored: {ex.Message} " +
                          "They stay in the journal and are retried when the server next attaches.");
            return;
        }
        if (outcome == PrefGroupRestore.Outcome.Failed)
        {
            string now = string.Join(", ", unit.Select(u => $"{u.Pref}={SafeRead(u.Kind, u.Pref)} (was {u.Old})"));
            _warn?.Invoke($"System preference(s) {names} could not be restored ({now}). " +
                          "They stay in the journal and are retried when the server next attaches.");
            return;
        }
        if (outcome == PrefGroupRestore.Outcome.ChangedExternally)
            _warn?.Invoke($"System preference(s) {names} changed externally while in use; left as is.");
        foreach (var u in unit) Done(u);
    }

    private void Done(Change c)
    {
        if (c.Journaled) _journal.Remove(c.Kind, c.Pref);
        else _journal.SetNewValue(c.Kind, c.Pref, SafeRead(c.Kind, c.Pref)); // the outer scope's entry holds this value again
    }

    private string SafeRead(PrefKind kind, int pref)
    {
        try { return Read(_app, kind, pref); }
        catch (Exception) { return "?"; }
    }

    /// <summary>Outcome of replaying one orphaned journal entry.</summary>
    public enum RestoreOutcome { Restored, AlreadyOriginal, ChangedExternally, Failed }

    /// <summary>
    /// Crash recovery for the entries of a dead server process: compare-and-swap per unit, with the members of a coupled
    /// group restored together in the group's order.
    /// </summary>
    public static List<(JournalEntry Entry, RestoreOutcome Outcome)> RestoreAll(ISldWorks app, IReadOnlyList<JournalEntry> entries)
    {
        var result = new List<(JournalEntry, RestoreOutcome)>();
        var handled = new HashSet<(PrefKind, int)>();
        foreach (var e in entries)
        {
            if (handled.Contains((e.Kind, e.Pref))) continue;
            var group = SystemPrefIds.GroupOf(e.Kind, e.Pref);
            var unit = group is null ? [e]
                : group.Select(g => entries.FirstOrDefault(x => x.Kind == g.Kind && x.Pref == g.Pref)).OfType<JournalEntry>().ToList();
            foreach (var u in unit) handled.Add((u.Kind, u.Pref));
            RestoreOutcome outcome;
            try
            {
                outcome = PrefGroupRestore.Restore(unit.Select(u => new PrefValue(u.Kind, u.Pref, u.OldValue, u.NewValue)).ToList(),
                    (k, p) => Read(app, k, p), (k, p, v) => Write(app, k, p, v)) switch
                {
                    PrefGroupRestore.Outcome.Restored => RestoreOutcome.Restored,
                    PrefGroupRestore.Outcome.AlreadyOriginal => RestoreOutcome.AlreadyOriginal,
                    PrefGroupRestore.Outcome.ChangedExternally => RestoreOutcome.ChangedExternally,
                    _ => RestoreOutcome.Failed,
                };
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or FormatException)
            {
                outcome = RestoreOutcome.Failed;
            }
            foreach (var u in unit) result.Add((u, outcome));
        }
        return result;
    }

    internal static string Read(ISldWorks app, PrefKind kind, int pref) => kind switch
    {
        PrefKind.Toggle => Fmt(app.GetUserPreferenceToggle(pref)),
        PrefKind.Integer => Fmt(app.GetUserPreferenceIntegerValue(pref)),
        PrefKind.Double => Fmt(app.GetUserPreferenceDoubleValue(pref)),
        PrefKind.String => app.GetUserPreferenceStringValue(pref) ?? "",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    internal static void Write(ISldWorks app, PrefKind kind, int pref, string value)
    {
        switch (kind)
        {
            case PrefKind.Toggle: app.SetUserPreferenceToggle(pref, bool.Parse(value)); break;
            case PrefKind.Integer: app.SetUserPreferenceIntegerValue(pref, int.Parse(value, CultureInfo.InvariantCulture)); break;
            case PrefKind.Double: app.SetUserPreferenceDoubleValue(pref, double.Parse(value, CultureInfo.InvariantCulture)); break;
            case PrefKind.String: app.SetUserPreferenceStringValue(pref, value); break;
        }
    }

    internal static bool Same(PrefKind kind, string a, string b) => PrefGroupRestore.Same(kind, a, b);

    private static string Fmt(bool v) => v ? "True" : "False";
    private static string Fmt(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
