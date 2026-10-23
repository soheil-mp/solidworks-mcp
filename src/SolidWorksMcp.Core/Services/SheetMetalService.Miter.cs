using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of sheetmetal_miter_flange: one flange along a chain of edges of a sheet face, mitered at the corners.</summary>
public sealed record MiterFlangeRequest
{
    public string? Doc { get; init; }
    /// <summary>Straight edges of the sheet's top or bottom face that meet end to end (any order).</summary>
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>The profile line's length: from the chain's start, away from the sheet face (at 90° the flange's height).</summary>
    public double LengthMm { get; init; }
    /// <summary>The wall's angle from the sheet: 90 = square, less leans outward, more leans back over the sheet.</summary>
    public double AngleDeg { get; init; } = 90;
    /// <summary>material_inside (default) | material_outside | bend_outside.</summary>
    public string? Position { get; init; }
    public double? BendRadiusMm { get; init; }
    /// <summary>The rip gap where the flange's pieces meet at a corner.</summary>
    public double GapMm { get; init; } = 0.1;
    public bool KeepOnFailure { get; init; }
}

public sealed partial class SheetMetalService
{
    // The bends are Miterbend sub-features (VERIFIED); like a hem's, the faces may belong to them, so the volume rule decides.
    private static readonly FeatureSpec MiterSpec = new("sheetmetal_miter_flange", ["SMMiteredFlange"], VolumeRule.Increase, RequireFaces: false);

    public Task<SheetMetalResult> MiterFlangeAsync(MiterFlangeRequest q, CancellationToken ct = default)
    {
        double len = Units.PositiveM(q.LengthMm, "lengthMm");
        if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg >= 180)
            throw SwException.InvalidArgument($"angleDeg must be between 0 and 180 (exclusive; got {q.AngleDeg}).");
        int position = FlangePosition(q.Position);
        if (q.BendRadiusMm is double br && (!double.IsFinite(br) || br < 0))
            throw SwException.InvalidArgument($"bendRadiusMm must be 0 or positive (got {br}).", "Leave it out for the sheet's default radius.");
        if (!double.IsFinite(q.GapMm) || q.GapMm < 0) throw SwException.InvalidArgument($"gapMm must be 0 or positive (got {q.GapMm}).");
        if (q.Edges is not { Count: > 0 })
            throw SwException.InvalidArgument("Pass 'edges': straight edges of the sheet's face that meet end to end, e.g. [{type:'edge', at:[0,0,15]}].");
        return session.RunAsync("sheetmetal_miter_flange", ctx => Miter(ctx, q, len, Units.Rad(q.AngleDeg), position),
            RunOptions.Write(q.Doc) with { Timeout = Long }, ct);
    }

