using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

// Interop-free half of the simulation area (linked into the offline unit tests): the sim_static inputs and their
// validation, the mesh-size guard, the force bookkeeping (components, applied resultant, reaction balance) and the
// interpretation of what the Simulation API returns.

/// <summary>One load of sim_static: a pressure normal to faces, or a force (normal, or along a model direction).</summary>
public sealed class SimLoad
{
    [Description("pressure | force (default: from valueMpa / valueN).")]
    public string? Kind { get; set; }

    [Description("Faces the load acts on.")]
    public EntityRef[]? Faces { get; set; }

    [Description("pressure: MPa (N/mm²), > 0.")]
    public double? ValueMpa { get; set; }

    [Description("force: N, > 0, the total over its faces.")]
    public double? ValueN { get; set; }

    [Description("\"normal\" (default: pushes into each face) or, for a force, a model direction [x,y,z] or \"-y\".")]
    public JsonElement? Direction { get; set; }
}

/// <summary>sim_static options (validated again by the service).</summary>
public sealed record SimStaticRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? FixedFaces { get; init; }
    public IReadOnlyList<SimLoad>? Loads { get; init; }
    /// <summary>Material to assign before the study (null = keep the part's material).</summary>
    public string? Material { get; init; }
    /// <summary>Global element size in mm (null = SOLIDWORKS' default for the part).</summary>
    public double? MeshSizeMm { get; init; }
    public bool KeepStudy { get; init; }
    public string? StudyName { get; init; }

    /// <summary>
    /// Live tests only (not reachable from the tool): run a study without a fixture, so the solver's own refusal and the
    /// cleanup after a failed run can be tested.
    /// </summary>
    internal bool AllowNoFixtures { get; init; }
}

internal enum SimLoadKind { Pressure, Force }

/// <summary>A validated load: <see cref="Value"/> in SI (Pa for a pressure, N for a force); <see cref="Direction"/> null = normal.</summary>
internal sealed record SimLoadSpec(int Index, SimLoadKind Kind, IReadOnlyList<EntityRef> Faces, double Value, Vec3? Direction)
{
    public string Arg => $"loads[{Index}]";
    public string KindName => Kind == SimLoadKind.Pressure ? "pressure" : "force";
}

/// <summary>A [nodeMin, min, nodeMax, max] answer of GetMinMaxStress / GetMinMaxDisplacement.</summary>
internal sealed record SimRange(int MinNode, double Min, int MaxNode, double Max);

/// <summary>A planar load face: its outward unit normal and its area (m²).</summary>
internal readonly record struct SimFace(Vec3 Normal, double AreaM2);

/// <summary>The outcome of comparing the fixture reactions with the applied loads.</summary>
internal sealed record SimBalanceCheck(bool Balanced, bool LoadSign, double ResidualN);

internal static class SimulationMath
{
    public const double PaPerMpa = 1e6;
    public const int MaxLoads = 20;
    public const string DefaultStudyPrefix = "MCP Static";

    /// <summary>sim_static's time limit (s): meshing and solving are single blocking calls, so the whole run gets it.</summary>
    public const int TimeoutS = 1800;

    /// <summary>Refuse element sizes whose mesh would have more elements than this (hours of solving, or a hang).</summary>
    public const long MaxEstimatedElements = 1_500_000;

    /// <summary>The size the refusal suggests aims at about this many elements.</summary>
    public const long SuggestedElements = 200_000;

    /// <summary>Tolerance / element size when the caller sets the size (SOLIDWORKS' own default ratio is 5 %).</summary>
    public const double DefaultToleranceRatio = 0.05;

    /// <summary>Reactions must cancel the loads to this fraction of the load (FEA balances them to solver precision).</summary>
    public const double BalanceTolerance = 0.02;

    /// <summary>The reaction check reads one entry per restrained node: skip it on meshes larger than this.</summary>
    public const int MaxNodesForReactions = 500_000;

    // ------------------------------------------------------------------ inputs

