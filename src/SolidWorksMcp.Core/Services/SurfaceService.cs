using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>surface_extrude options.</summary>
public sealed record SurfaceExtrudeRequest
{
    public string? Doc { get; init; }
    public string? Sketch { get; init; }
    public double DepthMm { get; init; }
    /// <summary>normal | reverse | a model axis "+y" (its sign along the sketch normal).</summary>
    public string? Direction { get; init; }
    /// <summary>Mid-plane: depthMm in total, half on each side of the sketch plane.</summary>
    public bool BothSides { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>surface_revolve options.</summary>
public sealed record SurfaceRevolveRequest
{
    public string? Doc { get; init; }
    public string? Sketch { get; init; }
    /// <summary>Default: the sketch's only centerline.</summary>
    public EntityRef? Axis { get; init; }
    public double AngleDeg { get; init; } = 360;
    public bool KeepOnFailure { get; init; }
}

/// <summary>
/// Surface features (DESIGN §4.7): extruded, revolved, planar and offset surfaces, knit and thicken. Tier A by
/// <see cref="FeatureVerifier"/>; Tier B from the new faces (the extrude spans the depth on the requested side, revolved
/// faces are coaxial with the axis, planar faces lie in the profile plane, offset faces sit at the distance) and the
/// bodies (a new surface body; knit: fewer surface bodies or a solid; thicken: area x thickness for a flat sheet).
/// Failures roll back. Verified recipes: the extruded surface and thicken; the others are best guesses.
/// </summary>
public sealed class SurfaceService(SwSession session)
{
    private static readonly FeatureSpec ExtrudeSpec = new("surface_extrude", ["ExtruRefSurface"], VolumeRule.Any);
    private static readonly FeatureSpec RevolveSpec = new("surface_revolve", ["RevolvRefSurf", "RevolvedRefSurface", "RevolveRefSurface"], VolumeRule.Any);
    private static readonly FeatureSpec PlanarSpec = new("surface_planar", ["PlanarSurface", "PlanarRefSurface", "PlanarRefSurf"], VolumeRule.Any);
    private static readonly FeatureSpec OffsetSpec = new("surface_offset", ["OffsetRefSuface", "OffsetRefSurface", "OffsetSurface"], VolumeRule.Any);
    private static readonly FeatureSpec KnitSpec = new("surface_knit", ["SewRefSurface", "KnitRefSurface", "SurfaceKnit"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec ThickenSpec = new("surface_thicken", ["Thicken", "ThickenBoss", "BossThicken"], VolumeRule.Increase);

    private const int RevolveAxisMark = 16; // best guess (PartFeatureService uses the same); the sketch's only centerline needs none

    // ================================================================== surface_extrude

    public Task<BodyResult> ExtrudeAsync(SurfaceExtrudeRequest q, CancellationToken ct = default) =>
        session.RunAsync("surface_extrude", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            double depth = Units.PositiveM(q.DepthMm, "depthMm");
            // The server's open sketch stays open: the recipe was verified on the open sketch.
            var (sketchFeat, sketch, _) = BodyKit.ProfileSketch(ctx, d, q.Sketch, "extrude a surface", keepOpen: true);
            string sketchName = sketchFeat.Name;
            try
            {
                if (SketchService.Contours(sketch).Segments == 0) throw SwException.InvalidArgument($"Sketch '{sketchName}' has no (non-construction) entities.");
                var (origin, normal) = BodyKit.SketchPlane(sketch);
                bool along = AlongNormal(ctx, q.Direction, normal, q.BothSides);
                int t1 = q.BothSides ? (int)swEndConditions_e.swEndCondMidPlane : (int)swEndConditions_e.swEndCondBlind;
                var geomsBefore = BodyKit.AllGeoms(ctx, part, d);

                using var prefs = ctx.SystemPrefs();
                prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
                var before = Snapshot.Take(d);
                // Dir false = along the sketch normal (assumed, as for FeatureExtrusion3). A result on the other side is redone flipped once.
                bool flag = !along, flipped = false;
                while (true)
                {
                    var state = new SideState();
                    bool dirFlag = flag;
                    void Call() => d.FeatureManager.FeatureExtruRefSurface3(true, dirFlag, (int)swStartConditions_e.swStartSketchPlane, 0, t1,
                        (int)swEndConditions_e.swEndCondBlind, depth, depth, false, false, false, false, 0, 0, false, false, false, false,
                        false, false, false, false); // void: the new feature is found by the snapshot difference
                    // VERIFIED on the open sketch (re-opened with EditSketch when it is closed); the selected closed sketch is the fallback.
                    string method = "FeatureExtruRefSurface3 on the open sketch";
                    if (!BodyKit.OnOpenSketch(ctx, d, sketchName, Call) || BodyKit.NothingNew(d, before))
                    {
                        method = "FeatureExtruRefSurface3 with the closed sketch selected";
                        BodyKit.OnSelectedSketch(d, sketchFeat, () =>
                        {
                            Call();
                            return null;
                        });
                    }
                    try
                    {
                        var o = BodyKit.Verify(ctx, d, before, ExtrudeSpec,
                            (main, after, checks) => ExtrudeIntent(main, after, before, origin, normal, depth, q.BothSides ? 0 : along ? 1 : -1, state, checks),
                            q.KeepOnFailure, null, "The sketch needs at least one curve; check the depth and direction.");
                        if (flipped) o = o with { Checks = [.. o.Checks, "the direction flag was inverted (SOLIDWORKS' default ran the other way)"] };
                        ctx.DocEntry.LastSketch = null; // consumed
                        return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), null,
                            method: method, consumedSketch: sketchName, direction: q.BothSides ? null : along ? normal : -normal);
                    }
                    catch (SwException) when (!q.KeepOnFailure && state.Reversed && !flipped && BodyKit.NothingNew(d, before))
                    {
                        flag = !flag;
                        flipped = true;
                    }
                }
            }
            finally
            {
                BodyKit.EndSketch(ctx, d, sketchName);
            }
        }, RunOptions.Write(q.Doc), ct);

    private sealed class SideState
    {
        public bool Reversed { get; set; }
    }

    /// <summary>
    /// Tier B: one more surface body and no new solid, and the new faces span the depth on the requested side of the
    /// sketch plane (<paramref name="side"/> 1 along the normal, -1 against it, 0 mid-plane).
    /// </summary>
    private static List<string> ExtrudeIntent(IFeature main, Snapshot after, Snapshot before, Vec3 origin, Vec3 normal, double depth, int side,
        SideState state, List<string> checks)
    {
        var problems = new List<string>();
        if (after.Bodies <= before.Bodies) problems.Add("no new surface body");
        if (after.SolidBodies != before.SolidBodies) problems.Add("the solid bodies changed");
        var (lo, hi) = BodyKit.Span(main.BdFaces(), origin, normal);
        double wantLo = side switch { 1 => 0, -1 => -depth, _ => -depth / 2 }, wantHi = wantLo + depth;
        if (Math.Abs(lo - wantLo) <= BodyKit.PosTol && Math.Abs(hi - wantHi) <= BodyKit.PosTol)
            checks.Add($"the surface spans {BodyKit.Mm(lo)}..{BodyKit.Mm(hi)} mm along the sketch normal [{BodyKit.Dir(normal)}]");
        else
        {
            if (side != 0 && Math.Abs(lo + wantHi) <= BodyKit.PosTol && Math.Abs(hi + wantLo) <= BodyKit.PosTol) state.Reversed = true;
            problems.Add($"the surface spans {BodyKit.Mm(lo)}..{BodyKit.Mm(hi)} mm along the sketch normal, expected {BodyKit.Mm(wantLo)}..{BodyKit.Mm(wantHi)} mm");
        }
        return problems;
    }

    /// <summary>True when the feature goes along the sketch normal (mid-plane: true).</summary>
    private static bool AlongNormal(SwContext ctx, string? direction, Vec3 normal, bool bothSides)
    {
        string? dir = BodyKit.Norm(direction);
        if (bothSides)
        {
            if (dir is not (null or "normal")) ctx.Warn("'direction' is ignored with bothSides (mid-plane).");
            return true;
        }
        switch (dir)
        {
            case null or "normal":
                return true;
            case "reverse":
                return false;
        }
        var axis = AxisSpec.Parse(direction!, "direction");
        double dot = axis.Dot(normal);
        if (Math.Abs(dot) < 1e-6)
            throw SwException.InvalidArgument($"direction [{BodyKit.Dir(axis)}] lies in the sketch plane; the surface goes along the normal [{BodyKit.Dir(normal)}] or against it.",
                "Use normal, reverse, or a model axis along the sketch normal.");
        return dot > 0;
    }

    // ================================================================== surface_revolve

    public Task<BodyResult> RevolveAsync(SurfaceRevolveRequest q, CancellationToken ct = default) =>
        session.RunAsync("surface_revolve", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg > 360)
                throw SwException.InvalidArgument($"angleDeg must be greater than 0 and at most 360 (got {q.AngleDeg}).");
            double angle = Units.Rad(q.AngleDeg);
            var (sketchFeat, sketch, _) = BodyKit.ProfileSketch(ctx, d, q.Sketch, "revolve a surface");
            var s2m = SketchService.SketchToModel(sketch);
            var segments = sketch.Segments();
            var centerlines = segments.Where(s => s.ConstructionGeometry && s.GetType() == (int)swSketchSegments_e.swSketchLINE).ToList();
            if (segments.All(s => s.ConstructionGeometry)) throw SwException.InvalidArgument($"Sketch '{sketchFeat.Name}' has no profile curve (only construction geometry).");

            Vec3 ap, au;
            Resolved? axisRef = null;
            string axisLabel;
            if (q.Axis is null)
            {
                if (centerlines.Count != 1)
                    throw SwException.InvalidArgument(centerlines.Count == 0
                            ? $"Sketch '{sketchFeat.Name}' has no centerline to revolve around."
                            : $"Sketch '{sketchFeat.Name}' has {centerlines.Count} centerlines.",
                        "Pass axis: a centerline {name:'Line3'}, a reference axis {name:'Axis1'} or a straight edge {type:'edge',at:[x,y,z]}.");
                (ap, au) = SketchLine(centerlines[0], s2m);
                axisLabel = $"centerline {centerlines[0].GetName()} of {sketchFeat.Name}";
            }
            else
            {
                var aref = AxisRef(d, q.Axis, sketchFeat.Name);
                axisRef = EntityResolver.Resolve(ctx, d, aref, "axis", EntityKind.Axis, EntityKind.Edge, EntityKind.SketchSegment);
                (ap, au) = axisRef.Obj switch
                {
                    ISketchSegment seg => SketchLine(seg, SketchService.SketchToModel(EntityResolver.SketchFor(ctx, d, aref.Sketch))),
                    IEdge e when Topology.Describe(e) is { Curve: "line", Direction: Vec3 u } g => (g.Start, u),
                    IFeature f when f.Specific() is IRefAxis ax && ax.RefAxisEnds() is { Length: >= 6 } p && Vec3.From(p, 3).DistanceTo(Vec3.From(p)) > 1e-12 =>
                        (Vec3.From(p), (Vec3.From(p, 3) - Vec3.From(p)).Normalized()),
                    _ => throw SwException.InvalidArgument($"'axis' ({axisRef.Label}) is not straight; use a centerline, a reference axis or a straight edge."),
                };
                axisLabel = axisRef.Label;
            }
            var (origin, sn) = BodyKit.SketchPlane(sketch);
            if (Math.Abs(au.Dot(sn)) > 1e-6 || Math.Abs((ap - origin).Dot(sn)) > BodyKit.PosTol)
                throw SwException.InvalidArgument($"The axis ({axisLabel}) does not lie in the plane of sketch '{sketchFeat.Name}'.");
            bool selectAxis = axisRef is not null && !(axisRef.Obj is ISketchSegment s0 && centerlines.Count == 1 &&
                                                        string.Equals(s0.GetName(), centerlines[0].GetName(), StringComparison.OrdinalIgnoreCase));
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // Unverified: InsertRevolvedRefSurface on the selected sketch (the 360° revolve type as well), FeatureRevolve2 as
            // a surface (IsSolid=false), and InsertRevolvedRefSurface on the open sketch when the axis is its only centerline.
            bool full = q.AngleDeg >= 360 - 1e-9;
            int blind = (int)swEndConditions_e.swEndCondBlind;
            var attempts = new List<(string Label, bool Open, Func<object?> Call)>
            {
                ("InsertRevolvedRefSurface", false, () => d.FeatureManager.InsertRevolvedRefSurface(angle, false, 0, (int)swRevolveType_e.swRevolveTypeOneDirection)),
            };
            if (full)
                attempts.Add(("InsertRevolvedRefSurface (360° type)", false,
                    () => d.FeatureManager.InsertRevolvedRefSurface(angle, false, 0, (int)swRevolveType_e.swRevolveTypeOneDirection360Degrees)));
            attempts.Add(("FeatureRevolve2 (IsSolid=false)", false, () => d.FeatureManager.FeatureRevolve2(true, false, false, false, false, false,
                blind, blind, angle, 0, false, false, 0, 0, 0, 0, 0, false, true, true)));
            if (!selectAxis)
                attempts.Add(("InsertRevolvedRefSurface on the open sketch", true,
                    () => d.FeatureManager.InsertRevolvedRefSurface(angle, false, 0, (int)swRevolveType_e.swRevolveTypeOneDirection)));
            var tried = new List<string>();
            foreach (var (label, open, call) in attempts)
            {
                object? api = null;
                if (open)
                {
                    if (!BodyKit.OnOpenSketch(ctx, d, sketchFeat.Name, () => api = call()))
                    {
                        tried.Add($"{label}: the sketch could not be re-opened");
                        continue;
                    }
                }
                else
                {
                    api = BodyKit.OnSelectedSketch(d, sketchFeat, call, selectAxis ? () =>
                    {
                        if (!axisRef!.Select(d, true, RevolveAxisMark))
                            throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {axisRef.Label} as the axis.");
                    } : null);
                }
                if (BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: nothing created");
                    continue;
                }
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, RevolveSpec,
                        (main, after, checks) => RevolveIntent(main, after, before, ap, au, checks), q.KeepOnFailure, api,
                        "The profile must lie on one side of the axis, with the axis in the sketch plane.", guessedType: true);
                    ctx.DocEntry.LastSketch = null; // consumed
                    return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)),
                        axisRef is null ? null : [axisRef.Info("axis")], method: label, consumedSketch: sketchFeat.Name);
                }
                catch (SwException ex) when (!q.KeepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 200)}");
                }
            }
            throw new SwException(ErrorCodes.FeatureFailed, $"surface_revolve: no API variant made a verified revolved surface. Tried: {BodyKit.Tried(tried)}.",
                "The profile must not cross the axis; the axis is the sketch's only centerline unless 'axis' is given.");
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>Tier B: a new surface body, no solid change, and every new face a surface of revolution about the axis.</summary>
    private static List<string> RevolveIntent(IFeature main, Snapshot after, Snapshot before, Vec3 ap, Vec3 au, List<string> checks)
    {
        var problems = new List<string>();
        if (after.Bodies <= before.Bodies) problems.Add("no new surface body");
        if (after.SolidBodies != before.SolidBodies) problems.Add("the solid bodies changed");
        int coaxial = 0, other = 0;
        var bad = new List<string>();
        foreach (var f in main.BdFaces())
        {
            var g = Topology.Describe(f);
            switch (g.Surface)
            {
                case "plane" when g.Normal is Vec3 n:
                    if (n.IsParallelTo(au, 1e-6)) coaxial++;
                    else bad.Add($"plane with normal [{BodyKit.Dir(n)}]");
                    break;
                case "cylinder" or "cone" or "torus" when g.Axis is Vec3 a && g.AxisOrigin is Vec3 o:
                    if (a.IsParallelTo(au, 1e-6) && Radial(o - ap, au).Length < BodyKit.PosTol) coaxial++;
                    else bad.Add($"{g.Surface} about [{BodyKit.Dir(a)}] through [{BodyKit.Pt(o)}]");
                    break;
                case "sphere" when g.AxisOrigin is Vec3 c:
                    if (Radial(c - ap, au).Length < BodyKit.PosTol) coaxial++;
                    else bad.Add($"sphere centered at [{BodyKit.Pt(c)}]");
                    break;
                default:
                    other++;
                    break;
            }
        }
        if (bad.Count > 0) problems.Add($"{bad.Count} new face(s) are not coaxial with the axis through [{BodyKit.Pt(ap)}] along [{BodyKit.Dir(au)}]: {string.Join("; ", bad.Take(3))}");
        else checks.Add($"{coaxial} face(s) coaxial with the axis [{BodyKit.Dir(au)}]" + (other > 0 ? $" ({other} freeform not checked)" : ""));
        return problems;
    }

    private static Vec3 Radial(Vec3 v, Vec3 u) => v - u * v.Dot(u);

    /// <summary>A sketch line in model space: start point and unit direction.</summary>
    private static (Vec3 P, Vec3 U) SketchLine(ISketchSegment seg, Xform s2m)
    {
        if (seg.GetType() != (int)swSketchSegments_e.swSketchLINE || seg is not ISketchLine line)
            throw SwException.InvalidArgument($"Sketch segment {seg.GetName()} is not a straight line, so it can't be an axis.");
        var a = line.SkStart();
        var b = line.SkEnd();
        if (a is null || b is null) throw new SwException(ErrorCodes.Internal, $"Could not read the end points of {seg.GetName()}.");
        var pa = s2m.ApplyPoint(new Vec3(a.X, a.Y, a.Z));
        var pb = s2m.ApplyPoint(new Vec3(b.X, b.Y, b.Z));
        if (pa.DistanceTo(pb) < 1e-12) throw SwException.InvalidArgument($"Sketch line {seg.GetName()} has zero length.");
        return (pa, (pb - pa).Normalized());
    }

    /// <summary>A bare name that is no feature or plane (e.g. "Line3") means a segment of the profile sketch.</summary>
    private static EntityRef AxisRef(IModelDoc2 d, EntityRef a, string profileSketch)
    {
        string? type = BodyKit.Norm(a.Type);
        bool segment = type is "sketch_segment" or "segment" or "sketchsegment";
        bool bareName = type is null && a.Id is null && a.Where is null && a.At is null && !string.IsNullOrWhiteSpace(a.Name) &&
                        EntityResolver.StandardPlane(d, a.Name!) is null && EntityResolver.FeatureByName(d, a.Name!) is null;
        if (!segment && !bareName) return a;
        return new EntityRef { Type = "sketch_segment", Name = a.Name, Sketch = a.Sketch ?? profileSketch, TolMm = a.TolMm, Strict = a.Strict };
    }

    // ================================================================== surface_planar

    public Task<BodyResult> PlanarAsync(string? doc, string? sketch, IReadOnlyList<EntityRef>? edges, bool keepOnFailure, CancellationToken ct = default) =>
        session.RunAsync("surface_planar", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            bool byEdges = edges is { Count: > 0 };
            if (byEdges && sketch is not null)
                throw SwException.InvalidArgument("Pass either 'sketch' or 'edges', not both.");
            IFeature? sketchFeat = null;
            List<Resolved> edgeRefs = [];
            Vec3 q0, n;
            if (byEdges)
            {
                BodyKit.NoOpenSketch(d);
                edgeRefs = EntityResolver.ResolveAll(ctx, d, edges, "edges", EntityKind.Edge);
                var pts = edgeRefs.Select(r => Topology.Describe((IEdge)r.Obj)).SelectMany(g => new[] { g.Start, g.End, g.Mid }).ToList();
                (q0, n) = PlaneThrough(pts) ?? throw SwException.InvalidArgument("The edges don't lie in one plane (or are collinear).",
                    "A planar surface needs a closed loop of edges in one plane, e.g. the top edges of an extruded surface.");
            }
            else
            {
                var (sf, sk, _) = BodyKit.ProfileSketch(ctx, d, sketch, "make a planar surface");
                var contours = SketchService.Contours(sk);
                if (contours.ClosedContours == 0)
                    throw SwException.InvalidArgument($"Sketch '{sf.Name}' has no closed contour ({contours.Status}).", "A planar surface needs a closed profile.");
                sketchFeat = sf;
                (q0, n) = BodyKit.SketchPlane(sk);
            }
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            if (sketchFeat is not null)
            {
                if (!sketchFeat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketchFeat.Name}'.");
            }
            else BodyKit.SelectAll(d, edgeRefs, 0);
            object? api = d.InsertPlanarRefSurface() ? true : null;
            d.ClearSelection2(true);
            var o = BodyKit.Verify(ctx, d, before, PlanarSpec, (main, after, checks) => PlanarIntent(main, after, before, q0, n, checks), keepOnFailure, api,
                "The boundary must be closed and planar: a closed sketch, or edges forming a closed loop in one plane.", guessedType: true);
            if (sketchFeat is not null) ctx.DocEntry.LastSketch = null; // consumed
            return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)),
                byEdges ? edgeRefs.Select(e => e.Info("edges")).ToList() : null, consumedSketch: sketchFeat?.Name);
        }, RunOptions.Write(doc), ct);

    /// <summary>Tier B: a new surface body whose faces are planar and lie in the boundary's plane.</summary>
    private static List<string> PlanarIntent(IFeature main, Snapshot after, Snapshot before, Vec3 q0, Vec3 n, List<string> checks)
    {
        var problems = new List<string>();
        if (after.Bodies <= before.Bodies) problems.Add("no new surface body");
        if (after.SolidBodies != before.SolidBodies) problems.Add("the solid bodies changed");
        double area = 0;
        foreach (var f in main.BdFaces())
        {
            var g = Topology.Describe(f);
            var p = Topology.InteriorPoint(f);
            if (g.Surface != "plane" || g.Normal is not Vec3 fn || !fn.IsParallelTo(n, 1e-6) || Math.Abs((p - q0).Dot(n)) > BodyKit.PosTol)
                problems.Add($"a new {g.Surface} face at [{BodyKit.Pt(p)}] is not in the plane through [{BodyKit.Pt(q0)}] with normal [{BodyKit.Dir(n)}]");
            area += f.GetArea();
        }
        if (problems.Count == 0) checks.Add($"planar surface of {Units.Mm2(area)} mm² in the plane with normal [{BodyKit.Dir(n)}]");
        return problems;
    }

    /// <summary>The plane through points (from the two most distant non-collinear triples); null if they aren't coplanar.</summary>
    private static (Vec3 Q, Vec3 N)? PlaneThrough(List<Vec3> pts)
    {
        if (pts.Count < 3) return null;
        var a = pts[0];
        var b = pts.OrderByDescending(p => p.DistanceTo(a)).First();
        var u = b - a;
        if (u.Length < 1e-9) return null;
        var c = pts.OrderByDescending(p => (p - a).Cross(u).Length).First();
        var nrm = u.Cross(c - a);
        if (nrm.Length < 1e-12 * Math.Max(1, u.Length)) return null;
        nrm = nrm.Normalized();
        return pts.All(p => Math.Abs((p - a).Dot(nrm)) <= BodyKit.PosTol) ? (a, nrm) : null;
    }

    // ================================================================== surface_offset

    public Task<BodyResult> OffsetAsync(string? doc, IReadOnlyList<EntityRef>? faces, double distanceMm, bool flip, bool keepOnFailure,
        CancellationToken ct = default) =>
        session.RunAsync("surface_offset", ctx =>
        {
            if (!double.IsFinite(distanceMm) || distanceMm < 0)
                throw SwException.InvalidArgument($"distanceMm must be 0 (a copy) or positive (got {distanceMm}).", "Use flip to offset to the other side.");
            double dist = Units.M(distanceMm);
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            if (faces is null || faces.Count == 0) throw SwException.InvalidArgument("'faces' must name at least one face.", "E.g. faces:[{at:[0,20,0]}].");
            var refs = EntityResolver.ResolveAll(ctx, d, faces, "faces", EntityKind.Face);
            var sources = refs.Select(r => (Ref: r, Geom: Topology.Describe((IFace2)r.Obj), Point: Topology.InteriorPoint((IFace2)r.Obj), Area: ((IFace2)r.Obj).GetArea())).ToList();
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // Reverse false = along the face normals (outward from a solid), a best guess; the other side is redone flipped once.
            bool reverse = flip, flipped = false;
            while (true)
            {
                var state = new SideState();
                d.ClearSelection2(true);
                BodyKit.SelectAll(d, refs, 0);
                d.InsertOffsetSurface(dist, reverse); // void: the new feature is found by the snapshot difference
                d.ClearSelection2(true);
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, OffsetSpec, (main, after, checks) =>
                        {
                            var problems = new List<string>();
                            if (after.Bodies <= before.Bodies) problems.Add("no new surface body");
                            if (after.SolidBodies != before.SolidBodies) problems.Add("the solid bodies changed");
                            problems.AddRange(OffsetIntent(main, sources.Select(s => (s.Ref.Label, s.Geom, s.Point, s.Area)).ToList(), flip ? -dist : dist, state, checks));
                            return problems;
                        }, keepOnFailure, null, "Offset faces must allow the distance (a convex face can't be offset inward beyond its radius).", guessedType: true);
                    if (flipped) o = o with { Checks = [.. o.Checks, "the reverse flag was inverted (SOLIDWORKS offset to the other side)"] };
                    return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), refs.Select(r => r.Info("faces")).ToList());
                }
                catch (SwException) when (!keepOnFailure && state.Reversed && !flipped && BodyKit.NothingNew(d, before))
                {
                    reverse = !reverse;
                    flipped = true;
                }
            }
        }, RunOptions.Write(doc), ct);

    /// <summary>
    /// Tier B: each planar source face has a new planar face parallel to it at the signed distance along its normal (same
    /// area); each cylinder a coaxial new cylinder of radius r ± distance. Other surfaces are counted, not measured.
    /// </summary>
    private static List<string> OffsetIntent(IFeature main, List<(string Label, FaceGeom Geom, Vec3 Point, double Area)> sources, double signed,
        SideState state, List<string> checks)
    {
        var made = main.BdFaces().Select(f => (Geom: Topology.Describe(f), Point: Topology.InteriorPoint(f), Area: f.GetArea())).ToList();
        var problems = new List<string>();
        int planes = 0, cylinders = 0, other = 0, mirrored = 0;
        foreach (var s in sources)
        {
            switch (s.Geom.Surface)
            {
                case "plane" when s.Geom.Normal is Vec3 n:
                {
                    bool Has(double want) => made.Any(m => m.Geom.Surface == "plane" && m.Geom.Normal is Vec3 mn && mn.IsParallelTo(n, 1e-6) &&
                                                           Math.Abs((m.Point - s.Point).Dot(n) - want) <= BodyKit.PosTol &&
                                                           Math.Abs(m.Area - s.Area) <= 1e-12 + 1e-6 * s.Area);
                    if (Has(signed)) planes++;
                    else if (signed != 0 && Has(-signed)) mirrored++;
                    else problems.Add($"no new planar face {Units.Mm(signed)} mm from {s.Label} along its normal [{BodyKit.Dir(n)}]");
                    break;
                }
                case "cylinder" when s.Geom.Axis is Vec3 a && s.Geom.AxisOrigin is Vec3 o && s.Geom.Radius is double r:
                {
                    var rs = made.Where(m => m.Geom.Surface == "cylinder" && m.Geom.Axis is Vec3 ma && ma.IsParallelTo(a, 1e-6) &&
                                             m.Geom.AxisOrigin is Vec3 mo && Radial(mo - o, a.Normalized()).Length < BodyKit.PosTol && m.Geom.Radius is not null)
                        .Select(m => m.Geom.Radius!.Value).ToList();
                    double want = Math.Abs(signed);
                    if (rs.Any(x => Math.Abs(Math.Abs(x - r) - want) <= BodyKit.PosTol)) cylinders++;
                    else problems.Add($"no coaxial cylinder of radius {Units.Mm(r)} ± {Units.Mm(want)} mm for {s.Label}" +
                                      (rs.Count == 0 ? "" : $" (found r {string.Join(", ", rs.Take(4).Select(x => Units.Mm(x)))} mm)"));
                    break;
                }
                default:
                    other++;
                    break;
            }
        }
        if (mirrored > 0 && problems.Count == 0 && planes == 0)
        {
            state.Reversed = true;
            problems.Add($"the offset went to the other side ({Units.Mm(-signed)} mm instead of {Units.Mm(signed)} mm along the face normals)");
        }
        else if (mirrored > 0) problems.Add($"{mirrored} face(s) were offset to the other side");
        if (problems.Count == 0)
            checks.Add($"offset {Units.Mm(signed)} mm: {planes} planar face(s) at the distance (same area)" +
                       (cylinders > 0 ? $", {cylinders} cylinder(s) at r ± {Units.Mm(Math.Abs(signed))} mm" : "") +
                       (other > 0 ? $", {other} other face(s) not measured" : ""));
        return problems;
    }

    // ================================================================== surface_knit

    public Task<BodyResult> KnitAsync(string? doc, IReadOnlyList<EntityRef>? surfaces, bool trySolid, bool keepOnFailure, CancellationToken ct = default) =>
        session.RunAsync("surface_knit", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var refs = BodyKit.ResolveBodies(ctx, d, surfaces, "surfaces", BodyKind.Surface);
            if (refs.Count < 2 && !trySolid)
                throw SwException.InvalidArgument($"Knitting needs at least two surface bodies (got {refs.Count}).", "Pass trySolid:true to close a single surface into a solid.");
            var inputs = refs.Select(r => BodyKit.Geom(ctx, d, (IBody2)r.Obj)).ToList();
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);
            int surfacesBefore = geomsBefore.Count(g => !g.Solid);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var fm = d.FeatureManager;
            var faces = refs.SelectMany(r => ((IBody2)r.Obj).Faces())
                .Select((f, i) => new Resolved { Obj = f, Kind = EntityKind.Face, Label = $"face {i + 1} of the surfaces" }).ToList();
            object? Sew() => fm.InsertSewRefSurface(false, trySolid, true, 1e-5, 1e-4);
            // IFeatureManager.InsertSewRefSurface (unverified): no gap filters, merge entities, tolerance 0.01 mm; the surface
            // bodies selected (mark 1, then 0), then their faces; the old void IModelDoc2 member last (no solid option).
            var attempts = new List<(string Label, Action Select, Func<object?> Call)>
            {
                ("InsertSewRefSurface, surface bodies on mark 1", () => BodyKit.SelectAll(d, refs, 1), Sew),
                ("InsertSewRefSurface, surface bodies on mark 0", () => BodyKit.SelectAll(d, refs, 0), Sew),
                ("InsertSewRefSurface, their faces on mark 0", () => BodyKit.SelectAll(d, faces, 0), Sew),
            };
            if (!trySolid)
                attempts.Add(("IModelDoc2.InsertSewRefSurface, surface bodies on mark 0", () => BodyKit.SelectAll(d, refs, 0), () =>
                {
                    d.InsertSewRefSurface(); // void: the new feature is found by the snapshot difference
                    return null;
                }));
            var tried = new List<string>();
            foreach (var (label, select, call) in attempts)
            {
                d.ClearSelection2(true);
                object? api;
                try
                {
                    select();
                    api = call();
                }
                catch (SwException ex) when (ex.Code == ErrorCodes.FeatureFailed && BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 120)}"); // the selection was refused
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
                bool solid = false;
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, KnitSpec, (_, _, checks) =>
                        KnitIntent(ctx, part, d, inputs, surfacesBefore, before.SolidBodies, trySolid, s => solid = s, checks),
                        keepOnFailure, api, "The surfaces must share edges (within 0.01 mm) to knit.", guessedType: true);
                    if (trySolid && !solid) ctx.Warn("No solid formed: the knitted surfaces don't enclose a volume (gaps or open edges); the result is a surface body.");
                    return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), refs.Select(r => r.Info("surfaces")).ToList(),
                        method: label, removed: inputs.Select(i => i.Name).ToList(), solidFormed: solid);
                }
                catch (SwException ex) when (!keepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 200)}");
                }
            }
            throw new SwException(ErrorCodes.FeatureFailed, $"surface_knit: the surfaces were not knitted. Tried: {BodyKit.Tried(tried)}.",
                "Knit surfaces that share edges; body_list shows the surface bodies.");
        }, RunOptions.Write(doc), ct);

    /// <summary>
    /// Tier B: the input surface bodies became one knit surface (fewer surface bodies, area kept) or, with trySolid, a
    /// new solid with a positive volume.
    /// </summary>
    private static List<string> KnitIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, List<BodyGeom> inputs, int surfacesBefore, int solidsBefore,
        bool trySolid, Action<bool> solidFormed, List<string> checks)
    {
        var after = BodyKit.AllGeoms(ctx, part, d);
        var problems = new List<string>();
        int solids = after.Count(a => a.Solid), surfaces = after.Count(a => !a.Solid);
        double inputArea = inputs.Sum(i => i.Area);
        bool solid = solids > solidsBefore;
        solidFormed(solid);
        foreach (var i in inputs)
            if (after.Any(a => a.Id == i.Id && BodyKit.SameSize(a, i) && BodyKit.SameBox(a, i))) problems.Add($"{i.Label} is still a separate surface");
        if (solid)
        {
            var made = BodyKit.NewOrChanged([], after.Where(a => a.Solid).ToList()).Where(a => a.Volume > 0).ToList();
            if (!trySolid) problems.Add("a solid formed although trySolid is false");
            else if (made.Count == 0) problems.Add("the new solid has no volume");
            else checks.Add($"formed a solid ({solids - solidsBefore} new; {Units.Mm3(made.Max(m => m.Volume))} mm³ for the largest)");
        }
        else
        {
            if (surfaces > surfacesBefore - inputs.Count + 1) problems.Add($"{surfaces} surface bodies remain; expected at most {surfacesBefore - inputs.Count + 1}");
            var knit = after.Where(a => !a.Solid).OrderByDescending(a => a.Area).FirstOrDefault();
            if (knit is not null && Math.Abs(knit.Area - inputArea) > 1e-4 * inputArea)
                ctx.Warn($"The knit surface's area ({Units.Mm2(knit.Area)} mm²) differs from the inputs' total ({Units.Mm2(inputArea)} mm²).");
            if (problems.Count == 0) checks.Add($"{inputs.Count} surface bodies knitted into one ({Units.Mm2(inputArea)} mm²)");
        }
        return problems;
    }

    // ================================================================== surface_thicken

    public Task<BodyResult> ThickenAsync(string? doc, EntityRef? surface, double thicknessMm, string? side, bool keepOnFailure, CancellationToken ct = default) =>
        session.RunAsync("surface_thicken", ctx =>
        {
            double t = Units.PositiveM(thicknessMm, "thicknessMm");
            string sideName = BodyKit.Norm(side) switch
            {
                null or "one" or "side1" or "side_1" => "one",
                "other" or "two" or "side2" or "side_2" => "other",
                "both" => "both",
                _ => throw SwException.InvalidArgument($"Unknown side '{side}'.", "Use one, other or both."),
            };
            int dir = sideName switch
            {
                "one" => (int)swThickenDirection_e.swThickenDirection_Side1,
                "other" => (int)swThickenDirection_e.swThickenDirection_Side2,
                _ => (int)swThickenDirection_e.swThickenDirection_Both,
            };
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var r = BodyKit.ResolveBody(ctx, d, surface, "surface", BodyKind.Surface);
            double area = ((IBody2)r.Obj).Faces().Sum(f => f.GetArea()); // one side of the sheet, face by face
            var flat = FlatSheet((IBody2)r.Obj);
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // VERIFIED: the surface body on mark 1, FeatureBossThicken(t, dir, 0, false, true, true, true).
            d.ClearSelection2(true);
            BodyKit.SelectAll(d, [r], 1);
            object? api = d.FeatureManager.FeatureBossThicken(t, dir, 0, false, true, true, true);
            d.ClearSelection2(true);
            var o = BodyKit.Verify(ctx, d, before, ThickenSpec, (_, after, checks) => ThickenIntent(ctx, part, d, area, flat, t, sideName, after, before, checks),
                keepOnFailure, api, "Thicken a surface body ({id} from body_list, or {at} on its face); a thickness larger than its curvature radius fails.",
                guessedType: true);
            return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)).Where(g => g.Solid), [r.Info("surface")]);
        }, RunOptions.Write(doc), ct);

    /// <summary>A flat sheet: all faces planar with one plane (point, unit normal); null otherwise.</summary>
    private static (Vec3 Q, Vec3 N)? FlatSheet(IBody2 b)
    {
        (Vec3 Q, Vec3 N)? plane = null;
        foreach (var f in b.Faces())
        {
            var g = Topology.Describe(f);
            if (g.Surface != "plane" || g.Normal is not Vec3 n) return null;
            var p = Topology.InteriorPoint(f);
            if (plane is null) plane = (p, n);
            else if (!n.IsParallelTo(plane.Value.N, 1e-9) || Math.Abs((p - plane.Value.Q).Dot(plane.Value.N)) > BodyKit.PosTol) return null;
        }
        return plane;
    }

    /// <summary>
    /// Tier B: a flat sheet gains area x thickness (both sides: total thickness t or 2t, reported), and the new solid
    /// lies within that thickness of the sheet plane; a curved sheet must gain volume (checked against area x t loosely).
    /// </summary>
    private static List<string> ThickenIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, double a, (Vec3 Q, Vec3 N)? flat, double t, string side,
        Snapshot after, Snapshot before, List<string> checks)
    {
        double dv = after.Volume - before.Volume;
        var problems = new List<string>();
        if (flat is not { } pl)
        {
            double ratio = dv / (a * t);
            if (ratio < 0.5 || ratio > 2.5) problems.Add($"the volume grew by {Units.Mm3(dv)} mm³, far from area x thickness = {Units.Mm3(a * t)} mm³");
            else checks.Add($"curved sheet: ΔV {Units.Mm3(dv)} mm³ ≈ {Math.Round(ratio, 3)} x area x thickness");
            return problems;
        }
        double total;
        if (Math.Abs(dv - a * t) <= 1e-12 + 1e-6 * a * t) total = t;
        else if (side == "both" && Math.Abs(dv - 2 * a * t) <= 1e-12 + 2e-6 * a * t) total = 2 * t;
        else
        {
            problems.Add($"the volume grew by {Units.Mm3(dv)} mm³, not area x thickness = {Units.Mm2(a)} mm² x {Units.Mm(t)} mm = {Units.Mm3(a * t)} mm³");
            return problems;
        }
        checks.Add($"ΔV = {Units.Mm2(a)} mm² x {Units.Mm(total)} mm = {Units.Mm3(a * total)} mm³" + (side == "both" ? $" (both sides: total thickness {Units.Mm(total)} mm)" : ""));
        // Where the material went: the new or changed solid's extent along the sheet normal.
        var solids = BodyKit.SolidGeoms(ctx, part, d);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in solids)
            foreach (var v in s.Vertices)
            {
                double x = (v - pl.Q).Dot(pl.N);
                if (Math.Abs(x) > total + BodyKit.PosTol) continue; // other solids far from the sheet
                lo = Math.Min(lo, x);
                hi = Math.Max(hi, x);
            }
        if (lo <= hi)
        {
            if (hi - lo > total + BodyKit.PosTol) ctx.Warn($"Solids near the sheet span {Units.Mm(hi - lo)} mm along its normal (merged with another body?).");
            else
                checks.Add(side == "both"
                    ? $"material from {Units.Mm(lo)} to {Units.Mm(hi)} mm along the sheet normal [{BodyKit.Dir(pl.N)}]"
                    : $"material on the {(hi > -lo ? "+" : "-")} side of the sheet normal [{BodyKit.Dir(pl.N)}] ({Units.Mm(lo)}..{Units.Mm(hi)} mm)");
        }
        return problems;
    }
}