    /// <summary>The flange position (swFlangePositionTypes_e) of an edge or miter flange.</summary>
    private static int FlangePosition(string? position) => BodyKit.Norm(position) switch
    {
        null or "material_inside" or "inside" => (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialInside,
        "material_outside" or "outside" => (int)swFlangePositionTypes_e.swFlangePositionTypeMaterialOutside,
        "bend_outside" => (int)swFlangePositionTypes_e.swFlangePositionTypeBendOutside,
        _ => throw SwException.InvalidArgument($"Unknown position '{position}'.", "Use material_inside, material_outside or bend_outside."),
    };

    private static SheetMetalResult Miter(SwContext ctx, MiterFlangeRequest q, double len, double angle, int position)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        var smFeature = SheetMetalFeature(d)
                        ?? throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a sheet-metal part (no SheetMetal feature).", "Start with sheetmetal_base_flange.");
        var sheet = ReadSheet(smFeature) ?? throw new SwException(ErrorCodes.Internal, $"Could not read the sheet-metal parameters of '{smFeature.Name}'.");
        double t = Units.M(sheet.ThicknessMm);
        bool defaultRadius = q.BendRadiusMm is null;
        double r = q.BendRadiusMm is double rr ? Units.M(rr) : Units.M(sheet.BendRadiusMm);
        double gap = Units.M(q.GapMm);
        if (FlatPatterns(d).Any(f => !f.IsSuppressed()))
            throw new SwException(ErrorCodes.InvalidArgument, $"'{ctx.Title(d)}' is flattened.", "Fold it first with sheetmetal_flatten {flat:false}.");
        var edgeRefs = EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge);
        var plans = edgeRefs.Select((e, i) => PlanEdge(e, t, $"edges[{i}]")).ToList();
        if (plans.Any(p => p.Up.Dot(plans[0].Up) < 1 - 1e-6))
            throw SwException.InvalidArgument("The edges are not all on the same side of the sheet.", "Pick edges of the top face, or of the bottom face, not both.");
        var chain = EdgeChain.Order(plans.Select(p => (p.S, p.E)).ToList(), 10 * BodyKit.PosTol)
                    ?? throw SwException.InvalidArgument("The edges do not meet end to end in one chain (a gap, a branch, or an edge twice).",
                        "Pass edges that follow each other around the sheet, e.g. two edges meeting at a corner.");
        var ordered = chain.Order.Select(i => plans[i]).ToList();
        var first = ordered[0];
        var along = ((chain.Reversed[0] ? first.S : first.E) - chain.Start).Normalized();
        var wall = (first.Up * Math.Sin(angle) + first.Out * Math.Cos(angle)).Normalized();

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        string sketchName;
        try
        {
            sketchName = MiterProfile(d, before, first, chain.Start, along, wall, len);
            var sketchFeat = EntityResolver.FeatureByName(d, sketchName)
                             ?? throw new SwException(ErrorCodes.Internal, $"The profile sketch '{sketchName}' could not be found.");
            d.ClearSelection2(true);
            // VERIFIED: the profile sketch on mark 0, the edges on mark 1 (in chain order).
            if (!sketchFeat.Select2(false, 0) || !ordered.All(p => p.Ref.Select(d, true, 1)) || d.Sel().GetSelectedObjectCount2(-1) != 1 + ordered.Count)
                throw new SwException(ErrorCodes.FeatureFailed, "The profile sketch and the edges could not be selected.");
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is SwException or COMException)
        {
            BodyKit.CleanUp(ctx, d, before);
            throw;
        }
        object? api;
        try
        {
            api = d.FeatureManager.InsertSheetMetalMiterFlange(defaultRadius, defaultRadius ? 0 : r, gap, true, false, 0.5, 0.0001, 0.0001,
                (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular, false, position, 0, 0, null);
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
        {
            BodyKit.CleanUp(ctx, d, before);
            throw new SwException(ErrorCodes.FeatureFailed, $"InsertSheetMetalMiterFlange failed: {BodyKit.Clip(ex.Message, 200)}");
        }
        finally
        {
            d.ClearSelection2(true);
        }
        var o = BodyKit.Verify(ctx, d, before, MiterSpec,
            (main, after, checks) => MiterIntent(main, before, after, ordered, chain.Corners, len, angle, t, r, gap, position, checks), q.KeepOnFailure, api,
            "A miter flange runs along straight edges of one sheet face that meet end to end; try a smaller bendRadiusMm, a larger gapMm, " +
            "fewer edges, or material_outside.");
        return Result(ctx, d, o, ReadSheet(SheetMetalFeature(d)), sketchName, edgeRefs.Select(e => e.Info("edges")).ToList());
    }

    /// <summary>
    /// The miter flange's profile: a sketch on the plane normal to the chain's first edge at the chain's start (the
    /// sheet's end face there, else a new reference plane through the start vertex), holding one line from the start
    /// along <paramref name="wall"/>, <paramref name="len"/> long. Returns the sketch's name.
    /// </summary>
    private static string MiterProfile(IModelDoc2 d, Snapshot before, EdgePlan first, Vec3 start, Vec3 along, Vec3 wall, double len)
    {
        var vertex = new[] { first.Edge.StartVertex(), first.Edge.EndVertex() }
            .FirstOrDefault(v => v is not null && Vec3.From(v.Point()).DistanceTo(start) <= 10 * BodyKit.PosTol)
            ?? throw new SwException(ErrorCodes.FeatureFailed, $"{first.Ref.Label} has no vertex at the chain's start.");
        var endFace = ComArgs.Objects<IFace2>((object?)vertex.GetAdjacentFaces())
            .FirstOrDefault(f => Topology.Describe(f) is { Surface: "plane", Normal: Vec3 n } && n.IsParallelTo(along, 1e-6));
        d.ClearSelection2(true);
        if (endFace is not null)
        {
            var face = new Resolved { Obj = endFace, Kind = EntityKind.Face, Label = "the sheet's end face at the chain's start" };
            if (!face.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {face.Label} to sketch the profile on.");
        }
        else
        {
            // No end face normal to the edge: a plane normal to the edge through the start vertex.
            var at = new Resolved { Obj = vertex, Kind = EntityKind.Vertex, Label = "the chain's start vertex" };
            if (!first.Ref.Select(d, false, 0) || !at.Select(d, true, 1))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {first.Ref.Label} and its start vertex for the profile plane.");
            var plane = (object?)d.FeatureManager.InsertRefPlane((int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Perpendicular, 0,
                (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0, 0, 0);
            d.ClearSelection2(true);
            // The returned object is the IRefPlane; its feature is the new RefPlane (not the last one: Flat-Pattern stays last).
            var pf = plane is null ? null : BodyKit.NewFeatures(d, before).FirstOrDefault(f => f.GetTypeName2() == "RefPlane");
            if (pf is null || !pf.Select2(false, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"No plane normal to {first.Ref.Label} at the chain's start could be made for the profile.");
        }
        var sm = d.SketchManager;
        sm.InsertSketch(true);
        if (sm.ActiveSketch is not ISketch sketch) throw new SwException(ErrorCodes.FeatureFailed, "No profile sketch opened.");
        string name = ((IFeature)sketch).Name;
        try
        {
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var a = m2s.ApplyPoint(start);
            var b = m2s.ApplyPoint(start + wall * len);
            if (Math.Abs(a.Z) > 10 * BodyKit.PosTol || Math.Abs(b.Z) > 10 * BodyKit.PosTol)
                throw new SwException(ErrorCodes.FeatureFailed, "The profile line is not in the plane of its sketch.");
            bool addToDb = sm.AddToDB;
            try
            {
                sm.AddToDB = true; // exact coordinates, no inferred relations
                if (sm.CreateLine(a.X, a.Y, 0, b.X, b.Y, 0) is null) throw new SwException(ErrorCodes.FeatureFailed, "The profile line could not be drawn.");
            }
            finally
            {
                sm.AddToDB = addToDb;
            }
        }
        finally
        {
            BodyKit.CloseOpenSketch(d);
        }
        d.ClearSelection2(true);
        return name;
    }

    /// <summary>
    /// Tier B of a miter flange (approximate, like an edge flange's): the position and rip gap read back; ΔV ≈ length x
    /// edge length x thickness within (r + 1.5 t) x edge length x t plus, per mitered corner, 2 (r + t + gap) x length x t;
    /// and the model grew about length x sin(angle) along the sheet face's normal.
    /// </summary>
    private static List<string> MiterIntent(IFeature main, Snapshot before, Snapshot after, List<EdgePlan> chain, int corners, double len, double angle,
        double t, double r, double gap, int position, List<string> checks)
    {
        var problems = new List<string>();
        if ((object?)main.GetDefinition() is IMiterFlangeFeatureData md)
        {
            int pos = md.PositionType;
            double g = md.GapDistance;
            if (pos != position) problems.Add($"the flange reads position {pos}, not {position}");
            else if (Math.Abs(g - gap) > 1e-7) problems.Add($"the rip gap reads {Units.Mm(g)} mm, not {Units.Mm(gap)} mm");
            else checks.Add($"position and rip gap ({Units.Mm(gap)} mm) read back");
        }
        // The bends are the flange's sub-features (Miterbend1, ...), next to its profile sketch.
        var bends = new List<string>();
        for (var sub = main.FirstSub(); sub is not null; sub = sub.NextSub())
            if (sub.GetTypeName2() is not ("ProfileFeature" or "3DProfileFeature")) bends.Add(sub.Name);
        if (bends.Count == 0) problems.Add("the flange has no bend sub-feature");
        else checks.Add($"{bends.Count} bend(s): {string.Join(", ", bends)}");
        double dv = after.Volume - before.Volume;
        double edges = chain.Sum(p => p.Length);
        double nominal = edges * t * len, slack = edges * t * (r + 1.5 * t) + corners * 2 * (r + t + gap) * len * t;
        if (Math.Abs(dv - nominal) > slack)
            problems.Add($"ΔV {Units.Mm3(dv)} mm³ is not about length x edge x thickness = {Units.Mm3(nominal)} mm³ (± {Units.Mm3(slack)} mm³ for bends and corners)");
        else
            checks.Add($"ΔV {Units.Mm3(dv)} mm³ ≈ {Units.Mm(len)} x {Units.Mm(edges)} x {Units.Mm(t)} mm = {Units.Mm3(nominal)} mm³ (within bends and {corners} corner(s))");
        if (before.Box is { Length: 6 } b0 && after.Box is { Length: 6 } b1)
        {
            var up = chain[0].Up;
            double grow = Reach(b1, up) - Reach(b0, up);
            double want = len * Math.Sin(angle);
            if (grow < want - r - 2 * t - BodyKit.PosTol || grow > len + t + BodyKit.PosTol)
                problems.Add($"the model grew {Units.Mm(grow)} mm along the sheet normal [{BodyKit.Dir(up)}], expected about {Units.Mm(want)} mm");
            else
                checks.Add($"the model grew {Units.Mm(grow)} mm along the flange side [{BodyKit.Dir(up)}]");
        }
        return problems;
    }
}
