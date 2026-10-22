using System.Globalization;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Prefs;

/// <summary>
/// Temporarily changes SOLIDWORKS <em>system</em> preferences and restores them on dispose: reverse order, verified by
/// read-back, one retry for values that only stick after others (STL quality after its deviation/angle), and
/// compare-and-swap (a value someone else changed meanwhile is left alone and reported). Every change is journaled
/// first, so a crash can't leave the user's settings modified. Only ids in <see cref="SystemPrefIds"/> are accepted.
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

    private sealed record Change(PrefKind Kind, int Pref, string Old, string New, bool Journaled);

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
        string old = Read(_app, kind, pref);
        if (Same(kind, old, value)) return this;
        bool journaled = _journal.Add(new JournalEntry(kind, pref, old, value, DateTime.UtcNow, Environment.ProcessId, _operation, _revision));
        Write(_app, kind, pref, value);
        string now = Read(_app, kind, pref);
        _changes.Add(new Change(kind, pref, old, now, journaled));
        if (!Same(kind, now, value))
            _warn?.Invoke($"System preference {kind} {pref} was set to '{value}' but reads '{now}'.");
        return this;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var pending = new List<Change>();
        for (int i = _changes.Count - 1; i >= 0; i--)
            if (!TryRestore(_changes[i], out _)) pending.Add(_changes[i]);
        // Second pass, original order, for values whose restore depends on others.
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var c = pending[i];
            if (!TryRestore(c, out string? why))
                _warn?.Invoke($"System preference {c.Kind} {c.Pref} could not be restored to '{c.Old}': {why} " +
                              "It stays in the journal and is retried when the server next attaches.");
        }
    }

    private bool TryRestore(Change c, out string? why)
    {
        why = null;
        try
        {
            string cur = Read(_app, c.Kind, c.Pref);
            if (Same(c.Kind, cur, c.Old)) { Done(c); return true; }
            if (!Same(c.Kind, cur, c.New))
            {
                // Someone else (the user, another tool) changed it while we held it: leave their value.
                _warn?.Invoke($"System preference {c.Kind} {c.Pref} changed externally to '{cur}' while in use; left as is.");
                Done(c);
                return true;
            }
            Write(_app, c.Kind, c.Pref, c.Old);
            cur = Read(_app, c.Kind, c.Pref);
            if (!Same(c.Kind, cur, c.Old)) { why = $"it reads '{cur}'."; return false; }
            Done(c);
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    private void Done(Change c)
    {
        if (c.Journaled) _journal.Remove(c.Kind, c.Pref);
    }

    /// <summary>Outcome of replaying one orphaned journal entry.</summary>
    public enum RestoreOutcome { Restored, AlreadyOriginal, ChangedExternally, Failed }

    /// <summary>Crash recovery for one entry, compare-and-swap: restore only if the value is still what was set.</summary>
    public static RestoreOutcome Restore(ISldWorks app, JournalEntry e)
    {
        string cur = Read(app, e.Kind, e.Pref);
        if (Same(e.Kind, cur, e.OldValue)) return RestoreOutcome.AlreadyOriginal;
        if (!Same(e.Kind, cur, e.NewValue)) return RestoreOutcome.ChangedExternally;
        Write(app, e.Kind, e.Pref, e.OldValue);
        return Same(e.Kind, Read(app, e.Kind, e.Pref), e.OldValue) ? RestoreOutcome.Restored : RestoreOutcome.Failed;
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

    internal static bool Same(PrefKind kind, string a, string b)
    {
        if (kind != PrefKind.Double) return string.Equals(a, b, StringComparison.Ordinal);
        double x = double.Parse(a, CultureInfo.InvariantCulture), y = double.Parse(b, CultureInfo.InvariantCulture);
        return Math.Abs(x - y) <= 1e-12 * Math.Max(1, Math.Abs(x));
    }

    private static string Fmt(bool v) => v ? "True" : "False";
    private static string Fmt(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