    public static IReadOnlyList<SimLoadSpec> ParseLoads(IReadOnlyList<SimLoad>? loads)
    {
        if (loads is null || loads.Count == 0)
            throw SwException.InvalidArgument("'loads' is empty: a static study needs at least one load.",
                "E.g. loads:[{kind:'pressure',faces:[{at:[50,10,5]}],valueMpa:0.1}].");
        if (loads.Count > MaxLoads)
            throw SwException.InvalidArgument($"'loads' has {loads.Count} entries; at most {MaxLoads} are supported in one study.");
        return loads.Select((l, i) => ParseLoad(l, i)).ToList();
    }

    public static SimLoadSpec ParseLoad(SimLoad? load, int index)
    {
        string arg = $"loads[{index}]";
        if (load is null) throw SwException.InvalidArgument($"'{arg}' is null.");
        string kind = (load.Kind ?? "").Trim().ToLowerInvariant();
        if (kind.Length == 0)
            kind = load.ValueMpa is not null && load.ValueN is null ? "pressure"
                 : load.ValueN is not null && load.ValueMpa is null ? "force"
                 : throw SwException.InvalidArgument($"'{arg}' needs 'kind' (pressure or force).",
                     "pressure takes valueMpa; force takes valueN.");
        if (load.Faces is null || load.Faces.Length == 0)
            throw SwException.InvalidArgument($"'{arg}.faces' is empty.", "Pass the faces the load acts on, e.g. faces:[{at:[50,10,5]}].");

        switch (kind)
        {
            case "pressure":
            {
                if (load.ValueN is not null)
                    throw SwException.InvalidArgument($"'{arg}' is a pressure: give valueMpa, not valueN.", "Or use kind 'force' with valueN.");
                double mpa = Positive(load.ValueMpa, $"{arg}.valueMpa");
                if (ParseDirection(load.Direction, $"{arg}.direction") is not null)
                    throw SwException.InvalidArgument($"'{arg}': a pressure always acts normal to its faces.",
                        "Omit 'direction' (or pass \"normal\"), or use kind 'force' with direction [x,y,z].");
                return new SimLoadSpec(index, SimLoadKind.Pressure, load.Faces, mpa * PaPerMpa, null);
            }
            case "force":
            {
                if (load.ValueMpa is not null)
                    throw SwException.InvalidArgument($"'{arg}' is a force: give valueN, not valueMpa.", "Or use kind 'pressure' with valueMpa.");
                double n = Positive(load.ValueN, $"{arg}.valueN");
                return new SimLoadSpec(index, SimLoadKind.Force, load.Faces, n, ParseDirection(load.Direction, $"{arg}.direction"));
            }
            default:
                throw SwException.InvalidArgument($"'{arg}.kind' '{load.Kind}' is not supported.", "Use pressure or force.");
        }
    }

    private static double Positive(double? v, string arg)
    {
        if (v is not double x) throw SwException.InvalidArgument($"'{arg}' is required.");
        if (!double.IsFinite(x) || x <= 0) throw SwException.InvalidArgument($"'{arg}' must be a positive number (got {x.ToString(CultureInfo.InvariantCulture)}).");
        return x;
    }

    /// <summary>null for "normal" (or nothing); otherwise the unit model direction.</summary>
    public static Vec3? ParseDirection(JsonElement? direction, string arg)
    {
        if (direction is not JsonElement je || je.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (je.ValueKind == JsonValueKind.String && je.GetString()?.Trim().ToLowerInvariant() is "normal" or "") return null;
        if (je.ValueKind == JsonValueKind.Array)
        {
            if (je.GetArrayLength() != 3 || je.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number))
                throw SwException.InvalidArgument($"'{arg}' must be \"normal\", an axis like \"-y\" or a vector [x, y, z].");
            var v = je.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (v.Any(c => !double.IsFinite(c)) || new Vec3(v[0], v[1], v[2]).Length < 1e-12)
                throw SwException.InvalidArgument($"'{arg}' must be a non-zero vector [x, y, z].");
        }
        else if (je.ValueKind != JsonValueKind.String)
            throw SwException.InvalidArgument($"'{arg}' must be \"normal\", an axis like \"-y\" or a vector [x, y, z].");
        return AxisSpec.Parse(je, arg);
    }

