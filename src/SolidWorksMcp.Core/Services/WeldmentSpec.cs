using System.Text.RegularExpressions;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of weldment_finish: trim/extend, end caps, gussets and weld beads on a weldment's members.</summary>
public sealed record WeldmentFinishRequest
{
    public string? Doc { get; init; }
    /// <summary>trim | end_cap | gusset | bead.</summary>
    public string? Kind { get; init; }
    /// <summary>trim: the members to trim or extend (bodies by name or id, or a face of each).</summary>
    public IReadOnlyList<EntityRef>? Bodies { get; init; }
    /// <summary>trim: where they end: bodies (by name or id) or faces.</summary>
    public IReadOnlyList<EntityRef>? Boundary { get; init; }
    /// <summary>trim: trim (default) | miter | butt1 | butt2.</summary>
    public string? Corner { get; init; }
    /// <summary>trim: weld gap left between the trimmed ends and the boundary.</summary>
    public double GapMm { get; init; }
    /// <summary>trim: cut to the boundary body's shape (a coped cut) instead of its nearest plane.</summary>
    public bool Coped { get; init; }
    /// <summary>end_cap: the members' open end faces; gusset: the two faces it joins; bead: the faces welded from.</summary>
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>bead: the faces welded to.</summary>
    public IReadOnlyList<EntityRef>? Faces2 { get; init; }
    /// <summary>end_cap and gusset: plate thickness.</summary>
    public double? ThicknessMm { get; init; }
    /// <summary>end_cap: how far the cap's outline sits inside the member's outer edges.</summary>
    public double OffsetMm { get; init; }
    /// <summary>end_cap: outward (default: beyond the end) | inward (the member gets shorter) | internal (in the bore).</summary>
    public string? Direction { get; init; }
    /// <summary>end_cap internal: how far inside the end the cap sits.</summary>
    public double InsetMm { get; init; }
    /// <summary>gusset: the triangle's legs, along faces[0] and along faces[1].</summary>
    public IReadOnlyList<double>? LegsMm { get; init; }
    /// <summary>gusset: start | center (default) | end of the edge where the faces meet.</summary>
    public string? Location { get; init; }
    /// <summary>gusset: both (default) | inner | outer side of its profile plane; bead: selection (default) | both | all_around.</summary>
    public string? Side { get; init; }
    /// <summary>bead: weld size (leg).</summary>
    public double? SizeMm { get; init; }
    /// <summary>bead: follow tangent edges.</summary>
    public bool Tangent { get; init; }
    /// <summary>bead: intermittent beads of this length, one every pitchMm (both or neither).</summary>
    public double? LengthMm { get; init; }
    public double? PitchMm { get; init; }
    public bool KeepOnFailure { get; init; }
}

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

    // ------------------------------------------------------------------ weldment_finish

    /// <summary>weldment_finish kinds: trim, end_cap, gusset, bead.</summary>
    public static string FinishKind(string? s) => Norm(s) switch
    {
        "trim" or "trim extend" or "extend" => "trim",
        "end cap" or "endcap" or "cap" => "end_cap",
        "gusset" => "gusset",
        "bead" or "weld bead" or "weld" or "fillet bead" => "bead",
        null => throw SwException.InvalidArgument("'kind' is required: trim, end_cap, gusset or bead."),
        _ => throw SwException.InvalidArgument($"Unknown kind '{s}'.",
            "Use trim (trim or extend members to a boundary), end_cap, gusset or bead (a weld bead)."),
    };

    /// <summary>Weld bead sides (swCosmeticWeldBeadSide_e): selection 1 (default), both 2, all_around 3.</summary>
    public static (int Code, string Name) BeadSide(string? s) => Norm(s) switch
    {
        null or "selection" or "selected" or "one" => (1, "selection"),
        "both" or "both sides" => (2, "both"),
        "all around" or "around" => (3, "all_around"),
        _ => throw SwException.InvalidArgument($"Unknown side '{s}' for a bead.", "Use selection (default: where the faces meet), both or all_around."),
    };

    /// <summary>Corner type of a trim/extend (swSolidworksWeldmentEndCondOptions_e): trim 4 (default), miter 1, butt1 2, butt2 3.</summary>
    public static (int Code, string Name) TrimCorner(string? s) => Norm(s) switch
    {
        null or "trim" or "end trim" => (4, "trim"),
        "miter" or "mitre" => (1, "miter"),
        "butt1" or "butt 1" or "butt" => (2, "butt1"),
        "butt2" or "butt 2" => (3, "butt2"),
        _ => throw SwException.InvalidArgument($"Unknown corner '{s}'.",
            "Use trim (default: the bodies end at the boundary), or miter, butt1 or butt2 for two members meeting at a corner."),
    };

    /// <summary>swGussetProfileLocationType_e: start 0, center 1 (default), end 2.</summary>
    public static (short Code, string Name) GussetLocation(string? s) => Norm(s) switch
    {
        "start" => (0, "start"),
        null or "center" or "centre" or "middle" or "mid" => (1, "center"),
        "end" => (2, "end"),
        _ => throw SwException.InvalidArgument($"Unknown location '{s}'.", "Use start, center (default) or end of the edge where the faces meet."),
    };

    /// <summary>swGussetThicknessType_e: inner 0, both 1 (default), outer 2.</summary>
    public static (short Code, string Name) GussetSide(string? s) => Norm(s) switch
    {
        "inner" or "inside" => (0, "inner"),
        null or "both" or "both sides" or "symmetric" => (1, "both"),
        "outer" or "outside" => (2, "outer"),
        _ => throw SwException.InvalidArgument($"Unknown side '{s}'.", "Use both (default: centred on the profile plane), inner or outer."),
    };

    /// <summary>
    /// End-cap thickness direction as InsertEndCapFeature3 takes it: outward 0 (default), inward 1, internal 2.
    /// VERIFIED: these are not swEndCapThicknessDirection_e's 1..3 that the help names (1 made an inward cap, 2 one
    /// inside the bore; the help also says the inset is "valid only if BIsEndCapInward = 2").
    /// </summary>
    public static (int Code, string Name) CapDirection(string? s) => Norm(s) switch
    {
        null or "outward" or "out" => (0, "outward"),
        "inward" or "in" => (1, "inward"),
        "internal" or "inside" or "inset" => (2, "internal"),
        _ => throw SwException.InvalidArgument($"Unknown direction '{s}'.",
            "Use outward (default: the cap adds length beyond the end), inward (the member gets shorter by the thickness) or internal (in the bore, insetMm from the end)."),
    };

    /// <summary>Checks what a weldment_finish kind needs before anything changes; returns the given parameters it ignores.</summary>
    public static IReadOnlyList<string> CheckFinish(string kind, WeldmentFinishRequest q)
    {
        static void Size(double? v, string name, string what, double max = 1000)
        {
            if (v is not double x) throw SwException.InvalidArgument($"'{name}' is required for {what}.");
            if (!double.IsFinite(x) || x <= 0 || x > max) throw SwException.InvalidArgument($"{name} must be > 0 and at most {max} mm (got {x}).");
        }
        static void Need(IReadOnlyList<EntityRef>? refs, string name, string what, string hint)
        {
            if (refs is not { Count: > 0 }) throw SwException.InvalidArgument($"'{name}' is required for {what}.", hint);
        }
        static void Range(double v, string name, double max)
        {
            if (!double.IsFinite(v) || v < 0 || v > max) throw SwException.InvalidArgument($"{name} must be between 0 and {max} mm (got {v}).");
        }
        var given = new List<string>();
        void Given(bool set, string name)
        {
            if (set) given.Add(name);
        }
        Given(q.Bodies is { Count: > 0 }, "bodies");
        Given(q.Boundary is { Count: > 0 }, "boundary");
        Given(q.Corner is not null, "corner");
        Given(q.GapMm != 0, "gapMm");
        Given(q.Coped, "coped");
        Given(q.Faces is { Count: > 0 }, "faces");
        Given(q.Faces2 is { Count: > 0 }, "faces2");
        Given(q.ThicknessMm is not null, "thicknessMm");
        Given(q.OffsetMm != 0, "offsetMm");
        Given(q.Direction is not null, "direction");
        Given(q.InsetMm != 0, "insetMm");
        Given(q.LegsMm is { Count: > 0 }, "legsMm");
        Given(q.Location is not null, "location");
        Given(q.Side is not null, "side");
        Given(q.SizeMm is not null, "sizeMm");
        Given(q.Tangent, "tangent");
        Given(q.LengthMm is not null, "lengthMm");
        Given(q.PitchMm is not null, "pitchMm");
        string[] uses;
        switch (kind)
        {
            case "trim":
                Need(q.Bodies, "bodies", "a trim", "The members to trim or extend, e.g. [{name:\"Structural Member1[2]\"}] (body_list), or a face of each.");
                Need(q.Boundary, "boundary", "a trim", "Where they end: bodies (e.g. the crossing member) or faces.");
                TrimCorner(q.Corner);
                Range(q.GapMm, "gapMm", 100);
                uses = ["bodies", "boundary", "corner", "gapMm", "coped"];
                break;
            case "end_cap":
                Need(q.Faces, "faces", "an end cap", "The open end faces of members, e.g. [{at:[x,y,z]}] on a tube's end.");
                Size(q.ThicknessMm, "thicknessMm", "an end cap", 100);
                Range(q.OffsetMm, "offsetMm", 100);
                Range(q.InsetMm, "insetMm", 100);
                if (q.InsetMm > 0 && CapDirection(q.Direction).Code != 2)
                    throw SwException.InvalidArgument("insetMm is for direction internal (a cap inside the bore).");
                uses = ["faces", "thicknessMm", "offsetMm", "direction", "insetMm"];
                break;
            case "gusset":
                if (q.Faces is not { Count: 2 })
                    throw SwException.InvalidArgument($"A gusset needs exactly two faces (got {q.Faces?.Count ?? 0}).",
                        "The two flat faces it joins, of members that meet at an edge: e.g. a post's side and a beam's top.");
                Size(q.ThicknessMm, "thicknessMm", "a gusset", 100);
                if (q.LegsMm is not { Count: 2 })
                    throw SwException.InvalidArgument("'legsMm' must be the gusset's two legs: [along faces[0], along faces[1]].");
                Size(q.LegsMm[0], "legsMm[0]", "a gusset");
                Size(q.LegsMm[1], "legsMm[1]", "a gusset");
                GussetLocation(q.Location);
                GussetSide(q.Side);
                uses = ["faces", "thicknessMm", "legsMm", "location", "side"];
                break;
            default:
                Need(q.Faces, "faces", "a weld bead", "The faces welded from, e.g. the side of a post.");
                Need(q.Faces2, "faces2", "a weld bead", "The faces welded to, e.g. the top of the beam the post stands on.");
                Size(q.SizeMm, "sizeMm", "a weld bead", 100);
                if (q.Tangent && BeadSide(q.Side).Code != 1)
                    throw SwException.InvalidArgument("side both or all_around works without tangent propagation only.", "Leave tangent out, or the side.");
                if ((q.LengthMm is null) != (q.PitchMm is null))
                    throw SwException.InvalidArgument("Intermittent beads need both lengthMm and pitchMm; leave both out for a full-length bead.");
                if (q.LengthMm is double len)
                {
                    Size(len, "lengthMm", "an intermittent bead");
                    Size(q.PitchMm, "pitchMm", "an intermittent bead");
                    if (q.PitchMm <= len)
                        throw SwException.InvalidArgument($"pitchMm ({q.PitchMm}) runs from bead start to bead start, so it must be longer than lengthMm ({len}).");
                }
                uses = ["faces", "faces2", "sizeMm", "side", "tangent", "lengthMm", "pitchMm"];
                break;
        }
        return given.Where(g => !uses.Contains(g)).ToList();
    }

    /// <summary>A list for an error message: the first <paramref name="max"/> names, then "…".</summary>
    public static string Few(IEnumerable<string> names, int max = 12)
    {
        var list = names.ToList();
        return string.Join(", ", list.Take(max)) + (list.Count > max ? $", … ({list.Count} in all)" : "");
    }
}
