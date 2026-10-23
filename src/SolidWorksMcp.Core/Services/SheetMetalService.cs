using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs

/// <summary>The sheet-metal parameters SOLIDWORKS reports for a part (read back from its SheetMetal feature).</summary>
public sealed class SheetMetalParams
{
    public double ThicknessMm { get; init; }
    public double BendRadiusMm { get; init; }

    /// <summary>The K-factor, when the bend allowance is a K-factor.</summary>
    public double? KFactor { get; init; }

    /// <summary>k_factor | bend_table | bend_allowance | bend_deduction | bend_calculation.</summary>
    public string? BendAllowance { get; init; }
}

/// <summary>Result of the sheet-metal feature tools.</summary>
public sealed class SheetMetalResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
    public required Verified Verified { get; init; }
    public required ModelState Model { get; init; }
    public SheetMetalParams? Sheet { get; init; }

    /// <summary>The API variant that produced the result (unverified recipes try several).</summary>
    public string? Method { get; init; }

    /// <summary>True for tools whose API recipe is not verified live yet.</summary>
    public bool? Experimental { get; init; }

    public string? ConsumedSketch { get; init; }
    public IReadOnlyList<FaceInfo>? NewFaces { get; init; }
    public int? NewFaceCount { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>sheetmetal_flatten result: the state read back and the size of the part.</summary>
public sealed class FlattenResult : ToolResult
{
    public bool Flat { get; init; }
    public bool Previous { get; init; }
    public bool Changed { get; init; }
    public required IReadOnlyList<string> FlatPatterns { get; init; }

    /// <summary>Flat only: the flat blank's size, the two largest extents of the part's box (mm).</summary>
    public double[]? FlatSizeMm { get; init; }

    /// <summary>Flat only: the smallest box extent (mm), the sheet thickness for a flat pattern along the model axes.</summary>
    public double? ThicknessMm { get; init; }

    public BoxMm? Box { get; init; }
    public double VolumeMm3 { get; init; }
    public SheetMetalParams? Sheet { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>sheetmetal_export_flat result: the file written and what it was checked for.</summary>
public sealed class FlatExportResult : ToolResult
{
    public required string Path { get; init; }

    /// <summary>dxf | dwg.</summary>
    public required string Format { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>What the export was asked to contain (flat-pattern geometry, bend lines).</summary>
    public required IReadOnlyList<string> Content { get; init; }

    public required ExportDetails Details { get; init; }

    /// <summary>DXF: entity counts, layers and the geometry's extents (file units).</summary>
    public DxfSummary? Dxf { get; init; }

    /// <summary>The flat blank's size in the model, mm (when the part is flat, or has no bends).</summary>
    public double[]? FlatSizeMm { get; init; }

    public bool? Replaced { get; init; }
    public string? BackupPath { get; init; }
}

/// <summary>sheetmetal_base_flange options.</summary>
public sealed record BaseFlangeRequest
{
    public string? Doc { get; init; }
    public string? Sketch { get; init; }
    public double ThicknessMm { get; init; }
    public double BendRadiusMm { get; init; }
    public double KFactor { get; init; } = 0.5;
    /// <summary>Thicken along the sketch normal instead of against it.</summary>
    public bool Reverse { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>sheetmetal_edge_flange options.</summary>
public sealed record EdgeFlangeRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    public double LengthMm { get; init; }
    public double AngleDeg { get; init; } = 90;
    /// <summary>material_inside | material_outside | bend_outside.</summary>
    public string? Position { get; init; } = "material_inside";
    /// <summary>Null: the sheet's default bend radius.</summary>
    public double? BendRadiusMm { get; init; }
    public bool KeepOnFailure { get; init; }
}

// ====================================================================== service

/// <summary>
/// Sheet metal (DESIGN §4.8): base flange, edge flange, flatten and flat-pattern DXF/DWG export. VERIFIED recipes:
/// InsertSheetMetalBaseFlange2 on a closed, selected sketch (Sheet-Metal + Base-Flange + Flat-Pattern, V = area x
/// thickness), the FlatPattern feature's suppression for flatten/fold, and IPartDoc.ExportToDWG2 for the flat DXF. The
/// edge flange is unresolved: it tries profile sketches from IModelDoc2.InsertSketchForEdgeFlange first, then other
/// variants, and is verified by volume.
/// </summary>
public sealed partial class SheetMetalService(SwSession session)
{
    private static readonly FeatureSpec BaseFlangeSpec = new("sheetmetal_base_flange", ["SMBaseFlange"], VolumeRule.Increase);
    private static readonly FeatureSpec EdgeFlangeSpec = new("sheetmetal_edge_flange", ["EdgeFlange", "SMEdgeFlange"], VolumeRule.Increase);
    private const double KTol = 1e-6;

    private TimeSpan Long => TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120));

    // ================================================================== sheetmetal_base_flange

    public Task<SheetMetalResult> BaseFlangeAsync(BaseFlangeRequest q, CancellationToken ct = default) =>
        session.RunAsync("sheetmetal_base_flange", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            double t = Units.PositiveM(q.ThicknessMm, "thicknessMm");
            if (!double.IsFinite(q.BendRadiusMm) || q.BendRadiusMm < 0)
                throw SwException.InvalidArgument($"bendRadiusMm must be 0 or positive (got {q.BendRadiusMm}).");
            double r = Units.M(q.BendRadiusMm);
            if (!double.IsFinite(q.KFactor) || q.KFactor <= 0 || q.KFactor > 1)
                throw SwException.InvalidArgument($"kFactor must be greater than 0 and at most 1 (got {q.KFactor}).", "0.5 puts the neutral axis mid-thickness.");
            // VERIFIED: the sketch must be closed (not in edit mode) and selected.
            var (sketchFeat, sketch, _) = BodyKit.ProfileSketch(ctx, d, q.Sketch, "make a base flange");
            var contours = SketchService.Contours(sketch);
            if (contours.ClosedContours == 0 || contours.OpenContours > 0)
                throw SwException.InvalidArgument($"Sketch '{sketchFeat.Name}' must hold closed profiles only ({contours.Status}).",
                    "A base flange here is a flat plate from a closed profile (rectangle, circle, closed polyline).");
            var (origin, normal) = BodyKit.SketchPlane(sketch);
            int side = q.Reverse ? 1 : -1;

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // VERIFIED: ThickenDir false thickens toward -normal, with no custom bend allowance (the part's defaults). ThickenDir
            // true did NOT change the side (live: both values thickened toward -normal), nor does
            // IBaseFlangeFeatureData.ReverseThickness; ReverseDirection does (VERIFIED: y -2..0 -> 0..2 mm), so 'reverse' flips
            // it through ModifyDefinition after the call. The plate's side is checked and the call redone once flipped. When the K-factor read back differs from the request, the call is redone once with a
            // K-factor custom bend allowance (unverified).
            bool thickenDir = false, flipped = false, custom = false;
            while (true)
            {
                var state = new FlangeState();
                CustomBendAllowance? cba = null;
                if (custom)
                {
                    cba = d.FeatureManager.CreateCustomBendAllowance();
                    cba.Type = (int)swBendAllowanceTypes_e.swBendAllowanceKFactor;
                    cba.KFactor = q.KFactor;
                }
                d.ClearSelection2(true);
                if (!sketchFeat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketchFeat.Name}'.");
                object? api = d.FeatureManager.InsertSheetMetalBaseFlange2(t, thickenDir, r, 0.02, 0.01, false, 0, 0, 1, cba, false, 0,
                    0.0001, 0.0001, 0.5, true, false, true, true);
                d.ClearSelection2(true);
                if (q.Reverse && api is not null) ReverseThickness(d, BodyKit.NewFeatures(d, before));
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, BaseFlangeSpec, (main, after, checks) =>
                        {
                            var problems = BaseFlangeIntent(d, before, after, main, origin, normal, t, side, state, checks);
                            if (problems.Count == 0) problems.AddRange(SheetIntent(ctx, d, before, q, custom, state, checks));
                            return problems;
                        },
                        q.KeepOnFailure, api, "The sketch must be a closed profile; a part that is sheet metal already may refuse a second base flange.");
                    var extra = new List<string>();
                    if (flipped) extra.Add("the thicken-direction flag was inverted (SOLIDWORKS thickened the other way)");
                    if (custom) extra.Add($"made with a custom K-factor bend allowance ({q.KFactor}): the part's default differed");
                    if (extra.Count > 0) o = o with { Checks = [.. o.Checks, .. extra] };
                    ctx.DocEntry.LastSketch = null; // consumed
                    return Result(ctx, d, o, state.Sheet, sketchFeat.Name, null,
                        method: custom ? "InsertSheetMetalBaseFlange2 with a custom bend allowance" : null);
                }
                catch (SwException) when (!q.KeepOnFailure && BodyKit.NothingNew(d, before) &&
                                          ((state.Reversed && !flipped) || (state.KMismatch && !custom)))
                {
                    if (state.Reversed && !flipped)
                    {
                        thickenDir = !thickenDir;
                        flipped = true;
                    }
                    else
                    {
                        custom = true;
                    }
                }
            }
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>Flips the side the new base flange thickens to (IBaseFlangeFeatureData.ReverseDirection through ModifyDefinition).</summary>
    private static void ReverseThickness(IModelDoc2 d, IEnumerable<IFeature> created)
    {
        foreach (var f in created)
        {
            if (f.Definition() is not IBaseFlangeFeatureData data) continue;
            bool access = data.AccessSelections(d, null);
            data.ReverseDirection = !data.ReverseDirection;
            if (!f.ModifyDefinition(data, d, null) && access) data.ReleaseSelectionAccess();
            return;
        }
        // No base-flange definition found: the side check reports the result.
    }

    private sealed class FlangeState
    {
        public bool Reversed { get; set; }
        public bool KMismatch { get; set; }
        public SheetMetalParams? Sheet { get; set; }
    }

    /// <summary>
    /// Tier A (the other two features) and Tier B: SheetMetal and FlatPattern features were created with the base flange;
    /// the plate has faces at 0 and at the thickness on the requested side of the sketch plane, and ΔV = profile area x t.
    /// </summary>
    private static List<string> BaseFlangeIntent(IModelDoc2 d, Snapshot before, Snapshot after, IFeature main, Vec3 origin, Vec3 normal, double t,
        int side, FlangeState state, List<string> checks)
    {
        var problems = new List<string>();
        var types = BodyKit.NewFeatures(d, before).Select(f => f.GetTypeName2()).ToHashSet(StringComparer.Ordinal);
        foreach (var want in new[] { "SheetMetal", "FlatPattern" })
            if (!types.Contains(want)) problems.Add($"no {want} feature was created (new: {string.Join(", ", types)})");
        if (problems.Count == 0) checks.Add("SheetMetal, SMBaseFlange and FlatPattern features created");
        var planes = main.BdFaces()
            .Select(f => (G: Topology.Describe(f), P: Topology.InteriorPoint(f), A: f.GetArea()))
            .Where(x => x.G.Surface == "plane" && x.G.Normal is Vec3 n && n.IsParallelTo(normal, 1e-9))
            .Select(x => (Offset: (x.P - origin).Dot(normal), x.A))
            .ToList();
        bool At(double o) => planes.Any(p => Math.Abs(p.Offset - o) <= BodyKit.PosTol);
        if (At(0) && At(side * t))
        {
            double a = planes.Where(p => Math.Abs(p.Offset) <= BodyKit.PosTol).Sum(p => p.A);
            double dv = after.Volume - before.Volume;
            if (Math.Abs(dv - a * t) > 1e-12 + 1e-6 * a * t)
                problems.Add($"ΔV {Units.Mm3(dv)} mm³ is not the profile area x thickness ({Units.Mm2(a)} mm² x {Units.Mm(t)} mm = {Units.Mm3(a * t)} mm³)");
            else
                checks.Add($"plate {Units.Mm(t)} mm thick on the {(side > 0 ? "+" : "-")} side of the sketch normal [{BodyKit.Dir(normal)}]; " +
                           $"ΔV = {Units.Mm2(a)} mm² x {Units.Mm(t)} mm");
        }
        else if (At(0) && At(-side * t))
        {
            state.Reversed = true;
            problems.Add($"the plate was thickened to the {(side > 0 ? "-" : "+")} side of the sketch plane, not the requested one");
        }
        else
        {
            problems.Add($"no plate faces at 0 and {Units.Mm(side * t)} mm from the sketch plane (planar faces at " +
                         $"{string.Join(", ", planes.Select(p => Units.Mm(p.Offset)).Distinct().Take(6))} mm)");
        }
        return problems;
    }

    /// <summary>
    /// Tier B: the new SheetMetal feature reads back the requested thickness, bend radius and K-factor (a K-factor
    /// mismatch on the default call is retried with a custom bend allowance). Unreadable parameters are a warning: the
    /// thickness is proven by the volume anyway.
    /// </summary>
    private static List<string> SheetIntent(SwContext ctx, IModelDoc2 d, Snapshot before, BaseFlangeRequest q, bool custom, FlangeState state,
        List<string> checks)
    {
        var problems = new List<string>();
        var sm = BodyKit.NewFeatures(d, before).FirstOrDefault(f => f.GetTypeName2() == "SheetMetal") ?? SheetMetalFeature(d);
        var sheet = ReadSheet(sm);
        state.Sheet = sheet;
        if (sheet is null)
        {
            ctx.Warn("The SheetMetal feature's parameters could not be read back; thickness checked by volume only, bend radius and K-factor not checked.");
            return problems;
        }
        if (Math.Abs(sheet.ThicknessMm - q.ThicknessMm) > 1e-6) problems.Add($"the SheetMetal feature reads thickness {sheet.ThicknessMm} mm, not {q.ThicknessMm} mm");
        if (Math.Abs(sheet.BendRadiusMm - q.BendRadiusMm) > 1e-6) problems.Add($"the SheetMetal feature reads bend radius {sheet.BendRadiusMm} mm, not {q.BendRadiusMm} mm");
        bool kOk = sheet.BendAllowance == "k_factor" && sheet.KFactor is double k && Math.Abs(k - q.KFactor) <= KTol;
        if (!kOk)
        {
            if (!custom) state.KMismatch = true;
            problems.Add($"the bend allowance reads {sheet.BendAllowance}{(sheet.KFactor is double kk ? $" {kk}" : "")}, not the K-factor {q.KFactor}");
        }
        if (problems.Count == 0)
            checks.Add($"SheetMetal feature reads {sheet.ThicknessMm} mm thick, bend radius {sheet.BendRadiusMm} mm, K-factor {sheet.KFactor}");
        return problems;
    }

    // ================================================================== sheetmetal_edge_flange

    public Task<SheetMetalResult> EdgeFlangeAsync(EdgeFlangeRequest q, CancellationToken ct = default) =>
        session.RunAsync("sheetmetal_edge_flange", ctx =>
        {
            double len = Units.PositiveM(q.LengthMm, "lengthMm");
            if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg >= 180)
                throw SwException.InvalidArgument($"angleDeg must be between 0 and 180 (exclusive; got {q.AngleDeg}).");
            double angle = Units.Rad(q.AngleDeg);
            int position = FlangePosition(q.Position);
            if (q.BendRadiusMm is double br && (!double.IsFinite(br) || br < 0))
                throw SwException.InvalidArgument($"bendRadiusMm must be 0 or positive (got {br}).", "Leave it out for the sheet's default radius.");
            if (q.Edges is null || q.Edges.Count == 0)
                throw SwException.InvalidArgument("Pass 'edges': straight edges of the sheet's face, e.g. [{type:'edge', at:[25,0,0]}].");
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var smFeature = SheetMetalFeature(d)
                            ?? throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a sheet-metal part (no SheetMetal feature).", "Start with sheetmetal_base_flange.");
            var sheet = ReadSheet(smFeature) ?? throw new SwException(ErrorCodes.Internal, $"Could not read the sheet-metal parameters of '{smFeature.Name}'.");
            double t = Units.M(sheet.ThicknessMm);
            double r = q.BendRadiusMm is double rr ? Units.M(rr) : Units.M(sheet.BendRadiusMm);
            if (FlatPatterns(d).Any(f => !f.IsSuppressed()))
                throw new SwException(ErrorCodes.InvalidArgument, $"'{ctx.Title(d)}' is flattened.", "Fold it first with sheetmetal_flatten {flat:false}.");
            var edgeRefs = EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge);
            var plans = edgeRefs.Select((e, i) => PlanEdge(e, t, $"edges[{i}]")).ToList();

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var fm = d.FeatureManager;
            // The old call (options 129, π/2, r, 1, 0, 1, 0.5, 0, 0, 1, null) returned null without profile sketches.
            int options = (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRelief |
                          (q.BendRadiusMm is null ? (int)swInsertEdgeFlangeOptions_e.swInsertEdgeFlangeUseDefaultRadius : 0);
            int relief = (int)swSheetMetalReliefTypes_e.swSheetMetalReliefRectangular;
            int sharp = (int)swFlangeDimTypes_e.swFlangeDimTypeOuterVirtualSharp;
            object Edges() => ComArgs.DispatchArray(plans.Select(p => (object)p.Edge));
            object? Made(object? f) => f ?? DropSketches(ctx, d, before);
            var attempts = new List<(string Label, Func<object?> Run)>
            {
                ("InsertSketchForEdgeFlange profiles + InsertSheetMetalEdgeFlange2", () =>
                {
                    var sketches = FlangeSketches(d, plans, len, angle);
                    return Made(fm.InsertSheetMetalEdgeFlange2(Edges(), ComArgs.DispatchArray(sketches), options, angle, r, position, 0, relief, 0.5, 0, 0, sharp, null));
                }),
            };
            if (plans.Count == 1)
                attempts.Add(("InsertSketchForEdgeFlange profile + InsertSheetMetalEdgeFlange", () =>
                {
                    var sketch = FlangeSketches(d, plans, len, angle)[0];
                    return Made(fm.InsertSheetMetalEdgeFlange((Edge)plans[0].Edge, (Feature)sketch, options, angle, r, position, 0, relief, 0.5, 0, 0, sharp, null));
                }));
            if (Math.Abs(q.AngleDeg - 90) < 1e-9)
                attempts.Add(("profile sketches on the edges' side faces + InsertSheetMetalEdgeFlange2", () =>
                {
                    var sketches = SideFaceSketches(d, plans, len);
                    return Made(fm.InsertSheetMetalEdgeFlange2(Edges(), ComArgs.DispatchArray(sketches), options, angle, r, position, 0, relief, 0.5, 0, 0, sharp, null));
                }));
            attempts.Add(("InsertSheetMetalEdgeFlange2 without profile sketches, then the length dimension set", () =>
            {
                var f = fm.InsertSheetMetalEdgeFlange2(Edges(), ComArgs.Empty, options, angle, r, position, 0, relief, 0.5, 0, 0, sharp, null);
                if (f is not null) SetFlangeLength(ctx, d, before, len);
                return f;
            }));
            var tried = new List<string>();
            foreach (var (label, run) in attempts)
            {
                d.ClearSelection2(true);
                object? api;
                try
                {
                    api = run();
                }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is COMException or InvalidCastException or ArgumentException or SwException)
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
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, EdgeFlangeSpec,
                        (_, after, checks) => EdgeFlangeIntent(before, after, plans, len, angle, t, r, checks), q.KeepOnFailure, api,
                        "Pick straight edges along the sheet's top or bottom face (not the thickness edges).", guessedType: true);
                    if (tried.Count > 0) o = o with { Checks = [.. o.Checks, $"earlier variants: {BodyKit.Tried(tried)}"] };
                    return Result(ctx, d, o, ReadSheet(SheetMetalFeature(d)), null, edgeRefs.Select(e => e.Info("edges")).ToList(), label, experimental: true);
                }
                catch (SwException ex) when (!q.KeepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 200)}");
                }
            }
            throw new SwException(ErrorCodes.FeatureFailed,
                $"sheetmetal_edge_flange (experimental): no API variant made a verified flange. Tried: {BodyKit.Tried(tried)}.",
                "The edge must be a straight edge of the sheet's face; this tool's API recipe is not verified yet (vba_run is the fallback).");
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    /// <summary>
    /// A straight edge between the sheet's face and a side face as thick as the sheet. <see cref="Up"/> is the sheet face's
    /// outward normal (a 90° flange goes that way), <see cref="Out"/> the side face's outward normal.
    /// </summary>
    private sealed record EdgePlan(Resolved Ref, IEdge Edge, Vec3 S, Vec3 E, Vec3 U, double Length, IFace2 Side, Vec3 Up, Vec3 Out);

    private static EdgePlan PlanEdge(Resolved r, double t, string arg)
    {
        var e = (IEdge)r.Obj;
        var g = Topology.Describe(e);
        if (g.Curve != "line" || g.Direction is not Vec3 u)
            throw SwException.InvalidArgument($"{arg} ({r.Label}) is a {g.Curve} edge; edge flanges go on straight edges.");
        var faces = e.AdjacentFaces();
        if (faces.Count != 2) throw SwException.InvalidArgument($"{arg} ({r.Label}) does not have two faces.");
        var fg = faces.Select(Topology.Describe).ToList();
        if (fg.Any(x => x.Surface != "plane" || x.Normal is null))
            throw SwException.InvalidArgument($"{arg} ({r.Label}) is not between two planar faces.", "Pick an edge of the flat sheet.");
        // The side face is the one whose width across the edge, measured along the other face's normal, is the thickness.
        double W(int i) => BodyKit.Span([faces[i]], g.Start, fg[1 - i].Normal!.Value) is var (lo, hi) ? hi - lo : 0;
        int side = Math.Abs(W(0) - t) <= BodyKit.PosTol ? 0 : Math.Abs(W(1) - t) <= BodyKit.PosTol ? 1 : -1;
        if (side < 0)
            throw SwException.InvalidArgument($"{arg} ({r.Label}) is not an edge of the sheet's face next to its {Units.Mm(t)} mm side face.",
                "Pick a long edge on the top or bottom face of the sheet, not an edge across the thickness.");
        return new EdgePlan(r, e, g.Start, g.End, u, g.Length, faces[side], fg[1 - side].Normal!.Value, fg[side].Normal!.Value);
    }

    /// <summary>
    /// One profile sketch per edge from IModelDoc2.InsertSketchForEdgeFlange (the flange's plane through the edge at the
    /// bend angle), holding a rectangle on the edge that reaches lengthMm into the flange (best guess of what
    /// InsertSheetMetalEdgeFlange2's SketchFeats expects; whatever SOLIDWORKS drew in it is replaced).
    /// </summary>
    private static List<IFeature> FlangeSketches(IModelDoc2 d, List<EdgePlan> plans, double len, double angle)
    {
        var list = new List<IFeature>();
        var sm = d.SketchManager;
        foreach (var p in plans)
        {
            d.ClearSelection2(true);
            var feat = d.BdSketchForEdgeFlange(p.Edge, angle, false)
                       ?? throw new SwException(ErrorCodes.FeatureFailed, $"InsertSketchForEdgeFlange made no sketch for {p.Ref.Label}.");
            if (sm.ActiveSketch is not ISketch active || ((IFeature)active).Name != feat.Name)
            {
                if (sm.ActiveSketch is not null) throw new SwException(ErrorCodes.FeatureFailed, "InsertSketchForEdgeFlange left another sketch open.");
                if (!feat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{feat.Name}'.");
                d.EditSketch();
                if (sm.ActiveSketch is not ISketch reopened || ((IFeature)reopened).Name != feat.Name)
                    throw new SwException(ErrorCodes.FeatureFailed, $"Could not edit the flange sketch '{feat.Name}'.");
            }
            try
            {
                DrawProfile(d, sm.ActiveSketch!, p, len, $"the flange sketch '{feat.Name}'");
            }
            finally
            {
                BodyKit.CloseOpenSketch(d);
            }
            list.Add(feat);
        }
        d.ClearSelection2(true);
        return list;
    }

    /// <summary>
    /// Draws the flange profile in the open <paramref name="sketch"/>: a rectangle from the edge, lengthMm along the
    /// sketch-plane direction perpendicular to the edge that points away from the sheet face (+Up, else +Out). Existing
    /// entities are deleted first. The edge must lie in the sketch plane.
    /// </summary>
    private static void DrawProfile(IModelDoc2 d, ISketch sketch, EdgePlan p, double len, string what)
    {
        var sm = d.SketchManager;
        var s2m = SketchService.SketchToModel(sketch);
        var m2s = s2m.Inverse();
        var ns = s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized();
        var o = s2m.ApplyPoint(Vec3.Zero);
        if (Math.Abs(p.U.Dot(ns)) > 1e-6 || Math.Abs((p.S - o).Dot(ns)) > BodyKit.PosTol)
            throw new SwException(ErrorCodes.FeatureFailed, $"The plane of {what} does not contain {p.Ref.Label}.");
        var w = ns.Cross(p.U).Normalized();
        double up = w.Dot(p.Up);
        if (up < -1e-6 || (Math.Abs(up) <= 1e-6 && w.Dot(p.Out) < 0)) w = -w;
        var old = sketch.Segments().Where(s => !s.ConstructionGeometry).ToList();
        if (old.Count > 0)
        {
            d.ClearSelection2(true);
            foreach (var s in old) s.Select4(true, null);
            d.EditDelete();
            d.ClearSelection2(true);
            if (sketch.Segments().Any(s => !s.ConstructionGeometry))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not clear the {old.Count} entities SOLIDWORKS drew in {what}.");
        }
        bool addToDb = sm.AddToDB;
        try
        {
            sm.AddToDB = true; // exact coordinates, no inferred relations
            var c = new[] { p.S, p.E, p.E + w * len, p.S + w * len }.Select(m2s.ApplyPoint).ToArray();
            for (int i = 0; i < 4; i++)
            {
                var a = c[i];
                var b = c[(i + 1) % 4];
                if (sm.CreateLine(a.X, a.Y, 0, b.X, b.Y, 0) is null)
                    throw new SwException(ErrorCodes.FeatureFailed, $"Could not draw the flange profile in {what}.");
            }
        }
        finally
        {
            sm.AddToDB = addToDb;
        }
    }

    /// <summary>One plain profile sketch per edge on its side face (the plane of a 90° flange), drawn like <see cref="DrawProfile"/>.</summary>
    private static List<IFeature> SideFaceSketches(IModelDoc2 d, List<EdgePlan> plans, double len)
    {
        var list = new List<IFeature>();
        var sm = d.SketchManager;
        foreach (var p in plans)
        {
            d.ClearSelection2(true);
            var side = new Resolved { Obj = p.Side, Kind = EntityKind.Face, Label = $"the side face of {p.Ref.Label}" };
            if (!side.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {side.Label}.");
            sm.InsertSketch(true);
            var sketch = sm.ActiveSketch ?? throw new SwException(ErrorCodes.FeatureFailed, $"No sketch opened on {side.Label}.");
            var feat = (IFeature)sketch;
            try
            {
                DrawProfile(d, sketch, p, len, $"the sketch on {side.Label}");
            }
            finally
            {
                BodyKit.CloseOpenSketch(d);
            }
            list.Add(EntityResolver.FeatureByName(d, feat.Name) ?? feat);
        }
        d.ClearSelection2(true);
        return list;
    }

    /// <summary>When the flange call returned nothing: deletes the profile sketches made for it (only sketches), so the next variant starts clean.</summary>
    private static object? DropSketches(SwContext ctx, IModelDoc2 d, Snapshot before)
    {
        var made = BodyKit.NewFeatures(d, before);
        if (made.Count > 0 && made.All(f => f.GetTypeName2() is "ProfileFeature" or "3DProfileFeature")) FeatureVerifier.Rollback(ctx, d, before);
        return null;
    }

    /// <summary>After a flange made without a profile: sets its one linear dimension (the flange length) to <paramref name="len"/> (best guess).</summary>
    private static void SetFlangeLength(SwContext ctx, IModelDoc2 d, Snapshot before, double len)
    {
        var dims = BodyKit.NewFeatures(d, before).SelectMany(f => f.BdDimensions())
            .Where(x => x.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleLinear).ToList();
        if (dims.Count != 1)
        {
            ctx.Warn($"The new flange has {dims.Count} linear dimensions, so its length could not be set; it is checked as made.");
            return;
        }
        if (Math.Abs(dims[0].SystemValue - len) <= 1e-9) return;
        dims[0].SetSystemValue3(len, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, ComArgs.Empty);
        d.EditRebuild3();
    }

    /// <summary>
    /// Tier B (approximate; the bend region depends on the position and the radius): ΔV ≈ length x edge length x
    /// thickness within (r + 1.5 t) x edge length x t, and the model grew about length x sin(angle) along the sheet normal.
    /// </summary>
    private static List<string> EdgeFlangeIntent(Snapshot before, Snapshot after, List<EdgePlan> plans, double len, double angle, double t, double r,
        List<string> checks)
    {
        var problems = new List<string>();
        double dv = after.Volume - before.Volume;
        double edges = plans.Sum(p => p.Length);
        double nominal = edges * t * len, slack = edges * t * (r + 1.5 * t);
        if (Math.Abs(dv - nominal) > slack)
            problems.Add($"ΔV {Units.Mm3(dv)} mm³ is not about length x edge x thickness = {Units.Mm3(nominal)} mm³ (± {Units.Mm3(slack)} mm³ for the bend)");
        else
            checks.Add($"ΔV {Units.Mm3(dv)} mm³ ≈ {Units.Mm(len)} x {Units.Mm(edges)} x {Units.Mm(t)} mm = {Units.Mm3(nominal)} mm³ (within the bend allowance)");
        if (before.Box is { Length: 6 } b0 && after.Box is { Length: 6 } b1 && plans.Count > 0)
        {
            var up = plans[0].Up;
            double grow = Reach(b1, up) - Reach(b0, up);
            double want = len * Math.Sin(angle);
            if (grow < want - r - 2 * t - BodyKit.PosTol || grow > len + t + BodyKit.PosTol)
                problems.Add($"the model grew {Units.Mm(grow)} mm along the sheet normal [{BodyKit.Dir(up)}], expected about {Units.Mm(want)} mm");
            else
                checks.Add($"the model grew {Units.Mm(grow)} mm along the flange side [{BodyKit.Dir(up)}]");
        }
        return problems;
    }

    /// <summary>How far the box reaches along <paramref name="dir"/> (the largest corner projection).</summary>
    private static double Reach(double[] b, Vec3 dir)
    {
        double best = double.MinValue;
        foreach (var x in new[] { b[0], b[3] })
            foreach (var y in new[] { b[1], b[4] })
                foreach (var z in new[] { b[2], b[5] })
                    best = Math.Max(best, new Vec3(x, y, z).Dot(dir));
        return best;
    }

    // ================================================================== sheetmetal_flatten

    public Task<FlattenResult> FlattenAsync(string? doc, bool flat, CancellationToken ct = default) =>
        session.RunAsync("sheetmetal_flatten", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var flats = FlatPatterns(d);
            if (flats.Count == 0)
                throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no flat pattern (it is not a sheet-metal part).", "Make it with sheetmetal_base_flange first.");
            var sheet = ReadSheet(SheetMetalFeature(d));
            bool previous = flats.All(f => !f.IsSuppressed());
            var before = Snapshot.Take(d);
            var checks = new List<string>();

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            // VERIFIED: unsuppressing the FlatPattern feature flattens the part, suppressing it folds it again.
            int action = flat ? (int)swFeatureSuppressionAction_e.swUnSuppressFeature : (int)swFeatureSuppressionAction_e.swSuppressFeature;
            var changed = new List<IFeature>();
            foreach (var f in flats.Where(f => f.IsSuppressed() == flat))
                if (f.SetSuppression2(action, (int)swInConfigurationOpts_e.swThisConfiguration, ComArgs.Empty)) changed.Add(f);
            if (changed.Count > 0) RebuildIfNeeded(d);
            var wrong = flats.Where(f => f.IsSuppressed() == flat).ToList();
            var errors = flats.Where(f => !f.IsSuppressed()).Select(f => (f, Code: f.GetErrorCode2(out bool w), Warn: w))
                .Where(x => x.Code != 0 && !x.Warn).ToList();
            if (wrong.Count > 0 || (flat && errors.Count > 0))
            {
                int back = flat ? (int)swFeatureSuppressionAction_e.swSuppressFeature : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;
                foreach (var f in changed)
                {
                    try { f.SetSuppression2(back, (int)swInConfigurationOpts_e.swThisConfiguration, ComArgs.Empty); }
                    catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* reported below */ }
                }
                if (changed.Count > 0) RebuildIfNeeded(d);
                string why = wrong.Count > 0
                    ? $"{string.Join(", ", wrong.Select(f => f.Name))} did not become {(flat ? "unsuppressed (flat)" : "suppressed (folded)")}"
                    : $"the flat pattern has rebuild error {string.Join(", ", errors.Select(x => $"{x.f.Name}: {FeatureVerifier.ErrorName(x.Code)}"))}";
                throw new SwException(flat && errors.Count > 0 ? ErrorCodes.RebuildError : ErrorCodes.VerifyFailed,
                    $"{why}; the part was set back.", "feature_list shows the flat pattern's state and rebuild errors (a bend that can't flatten).");
            }
            var after = Snapshot.Take(d);
            checks.Add($"{string.Join(", ", flats.Select(f => f.Name))} {(flat ? "unsuppressed: flat" : "suppressed: folded")} (read back)");
            double[]? size = null;
            double? thick = null;
            if (flat && after.Box is { Length: 6 } b)
            {
                var ext = new[] { b[3] - b[0], b[4] - b[1], b[5] - b[2] }.OrderByDescending(x => x).ToArray();
                size = [Units.Mm(ext[0]), Units.Mm(ext[1])];
                thick = Units.Mm(ext[2]);
                if (sheet is not null)
                {
                    if (Math.Abs(ext[2] - Units.M(sheet.ThicknessMm)) <= BodyKit.PosTol)
                        checks.Add($"flat: {size[0]} x {size[1]} mm, as thin as the sheet ({sheet.ThicknessMm} mm)");
                    else
                        checks.Add($"the box's smallest extent is {thick} mm, not the sheet thickness {sheet.ThicknessMm} mm (the flat may lie skew to the axes)");
                }
            }
            if (changed.Count > 0 && Math.Abs(after.Volume - before.Volume) > 1e-12 + 1e-4 * before.Volume)
                checks.Add($"volume {Units.Mm3(before.Volume)} → {Units.Mm3(after.Volume)} mm³ (the bend allowance changes the flat length)");
            return new FlattenResult
            {
                Doc = ctx.Title(d),
                Flat = flats.All(f => !f.IsSuppressed()),
                Previous = previous,
                Changed = changed.Count > 0,
                FlatPatterns = flats.Select(f => f.Name).ToList(),
                FlatSizeMm = size,
                ThicknessMm = thick,
                Box = BoxMm.FromMeters(after.Box),
                VolumeMm3 = Units.Mm3(after.Volume),
                Sheet = sheet,
                Checks = checks,
            };
        }, RunOptions.Write(doc), ct);

    private static void RebuildIfNeeded(IModelDoc2 d)
    {
        int status;
        try { status = d.Extension.NeedsRebuild2; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { status = -1; }
        if (status != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt) d.EditRebuild3();
    }

    // ================================================================== sheetmetal_export_flat

    public Task<FlatExportResult> ExportFlatAsync(string? doc, string? path, bool bendLines, bool overwrite, CancellationToken ct = default)
    {
        string p = path?.Trim().Trim('"') ?? "";
        if (p.Length == 0) throw SwException.InvalidArgument("'path' is empty.", "E.g. path:'bracket_flat.dxf' (relative paths go under the output folder).");
        if (System.IO.Path.GetFileName(p) is not { Length: > 0 } name || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            throw SwException.InvalidArgument($"'path' must name a file ('{p}').");
        string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
        if (ext.Length == 0)
        {
            p += ".dxf";
            ext = ".dxf";
        }
        if (ext is not (".dxf" or ".dwg")) throw SwException.InvalidArgument($"'{ext}' is not .dxf or .dwg.", "The flat pattern exports as DXF or DWG.");
        string file = p, format = ext[1..];
        var timeout = TimeSpan.FromSeconds(Math.Max(180, session.Options.DefaultTimeout.TotalSeconds));
        return session.RunAsync("sheetmetal_export_flat", ctx => ExportFlat(ctx, file, format, bendLines, overwrite), RunOptions.Read(doc) with { Timeout = timeout }, ct);
    }

    private static FlatExportResult ExportFlat(SwContext ctx, string file, string format, bool bendLines, bool overwrite)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        if (SheetMetalFeature(d) is null)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a sheet-metal part (no SheetMetal feature).", "sheetmetal_base_flange makes one; model_export writes other formats.");
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
        // VERIFIED recipe: ExportToDWG2 takes the saved part's path.
        string modelPath = DocumentRegistry.SafePath(d);
        if (modelPath.Length == 0)
            throw new SwException(ErrorCodes.Dirty, $"'{ctx.Title(d)}' has never been saved; the flat-pattern export needs the part's file.",
                "Save it first: doc_save {mode:'as', path:'name.sldprt'}, then retry.");
        if (d.GetSaveFlag()) ctx.Warn($"'{ctx.Title(d)}' has unsaved changes; the DXF/DWG was checked against the model as it is now.");
        ctx.CheckUserCommand();
        var sheet = ReadSheet(SheetMetalFeature(d));
        var flatSize = FlatSizeNow(d, sheet);
        string full = ctx.Paths.ForWrite(file, overwrite, out string? backup);
        string? previous = ExportService.ActualFile(full);
        string? aside = previous is null ? null : MoveAside(previous);
        var content = new List<string> { "flat-pattern geometry" };
        if (bendLines) content.Add("bend lines");
        int options = 1 | (bendLines ? 4 : 0); // 1 = flat-pattern geometry, 4 = bend lines (VERIFIED)
        var start = DateTime.UtcNow;
        string? written = null;
        bool keep = false;
        try
        {
            bool ok = false;
            // VERIFIED call shape; the "no alignment / no views" VARIANTs are tried as VT_DISPATCH null, then VT_EMPTY.
            foreach (var none in new[] { ComArgs.Nothing, ComArgs.Empty })
            {
                ok = part.ExportToDWG2(full, modelPath, (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal, true, none, false, false, options, none);
                if (ok || ExportService.ActualFile(full) is not null) break;
            }
            written = WaitForFile(full);
            if (!ok && written is null)
                throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not export the flat pattern to '{full}'.",
                    "Check that the part flattens (sheetmetal_flatten flat:true shows errors) and that the folder is writable.");
            if (written is null) throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS reported success but did not write '{System.IO.Path.GetFileName(full)}'.");
            var fi = new FileInfo(written);
            var problems = new List<string>();
            if (fi.Length == 0) problems.Add("the file is empty");
            if (fi.LastWriteTimeUtc < start.AddSeconds(-2)) problems.Add($"its time stamp ({fi.LastWriteTimeUtc:u}) is older than this call");
            var details = problems.Count > 0 ? new ExportDetails() : ExportFileCheck.Inspect(format, written, new ExportFileCheck.Expect(), problems);
            DxfSummary? dxf = null;
            var checks = new List<string> { $"new file written by this call ({fi.Length} bytes)" };
            if (problems.Count == 0 && format == "dxf" && details.Header == "DXF")
            {
                dxf = DxfReader.ParseFile(written);
                if (dxf.Entities.Count == 0) problems.Add("the DXF has no entities");
                else checks.AddRange(DxfChecks(ctx, dxf, flatSize, bendLines));
            }
            if (problems.Count > 0)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The exported '{System.IO.Path.GetFileName(written)}' failed verification: {string.Join("; ", problems)}. " +
                    $"It was removed{(aside is null ? "" : " and the previous file put back")}.", "Check the flat pattern with sheetmetal_flatten.");
            details.Checks = [.. checks, .. details.Checks ?? []];
            ctx.Paths.RecordWritten(written);
            keep = true;
            return new FlatExportResult
            {
                Doc = ctx.Title(d),
                Path = written,
                Format = format,
                SizeBytes = fi.Length,
                Content = content,
                Details = details,
                Dxf = dxf,
                FlatSizeMm = flatSize,
                Replaced = previous is not null ? true : null,
                BackupPath = backup,
            };
        }
        finally
        {
            if (keep)
            {
                if (aside is not null) TryDelete(aside);
            }
            else
            {
                var mine = written ?? ExportService.ActualFile(full);
                if (mine is not null) TryDelete(mine);
                if (aside is not null && previous is not null && !TryMove(aside, previous))
                    ctx.Warn($"The previous file could not be put back; it is at '{aside}'.");
            }
        }
    }

    /// <summary>Tier B of the DXF: its geometry's extents against the flat blank (when known), and its bend lines (on a BEND layer, or dashed).</summary>
    private static List<string> DxfChecks(SwContext ctx, DxfSummary dxf, double[]? flatSize, bool bendLines)
    {
        var checks = new List<string> { $"{dxf.Entities.Values.Sum()} entities ({string.Join(", ", dxf.Entities.OrderByDescending(e => e.Value).Take(5).Select(e => $"{e.Key} {e.Value}"))})" };
        if (dxf.Size is not { } size)
        {
            checks.Add("no line/arc geometry in ENTITIES to measure (blocks are not measured)");
            return checks;
        }
        double scale = dxf.InsUnits == 1 ? 25.4 : 1; // $INSUNITS 1 = inches
        var got = size.Select(x => x * scale).OrderByDescending(x => x).ToArray();
        if (flatSize is null)
            checks.Add($"geometry {Math.Round(got[0], 4)} x {Math.Round(got[1], 4)}{(scale == 1 ? "" : " mm (from inches)")} (the folded part's flat size isn't known to compare)");
        else
        {
            var want = flatSize.OrderByDescending(x => x).ToArray();
            if (Math.Abs(got[0] - want[0]) <= 0.01 + 1e-4 * want[0] && Math.Abs(got[1] - want[1]) <= 0.01 + 1e-4 * want[1])
                checks.Add($"geometry extents {Math.Round(got[0], 4)} x {Math.Round(got[1], 4)} mm = the flat blank");
            else
                ctx.Warn($"The DXF geometry spans {Math.Round(got[0], 4)} x {Math.Round(got[1], 4)} (file units x {scale}), not the flat blank {want[0]} x {want[1]} mm.");
        }
        if (bendLines)
            checks.Add(dxf.BendLayerEntities > 0 ? $"{dxf.BendLayerEntities} entities on BEND layers (bend lines)"
                : dxf.DashedEntities > 0 ? $"{dxf.DashedEntities} dashed entities (bend lines; without a layer map SOLIDWORKS draws them as HIDDEN lines on layer 0)"
                : "no bend lines found (no BEND layer, no dashed entities)");
        return checks;
    }

    /// <summary>The flat blank's two extents (mm): from the flattened part, or from a folded part without bends (as thin as the sheet).</summary>
    private static double[]? FlatSizeNow(IModelDoc2 d, SheetMetalParams? sheet)
    {
        var flats = FlatPatterns(d);
        bool isFlat = flats.Count > 0 && flats.All(f => !f.IsSuppressed());
        var box = Snapshot.Take(d).Box;
        if (box is not { Length: 6 }) return null;
        var ext = new[] { box[3] - box[0], box[4] - box[1], box[5] - box[2] }.OrderByDescending(x => x).ToArray();
        if (!isFlat && !(sheet is not null && Math.Abs(ext[2] - Units.M(sheet.ThicknessMm)) <= BodyKit.PosTol)) return null;
        return [Units.Mm(ext[0]), Units.Mm(ext[1])];
    }

    private static string? WaitForFile(string full)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        long last = -1;
        while (true)
        {
            string? f = ExportService.ActualFile(full);
            long len = -1;
            if (f is not null)
            {
                try { len = new FileInfo(f).Length; }
                catch (IOException) { /* still being written */ }
            }
            if (f is not null && len > 0 && len == last) return f;
            if (DateTime.UtcNow >= deadline) return f;
            last = len;
            Thread.Sleep(len > 0 ? 50 : 100);
        }
    }

    private static string MoveAside(string existing)
    {
        if ((File.GetAttributes(existing) & FileAttributes.ReadOnly) != 0)
            throw new SwException(ErrorCodes.IoError, $"'{existing}' is read-only, so it isn't replaced.", "Choose another file name.");
        string aside = existing + "." + Guid.NewGuid().ToString("N")[..8] + ".swmcp-old";
        try
        {
            File.Move(existing, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SwException(ErrorCodes.IoError, $"'{existing}' can't be replaced: {ex.Message}", "It may be open in another program; close it or choose another name.");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ================================================================== shared

    private static IFeature? SheetMetalFeature(IModelDoc2 d) => Tree.Walk(d).FirstOrDefault(f => f.GetTypeName2() == "SheetMetal");

    private static List<IFeature> FlatPatterns(IModelDoc2 d) => Tree.Walk(d).Where(f => f.GetTypeName2() == "FlatPattern").ToList();

    /// <summary>The SheetMetal feature's thickness, bend radius and bend allowance (read only, no AccessSelections).</summary>
    private static SheetMetalParams? ReadSheet(IFeature? sheetMetal)
    {
        if (sheetMetal?.BdSheetMetalData() is not { } data) return null;
        int type = data.BendAllowanceType;
        double? k = type == (int)swBendAllowanceTypes_e.swBendAllowanceKFactor ? data.KFactor : null;
        try
        {
            // A K-factor custom bend allowance on the feature (where SOLIDWORKS keeps the bend allowance) wins.
            if (data.GetCustomBendAllowance() is { } cba && cba.Type == (int)swBendAllowanceTypes_e.swBendAllowanceKFactor)
            {
                type = cba.Type;
                k = cba.KFactor;
            }
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* not available: the feature's own values stand */ }
        return new SheetMetalParams
        {
            ThicknessMm = Units.Mm(data.Thickness),
            BendRadiusMm = Units.Mm(data.BendRadius),
            KFactor = k is double v ? Math.Round(v, 6) : null,
            BendAllowance = type switch
            {
                (int)swBendAllowanceTypes_e.swBendAllowanceKFactor => "k_factor",
                (int)swBendAllowanceTypes_e.swBendAllowanceBendTable => "bend_table",
                (int)swBendAllowanceTypes_e.swBendAllowanceDirect => "bend_allowance",
                (int)swBendAllowanceTypes_e.swBendAllowanceDeduction => "bend_deduction",
                (int)swBendAllowanceTypes_e.swBendAllowanceBendCalculationTable => "bend_calculation",
                _ => $"type {type}",
            },
        };
    }

    private static SheetMetalResult Result(SwContext ctx, IModelDoc2 d, FeatureOutcome o, SheetMetalParams? sheet, string? consumedSketch,
        IReadOnlyList<ResolvedInfo>? resolved, string? method = null, bool experimental = false)
    {
        var fr = FeatureVerifier.Result(ctx, d, o, consumedSketch, null, resolved);
        return new SheetMetalResult
        {
            Doc = fr.Doc,
            Feature = fr.Feature,
            AlsoCreated = fr.AlsoCreated,
            Verified = fr.Verified,
            Model = fr.Model,
            Sheet = sheet,
            Method = method,
            Experimental = experimental ? true : null,
            ConsumedSketch = fr.ConsumedSketch,
            NewFaces = fr.NewFaces is { Count: > 0 } ? fr.NewFaces : null,
            NewFaceCount = fr.NewFaceCount is > 0 ? fr.NewFaceCount : null,
            Dimensions = fr.Dimensions is { Count: > 0 } ? fr.Dimensions : null,
            Resolved = fr.Resolved,
        };
    }
}