    /// <summary>The requested study name (checked), or a fresh default "MCP Static 1a2b".</summary>
    public static string StudyName(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return $"{DefaultStudyPrefix} {Guid.NewGuid().ToString("N")[..4]}";
        string name = requested.Trim();
        if (name.Length > 64) throw SwException.InvalidArgument($"'studyName' is {name.Length} characters long; use at most 64.");
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            throw SwException.InvalidArgument($"'studyName' '{name}' contains a character SOLIDWORKS doesn't accept (\\ / : * ? \" < > |).");
        return name;
    }

    // ------------------------------------------------------------------ mesh

    /// <summary>
    /// Rough tetrahedron count for a mesh of edge length <paramref name="sizeMm"/> over <paramref name="volumeM3"/>:
    /// a regular tetrahedron of edge h holds h³/(6√2) ≈ 0.118 h³, so N ≈ 8.5 V / h³.
    /// </summary>
    public static double EstimatedElements(double volumeM3, double sizeMm)
    {
        double vmm3 = volumeM3 * 1e9;
        return sizeMm > 0 ? 6 * Math.Sqrt(2) * vmm3 / (sizeMm * sizeMm * sizeMm) : double.PositiveInfinity;
    }

    /// <summary>The element size (mm) giving about <paramref name="elements"/> elements.</summary>
    public static double SizeFor(double volumeM3, long elements) => Math.Cbrt(6 * Math.Sqrt(2) * volumeM3 * 1e9 / elements);

    /// <summary>Validates a requested element size against the part volume (before anything is created).</summary>
    public static void CheckMeshSize(double? sizeMm, double volumeM3)
    {
        if (sizeMm is not double s) return;
        if (!double.IsFinite(s) || s <= 0)
            throw SwException.InvalidArgument($"'meshSizeMm' must be a positive length in mm (got {s.ToString(CultureInfo.InvariantCulture)}).");
        double n = EstimatedElements(volumeM3, s);
        if (n > MaxEstimatedElements)
        {
            double suggest = SizeFor(volumeM3, SuggestedElements);
            throw SwException.InvalidArgument(
                $"meshSizeMm {F(s)} would give about {F(Sig(n / 1e6, 2))} million elements for this part ({F(Sig(volumeM3 * 1e9, 4))} mm³): " +
                "that can take hours to solve.",
                $"Use about {F(Sig(suggest, 2))} mm (≈{SuggestedElements / 1000}k elements), or omit meshSizeMm for SOLIDWORKS' default size.");
        }
    }

    /// <summary>Tolerance for a caller-chosen size: SOLIDWORKS' default ratio when its default pair is known, else 5 %.</summary>
    public static double Tolerance(double sizeMm, double defaultSizeMm, double defaultTolMm) =>
        sizeMm * (defaultSizeMm > 0 && defaultTolMm > 0 && defaultTolMm < defaultSizeMm ? defaultTolMm / defaultSizeMm : DefaultToleranceRatio);

    // ------------------------------------------------------------------ forces

    /// <summary>A force vector expressed along a reference plane's axes (Simulation's direction 1, direction 2, normal).</summary>
    public static double[] Components(Vec3 force, Vec3 xAxis, Vec3 yAxis, Vec3 normal) =>
        [force.Dot(xAxis.Normalized()), force.Dot(yAxis.Normalized()), force.Dot(normal.Normalized())];

