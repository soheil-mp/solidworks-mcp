using System.Globalization;

namespace SolidWorksMcp.Core.Prefs;

/// <summary>One preference of a restore: the user's value (<see cref="Old"/>) and the last value the server left.</summary>
public sealed record PrefValue(PrefKind Kind, int Pref, string Old, string New);

/// <summary>
/// Puts a unit of preferences back: a single preference, or a group that SOLIDWORKS keeps consistent
/// (<see cref="SystemPrefIds.Coupled"/>). Pure logic: the reads and writes are passed in, so the unit tests can
/// simulate SOLIDWORKS' coupling.
/// </summary>
public static class PrefGroupRestore
{
    public enum Outcome { Restored, AlreadyOriginal, ChangedExternally, Failed }

    /// <summary>
    /// Compare-and-swap for the whole unit: when a value the server changed now reads neither the server's value nor
    /// the original, someone else changed the unit, and it is left alone. Otherwise the originals are written in the
    /// unit's order (for a group: the values others are derived from first), at most twice, and every member is
    /// verified.
    /// </summary>
    public static Outcome Restore(IReadOnlyList<PrefValue> members, Func<PrefKind, int, string> read, Action<PrefKind, int, string> write)
    {
        var current = members.Select(m => read(m.Kind, m.Pref)).ToList();
        if (members.Select((m, i) => Same(m.Kind, current[i], m.Old)).All(x => x)) return Outcome.AlreadyOriginal;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            bool ours = !Same(m.Kind, m.New, m.Old);
            if (ours && !Same(m.Kind, current[i], m.New) && !Same(m.Kind, current[i], m.Old)) return Outcome.ChangedExternally;
        }
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var m in members)
                if (!Same(m.Kind, read(m.Kind, m.Pref), m.Old)) write(m.Kind, m.Pref, m.Old);
            if (members.All(m => Same(m.Kind, read(m.Kind, m.Pref), m.Old))) return Outcome.Restored;
        }
        return Outcome.Failed;
    }

    /// <summary>Value equality as SOLIDWORKS stores it (doubles relative to 1e-12).</summary>
    public static bool Same(PrefKind kind, string a, string b)
    {
        if (kind != PrefKind.Double) return string.Equals(a, b, StringComparison.Ordinal);
        double x = double.Parse(a, CultureInfo.InvariantCulture), y = double.Parse(b, CultureInfo.InvariantCulture);
        return Math.Abs(x - y) <= 1e-12 * Math.Max(1, Math.Abs(x));
    }
}
