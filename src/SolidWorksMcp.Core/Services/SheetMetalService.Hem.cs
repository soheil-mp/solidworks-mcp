using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of sheetmetal_hem: a hem (the sheet folded back on itself) along straight edges.</summary>
public sealed record HemRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>closed (default) | open | teardrop | rolled.</summary>
    public string? Type { get; init; }
    /// <summary>open / closed: the hem's length.</summary>
    public double? LengthMm { get; init; }
    /// <summary>open: the gap between the hem and the sheet.</summary>
    public double? GapMm { get; init; }
    /// <summary>teardrop / rolled: the bend radius and angle.</summary>
    public double? RadiusMm { get; init; }
    public double? AngleDeg { get; init; }
    /// <summary>inside (default: the hem's material inside the sheet's edge) | outside.</summary>
    public string? Position { get; init; }
    public bool Reverse { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class SheetMetalService
{
    // The faces belong to the hem's HemBend sub-feature, not to the Hem itself (VERIFIED): the volume rule decides.
    private static readonly FeatureSpec HemSpec = new("sheetmetal_hem", ["Hem", "SMHem"], VolumeRule.Increase, RequireFaces: false);

    private sealed record HemPlan(int Type, string Name, double Length, double Gap, double Radius, double Angle, int Position);

    public Task<SheetMetalResult> HemAsync(HemRequest q, CancellationToken ct = default)
    {
        var plan = HemPlanOf(q);
        if (q.Edges is not { Count: > 0 })
            throw SwException.InvalidArgument("Pass 'edges': straight edges of the sheet's face, e.g. [{type:'edge', at:[25,0,0]}].");
        return session.RunAsync("sheetmetal_hem", ctx => Hem(ctx, q, plan), RunOptions.Write(q.Doc) with { Timeout = Long }, ct);
    }

    private static HemPlan HemPlanOf(HemRequest q)
    {
        var (type, name) = BodyKit.Norm(q.Type) switch
        {
            null or "closed" => ((int)swHemTypes_e.swHemTypeClosed, "closed"),
            "open" => ((int)swHemTypes_e.swHemTypeOpen, "open"),
            "teardrop" or "tear_drop" => ((int)swHemTypes_e.swHemTypeTearDrop, "teardrop"),
            "rolled" => ((int)swHemTypes_e.swHemTypeRolled, "rolled"),
            _ => throw SwException.InvalidArgument($"Unknown hem type '{q.Type}'.", "Use closed (default), open, teardrop or rolled."),
        };
        int position = BodyKit.Norm(q.Position) switch
        {
            null or "inside" => (int)swHemPositionTypes_e.swHemPositionTypeInside,
            "outside" => (int)swHemPositionTypes_e.swHemPositionTypeOutside,
            _ => throw SwException.InvalidArgument($"Unknown position '{q.Position}'.", "Use inside (default) or outside."),
        };
        double len = 0, gap = 0, rad = 0, ang = 0;
        if (name is "closed" or "open")
        {
            len = Units.PositiveM(q.LengthMm ?? throw SwException.InvalidArgument($"A {name} hem needs lengthMm."), "lengthMm");
            if (q.RadiusMm is not null || q.AngleDeg is not null) throw SwException.InvalidArgument($"radiusMm and angleDeg are for teardrop and rolled hems, not {name} ones.");
            if (name == "open") gap = Units.PositiveM(q.GapMm ?? throw SwException.InvalidArgument("An open hem needs gapMm (the gap to the sheet)."), "gapMm");
            else if (q.GapMm is not null) throw SwException.InvalidArgument("A closed hem has no gap; use type open for one.");
        }
        else
        {
            if (q.LengthMm is not null || q.GapMm is not null) throw SwException.InvalidArgument($"lengthMm and gapMm are for open and closed hems, not {name} ones.");
            rad = Units.PositiveM(q.RadiusMm ?? throw SwException.InvalidArgument($"A {name} hem needs radiusMm."), "radiusMm");
            double a = q.AngleDeg ?? (name == "teardrop" ? 200 : 180); // 270° teardrops failed on a 2 mm sheet, 200° worked (VERIFIED)
            if (!double.IsFinite(a) || a <= 0 || a >= 360) throw SwException.InvalidArgument($"angleDeg must be between 0 and 360 (got {a}).");
            ang = Units.Rad(a);
        }
        return new HemPlan(type, name, len, gap, rad, ang, position);
    }

    private static SheetMetalResult Hem(SwContext ctx, HemRequest q, HemPlan plan)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        var smFeature = SheetMetalFeature(d)
                        ?? throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a sheet-metal part (no SheetMetal feature).", "Start with sheetmetal_base_flange.");
        var sheet = ReadSheet(smFeature) ?? throw new SwException(ErrorCodes.Internal, $"Could not read the sheet-metal parameters of '{smFeature.Name}'.");
        double t = Units.M(sheet.ThicknessMm);
        if (FlatPatterns(d).Any(f => !f.IsSuppressed()))
            throw new SwException(ErrorCodes.InvalidArgument, $"'{ctx.Title(d)}' is flattened.", "Fold it first with sheetmetal_flatten {flat:false}.");
        var edgeRefs = EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge);
        var plans = edgeRefs.Select((e, i) => PlanEdge(e, t, $"edges[{i}]")).ToList();

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        var fm = d.FeatureManager;
        ICustomBendAllowance Kfactor()
        {
            var cba = fm.CreateCustomBendAllowance();
            cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
            cba.KFactor = sheet.KFactor ?? 0.5;
            return cba;
        }
        int obround = (int)swSheetMetalReliefTypes_e.swSheetMetalReliefObround;
        double miter = Math.Max(0.0001, t / 2);
        var attempts = new List<(string Label, Func<object?> Run)>
        {
            ("InsertSheetMetalHem2, default relief, the sheet's bend allowance", () =>
                fm.InsertSheetMetalHem2(plan.Type, plan.Position, q.Reverse, plan.Length, plan.Gap, plan.Angle, plan.Radius, miter, null, true, 0, 0, false, 0.5, 0, 0)),
            ("InsertSheetMetalHem2, obround relief, a K-factor bend allowance (SOLIDWORKS' example)", () =>
                fm.InsertSheetMetalHem2(plan.Type, plan.Position, q.Reverse, plan.Length, plan.Gap, plan.Angle, plan.Radius, miter, (CustomBendAllowance)(object)Kfactor(), false, obround, 0, true, 1.0, 0, 0)),
            ("InsertSheetMetalHem", () =>
                fm.InsertSheetMetalHem(plan.Type, plan.Position, q.Reverse, plan.Length, plan.Gap, plan.Angle, plan.Radius, miter, null)),
        };
        var tried = new List<string>();
        // Each variant with the edges picked at their midpoints (as a user picks them), then selected as entities.
        var selections = new (string How, Func<EdgePlan, bool> Select)[]
        {
            ("picked", p =>
            {
                var m = (p.S + p.E) / 2;
                return d.Extension.SelectByID2("", "EDGE", m.X, m.Y, m.Z, true, 0, null, 0);
            }),
            ("selected", p => p.Ref.Select(d, true, 0)),
        };
        foreach (var ((label0, run), (how, select)) in attempts.SelectMany(a => selections.Select(sel => (a, sel))))
        {
            string label = $"{label0}, edges {how}";
            d.ClearSelection2(true);
            if (!plans.All(select) || d.Sel().GetSelectedObjectCount2(-1) != plans.Count)
            {
                tried.Add($"{label}: the edges could not be selected");
                continue;
            }
            object? api;
            try
            {
                api = run();
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is System.Runtime.InteropServices.COMException or InvalidCastException or ArgumentException)
            {
                tried.Add($"{label}: {BodyKit.Clip(ex.Message, 160)}");
                BodyKit.CleanUp(ctx, d, before);
                continue;
            }
            finally
            {
                d.ClearSelection2(true);
            }
            if (BodyKit.NothingNew(d, before))
            {
                tried.Add($"{label}: nothing created");
                continue;
            }
            var o = BodyKit.Verify(ctx, d, before, HemSpec, (main, after, checks) => HemIntent(main, before, after, plans, plan, t, checks), q.KeepOnFailure, api,
                "Hems go on straight edges of the sheet's face, away from other flanges; try a shorter length or type open.", guessedType: true);
            if (tried.Count > 0) o = o with { Checks = [.. o.Checks, $"earlier variants: {BodyKit.Tried(tried)}"] };
            return Result(ctx, d, o, ReadSheet(SheetMetalFeature(d)), null, edgeRefs.Select(e => e.Info("edges")).ToList(), label);
        }
        throw new SwException(ErrorCodes.FeatureFailed, $"sheetmetal_hem: SOLIDWORKS made no {plan.Name} hem. Tried: {BodyKit.Tried(tried)}.",
            plan.Name == "teardrop"
                ? "A teardrop needs room to curl: on a 2 mm sheet 200° with r 1 mm worked (position outside), 270° did not; try a smaller angle or radius."
                : "Hems go on straight edges of the sheet's face (not across its thickness), away from other flanges.");
    }

    /// <summary>
    /// Tier B of a hem: the type reads back, and the added volume is about the hem's material: per edge length the
    /// thickness times the flat length plus the 180° bend (open: gap/2 + t/2 mean radius), within a bend allowance.
    /// </summary>
    private static List<string> HemIntent(IFeature main, Snapshot before, Snapshot after, List<EdgePlan> plans, HemPlan plan, double t, List<string> checks)
    {
        var problems = new List<string>();
        if ((object?)main.GetDefinition() is IHemFeatureData hd)
        {
            int got = hd.Type;
            if (got != plan.Type) problems.Add($"the hem reads type {got}, not {plan.Name} ({plan.Type})");
            else checks.Add($"hem type {plan.Name} read back");
        }
        double dv = after.Volume - before.Volume, edges = plans.Sum(p => p.Length);
        if (plan.Name is "closed" or "open")
        {
            double arc = Math.PI * (plan.Gap / 2 + t / 2);
            double lo = edges * t * plan.Length * 0.5, hi = edges * t * (plan.Length + arc + 2 * t);
            if (dv < lo || dv > hi)
                problems.Add($"ΔV {Units.Mm3(dv)} mm³ is not about the hem's material ({Units.Mm3(lo)}..{Units.Mm3(hi)} mm³ for {Units.Mm(edges)} mm of edge)");
            else checks.Add($"ΔV {Units.Mm3(dv)} mm³ ≈ edge {Units.Mm(edges)} x thickness {Units.Mm(t)} x (length {Units.Mm(plan.Length)} + bend) mm");
        }
        else checks.Add($"ΔV {Units.Mm3(dv)} mm³ added by the {plan.Name} hem");
        return problems;
    }
}