    /// <summary>
    /// AddForce3's component arguments for a force along a plane's axes: which components are used (|c| above a
    /// relative epsilon), the Ucode bit mask (1 = dir 1, 2 = dir 2, 4 = normal) and the Comps array. The API wants every
    /// Comps element non-zero, so unused ones carry 1 (Ucode excludes them).
    /// </summary>
    public static (bool[] Use, int Ucode, double[] Comps) ForceComps(double[] components, double magnitude)
    {
        var use = components.Select(c => Math.Abs(c) > 1e-9 * Math.Max(magnitude, 1e-12)).ToArray();
        int ucode = (use[0] ? 1 : 0) | (use[1] ? 2 : 0) | (use[2] ? 4 : 0);
        var comps = components.Select((c, i) => use[i] ? c : 1.0).ToArray();
        return (use, ucode, comps);
    }

    /// <summary>The resultant (N) of a normal pressure <paramref name="pa"/> on planar faces: it pushes into each face.</summary>
    public static Vec3 PressureResultant(double pa, IReadOnlyList<SimFace> faces) =>
        faces.Aggregate(Vec3.Zero, (sum, f) => sum - f.Normal.Normalized() * (pa * f.AreaM2));

    /// <summary>
    /// The resultant (N) of a normal force <paramref name="n"/> spread over planar faces by area, pushing into each face
    /// (the same sense as a positive pressure).
    /// </summary>
    public static Vec3 NormalForceResultant(double n, IReadOnlyList<SimFace> faces)
    {
        double area = faces.Sum(f => f.AreaM2);
        return area > 0 ? PressureResultant(n / area, faces) : Vec3.Zero;
    }

    /// <summary>
    /// The total reaction force (N) from GetReactionForcesAndMomentsWithSelections' 16-value summary: the entire model's
    /// sums (elements 8..10), else the selections' sums (0..2); null when the answer is unusable.
    /// </summary>
    public static Vec3? ReactionTotal(double[]? summary)
    {
        if (summary is null || summary.Length < 4 || summary.Take(Math.Min(summary.Length, 11)).Any(v => !double.IsFinite(v))) return null;
        if (summary.Length >= 11)
        {
            var all = new Vec3(summary[8], summary[9], summary[10]);
            if (all.Length > 0) return all;
        }
        return new Vec3(summary[0], summary[1], summary[2]);
    }

    /// <summary>
    /// Do the reactions cancel the applied loads? Equilibrium means reaction + load = 0. Which sign SOLIDWORKS gives its
    /// reactions is not verified yet, so reaction = load (the force on the fixture) also counts and is reported as
    /// <see cref="SimBalanceCheck.LoadSign"/>.
    /// </summary>
    public static SimBalanceCheck Balance(Vec3 reaction, Vec3 load)
    {
        double scale = Math.Max(load.Length, 1e-9);
        double opposite = (reaction + load).Length, same = (reaction - load).Length;
        double tol = BalanceTolerance * scale;
        if (opposite <= tol) return new SimBalanceCheck(true, false, opposite);
        if (same <= tol) return new SimBalanceCheck(true, true, same);
        return new SimBalanceCheck(false, false, Math.Min(opposite, same));
    }

    // ------------------------------------------------------------------ results

    /// <summary>Parses [nodeMin, min, nodeMax, max]; null when the answer is missing or not finite.</summary>
    public static SimRange? Range(double[]? raw)
    {
        if (raw is null || raw.Length < 4 || raw.Take(4).Any(v => !double.IsFinite(v))) return null;
        return new SimRange((int)Math.Round(raw[0]), raw[1], (int)Math.Round(raw[2]), raw[3]);
    }

