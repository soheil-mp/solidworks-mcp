using System.Globalization;
using System.Text.RegularExpressions;

namespace SolidWorksMcp.Core.Services;

/// <summary>
/// The names drw_annotation_add takes and the SOLIDWORKS codes they map to (pure; offline-tested). Geometric
/// characteristics are the ISO symbols of SOLIDWORKS' gtol.sym library ("&lt;IGTOL-POSI&gt;"); material conditions are
/// its modifying symbols ("&lt;MOD-MMC&gt;").
/// </summary>
internal static class DrwAnnotationSpec
{
    internal sealed record Characteristic(string Name, string Code, bool NeedsDatum, bool TakesDatum);

    private static readonly Characteristic[] All =
    [
        new("flatness", "FLAT", false, false),
        new("straightness", "STRAIGHT", false, false),
        new("circularity", "CIRC", false, false),
        new("cylindricity", "CYL", false, false),
        new("line_profile", "LPROF", false, true),
        new("surface_profile", "SPROF", false, true),
        new("parallelism", "PARA", true, true),
        new("perpendicularity", "PERP", true, true),
        new("angularity", "ANGULAR", true, true),
        new("position", "POSI", false, true),
        new("concentricity", "CONC", true, true),
        new("symmetry", "SYMMETRY", true, true),
        new("circular_runout", "SRUN", true, true),
        new("total_runout", "TRUN", true, true),
    ];

    public static string Names => string.Join(", ", All.Select(c => c.Name));

    public static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    public static string Kind(string? s) => Norm(s) switch
    {
        "datum" or "datum_tag" or "datum_feature" => "datum",
        "surface_finish" or "finish" or "roughness" => "surface_finish",
        "gtol" or "geometric_tolerance" or "fcf" or "feature_control_frame" => "gtol",
        "balloon" => "balloon",
        null => throw SwException.InvalidArgument("'kind' is required: datum, surface_finish, gtol or balloon."),
        _ => throw SwException.InvalidArgument($"Unknown kind '{s}'.", "Use datum, surface_finish, gtol or balloon (drw_balloons balloons a whole view)."),
    };

    public static Characteristic Gtol(string? s)
    {
        string? n = Norm(s) switch
        {
            null => null,
            "flat" => "flatness",
            "straight" => "straightness",
            "roundness" or "round" => "circularity",
            "profile" or "profile_of_a_surface" => "surface_profile",
            "profile_of_a_line" => "line_profile",
            "parallel" => "parallelism",
            "perpendicular" or "squareness" => "perpendicularity",
            "angle" => "angularity",
            "true_position" or "pos" => "position",
            "coaxiality" or "concentric" => "concentricity",
            "runout" => "circular_runout",
            var x => x,
        };
        if (n is null) throw SwException.InvalidArgument($"kind 'gtol' needs 'characteristic': {Names}.");
        return All.FirstOrDefault(c => c.Name == n)
               ?? throw SwException.InvalidArgument($"Unknown characteristic '{s}'.", $"Use {Names}.");
    }

    /// <summary>The frame's symbol code, e.g. "&lt;IGTOL-POSI&gt;".</summary>
    public static string Symbol(Characteristic c) => $"<IGTOL-{c.Code}>";

    /// <summary>The material condition modifier for the tolerance: "" (RFS), "&lt;MOD-MMC&gt;" or "&lt;MOD-LMC&gt;".</summary>
    public static string Material(string? s) => Norm(s) switch
    {
        null or "none" or "rfs" => "",
        "mmc" or "maximum" or "m" => "<MOD-MMC>",
        "lmc" or "least" or "l" => "<MOD-LMC>",
        _ => throw SwException.InvalidArgument($"Unknown material condition '{s}'.", "Use none (default), mmc or lmc."),
    };

    /// <summary>A tolerance value: a positive number (mm), returned as SOLIDWORKS shows it ("0.05").</summary>
    public static string Tolerance(string? s, string arg)
    {
        string t = (s ?? "").Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v <= 0 || v > 1000)
            throw SwException.InvalidArgument($"'{arg}' must be a positive number in mm, e.g. \"0.05\" (got '{s}').");
        return v.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    /// <summary>Datum letters: one or two capitals (not I, O or Q, which ASME Y14.5 leaves out).</summary>
    public static string Label(string? s, string arg)
    {
        string l = (s ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(l, "^[A-Z]{1,2}$") || l.Any(ch => ch is 'I' or 'O' or 'Q'))
            throw SwException.InvalidArgument($"'{arg}' must be a datum letter such as \"A\" (one or two capitals, not I, O or Q; got '{s}').");
        return l;
    }

    /// <summary>The swSFSymType_e of a finish: basic 0, machining required 1, no machining 2.</summary>
    public static (int Code, string Name) Finish(string? s) => Norm(s) switch
    {
        null or "basic" or "any" => (0, "basic"),
        "machining_required" or "machined" or "machining" => (1, "machining_required"),
        "no_machining" or "not_machined" or "dont_machine" => (2, "no_machining"),
        _ => throw SwException.InvalidArgument($"Unknown finish '{s}'.", "Use basic (default), machining_required or no_machining."),
    };

    /// <summary>The frame as text, e.g. "position Ø0.05 (MMC) | A | B".</summary>
    public static string FrameText(Characteristic c, string tol, bool diameter, string material, IReadOnlyList<string> datums) =>
        $"{c.Name} {(diameter ? "Ø" : "")}{tol}{material switch { "<MOD-MMC>" => " (MMC)", "<MOD-LMC>" => " (LMC)", _ => "" }}" +
        string.Concat(datums.Select(x => $" | {x}"));
}
