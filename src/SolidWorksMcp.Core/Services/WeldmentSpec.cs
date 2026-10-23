using System.Text.RegularExpressions;

namespace SolidWorksMcp.Core.Services;

/// <summary>
/// The names weldment_member takes (pure; offline-tested). Profiles live in the weldment-profile folder as
/// &lt;standard&gt;\&lt;type&gt;.sldlfp, and each size is a configuration of that file ("40 x 40 x 4").
/// </summary>
internal static class WeldmentSpec
{
    public static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(s.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), @"\s+", " ");

    /// <summary>A size as SOLIDWORKS spells it, compared loosely: "40x40x4", "40 X 40 X 4" and "40 x 40 x 4" are the same.</summary>
    public static string SizeKey(string s) => Regex.Replace(s.ToLowerInvariant().Replace('×', 'x'), @"\s+", "");

    /// <summary>The configuration of <paramref name="sizes"/> that is <paramref name="want"/>, or null.</summary>
    public static string? MatchSize(IReadOnlyList<string> sizes, string want)
    {
        string k = SizeKey(want);
        return sizes.FirstOrDefault(s => SizeKey(s) == k);
    }

    /// <summary>The <paramref name="names"/> entry that is <paramref name="want"/> (case, spaces, '-' and '_' ignored), or null.</summary>
    public static string? MatchName(IEnumerable<string> names, string want)
    {
        string? k = Norm(want);
        return names.FirstOrDefault(n => Norm(n) == k);
    }

    /// <summary>swSolidworksWeldmentEndCondOptions_e of a corner treatment: miter 1 (default), butt1 2, butt2 3; none 0.</summary>
    public static (int Code, string Name) Corner(string? s) => Norm(s) switch
    {
        null or "miter" or "mitre" => (1, "miter"),
        "butt1" or "butt 1" or "butt" => (2, "butt1"),
        "butt2" or "butt 2" => (3, "butt2"),
        "none" or "no" => (0, "none"),
        _ => throw SwException.InvalidArgument($"Unknown corner '{s}'.", "Use miter (default), butt1, butt2 or none."),
    };

    /// <summary>A list for an error message: the first <paramref name="max"/> names, then "…".</summary>
    public static string Few(IEnumerable<string> names, int max = 12)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(max)) + (list.Count > max ? $", … ({list.Count} in all)" : "");
    }
}