    /// <summary>
    /// A mesh node location in mm. The Simulation API's length unit for node coordinates is not documented, so the point
    /// is accepted in meters or in millimeters, whichever lies inside the part's box (<paramref name="boxM"/>, meters).
    /// </summary>
    public static double[]? LocationMm(double x, double y, double z, double[]? boxM)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z) || boxM is not { Length: >= 6 }) return null;
        double diag = new Vec3(boxM[3] - boxM[0], boxM[4] - boxM[1], boxM[5] - boxM[2]).Length;
        double pad = Math.Max(1e-6, 1e-3 * diag);
        if (Inside(x, y, z, boxM, pad)) return [Units.Mm(x), Units.Mm(y), Units.Mm(z)];
        if (Inside(x / 1000, y / 1000, z / 1000, boxM, pad)) return [Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6)];
        return null;
    }

    private static bool Inside(double x, double y, double z, double[] b, double pad) =>
        x >= b[0] - pad && x <= b[3] + pad && y >= b[1] - pad && y <= b[4] + pad && z >= b[2] - pad && z <= b[5] + pad;

    /// <summary>Yield strength over the largest von Mises stress (null when either is unknown or the stress is zero).</summary>
    public static double? FactorOfSafety(double? yieldMpa, double maxVonMisesMpa) =>
        yieldMpa is double y && y > 0 && maxVonMisesMpa > 0 && double.IsFinite(maxVonMisesMpa) ? Sig(y / maxVonMisesMpa, 4) : null;

    /// <summary>Rounds to <paramref name="digits"/> significant digits (results span many orders of magnitude).</summary>
    public static double Sig(double v, int digits = 6)
    {
        if (v == 0 || !double.IsFinite(v)) return v;
        int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        int decimals = digits - 1 - exp;
        if (decimals is >= 0 and <= 15) return Math.Round(v, decimals);
        if (decimals < 0)
        {
            double step = Math.Pow(10, -decimals); // exact for integers: 1234567 → 1234570
            return Math.Round(v / step) * step;
        }
        double scale = Math.Pow(10, decimals);
        return Math.Round(v * scale) / scale;
    }

    /// <summary>A vector rounded to significant digits, with float noise near zero (relative to its length) cleared.</summary>
    public static double[] Sig(Vec3 v, int digits = 6)
    {
        double floor = 1e-9 * v.Length;
        return [Clean(v.X), Clean(v.Y), Clean(v.Z)];
        double Clean(double c) => Math.Abs(c) <= floor ? 0 : Sig(c, digits);
    }

    public static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static string Vector(Vec3 v) => $"{F(Units.R(v.X))},{F(Units.R(v.Y))},{F(Units.R(v.Z))}";

    public static string Newtons(Vec3 v) => "[" + string.Join(", ", Sig(v, 4).Select(F)) + "] N";

    // ------------------------------------------------------------------ names and hints

    /// <summary>swsAnalysisStudyType_e as a word.</summary>
    public static string StudyType(int type) => type switch
    {
        0 => "static",
        1 => "frequency",
        2 => "buckling",
        3 => "thermal",
        4 => "design_study",
        5 => "nonlinear",
        6 => "drop_test",
        7 => "fatigue",
        8 => "dynamic",
        9 => "pressure_vessel",
        13 => "topology",
        _ => $"type_{type}",
    };

    /// <summary>swsMeshState_e as a word.</summary>
    public static string MeshState(int state) => state switch
    {
        0 => "none",
        1 => "current",
        2 => "outdated",
        3 => "failed",
        4 => "interrupted",
        _ => $"state_{state}",
    };

    /// <summary>What to do about a non-zero RunAnalysis status (swsRunAnalysisError_e).</summary>
    public static string RunHint(int code) => code switch
    {
        30 => "SOLIDWORKS rejected the fixtures or loads: check that fixedFaces really hold the part (a free body can't be solved) " +
              "and that every load sits on faces of the solid body.",
        22 => "No SOLIDWORKS Simulation license could be obtained; ask the user to check the license (Premium/Professional).",
        13 or 14 or 15 or 18 or 19 or 20 => "The material lacks elastic properties: pass a library material with 'material' (material_list).",
        21 => "The part has no solid body to analyze.",
        23 => "No mesh was found: meshing failed. Try another meshSizeMm or omit it.",
        24 => "The solver failed, often because the model is not held enough: add fixtures (or check model_check for bad geometry).",
        _ => "Check the fixtures, loads and mesh size; running the study in the SOLIDWORKS UI shows the solver messages.",
    };
}
