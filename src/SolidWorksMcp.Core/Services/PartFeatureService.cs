using System.Globalization;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== requests

/// <summary>feature_revolve options.</summary>
public sealed record RevolveRequest
{
    public string? Doc { get; init; }
    /// <summary>Profile sketch (default: the server's open sketch, else the last finished one).</summary>
    public string? Sketch { get; init; }
    /// <summary>Axis of revolution (default: the profile sketch's only centerline).</summary>
    public EntityRef? Axis { get; init; }
    public double AngleDeg { get; init; } = 360;
    /// <summary>Angle in the second (opposite) direction; null = one direction.</summary>
    public double? Angle2Deg { get; init; }
    public bool Cut { get; init; }
    public bool Merge { get; init; } = true;
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_chamfer options.</summary>
public sealed record ChamferRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>equal | distance_distance | angle_distance.</summary>
    public string? Type { get; init; } = "equal";
    public double DistanceMm { get; init; }
    public double? Distance2Mm { get; init; }
    public double AngleDeg { get; init; } = 45;
    public bool Flip { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_shell options.</summary>
public sealed record ShellRequest
{
    public string? Doc { get; init; }
    /// <summary>Faces to remove (the open sides). Empty = a closed hollow body.</summary>
    public IReadOnlyList<EntityRef>? RemoveFaces { get; init; }
    public double ThicknessMm { get; init; }
    public bool Outward { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_hole options.</summary>
public sealed record HoleRequest
{
    public string? Doc { get; init; }
    /// <summary>Hole centers, model mm [x, y, z], each on a planar face.</summary>
    public IReadOnlyList<double[]>? Points { get; init; }
    /// <summary>The planar face all points lie on (default: the face under each point).</summary>
    public EntityRef? Face { get; init; }
    /// <summary>drill | clearance | counterbore | countersink | tapped.</summary>
    public string? Type { get; init; } = "drill";
    /// <summary>ansi_metric | iso | ansi_inch.</summary>
    public string? Standard { get; init; } = "ansi_metric";
    /// <summary>"M5", "M6x0.75", "Ø6" (drill).</summary>
    public string? Size { get; init; }
    public double? DiameterMm { get; init; }
    /// <summary>close | normal | loose (clearance, counterbore, countersink).</summary>
    public string? Fit { get; init; } = "normal";
    /// <summary>blind | through_all.</summary>
    public string? End { get; init; } = "blind";
    public double? DepthMm { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_pattern_linear options.</summary>
public sealed record LinearPatternRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<string>? Features { get; init; }
    /// <summary>Model axis "+x", "-y", … (or the sign for <see cref="DirectionRef"/>).</summary>
    public string? Direction { get; init; }
    /// <summary>Straight edge, axis, plane or planar face giving the direction.</summary>
    public EntityRef? DirectionRef { get; init; }
    /// <summary>Instances in direction 1, the seed included.</summary>
    public int Count { get; init; }
    public double SpacingMm { get; init; }
    public string? Direction2 { get; init; }
    public int? Count2 { get; init; }
    public double? Spacing2Mm { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_pattern_circular options.</summary>
public sealed record CircularPatternRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<string>? Features { get; init; }
    /// <summary>Axis, cylindrical/conical face, circular edge or straight edge.</summary>
    public EntityRef? Axis { get; init; }
    /// <summary>Instances, the seed included.</summary>
    public int Count { get; init; }
    /// <summary>With equal spacing: the angle the instances span (360 = full circle); else the angle between instances.</summary>
    public double TotalAngleDeg { get; init; } = 360;
    public bool EqualSpacing { get; init; } = true;
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_mirror options.</summary>
public sealed record MirrorRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<string>? Features { get; init; }
    /// <summary>Reference plane or planar face.</summary>
    public EntityRef? Plane { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

// ====================================================================== service

/// <summary>
/// Part features beyond extrude and fillet: revolve, chamfer, shell, Hole Wizard holes, linear and circular patterns and
/// mirror. Every call is verified — Tier A by <see cref="FeatureVerifier"/> (new feature of the right type, no rebuild
/// error, faces, volume rule) and Tier B by the intent checks in this file (coaxial revolve faces, chamfer legs, shell
/// walls, hole cylinders, instance positions) — and rolled back on failure.
/// </summary>
public sealed partial class PartFeatureService(SwSession session)
{
    private const double PosTol = 2e-6;      // m: positions and plane offsets read back from SOLIDWORKS geometry
    private const double RadiusTol = 1e-6;   // m: radii and chamfer legs
    private const double HoleAxisTol = 1e-5; // m: a hole axis through the requested center
    private const int RevolveAxisMark = 16;  // best guess; the verified path (the sketch's only centerline) selects no axis
    private const int MaxSignatureFaces = 600;

    private static readonly FeatureSpec RevolveSpec = new("feature_revolve", ["Revolution", "RevolutionThin", "ICE"], VolumeRule.Increase);
    private static readonly FeatureSpec RevolveCutSpec = new("feature_revolve", ["RevCut", "RevCutThin", "ICE"], VolumeRule.Decrease);
    private static readonly FeatureSpec ChamferSpec = new("feature_chamfer", ["Chamfer"], VolumeRule.Change);
    private static readonly FeatureSpec ShellSpec = new("feature_shell", ["Shell"], VolumeRule.Change);
    private static readonly FeatureSpec HoleSpec = new("feature_hole", ["HoleWzd"], VolumeRule.Decrease);
    private static readonly FeatureSpec LinearSpec = new("feature_pattern_linear", ["LPattern"], VolumeRule.Change);
    private static readonly FeatureSpec CircularSpec = new("feature_pattern_circular", ["CirPattern"], VolumeRule.Change);
    private static readonly FeatureSpec MirrorSpec = new("feature_mirror", ["MirrorPattern"], VolumeRule.Change);

    /// <summary>SWMCP_EXPERIMENTAL=1 enables API paths that are not verified yet (the Hole Wizard tapped hole).</summary>
    internal static bool Experimental =>
        Environment.GetEnvironmentVariable("SWMCP_EXPERIMENTAL") is { } v &&
        (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    // ================================================================== revolve

    public Task<FeatureResult> RevolveAsync(RevolveRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_revolve", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg > 360)
                throw SwException.InvalidArgument($"angleDeg must be greater than 0 and at most 360 (got {q.AngleDeg}).");
            if (q.Angle2Deg is double a2 && (!double.IsFinite(a2) || a2 <= 0 || q.AngleDeg + a2 > 360 + 1e-9))
                throw SwException.InvalidArgument($"angle2Deg must be greater than 0, and angleDeg + angle2Deg at most 360 (got {q.AngleDeg} + {a2}).",
                    "Leave angle2Deg out for one direction.");
            double rad1 = Units.Rad(q.AngleDeg), rad2 = q.Angle2Deg is double x ? Units.Rad(x) : 0;
            double total = rad1 + rad2;

            var (sketchFeat, sketch) = ProfileSketch(ctx, d, q.Sketch);
            var contours = SketchService.Contours(sketch);
            if (contours.ClosedContours == 0)
                throw SwException.InvalidArgument($"Sketch '{sketchFeat.Name}' has no closed contour ({contours.Status}{(contours.Problem is null ? "" : ", " + contours.Problem)}).",
                    "Close the profile (rectangle, circle, closed polyline) before revolving; the axis is a centerline.");

            var s2m = SketchService.SketchToModel(sketch);
            var centerlines = sketch.Segments().Where(IsCenterline).ToList();
            Vec3 ap, au;
            Resolved? axisRef = null;
            bool selectAxis = false;
            string axisLabel;
            if (q.Axis is null)
            {
                if (centerlines.Count != 1)
                    throw SwException.InvalidArgument(centerlines.Count == 0
                            ? $"Sketch '{sketchFeat.Name}' has no centerline to revolve around."
                            : $"Sketch '{sketchFeat.Name}' has {centerlines.Count} centerlines ({string.Join(", ", centerlines.Select(c => c.GetName()))}).",
                        "Pass axis: a centerline {name:'Line3'}, a reference axis {name:'Axis1'} or a straight edge {type:'edge', at:[x,y,z]}, " +
                        "or add one centerline to the sketch.");
                (ap, au) = SketchLine(centerlines[0], s2m);
                axisLabel = $"centerline {centerlines[0].GetName()} of {sketchFeat.Name}";
            }
            else
            {
                var aref = AxisRefFor(d, q.Axis, sketchFeat.Name);
                axisRef = EntityResolver.Resolve(ctx, d, aref, "axis", EntityKind.Axis, EntityKind.Edge, EntityKind.SketchSegment);
                (ap, au) = AxisLine(ctx, d, axisRef, aref.Sketch, "axis", allowCircle: false, allowFace: false);
                // The sketch's only centerline is picked automatically (the verified path); anything else is selected.
                bool onlyCenterline = axisRef.Obj is ISketchSegment seg && centerlines.Count == 1 &&
                                      string.Equals(aref.Sketch, sketchFeat.Name, StringComparison.OrdinalIgnoreCase) &&
                                      string.Equals(seg.GetName(), centerlines[0].GetName(), StringComparison.OrdinalIgnoreCase);
                selectAxis = !onlyCenterline;
                axisLabel = axisRef.Label;
            }
            var sn = s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized();
            if (Math.Abs(au.Dot(sn)) > 1e-6 || Math.Abs((ap - s2m.ApplyPoint(Vec3.Zero)).Dot(sn)) > PosTol)
                throw SwException.InvalidArgument($"The axis ({axisLabel}) does not lie in the plane of sketch '{sketchFeat.Name}'.",
                    "Use a centerline of the sketch, or an axis or edge in the sketch plane.");

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            if (!sketchFeat.Select2(false, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketchFeat.Name}'.");
            if (selectAxis && !axisRef!.Select(d, true, RevolveAxisMark))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {axisRef.Label} as the axis.");
            int blind = (int)swEndConditions_e.swEndCondBlind;
            object? api = d.FeatureManager.FeatureRevolve2(q.Angle2Deg is null, true, false, q.Cut, false, false, blind, blind,
                rad1, rad2, false, false, 0, 0, 0, 0, 0, q.Merge, true, true);
            d.ClearSelection2(true);

            var o = Verify(ctx, d, before, q.Cut ? RevolveCutSpec : RevolveSpec,
                (main, _, checks) => RevolveIntent(main, ap, au, total, checks), q.KeepOnFailure, api,
                "The profile must be closed and lie on one side of the axis (touching it is fine, crossing it fails), with the axis in the sketch plane.");
            ctx.DocEntry.LastSketch = null; // consumed
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, sketchFeat.Name, null, axisRef is null ? null : [axisRef.Info("axis")]);
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>
    /// Tier B: every new face is a surface of revolution about the axis (planes perpendicular to it, cylinders, cones,
    /// tori and spheres centered on it); a partial revolve has end caps through the axis, the requested angle apart.
    /// </summary>
    private static IEnumerable<string> RevolveIntent(IFeature main, Vec3 ap, Vec3 au, double totalRad, List<string> checks)
    {
        var caps = new List<Vec3>();
        var bad = new List<string>();
        int coaxial = 0, other = 0;
        foreach (var f in FeatureFaces(main))
        {
            var g = Topology.Describe(f);
            switch (g.Surface)
            {
                case "plane" when g.Normal is Vec3 n:
                {
                    double c = Math.Abs(n.Dot(au));
                    if (c > 1 - 1e-9) { coaxial++; break; }
                    var p = PointOn(f);
                    if (c < 1e-6 && p is Vec3 pp && Math.Abs(n.Dot(pp - ap)) < PosTol)
                    {
                        var v = Radial(Topology.InteriorPoint(f) - ap, au);
                        if (v.Length > PosTol) caps.Add(v.Normalized());
                        break;
                    }
                    bad.Add($"plane with normal [{Dir(n)}]");
                    break;
                }
                case "cylinder" or "cone" or "torus" when g.Axis is Vec3 a && g.AxisOrigin is Vec3 o:
                    if (a.IsParallelTo(au, 1e-6) && Radial(o - ap, au).Length < PosTol) coaxial++;
                    else bad.Add($"{g.Surface} r {Units.Mm(g.Radius ?? 0)} mm about [{Dir(a)}] through [{Pt(o)}]");
                    break;
                case "sphere" when g.AxisOrigin is Vec3 sc:
                    if (Radial(sc - ap, au).Length < PosTol) coaxial++;
                    else bad.Add($"sphere centered at [{Pt(sc)}]");
                    break;
                default:
                    other++;
                    break;
            }
        }
        if (bad.Count > 0)
        {
            yield return $"{bad.Count} new face(s) are not coaxial with the axis through [{Pt(ap)}] along [{Dir(au)}]: {string.Join("; ", bad.Take(4))}";
            yield break;
        }
        checks.Add($"{coaxial} face(s) coaxial with the axis [{Dir(au)}]" + (other > 0 ? $" ({other} freeform not checked)" : ""));
        bool full = totalRad >= 2 * Math.PI - 1e-9;
        if (full)
        {
            if (caps.Count > 0) yield return $"a 360° revolve should have no end caps, but {caps.Count} planar face(s) contain the axis";
            yield break;
        }
        var dirs = Cluster(caps);
        if (dirs.Count == 2)
        {
            double got = Math.Acos(Math.Clamp(dirs[0].Dot(dirs[1]), -1, 1));
            double want = Math.Min(totalRad, 2 * Math.PI - totalRad);
            if (Math.Abs(got - want) < 1e-5) checks.Add($"end caps {Units.Deg(totalRad)}° apart");
            else yield return $"the end caps are {Units.Deg(got)}° apart, expected {Units.Deg(want)}°";
        }
        else
        {
            checks.Add($"end-cap angle not checked ({dirs.Count} cap direction(s) found; caps merged with other faces?)");
        }
    }

    // ================================================================== chamfer

    public Task<FeatureResult> ChamferAsync(ChamferRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_chamfer", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            string type = Norm(q.Type) switch
            {
                null or "equal" or "equal_distance" => "equal",
                "distance_distance" or "two_distances" or "distances" => "distance_distance",
                "angle_distance" or "distance_angle" => "angle_distance",
                _ => throw SwException.InvalidArgument($"Unknown chamfer type '{q.Type}'.", "Use equal, distance_distance or angle_distance."),
            };
            double d1 = Units.PositiveM(q.DistanceMm, "distanceMm");
            double d2 = d1;
            if (type == "distance_distance")
            {
                if (q.Distance2Mm is not double dd)
                    throw SwException.InvalidArgument("type 'distance_distance' needs distance2Mm (the distance along the other face).");
                d2 = Units.PositiveM(dd, "distance2Mm");
            }
            else if (q.Distance2Mm is not null)
                throw SwException.InvalidArgument($"distance2Mm only applies to type 'distance_distance' (got type '{type}').");
            double ang = Units.Rad(45);
            if (type == "angle_distance")
            {
                if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg >= 90)
                    throw SwException.InvalidArgument($"angleDeg must be between 0 and 90 (got {q.AngleDeg}).");
                ang = Units.Rad(q.AngleDeg);
            }
            if (q.Edges is null || q.Edges.Count == 0)
                throw SwException.InvalidArgument("Pass 'edges' to chamfer.", "E.g. edges: [{at:[0,20,30]}] or [{where:{curve:'line', parallelTo:'x'}, expect:4}].");
            NoOpenSketch(d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            int options = (int)swFeatureChamferOption_e.swFeatureChamferTangentPropagation |
                          (q.Flip ? (int)swFeatureChamferOption_e.swFeatureChamferFlipDirection : 0);
            const string hint = "A distance larger than the adjacent faces fails; use smaller distances or fewer edges.";

            List<ResolvedInfo>? resolved = null;
            FeatureOutcome Attempt(int apiType, double w, double other)
            {
                var edges = EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge);
                resolved = edges.Select(e => e.Info("edges")).ToList();
                var legs = edges.Select(LegInfo).ToList(); // captured before the edges disappear
                var before = Snapshot.Take(d);
                d.ClearSelection2(true);
                foreach (var e in edges)
                    if (!e.Select(d, true, 0))
                        throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {e.Label}.");
                object? api = d.FeatureManager.InsertFeatureChamfer(options, apiType, w, ang, other, 0, 0, 0);
                d.ClearSelection2(true);
                return Verify(ctx, d, before, ChamferSpec, (main, _, checks) => ChamferIntent(main, legs, type, d1, d2, ang, checks),
                    q.KeepOnFailure, api, hint);
            }

            FeatureOutcome o;
            switch (type)
            {
                case "equal":
                    try
                    {
                        o = Attempt((int)swChamferType_e.swChamferEqualDistance, d1, d1);
                    }
                    catch (SwException ex) when (!q.KeepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.VerifyFailed or ErrorCodes.RebuildError)
                    {
                        // The equal-distance type is unverified; distance-distance with equal legs is the same geometry.
                        ctx.Warn($"The equal-distance chamfer type failed ({Clip(ex.Message, 200)}); used distance-distance {Units.Mm(d1)} x {Units.Mm(d1)} mm instead.");
                        o = Attempt((int)swChamferType_e.swChamferDistanceDistance, d1, d1);
                    }
                    break;
                case "distance_distance":
                    o = Attempt((int)swChamferType_e.swChamferDistanceDistance, d1, d2);
                    break;
                default:
                    o = Attempt((int)swChamferType_e.swChamferAngleDistance, d1, 0);
                    break;
            }
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: resolved);
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>A straight edge between two planar faces: its line and the wedge angle the chamfer cuts (or fills).</summary>
    private sealed record EdgeLegs(Vec3 P, Vec3 U, double Wedge, string Label);

    private static EdgeLegs? LegInfo(Resolved r)
    {
        if (r.Obj is not IEdge e) return null;
        var g = Topology.Describe(e);
        if (g.Curve != "line" || g.Direction is not Vec3 u) return null;
        var faces = e.AdjacentFaces();
        if (faces.Count != 2) return null;
        var g1 = Topology.Describe(faces[0]);
        var g2 = Topology.Describe(faces[1]);
        if (g1.Normal is not Vec3 n1 || g2.Normal is not Vec3 n2 || g1.Surface != "plane" || g2.Surface != "plane") return null;
        double wedge = Math.PI - Math.Acos(Math.Clamp(n1.Dot(n2), -1, 1));
        return new EdgeLegs(g.Start, u, wedge, r.Label);
    }

    /// <summary>The chamfer's legs (distances from the original edge along its two faces), sorted, for each accepted reading.</summary>
    internal static List<double[]> ExpectedLegs(string type, double d1, double d2, double angleRad, double wedgeRad)
    {
        var list = new List<double[]>();
        switch (type)
        {
            case "equal":
                list.Add([d1, d1]);
                break;
            case "distance_distance":
                list.Add([Math.Min(d1, d2), Math.Max(d1, d2)]);
                break;
            default:
            {
                // Triangle with the wedge angle at the original edge, leg d1 on one face and the angle at that leg's end
                // (or at the other end: both readings are accepted, and flip swaps the faces).
                double third = Math.PI - wedgeRad - angleRad;
                if (third <= 1e-6 || wedgeRad <= 1e-6) break;
                foreach (double b in new[] { d1 * Math.Sin(angleRad) / Math.Sin(third), d1 * Math.Sin(third) / Math.Sin(angleRad) })
                    list.Add([Math.Min(d1, b), Math.Max(d1, b)]);
                break;
            }
        }
        return list;
    }

    /// <summary>Tier B: for each requested straight edge, a planar chamfer face whose two boundary lines lie at the requested legs.</summary>
    private static IEnumerable<string> ChamferIntent(IFeature main, List<EdgeLegs?> edges, string type, double d1, double d2, double ang,
        List<string> checks)
    {
        var planar = FeatureFaces(main).Where(f => Topology.SurfaceKind(f.Surface()) == "plane")
            .Select(f => f.Edges().Select(Topology.Describe).Where(g => g.Curve == "line" && g.Direction is not null).ToList())
            .ToList();
        int notChecked = 0, ok = 0;
        foreach (var e in edges)
        {
            List<double[]> expect = e is null ? [] : ExpectedLegs(type, d1, d2, ang, e.Wedge);
            if (e is null || expect.Count == 0) { notChecked++; continue; }
            var seen = new List<string>();
            bool found = false;
            foreach (var lines in planar)
            {
                // The chamfer face of this edge is bounded by two lines parallel to it, one on each original face. They
                // are told apart by their offset vectors from the original edge line (an equal chamfer has equal lengths).
                var offsets = new List<Vec3>();
                foreach (var g in lines)
                {
                    if (!g.Direction!.Value.IsParallelTo(e.U, 1e-6)) continue;
                    var v = Radial(g.Start - e.P, e.U);
                    if (!offsets.Any(x => x.DistanceTo(v) < 1e-7)) offsets.Add(v);
                }
                if (offsets.Count != 2) continue;
                var dist = offsets.Select(x => x.Length).OrderBy(x => x).ToList();
                if (dist[1] > 10 * Math.Max(d1, d2) + 1e-3) continue; // the chamfer face of another edge
                seen.Add($"{Units.Mm(dist[0])}/{Units.Mm(dist[1])}");
                if (expect.Any(x => Math.Abs(x[0] - dist[0]) < RadiusTol && Math.Abs(x[1] - dist[1]) < RadiusTol)) { found = true; break; }
            }
            if (found) { ok++; continue; }
            yield return $"no chamfer face with legs {string.Join(" or ", expect.Select(x => $"{Units.Mm(x[0])}/{Units.Mm(x[1])}"))} mm along {e.Label}" +
                         (seen.Count == 0 ? "" : $" (found legs {string.Join(", ", seen.Take(4))} mm)");
        }
        if (ok > 0) checks.Add($"legs measured on {ok} edge(s)");
        if (notChecked > 0) checks.Add($"{notChecked} edge(s) not leg-checked (curved edge or non-planar faces)");
    }

    // ================================================================== shell

    public Task<FeatureResult> ShellAsync(ShellRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_shell", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var part = ctx.Part();
            double t = Units.PositiveM(q.ThicknessMm, "thicknessMm");
            NoOpenSketch(d);
            var remove = q.RemoveFaces is { Count: > 0 } ? EntityResolver.ResolveAll(ctx, d, q.RemoveFaces, "removeFaces", EntityKind.Face) : [];
            var removed = remove.Select(r => (IFace2)r.Obj).ToList();
            var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
            if (solids.Count == 0) throw SwException.InvalidArgument("The part has no solid body to shell.");

            // The planar faces that stay: the shell puts a wall at the thickness behind (inward) or in front of (outward) each.
            var kept = new List<(Vec3 N, double D)>();
            foreach (var b in solids)
                foreach (var f in b.Faces())
                {
                    if (removed.Any(r => SameFace(r, f))) continue;
                    var g = Topology.Describe(f);
                    if (g.Surface != "plane" || g.Normal is not Vec3 n || PointOn(f) is not Vec3 p) continue;
                    kept.Add((n, n.Dot(p)));
                }

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            foreach (var r in remove)
                if (!r.Select(d, true, 0))
                    throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");
            d.InsertFeatureShell(t, q.Outward); // void: the new feature is found by the snapshot difference
            d.ClearSelection2(true);
            var o = Verify(ctx, d, before, ShellSpec, (_, _, checks) => ShellIntent(ctx, part, kept, t, q.Outward, checks), q.KeepOnFailure, null,
                "A thickness at or above the thinnest wall or the smallest fillet radius fails; use a thinner wall or remove other faces.");
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: remove.Select(r => r.Info("removeFaces")).ToList());
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>Tier B: walls at the thickness from the original planar faces (inside them, or outside for outward).</summary>
    private static IEnumerable<string> ShellIntent(SwContext ctx, IPartDoc part, List<(Vec3 N, double D)> kept, double t, bool outward,
        List<string> checks)
    {
        if (kept.Count == 0) { checks.Add("walls not checked (no planar faces kept)"); yield break; }
        var planes = new List<(Vec3 N, double D)>();
        foreach (var b in part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false))
            foreach (var f in b.Faces())
            {
                var g = Topology.Describe(f);
                if (g.Surface == "plane" && g.Normal is Vec3 n && PointOn(f) is Vec3 p) planes.Add((n, n.Dot(p)));
            }
        int matched = 0;
        foreach (var (n, dist) in kept)
        {
            // Inward: the inner wall faces the cavity (normal -n) at n·x = D - t. Outward: the new skin at n·x = D + t.
            var (en, ed) = outward ? (n, dist + t) : (-n, -(dist - t));
            if (planes.Any(p => p.N.Dot(en) > 1 - 1e-9 && Math.Abs(p.D - ed) < PosTol)) matched++;
        }
        if (matched == 0)
        {
            yield return $"no wall found {Units.Mm(t)} mm {(outward ? "outside" : "inside")} any of the {kept.Count} kept planar faces";
            yield break;
        }
        checks.Add($"{matched}/{kept.Count} walls at {Units.Mm(t)} mm {(outward ? "outside" : "inside")} the original faces");
        if (matched < kept.Count)
            ctx.Warn($"Only {matched} of {kept.Count} planar faces got a wall at {Units.Mm(t)} mm (thin regions may have merged).");
    }

    // ================================================================== hole

    public Task<FeatureResult> HoleAsync(HoleRequest q, CancellationToken ct = default)
    {
        int n = q.Points?.Count ?? 0;
        var timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 20 + 8 * n));
        return session.RunAsync("feature_hole", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var plan = HolePlan.Parse(q.Type, q.Standard, q.Size, q.DiameterMm, q.Fit, q.End, q.DepthMm, Experimental);
            if (q.Points is null || q.Points.Count == 0)
                throw SwException.InvalidArgument("Pass 'points': the hole centers [[x,y,z], …] in model mm, on a planar face.");
            if (q.Points.Count > 50) throw SwException.InvalidArgument($"At most 50 points per call (got {q.Points.Count}).");
            var points = q.Points.Select((p, i) => Units.PointM(p, $"points[{i}]")).ToList();
            double minGap = Units.M(plan.Primary.DiameterMm);
            for (int i = 1; i < points.Count; i++)
                for (int j = 0; j < i; j++)
                    if (points[i].DistanceTo(points[j]) < minGap)
                        throw SwException.InvalidArgument(
                            $"points[{j}] and points[{i}] are {Units.Mm(points[i].DistanceTo(points[j]))} mm apart, less than the hole diameter ({plan.Primary.DiameterMm} mm).",
                            "Overlapping holes are not supported; space the points at least one diameter apart.");
            NoOpenSketch(d);

            // An explicit face is resolved once, then re-found through its persistent reference (each hole changes it).
            byte[]? facePersist = null;
            string? faceLabel = null;
            var resolved = new List<ResolvedInfo>();
            if (q.Face is not null)
            {
                var fr = EntityResolver.Resolve(ctx, d, q.Face, "face", EntityKind.Face);
                var fg = Topology.Describe((IFace2)fr.Obj);
                if (fg.Surface != "plane" || fg.Normal is null)
                    throw SwException.InvalidArgument($"'face' ({fr.Label}) is a {fg.Surface} face; holes need a planar face.");
                facePersist = ComArgs.Bytes((object?)d.Extension.GetPersistReference3(fr.Obj))
                              ?? throw new SwException(ErrorCodes.Internal, $"No persistent reference for {fr.Label}.");
                faceLabel = fr.Label;
                resolved.Add(fr.Info("face"));
            }

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var first = Snapshot.Take(d);
            double throughDepth = first.Box is { Length: 6 } bx
                ? new Vec3(bx[3] - bx[0], bx[4] - bx[1], bx[5] - bx[2]).Length + 0.001 : 1.0;
            const string hint = "The point must lie on a planar face with material behind it (the hole goes against the face normal); " +
                                "check the size, the depth and that through_all meets material.";

            var mains = new List<IFeature>();
            var checksAll = new List<string>();
            Snapshot last = first;
            for (int i = 0; i < points.Count; i++)
            {
                try
                {
                    FeatureOutcome MakeHole(HoleCall call)
                    {
                        string arg = $"points[{i}]";
                        var (face, center, nrm, label) = HoleFace(ctx, d, q.Points[i], points[i], arg, facePersist, faceLabel, resolved);
                        var before = Snapshot.Take(d);
                        SelectFaceAt(ctx, d, face, center, nrm, label);
                        double depth = call.DepthMm is double dm ? Units.M(dm) : throughDepth;
                        var v = call.Values;
                        object? api = d.FeatureManager.HoleWizard5(call.GenericType, call.Standard, call.Fastener, call.Size, (short)call.EndType,
                            Units.M(call.DiameterMm), depth, -1, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11],
                            "", false, true, true, true, true, false);
                        d.ClearSelection2(true);
                        return Verify(ctx, d, before, HoleSpec, (main, _, checks) => HoleIntent(ctx, main, call, center, nrm, i, checks),
                            q.KeepOnFailure, api, hint);
                    }

                    FeatureOutcome o;
                    if (plan.Fallback is { } fb)
                    {
                        try { o = MakeHole(plan.Primary); }
                        catch (SwException ex) when (!q.KeepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.VerifyFailed or ErrorCodes.RebuildError)
                        {
                            ctx.Warn($"points[{i}]: the experimental Hole Wizard tapped hole failed ({Clip(ex.Message, 200)}); made the Ø{Units.Mm(Units.M(fb.DiameterMm))} mm tap-drill hole instead.");
                            o = MakeHole(fb);
                        }
                    }
                    else
                    {
                        o = MakeHole(plan.Primary);
                    }
                    mains.Add(o.Main);
                    checksAll.AddRange(o.Checks);
                    last = o.After;
                    Rename(ctx, o.Main, string.IsNullOrWhiteSpace(q.Name) ? null : points.Count == 1 ? q.Name : $"{q.Name}{i + 1}");
                }
                catch (SwException ex) when (i > 0 && !q.KeepOnFailure)
                {
                    string rb = FeatureVerifier.Rollback(ctx, d, first);
                    throw new SwException(ex.Code, $"points[{i}]: {ex.Message} The holes made for points[0..{i - 1}] were removed as well ({rb})", ex.Hint, ex);
                }
            }
            if (plan.Note is not null) ctx.Warn(plan.Note);
            var outcome = new FeatureOutcome(mains[0], mains, first, last, checksAll.Distinct().ToList());
            return FeatureVerifier.Result(ctx, d, outcome, resolved: resolved);
        }, RunOptions.Write(q.Doc) with { Timeout = timeout }, ct);
    }

    /// <summary>
    /// The planar face a hole starts on (the explicit 'face', else the face under the point) and the hole center: the
    /// point projected onto that face. A point whose projection falls outside the face is an error.
    /// </summary>
    private static (IFace2 Face, Vec3 Center, Vec3 Normal, string Label) HoleFace(SwContext ctx, IModelDoc2 d, double[] pointMm, Vec3 p,
        string arg, byte[]? facePersist, string? faceLabel, List<ResolvedInfo> resolved)
    {
        IFace2 face;
        string label;
        if (facePersist is not null)
        {
            var obj = (object?)d.Extension.GetObjectByPersistReference3(facePersist, out int status);
            face = status == 0 && obj is IFace2 f ? f
                : throw new SwException(ErrorCodes.EntityStale, $"'face' ({faceLabel}) no longer resolves (status {status}) after the previous hole.",
                    "Leave 'face' out to use the face under each point.");
            label = faceLabel!;
        }
        else
        {
            var r = EntityResolver.Resolve(ctx, d, new EntityRef { Type = "face", At = pointMm }, arg, EntityKind.Face);
            face = (IFace2)r.Obj;
            label = r.Label;
            if (resolved.Count < 12) resolved.Add(r.Info(arg));
        }
        var g = Topology.Describe(face);
        if (g.Surface != "plane" || g.Normal is not Vec3 n)
            throw SwException.InvalidArgument($"{arg} lies on a {g.Surface} face ({label}); holes need a planar face.",
                "Pick a point on a flat face, or pass 'face'.");
        var cp = face.ClosestPoint(p);
        if (cp.Length < 3) throw new SwException(ErrorCodes.Internal, $"Could not project {arg} onto {label}.");
        var c = Vec3.From(cp);
        var off = p - c;
        if (Radial(off, n).Length > PosTol)
            throw SwException.InvalidArgument($"{arg} [{TopoQuery.Fmt(pointMm)}] is not over {label} (its nearest point is [{Pt(c)}], on the boundary).",
                "Put the hole center inside the face, or leave 'face' out to use the face under each point.");
        if (off.Length > 1e-5) ctx.Warn($"{arg} was {Units.Mm(off.Length)} mm off {label}; the hole starts at [{Pt(c)}].");
        return (face, c, n, label);
    }

    /// <summary>
    /// Selects the face with the pick point at the hole center (Hole Wizard places the hole exactly there). A ray along
    /// the face normal picks in model space; SelectByID2 with coordinates picks through the screen, so its pick point
    /// is rounded to a pixel (live: 0.1-0.15 mm off at normal zoom) and is only the fallback. The pick point is read
    /// back and the first method that hits the center wins.
    /// </summary>
    private static void SelectFaceAt(SwContext ctx, IModelDoc2 d, IFace2 face, Vec3 c, Vec3 n, string label)
    {
        Vec3? best = null;
        foreach (var how in new[] { "ray", "point" })
        {
            d.ClearSelection2(true);
            var s = c + n * 1e-4; // 0.1 mm in front of the face, looking back at it
            bool ok = how == "ray"
                ? d.Extension.SelectByRay(s.X, s.Y, s.Z, -n.X, -n.Y, -n.Z, 1e-5, (int)swSelectType_e.swSelFACES, false, 0, 0)
                : d.Extension.SelectByID2("", "FACE", c.X, c.Y, c.Z, false, 0, null, 0);
            if (!ok || !SelectedIs(d, face)) continue;
            var sp = ComArgs.Doubles((object?)d.Sel().GetSelectionPoint2(1, -1));
            if (sp.Length < 3) return; // no pick point to check: the hole check will tell where it went
            var at = Vec3.From(sp);
            if (at.DistanceTo(c) <= 1e-7) return;
            best ??= at;
            if (how == "point")
            {
                ctx.Warn($"The face was picked at [{Pt(at)}] instead of [{Pt(c)}]; the hole check will tell where it went.");
                return;
            }
        }
        d.ClearSelection2(true);
        throw new SwException(ErrorCodes.FeatureFailed,
            best is { } b ? $"Could not pick {label} exactly at [{Pt(c)}] mm for the hole (closest pick [{Pt(b)}])."
                          : $"Could not select {label} at [{Pt(c)}] mm for the hole.",
            "Move the point away from the face's edges, or check that nothing else covers the face there.");
    }

    private static bool SelectedIs(IModelDoc2 d, IFace2 face)
    {
        var sm = d.Sel();
        if (sm.GetSelectedObjectCount2(-1) != 1) return false;
        return (object?)sm.GetSelectedObject6(1, -1) is IFace2 f && SameFace(f, face);
    }

    /// <summary>A cylinder or cone of a hole: radius (m), axial range along the drill direction (m from the face).</summary>
    private sealed record HoleSurface(string Kind, double R, double T0, double T1, double HalfAngle);

    /// <summary>Tier B: the hole's cylinders are coaxial with the center line, of the planned diameter, and (blind) as deep as asked.</summary>
    private static IEnumerable<string> HoleIntent(SwContext ctx, IFeature main, HoleCall call, Vec3 center, Vec3 n, int index, List<string> checks)
    {
        var drill = -n;
        var surfaces = new List<HoleSurface>();
        var offAxis = new List<string>();
        foreach (var f in FeatureFaces(main))
        {
            var g = Topology.Describe(f);
            if (g.Surface is not ("cylinder" or "cone") || g.Axis is not Vec3 a || g.AxisOrigin is not Vec3 o || g.Radius is not double r) continue;
            if (!a.IsParallelTo(n, 1e-6) || Radial(o - center, n).Length > HoleAxisTol)
            {
                offAxis.Add($"{g.Surface} Ø{Units.Mm(2 * r)} about [{Dir(a)}] through [{Pt(o)}]");
                continue;
            }
            var (t0, t1) = AxialRange(f, center, drill);
            surfaces.Add(new HoleSurface(g.Surface, r, t0, t1, g.HalfAngle ?? 0));
        }
        string where = $"[{Pt(center)}]";
        var cyl = surfaces.Where(s => s.Kind == "cylinder").ToList();
        if (cyl.Count == 0)
        {
            yield return $"no cylinder coaxial with the hole center {where} along [{Dir(drill)}]" +
                         (offAxis.Count == 0 ? "" : $" (found {string.Join("; ", offAxis.Take(3))})");
            yield break;
        }
        if (cyl.Any(c => c.T1 < -PosTol))
        {
            yield return $"the hole at {where} points out of the material (it should go along [{Dir(drill)}])";
            yield break;
        }
        string found = string.Join(", ", cyl.Select(c => $"Ø{Units.Mm(2 * c.R)} x {Units.Mm(c.T1 - c.T0)}").Distinct().Take(6));
        double rMin = Units.M(call.MinDiameterMm) / 2 - RadiusTol, rMax = Units.M(call.MaxDiameterMm) / 2 + RadiusTol;
        double rWant = Units.M(call.DiameterMm) / 2;
        var through = cyl.Where(c => c.R >= rMin && c.R <= rMax).OrderBy(c => Math.Abs(c.R - rWant)).FirstOrDefault();
        if (through is null)
        {
            yield return $"no Ø{(call.MinDiameterMm == call.MaxDiameterMm ? $"{call.DiameterMm}" : $"{call.MinDiameterMm}..{call.MaxDiameterMm}")} mm cylinder at {where} (found {found} mm)";
            yield break;
        }
        if (Math.Abs(through.R - rWant) > RadiusTol)
            ctx.Warn($"points[{index}]: the hole is Ø{Units.Mm(2 * through.R)} mm, not the planned Ø{call.DiameterMm} mm (SOLIDWORKS used its own table).");
        string desc = $"hole {index + 1}: Ø{Units.Mm(2 * through.R)} mm at {where}";

        switch (call.Kind)
        {
            case "counterbore":
            {
                var cb = cyl.Where(c => c.R > through.R + RadiusTol && Math.Abs(c.T0) < HoleAxisTol).OrderByDescending(c => c.R).FirstOrDefault();
                if (cb is null) { yield return $"no counterbore (a wider coaxial cylinder at the face) at {where} (found {found} mm)"; yield break; }
                desc += $", c'bore Ø{Units.Mm(2 * cb.R)} x {Units.Mm(cb.T1 - cb.T0)} mm";
                if (call.CboreDiameterMm is double cbd && Math.Abs(2 * cb.R - Units.M(cbd)) > 2 * RadiusTol)
                    ctx.Warn($"points[{index}]: the counterbore is Ø{Units.Mm(2 * cb.R)} mm, not the planned Ø{cbd} mm.");
                break;
            }
            case "countersink":
            {
                var cs = surfaces.Where(s => s.Kind == "cone").OrderBy(s => s.T0).FirstOrDefault();
                if (cs is null) { yield return $"no countersink cone at {where} (found {found} mm)"; yield break; }
                double incl = Units.Deg(2 * cs.HalfAngle);
                desc += $", c'sink {incl}°";
                if (call.CsinkAngleDeg is double want && cs.HalfAngle > 0 && Math.Abs(incl - want) > 0.01)
                    ctx.Warn($"points[{index}]: the countersink angle reads {incl}°, not the planned {want}°.");
                break;
            }
        }
        if (call.DepthMm is double dep && call.Kind is "drill" or "clearance" or "tapped")
        {
            if (Math.Abs(through.T0) > HoleAxisTol || Math.Abs(through.T1 - through.T0 - Units.M(dep)) > HoleAxisTol)
            {
                yield return $"the Ø{Units.Mm(2 * through.R)} mm cylinder at {where} runs {Units.Mm(through.T0)}..{Units.Mm(through.T1)} mm deep, expected 0..{dep} mm";
                yield break;
            }
            desc += $", {dep} mm deep";
        }
        else
        {
            desc += $", {Units.Mm(cyl.Max(c => c.T1))} mm long";
        }
        checks.Add(desc);
    }

    /// <summary>The axial range a face covers along <paramref name="dir"/> from <paramref name="origin"/> (its edges' end and mid points).</summary>
    private static (double T0, double T1) AxialRange(IFace2 f, Vec3 origin, Vec3 dir)
    {
        double t0 = double.MaxValue, t1 = double.MinValue;
        foreach (var e in f.Edges())
        {
            var g = Topology.Describe(e);
            foreach (var p in new[] { g.Start, g.End, g.Mid })
            {
                double t = (p - origin).Dot(dir);
                t0 = Math.Min(t0, t);
                t1 = Math.Max(t1, t);
            }
        }
        return t0 > t1 ? (0, 0) : (t0, t1);
    }

    // ================================================================== linear pattern

    public Task<FeatureResult> LinearPatternAsync(LinearPatternRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_pattern_linear", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (q.Count < 1 || q.Count > 1000) throw SwException.InvalidArgument($"count must be between 1 and 1000, the seed included (got {q.Count}).");
            double s1 = Units.PositiveM(q.SpacingMm, "spacingMm");
            bool two = !string.IsNullOrWhiteSpace(q.Direction2);
            int n1 = q.Count, n2 = 1;
            double s2 = s1;
            if (two)
            {
                n2 = q.Count2 ?? throw SwException.InvalidArgument("direction2 needs count2 (instances in direction 2, the seed row included).");
                if (n2 < 1 || n2 > 1000) throw SwException.InvalidArgument($"count2 must be between 1 and 1000 (got {n2}).");
                s2 = Units.PositiveM(q.Spacing2Mm ?? throw SwException.InvalidArgument("direction2 needs spacing2Mm."), "spacing2Mm");
            }
            else if (q.Count2 is not null || q.Spacing2Mm is not null)
                throw SwException.InvalidArgument("count2 and spacing2Mm need direction2 (e.g. \"+z\").");
            if (n1 * n2 < 2) throw SwException.InvalidArgument("The pattern needs at least 2 instances (count includes the seed).");
            NoOpenSketch(d);
            var seeds = Seeds(d, q.Features, "features");
            var dir1 = PatternDirection(ctx, d, q.Direction, q.DirectionRef, "direction");
            DirSpec? dir2 = two ? PatternDirection(ctx, d, q.Direction2, null, "direction2") : null;
            if (dir2 is not null && dir2.U.IsParallelTo(dir1.U, Units.Rad(1)))
                throw SwException.InvalidArgument("direction2 is parallel to direction.", "Pick two different directions, e.g. +x and +z.");
            var seedSigs = Signatures(seeds.SelectMany(FeatureFaces), out double seedArea);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            bool flip1 = false, flip2 = false;
            const string hint = "Instances must land on the body (cuts) or touch it (bosses); check spacing, count and direction.";
            // SOLIDWORKS picks its own sense along a direction reference (a plane's normal, an edge's parameterisation).
            // The instance positions tell which sense came out; a wrong one is deleted and redone flipped. A sense that
            // produced nothing usable (every cut instance off the part, say) is retried flipped too: the position check
            // of the retry decides whether that is the requested direction.
            bool signs = dir1.SignGiven || dir2 is { SignGiven: true };
            var tried = new List<(bool, bool)>();
            while (true)
            {
                tried.Add((flip1, flip2));
                d.ClearSelection2(true);
                if (!dir1.Select(d, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {dir1.Label} as direction 1.");
                if (dir2 is not null && !dir2.Select(d, 2)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {dir2.Label} as direction 2.");
                SelectSeeds(d, seeds, 4);
                object? api = d.FeatureManager.FeatureLinearPattern5(n1, s1, n2, s2, flip1, flip2, "", "", false, false,
                    false, false, false, false, false, false, false, false, 0, 0, false, false);
                d.ClearSelection2(true);

                Hypothesis? best = null;
                bool reversed = false;
                int instances = n1 * n2 - 1;
                FeatureOutcome o;
                try
                {
                    o = Verify(ctx, d, before, LinearSpec, (main, _, checks) =>
                    {
                        var faces = FeatureFaces(main);
                        if (!CanCheckPositions(seedSigs, faces, checks))
                        {
                            // After a failed sense the direction must be proven by positions; without them, give up.
                            return tried.Count > 1 ? ["the default sense failed and the flipped one could not be position-checked"] : [];
                        }
                        var res = Signatures(faces, out double resArea);
                        var hyps = LinearHypotheses(seedSigs, res, dir1.U, s1, n1, dir2?.U, s2, n2);
                        var h = BestOf(hyps);
                        best = h;
                        if (h.Matched == 0)
                            return [$"no instance faces at the expected offsets ({n1} x {Units.Mm(s1)} mm along ±[{Dir(dir1.U)}]" +
                                    (dir2 is null ? "" : $", {n2} x {Units.Mm(s2)} mm along ±[{Dir(dir2.U)}]") + ")"];
                        if (Reversed(h, hyps[0], dir1, dir2))
                        {
                            reversed = true;
                            return []; // redone below with the flip
                        }
                        AreaCheck(ctx, seedArea, resArea, instances);
                        PositionsCheck(ctx, h, checks, $"{n1} x {Units.Mm(s1)} mm along [{Dir(dir1.U * h.S1)}]" +
                                                       (dir2 is null ? "" : $" and {n2} x {Units.Mm(s2)} mm along [{Dir(dir2.U * h.S2)}]"));
                        return [];
                    }, q.KeepOnFailure, api, hint);
                }
                catch (SwException ex) when (signs && seedSigs.Count > 0 && !q.KeepOnFailure &&
                                             ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed)
                {
                    var next = NextSense(tried, dir1.SignGiven, dir2 is { SignGiven: true }, flip1, flip2);
                    if (next is null || NewFeatures(d, before))
                        throw tried.Count > 1
                            ? new SwException(ex.Code, ex.Message + $" (tried {tried.Count} senses of the direction)", ex.Hint, ex)
                            : ex;
                    (flip1, flip2) = next.Value;
                    continue;
                }

                if (reversed && best is not null)
                {
                    var fix = (dir1.SignGiven && best.S1 < 0 ? !flip1 : flip1, dir2 is { SignGiven: true } && best.S2 < 0 ? !flip2 : flip2);
                    string rb = FeatureVerifier.Rollback(ctx, d, before);
                    if (NewFeatures(d, before))
                        throw new SwException(ErrorCodes.FeatureFailed, $"The pattern came out reversed and could not be removed to redo it: {rb}");
                    if (tried.Contains(fix))
                        throw new SwException(ErrorCodes.VerifyFailed,
                            "feature_pattern_linear: along the requested direction the pattern produced nothing usable; only the opposite " +
                            $"direction does. {rb}", "Check the direction's sign, the spacing and the count: the instances must land on the body.");
                    (flip1, flip2) = fix;
                    continue;
                }
                var actual = best is null ? dir1.U : dir1.U * best.S1;
                var extra = new List<string> { PerInstance(o, n1 * n2 - 1) };
                if (flip1 || flip2) extra.Add("direction flipped (SOLIDWORKS' default ran the other way)");
                if (BoxGrowth(o, actual, dir1.Principal) is { } grew) extra.Add(grew);
                if (extra.Count > 0) o = o with { Checks = [.. o.Checks, .. extra] };
                Rename(ctx, o.Main, q.Name);
                var echo = new List<ResolvedInfo>();
                if (dir1.Info is not null) echo.Add(dir1.Info);
                return FeatureVerifier.Result(ctx, d, o, null, actual, echo.Count == 0 ? null : echo);
            }
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);

    /// <summary>A pattern direction: what to select, the unit vector, and whether the caller gave its sign.</summary>
    private sealed record DirSpec(Func<IModelDoc2, int, bool> Selector, Vec3 U, bool SignGiven, string Label, ResolvedInfo? Info, bool Principal)
    {
        public bool Select(IModelDoc2 d, int mark) => Selector(d, mark);
    }

    private static DirSpec PatternDirection(SwContext ctx, IModelDoc2 d, string? axis, EntityRef? reference, string arg)
    {
        Vec3? want = string.IsNullOrWhiteSpace(axis) ? null : AxisSpec.Parse(axis!, arg);
        if (reference is null)
        {
            if (want is not Vec3 w)
                throw SwException.InvalidArgument($"Pass '{arg}' (\"+x\", \"-y\", …){(arg == "direction" ? " or 'directionRef' (an edge, axis or planar face)" : "")}.");
            string plane = PrincipalPlane(w) ?? throw SwException.InvalidArgument(
                $"'{arg}' must be a model axis (+x, -x, +y, -y, +z, -z)." + (arg == "direction" ? " For other directions pass directionRef (a straight edge, axis or planar face)." : ""));
            var feat = EntityResolver.StandardPlane(d, plane)
                       ?? throw new SwException(ErrorCodes.EntityNotFound, $"The standard {plane} plane was not found.", "feature_list shows the planes.");
            return new DirSpec((doc, mark) => feat.Select2(true, mark), w, true, $"{axis} (normal of {feat.Name})", null, true);
        }
        var r = EntityResolver.Resolve(ctx, d, reference, arg + "Ref", EntityKind.Edge, EntityKind.Axis, EntityKind.Face, EntityKind.Plane);
        Vec3 u = r.Obj switch
        {
            IEdge e when Topology.Describe(e) is { Curve: "line", Direction: Vec3 ed } => ed,
            IEdge => throw SwException.InvalidArgument($"{r.Label} is not straight; a pattern direction needs a straight edge, an axis or a planar face."),
            IFace2 f when Topology.Describe(f) is { Surface: "plane", Normal: Vec3 fn } => fn,
            IFace2 => throw SwException.InvalidArgument($"{r.Label} is not planar; a pattern direction needs a planar face (its normal), an edge or an axis."),
            IFeature f when f.Specific() is IRefAxis => AxisLine(ctx, d, r, null, arg + "Ref", false, false).U,
            IFeature f when f.Specific() is IRefPlane => PlaneOf(r).M,
            _ => throw SwException.InvalidArgument($"{r.Label} can't give a pattern direction; use a straight edge, an axis or a planar face."),
        };
        bool signGiven = false;
        if (want is Vec3 w2)
        {
            if (!u.IsParallelTo(w2, Units.Rad(1)))
                throw SwException.InvalidArgument($"'{arg}' [{Dir(w2)}] is not parallel to {r.Label} [{Dir(u)}].", $"Leave '{arg}' out, or give its sign as \"+\"/\"-\" of the reference's own direction.");
            if (u.Dot(w2) < 0) u = -u;
            signGiven = true;
        }
        return new DirSpec((doc, mark) => r.Select(doc, true, mark), u, signGiven, r.Label, r.Info(arg + "Ref"), false);
    }

    private static string? PrincipalPlane(Vec3 w) =>
        Math.Abs(Math.Abs(w.X) - 1) < 1e-9 ? "right" : Math.Abs(Math.Abs(w.Y) - 1) < 1e-9 ? "top" : Math.Abs(Math.Abs(w.Z) - 1) < 1e-9 ? "front" : null;

    /// <summary>The next untried (flip1, flip2), flipping only directions whose sign the caller gave; null when none is left.</summary>
    private static (bool, bool)? NextSense(List<(bool, bool)> tried, bool given1, bool given2, bool f1, bool f2)
    {
        (bool, bool)[] order = [(given1 ? !f1 : f1, given2 ? !f2 : f2), (given1 ? !f1 : f1, f2), (f1, given2 ? !f2 : f2)];
        foreach (var c in order)
            if (!tried.Contains(c)) return c;
        return null;
    }

    /// <summary>True when features created since <paramref name="before"/> are still in the tree (a rollback was incomplete).</summary>
    private static bool NewFeatures(IModelDoc2 d, Snapshot before) =>
        Snapshot.Take(d).Features.Keys.Any(id => !before.Features.ContainsKey(id));

    /// <summary>A reversal the caller can fix: a direction whose sign was given came out opposite, and that fits better.</summary>
    private static bool Reversed(Hypothesis best, Hypothesis intended, DirSpec d1, DirSpec? d2) =>
        best.Matched > intended.Matched && ((d1.SignGiven && best.S1 < 0) || (d2 is { SignGiven: true } && best.S2 < 0));

    private static List<Hypothesis> LinearHypotheses(List<FaceSig> seed, List<FaceSig> result, Vec3 u1, double s1, int n1, Vec3? u2, double s2, int n2)
    {
        var list = new List<Hypothesis>();
        int[] signs2 = n2 > 1 && u2 is not null ? [1, -1] : [1];
        foreach (int a in new[] { 1, -1 })
            foreach (int b in signs2)
            {
                var xf = GridSample(n1, n2).Select(ij => Rigid.Translate(u1 * (ij.I * s1 * a) + (u2 ?? Vec3.Zero) * (ij.J * s2 * b)));
                var (m, t) = Score(seed, result, xf);
                list.Add(new Hypothesis(a, b, m, t));
            }
        return list;
    }

    /// <summary>Instance indices to check: all for small patterns, else the first, middle and last ones.</summary>
    private static IEnumerable<(int I, int J)> GridSample(int n1, int n2)
    {
        foreach (int i in Sample(n1))
            foreach (int j in Sample(n2))
                if (i != 0 || j != 0) yield return (i, j);
    }

    private static int[] Sample(int n) => n <= 8 ? Enumerable.Range(0, n).ToArray() : new[] { 0, 1, 2, 3, n / 2, n - 3, n - 2, n - 1 }.Distinct().ToArray();

    /// <summary>Boss patterns that stick out: how far the model box grew along a model axis (information).</summary>
    private static string? BoxGrowth(FeatureOutcome o, Vec3 dir, bool principal)
    {
        if (!principal || o.After.Volume <= o.Before.Volume || o.Before.Box is not { Length: 6 } b || o.After.Box is not { Length: 6 } a) return null;
        int k = Math.Abs(dir.X) > 0.5 ? 0 : Math.Abs(dir.Y) > 0.5 ? 1 : 2;
        double grew = dir.X + dir.Y + dir.Z > 0 ? a[k + 3] - b[k + 3] : b[k] - a[k];
        return grew > PosTol ? $"model grew {Units.Mm(grew)} mm along [{Dir(dir)}]" : null;
    }

    // ================================================================== circular pattern

    public Task<FeatureResult> CircularPatternAsync(CircularPatternRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_pattern_circular", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (q.Count < 2 || q.Count > 1000) throw SwException.InvalidArgument($"count must be between 2 and 1000, the seed included (got {q.Count}).");
            double a = q.TotalAngleDeg;
            if (!double.IsFinite(a) || a <= 0) throw SwException.InvalidArgument($"totalAngleDeg must be positive (got {a}).");
            double step;
            if (q.EqualSpacing)
            {
                if (a > 360 + 1e-9) throw SwException.InvalidArgument($"totalAngleDeg must be at most 360 with equalSpacing (got {a}).");
                step = a >= 360 - 1e-9 ? 2 * Math.PI / q.Count : Units.Rad(a) / (q.Count - 1);
            }
            else
            {
                if ((q.Count - 1) * a > 360 + 1e-9)
                    throw SwException.InvalidArgument($"{q.Count} instances {a}° apart go past 360°.", "Lower the count or the angle (without equalSpacing, totalAngleDeg is the angle between instances).");
                step = Units.Rad(a);
            }
            if (q.Axis is null) throw SwException.InvalidArgument("'axis' is required: an axis {name:'Axis1'}, a cylindrical face {type:'face', at:[…]} or an edge.");
            NoOpenSketch(d);
            var seeds = Seeds(d, q.Features, "features");
            var axisRef = EntityResolver.Resolve(ctx, d, q.Axis, "axis", EntityKind.Axis, EntityKind.Face, EntityKind.Edge);
            var (ap, au) = AxisLine(ctx, d, axisRef, null, "axis", allowCircle: true, allowFace: true);
            var seedSigs = Signatures(seeds.SelectMany(FeatureFaces), out double seedArea);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // A full circle looks the same both ways round. A partial one goes in SOLIDWORKS' own sense; when that sense
            // yields nothing usable (the instances miss the part), the other sense is tried once (the result reports it).
            bool partial = !(q.EqualSpacing && a >= 360 - 1e-9);
            bool flip = false;
            FeatureOutcome o;
            while (true)
            {
                d.ClearSelection2(true);
                if (!axisRef.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {axisRef.Label} as the axis.");
                SelectSeeds(d, seeds, 4);
                object? api = d.FeatureManager.FeatureCircularPattern5(q.Count, Units.Rad(a), flip, "", false, q.EqualSpacing, false, false,
                    false, false, 1, 0, "", false);
                d.ClearSelection2(true);
                try
                {
                    o = Verify(ctx, d, before, CircularSpec, (main, _, checks) =>
                    {
                        var faces = FeatureFaces(main);
                        if (!CanCheckPositions(seedSigs, faces, checks)) return [];
                        var res = Signatures(faces, out double resArea);
                        var hyps = new List<Hypothesis>();
                        foreach (int s in new[] { 1, -1 })
                        {
                            var xf = Sample(q.Count).Where(k => k > 0).Select(k => Rigid.Rotate(ap, au, s * k * step));
                            var (m, t) = Score(seedSigs, res, xf);
                            hyps.Add(new Hypothesis(s, 1, m, t));
                        }
                        var best = BestOf(hyps);
                        if (best.Matched == 0)
                            return [$"no instance faces at the expected rotations ({q.Count - 1} x {Units.Deg(step)}° about [{Dir(au)}] through [{Pt(ap)}])"];
                        AreaCheck(ctx, seedArea, resArea, q.Count - 1);
                        string sense = hyps[0].Matched == hyps[1].Matched ? "" : best.S1 > 0 ? ", counter-clockwise" : ", clockwise";
                        PositionsCheck(ctx, best, checks, $"{q.Count} x {Units.Deg(step)}° about [{Dir(au)}]{sense}");
                        return [];
                    }, q.KeepOnFailure, api, "Instances must land on the body; check the axis, the count and the angle.");
                    break;
                }
                catch (SwException ex) when (partial && !flip && !q.KeepOnFailure && seedSigs.Count > 0 &&
                                             ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             !NewFeatures(d, before))
                {
                    flip = true;
                }
            }
            o = o with { Checks = [.. o.Checks, PerInstance(o, q.Count - 1), .. flip ? ["rotation sense flipped (SOLIDWORKS' default missed the part)"] : Array.Empty<string>()] };
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: [axisRef.Info("axis")]);
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);

    // ================================================================== mirror

    public Task<FeatureResult> MirrorAsync(MirrorRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_mirror", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (q.Plane is null) throw SwException.InvalidArgument("'plane' is required: a plane {name:'right'} or a planar face {at:[x,y,z]}.");
            NoOpenSketch(d);
            var seeds = Seeds(d, q.Features, "features");
            var planeRef = EntityResolver.Resolve(ctx, d, q.Plane, "plane", EntityKind.Plane, EntityKind.Face);
            var (q0, m) = PlaneOf(planeRef);
            var seedSigs = Signatures(seeds.SelectMany(FeatureFaces), out double seedArea);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            SelectSeeds(d, seeds, 1);
            if (!planeRef.Select(d, true, 2)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {planeRef.Label} as the mirror plane.");
            object? api = d.FeatureManager.InsertMirrorFeature2(false, false, false, false, 0);
            d.ClearSelection2(true);

            var o = Verify(ctx, d, before, MirrorSpec, (main, _, checks) =>
            {
                var faces = FeatureFaces(main);
                if (!CanCheckPositions(seedSigs, faces, checks)) return [];
                var res = Signatures(faces, out double resArea);
                var (matched, total) = Score(seedSigs, res, [Rigid.Reflect(q0, m)]);
                if (matched == 0) return [$"no mirrored face is the reflection of a seed face about the plane through [{Pt(q0)}] with normal [{Dir(m)}]"];
                AreaCheck(ctx, seedArea, resArea, 1);
                PositionsCheck(ctx, new Hypothesis(1, 1, matched, total), checks, $"reflected about [{Dir(m)}] through [{Pt(q0)}]");
                return [];
            }, q.KeepOnFailure, api, "The mirrored copy must touch the body; check the plane.");
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: [planeRef.Info("plane")]);
        }, RunOptions.Write(q.Doc), ct);

    // ================================================================== shared helpers

    /// <summary>Tier A plus the tool's own hint on errors that carry none.</summary>
    private static FeatureOutcome Verify(SwContext ctx, IModelDoc2 d, Snapshot before, FeatureSpec spec,
        Func<IFeature, Snapshot, List<string>, IEnumerable<string>>? intent, bool keep, object? api, string hint)
    {
        try
        {
            return FeatureVerifier.Check(ctx, d, before, spec, intent, keep, api);
        }
        catch (SwException ex) when (ex.Hint is null)
        {
            throw new SwException(ex.Code, ex.Message, hint, ex);
        }
    }

    private static void NoOpenSketch(IModelDoc2 d)
    {
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
    }

    private static void Rename(SwContext ctx, IFeature f, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        f.Name = name;
        if (f.Name != name) ctx.Warn($"Could not rename '{f.Name}' to '{name}' (the name may be in use).");
    }

    private static List<IFace2> FeatureFaces(IFeature f) => ComArgs.Objects<IFace2>((object?)f.GetFaces());

    private static bool SameFace(IFace2 a, IFace2 b) => ReferenceEquals(a, b) || a.IsSame(b);

    /// <summary>Some point on a face (the closest to the origin), for plane offsets.</summary>
    private static Vec3? PointOn(IFace2 f)
    {
        var p = f.ClosestPoint(Vec3.Zero);
        return p.Length >= 3 ? Vec3.From(p) : null;
    }

    /// <summary>The component of <paramref name="v"/> perpendicular to the unit vector <paramref name="u"/>.</summary>
    private static Vec3 Radial(Vec3 v, Vec3 u) => v - u * v.Dot(u);

    private static string Pt(Vec3 p) => TopoQuery.Fmt(Units.Mm(p));

    private static string Dir(Vec3 v) => TopoQuery.Fmt(Units.Unit(v.Normalized()));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    internal static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    /// <summary>Distinct unit directions.</summary>
    private static List<Vec3> Cluster(List<Vec3> dirs)
    {
        var res = new List<Vec3>();
        foreach (var v in dirs)
            if (!res.Any(r => r.Dot(v) > 1 - 1e-9)) res.Add(v);
        return res;
    }

    private static bool IsCenterline(ISketchSegment s) => s.ConstructionGeometry && s.GetType() == (int)swSketchSegments_e.swSketchLINE;

    /// <summary>A sketch line in model space: start point and unit direction.</summary>
    private static (Vec3 P, Vec3 U) SketchLine(ISketchSegment seg, Xform s2m)
    {
        if (seg.GetType() != (int)swSketchSegments_e.swSketchLINE)
            throw SwException.InvalidArgument($"Sketch segment {seg.GetName()} is not a straight line, so it can't be an axis.");
        var line = (ISketchLine)seg;
        var a = (object?)line.GetStartPoint2() as ISketchPoint;
        var b = (object?)line.GetEndPoint2() as ISketchPoint;
        if (a is null || b is null) throw new SwException(ErrorCodes.Internal, $"Could not read the end points of {seg.GetName()}.");
        var pa = s2m.ApplyPoint(new Vec3(a.X, a.Y, a.Z));
        var pb = s2m.ApplyPoint(new Vec3(b.X, b.Y, b.Z));
        if (pa.DistanceTo(pb) < 1e-12) throw SwException.InvalidArgument($"Sketch line {seg.GetName()} has zero length.");
        return (pa, (pb - pa).Normalized());
    }

    /// <summary>
    /// A bare name that is no feature or plane (e.g. "Line3") means a segment of the profile sketch; segment references
    /// without a sketch name default to the profile sketch (it is closed by then, so "the open sketch" would not work).
    /// </summary>
    private static EntityRef AxisRefFor(IModelDoc2 d, EntityRef a, string profileSketch)
    {
        string? type = Norm(a.Type);
        bool segment = type is "sketch_segment" or "segment" or "sketchsegment";
        bool bareName = type is null && a.Id is null && a.Where is null && a.At is null && !string.IsNullOrWhiteSpace(a.Name) &&
                        EntityResolver.StandardPlane(d, a.Name!) is null && EntityResolver.FeatureByName(d, a.Name!) is null;
        if (!segment && !bareName) return a;
        return new EntityRef { Type = "sketch_segment", Name = a.Name, Sketch = a.Sketch ?? profileSketch, TolMm = a.TolMm, Strict = a.Strict };
    }

    /// <summary>An axis line (point, unit direction) from a reference axis, straight or circular edge, round face or sketch line.</summary>
    private static (Vec3 P, Vec3 U) AxisLine(SwContext ctx, IModelDoc2 d, Resolved r, string? sketchName, string arg, bool allowCircle, bool allowFace)
    {
        switch (r.Obj)
        {
            case IFeature f when f.Specific() is IRefAxis ax:
            {
                var p = ComArgs.Doubles((object?)ax.GetRefAxisParams());
                if (p.Length < 6) throw new SwException(ErrorCodes.Internal, $"Could not read the geometry of {r.Label}.");
                var a = new Vec3(p[0], p[1], p[2]);
                var b = new Vec3(p[3], p[4], p[5]);
                if (a.DistanceTo(b) < 1e-12) throw new SwException(ErrorCodes.Internal, $"{r.Label} has no direction.");
                return (a, (b - a).Normalized());
            }
            case IEdge e:
            {
                var g = Topology.Describe(e);
                if (g.Curve == "line" && g.Direction is Vec3 u) return (g.Start, u);
                if (allowCircle && g.Curve == "circle" && g.Center is Vec3 c && g.Axis is Vec3 ca) return (c, ca);
                throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is a {g.Curve} edge; use a straight{(allowCircle ? " or circular" : "")} edge.");
            }
            case IFace2 face when allowFace:
            {
                var g = Topology.Describe(face);
                if (g.Surface is "cylinder" or "cone" && g.AxisOrigin is Vec3 o && g.Axis is Vec3 fa) return (o, fa);
                throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is a {g.Surface} face; an axis needs a cylindrical or conical face.");
            }
            case ISketchSegment seg:
                return SketchLine(seg, SketchService.SketchToModel(EntityResolver.SketchFor(ctx, d, sketchName)));
        }
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}, which is not an axis.",
            "Use a reference axis {name:'Axis1'}, a straight edge" + (allowFace ? ", a cylindrical face" : "") + " or a sketch centerline.");
    }

    /// <summary>A plane (point, unit normal) from a reference plane or a planar face.</summary>
    private static (Vec3 Q, Vec3 M) PlaneOf(Resolved r)
    {
        switch (r.Obj)
        {
            case IFeature f when f.Specific() is IRefPlane rp:
            {
                var t = ComArgs.Doubles((object?)rp.Transform?.ArrayData);
                if (t.Length < 12) throw new SwException(ErrorCodes.Internal, $"Could not read the frame of {r.Label}.");
                // The plane's normal is the transform's Z row; its origin the translation.
                return (new Vec3(t[9], t[10], t[11]), new Vec3(t[6], t[7], t[8]).Normalized());
            }
            case IFace2 face:
            {
                var g = Topology.Describe(face);
                if (g.Surface != "plane" || g.Normal is not Vec3 n || PointOn(face) is not Vec3 p)
                    throw SwException.InvalidArgument($"{r.Label} is a {g.Surface} face; this needs a plane or a planar face.");
                return (p, n);
            }
        }
        throw SwException.InvalidArgument($"{r.Label} is not a plane or a planar face.");
    }

    /// <summary>The profile sketch: the server's open sketch (closed now), the named one, or the last finished one.</summary>
    private static (IFeature Feature, ISketch Sketch) ProfileSketch(SwContext ctx, IModelDoc2 d, string? name)
    {
        var entry = ctx.DocEntry;
        var sm = d.SketchManager;
        if (sm.ActiveSketch is ISketch open)
        {
            string openName = ((IFeature)open).Name;
            if (entry.ServerSketch != openName)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' is open, but this server didn't open it.",
                    "Ask the user to exit the sketch first.");
            if (name is not null && !string.Equals(name, openName, StringComparison.OrdinalIgnoreCase))
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' is open; finish it before revolving '{name}'.");
            if (open.Is3D()) throw SwException.InvalidArgument("A 3D sketch can't be revolved.");
            sm.InsertSketch(true); // close it: features and mass properties need it closed
            if (sm.ActiveSketch is not null) throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' did not close.");
            entry.ServerSketch = null;
            entry.LastSketch = openName;
            name = openName;
        }
        name ??= entry.LastSketch ?? throw SwException.InvalidArgument("No sketch to revolve.",
            "Pass 'sketch' (e.g. \"Sketch1\"), or draw the profile and a centerline with sketch_start / sketch_add first.");
        var feat = EntityResolver.FeatureByName(d, name)
                   ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.", "feature_list shows the sketches.");
        if (feat.Specific() is not ISketch sk || EntityResolver.FeatureKind(feat) != EntityKind.Sketch)
            throw SwException.InvalidArgument($"'{name}' is a {feat.GetTypeName2()}, not a sketch.");
        return (feat, sk);
    }

    /// <summary>The features to pattern or mirror, by name (solid features with faces), de-duplicated.</summary>
    private static List<IFeature> Seeds(IModelDoc2 d, IReadOnlyList<string>? names, string arg)
    {
        if (names is null || names.Count == 0)
            throw SwException.InvalidArgument($"'{arg}' must name at least one feature, e.g. [\"Boss-Extrude2\"].", "feature_list shows the feature names.");
        if (names.Count > 50) throw SwException.InvalidArgument($"At most 50 features per call (got {names.Count}).");
        var list = new List<IFeature>();
        for (int i = 0; i < names.Count; i++)
        {
            string n = names[i]?.Trim() ?? "";
            if (n.Length == 0) throw SwException.InvalidArgument($"{arg}[{i}] is empty.");
            var f = EntityResolver.FeatureByName(d, n)
                    ?? throw new SwException(ErrorCodes.EntityNotFound, $"No feature named '{n}'.", "feature_list shows the names (Hole Wizard names contain Ø).");
            var kind = EntityResolver.FeatureKind(f);
            if (kind != EntityKind.Feature)
                throw SwException.InvalidArgument($"'{f.Name}' is a {EntityResolver.KindName(kind)}; '{arg}' takes solid features such as Boss-Extrude2 or Cut-Extrude1.");
            if (f.GetFaceCount() == 0) throw SwException.InvalidArgument($"'{f.Name}' ({f.GetTypeName2()}) has no faces to copy.");
            if (list.Any(x => x.GetID() == f.GetID())) continue;
            list.Add(f);
        }
        return list;
    }

    private static void SelectSeeds(IModelDoc2 d, List<IFeature> seeds, int mark)
    {
        foreach (var f in seeds)
            if (!f.Select2(true, mark))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select feature '{f.Name}'.");
    }

    // ------------------------------------------------------------------ instance positions (Tier B of patterns and mirror)

    /// <summary>
    /// A face's position-defining geometry: a plane (unit normal, a point on it), a cylinder (axis, a point on the axis,
    /// radius) or a sphere (center, radius). Instances of a pattern are the seed faces moved by the pattern transforms.
    /// </summary>
    private readonly record struct FaceSig(char Kind, Vec3 A, Vec3 P, double R);

    private sealed record Hypothesis(int S1, int S2, int Matched, int Total);

    private static List<FaceSig> Signatures(IEnumerable<IFace2> faces, out double area)
    {
        var list = new List<FaceSig>();
        area = 0;
        foreach (var f in faces)
        {
            area += f.GetArea();
            var g = Topology.Describe(f);
            switch (g.Surface)
            {
                case "plane" when g.Normal is Vec3 n && PointOn(f) is Vec3 p:
                    list.Add(new FaceSig('p', n, p, 0));
                    break;
                case "cylinder" when g.Axis is Vec3 a && g.AxisOrigin is Vec3 o && g.Radius is double r:
                    list.Add(new FaceSig('c', a, o, r));
                    break;
                case "sphere" when g.AxisOrigin is Vec3 c && g.Radius is double sr:
                    list.Add(new FaceSig('s', Vec3.Zero, c, sr));
                    break;
            }
        }
        return list;
    }

    private static bool CanCheckPositions(List<FaceSig> seed, List<IFace2> resultFaces, List<string> checks)
    {
        if (seed.Count == 0) { checks.Add("instance positions not checked (the seed has no planar, cylindrical or spherical faces)"); return false; }
        if (resultFaces.Count > MaxSignatureFaces) { checks.Add($"instance positions not checked ({resultFaces.Count} faces)"); return false; }
        return true;
    }

    private static FaceSig Apply(FaceSig s, Rigid x) => s.Kind switch
    {
        'p' or 'c' => s with { A = x.Dir(s.A), P = x.Pt(s.P) },
        _ => s with { P = x.Pt(s.P) },
    };

    private static bool Same(FaceSig a, FaceSig b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            'p' => a.A.Dot(b.A) > 1 - 1e-9 && Math.Abs(a.A.Dot(b.P - a.P)) < PosTol,
            'c' => Math.Abs(a.R - b.R) < RadiusTol && a.A.IsParallelTo(b.A, 1e-6) && Radial(b.P - a.P, a.A.Normalized()).Length < PosTol,
            _ => Math.Abs(a.R - b.R) < RadiusTol && a.P.DistanceTo(b.P) < PosTol,
        };
    }

    /// <summary>How many (instance transform, seed face) pairs have a matching face in the result.</summary>
    private static (int Matched, int Total) Score(List<FaceSig> seed, List<FaceSig> result, IEnumerable<Rigid> transforms)
    {
        int matched = 0, total = 0;
        foreach (var x in transforms)
            foreach (var s in seed)
            {
                total++;
                var t = Apply(s, x);
                if (result.Any(r => Same(t, r))) matched++;
            }
        return (matched, total);
    }

    /// <summary>The best-scoring hypothesis; ties keep the earlier (the intended) one.</summary>
    private static Hypothesis BestOf(List<Hypothesis> hs) => hs.Aggregate((best, h) => h.Matched > best.Matched ? h : best);

    private static void PositionsCheck(SwContext ctx, Hypothesis h, List<string> checks, string what)
    {
        checks.Add($"instance faces at the expected positions: {h.Matched}/{h.Total} ({what})");
        if (h.Matched < h.Total)
            ctx.Warn($"{h.Total - h.Matched} of {h.Total} instance faces were not found where expected (instances may merge with each other or the body).");
    }

    /// <summary>The volume change per pattern instance (compare it with the seed's own change).</summary>
    private static string PerInstance(FeatureOutcome o, int instances) =>
        $"volume change per instance {Units.Mm3((o.After.Volume - o.Before.Volume) / Math.Max(1, instances))} mm³ ({instances} instance(s))";

    /// <summary>Instances are congruent copies of the seed, so their faces' total area is (instances × the seed's) unless they merge.</summary>
    private static void AreaCheck(SwContext ctx, double seedArea, double resArea, int instances)
    {
        if (seedArea <= 0 || instances <= 0) return;
        double ratio = resArea / (seedArea * instances);
        if (Math.Abs(ratio - 1) > 1e-3)
            ctx.Warn($"The new faces' area is {Math.Round(ratio, 3)} x {instances} seed copies: instances overlap, merge or are clipped.");
    }

    /// <summary>A rigid motion (rotation or reflection matrix plus translation) acting on column vectors.</summary>
    private sealed class Rigid
    {
        private readonly double[] _m; // 3x3 row-major
        private readonly Vec3 _t;

        private Rigid(double[] m, Vec3 t)
        {
            _m = m;
            _t = t;
        }

        public Vec3 Dir(Vec3 v) => new(_m[0] * v.X + _m[1] * v.Y + _m[2] * v.Z, _m[3] * v.X + _m[4] * v.Y + _m[5] * v.Z,
            _m[6] * v.X + _m[7] * v.Y + _m[8] * v.Z);

        public Vec3 Pt(Vec3 p) => Dir(p) + _t;

        public static Rigid Translate(Vec3 t) => new([1, 0, 0, 0, 1, 0, 0, 0, 1], t);

        /// <summary>Rotation by <paramref name="angle"/> (right hand) about the line through <paramref name="c"/> along <paramref name="axis"/>.</summary>
        public static Rigid Rotate(Vec3 c, Vec3 axis, double angle)
        {
            var u = axis.Normalized();
            double cs = Math.Cos(angle), sn = Math.Sin(angle), k = 1 - cs;
            double[] m =
            [
                cs + u.X * u.X * k, u.X * u.Y * k - u.Z * sn, u.X * u.Z * k + u.Y * sn,
                u.Y * u.X * k + u.Z * sn, cs + u.Y * u.Y * k, u.Y * u.Z * k - u.X * sn,
                u.Z * u.X * k - u.Y * sn, u.Z * u.Y * k + u.X * sn, cs + u.Z * u.Z * k,
            ];
            var r = new Rigid(m, Vec3.Zero);
            return new Rigid(m, c - r.Dir(c));
        }

        /// <summary>Reflection about the plane through <paramref name="q"/> with normal <paramref name="normal"/>.</summary>
        public static Rigid Reflect(Vec3 q, Vec3 normal)
        {
            var n = normal.Normalized();
            double[] m =
            [
                1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Z,
                -2 * n.Y * n.X, 1 - 2 * n.Y * n.Y, -2 * n.Y * n.Z,
                -2 * n.Z * n.X, -2 * n.Z * n.Y, 1 - 2 * n.Z * n.Z,
            ];
            return new Rigid(m, n * (2 * q.Dot(n)));
        }
    }
}

// ====================================================================== hole planning (pure)

/// <summary>One HoleWizard5 call and what its result must show. Lengths in mm; <see cref="Values"/> in API units.</summary>
internal sealed record HoleCall
{
    /// <summary>drill | clearance | counterbore | countersink | tapped.</summary>
    public required string Kind { get; init; }
    public required int GenericType { get; init; }
    public required int Standard { get; init; }
    public required int Fastener { get; init; }
    public required string Size { get; init; }
    public required int EndType { get; init; }
    /// <summary>The diameter passed to the API: drill, clearance (fit) or tap-drill diameter.</summary>
    public required double DiameterMm { get; init; }
    /// <summary>Blind depth; null = through all.</summary>
    public double? DepthMm { get; init; }
    /// <summary>Value1..Value12 (meters / radians); unused slots are -1 (zeros give a wrong hole).</summary>
    public required double[] Values { get; init; }
    /// <summary>Accepted diameter of the main cylinder (a clearance fit range, else the diameter itself).</summary>
    public required double MinDiameterMm { get; init; }
    public required double MaxDiameterMm { get; init; }
    public double? CboreDiameterMm { get; init; }
    public double? CboreDepthMm { get; init; }
    public double? CsinkDiameterMm { get; init; }
    public double? CsinkAngleDeg { get; init; }
}

/// <summary>
/// Turns feature_hole arguments into Hole Wizard calls. Verified live: the ANSI-metric drill-size recipe. Best guesses:
/// ISO/inch drills, clearance (ISO 273 diameters), counterbore and countersink (Value1/Value2 = diameter and
/// depth/angle), and the tapped hole (experimental; by default it is made as its tap-drill hole).
/// </summary>
internal sealed record HolePlan(HoleCall Primary, HoleCall? Fallback, string? Note)
{
    private static readonly double[] Unused = Enumerable.Repeat(-1.0, 12).ToArray();

    /// <summary>ISO 273 clearance holes for metric screws: close (fine), normal (medium), loose (coarse), mm.</summary>
    internal static readonly IReadOnlyDictionary<double, (double Close, double Normal, double Loose)> Clearance =
        new Dictionary<double, (double, double, double)>
        {
            [1.6] = (1.7, 1.8, 2.0), [2] = (2.2, 2.4, 2.6), [2.5] = (2.7, 2.9, 3.1), [3] = (3.2, 3.4, 3.6), [3.5] = (3.7, 3.9, 4.2),
            [4] = (4.3, 4.5, 4.8), [5] = (5.3, 5.5, 5.8), [6] = (6.4, 6.6, 7.0), [8] = (8.4, 9.0, 10.0), [10] = (10.5, 11.0, 12.0),
            [12] = (13.0, 13.5, 14.5), [14] = (15.0, 15.5, 16.5), [16] = (17.0, 17.5, 18.5), [18] = (19.0, 20.0, 21.0),
            [20] = (21.0, 22.0, 24.0), [22] = (23.0, 24.0, 26.0), [24] = (25.0, 26.0, 28.0), [27] = (28.0, 30.0, 32.0),
            [30] = (31.0, 33.0, 35.0), [33] = (34.0, 36.0, 38.0), [36] = (37.0, 39.0, 42.0),
        };

    /// <summary>ISO metric coarse thread pitches, mm.</summary>
    internal static readonly IReadOnlyDictionary<double, double> CoarsePitch = new Dictionary<double, double>
    {
        [1.6] = 0.35, [2] = 0.4, [2.5] = 0.45, [3] = 0.5, [3.5] = 0.6, [4] = 0.7, [5] = 0.8, [6] = 1.0, [8] = 1.25, [10] = 1.5,
        [12] = 1.75, [14] = 2.0, [16] = 2.0, [18] = 2.5, [20] = 2.5, [22] = 2.5, [24] = 3.0, [27] = 3.0, [30] = 3.5, [33] = 3.5, [36] = 4.0,
    };

    /// <summary>Counterbores for ISO 4762 socket head cap screws (DIN 974-1 style): diameter, depth, mm.</summary>
    internal static readonly IReadOnlyDictionary<double, (double D, double Depth)> Counterbore = new Dictionary<double, (double, double)>
    {
        [1.6] = (3.5, 1.8), [2] = (4.4, 2.3), [2.5] = (5.5, 2.9), [3] = (6.5, 3.4), [4] = (8.0, 4.4), [5] = (10.0, 5.4), [6] = (11.0, 6.4),
        [8] = (15.0, 8.6), [10] = (18.0, 10.6), [12] = (20.0, 12.6), [14] = (24.0, 14.6), [16] = (26.0, 16.6), [18] = (30.0, 18.6),
        [20] = (33.0, 20.6), [22] = (36.0, 22.8), [24] = (40.0, 24.8), [27] = (46.0, 27.8), [30] = (50.0, 30.8), [33] = (54.0, 33.8),
        [36] = (58.0, 36.8),
    };

    /// <summary>90° countersinks for ISO 10642 countersunk socket screws: top diameter, mm.</summary>
    internal static readonly IReadOnlyDictionary<double, double> Countersink = new Dictionary<double, double>
    {
        [3] = 6.9, [4] = 9.2, [5] = 11.4, [6] = 13.7, [8] = 18.1, [10] = 22.6, [12] = 27.1, [14] = 31.0, [16] = 33.8, [20] = 40.5,
    };

    public static HolePlan Parse(string? type, string? standard, string? size, double? diameterMm, string? fit, string? end, double? depthMm,
        bool experimental)
    {
        string kind = PartFeatureService.Norm(type) switch
        {
            null or "drill" or "simple" or "plain" => "drill",
            "clearance" or "clearance_hole" => "clearance",
            "counterbore" or "cbore" or "counter_bore" => "counterbore",
            "countersink" or "csink" or "counter_sink" => "countersink",
            "tapped" or "tap" or "threaded" => "tapped",
            _ => throw SwException.InvalidArgument($"Unknown hole type '{type}'.", "Use drill, clearance, counterbore, countersink or tapped."),
        };
        string std = PartFeatureService.Norm(standard) switch
        {
            null or "ansi_metric" or "ansimetric" or "metric" => "ansi_metric",
            "iso" => "iso",
            "ansi_inch" or "ansiinch" or "inch" => "ansi_inch",
            _ => throw SwException.InvalidArgument($"Unknown standard '{standard}'.", "Use ansi_metric (default), iso or ansi_inch."),
        };
        string fitName = PartFeatureService.Norm(fit) switch
        {
            null or "normal" or "medium" => "normal",
            "close" or "fine" => "close",
            "loose" or "coarse" => "loose",
            _ => throw SwException.InvalidArgument($"Unknown fit '{fit}'.", "Use close, normal or loose."),
        };
        string endName = PartFeatureService.Norm(end) switch
        {
            null or "blind" => "blind",
            "through_all" or "through" or "thru_all" or "thru" => "through_all",
            _ => throw SwException.InvalidArgument($"Unknown end '{end}'.", "Use blind (with depthMm) or through_all."),
        };
        double? depth = null;
        if (endName == "blind")
        {
            if (depthMm is not double dm || !double.IsFinite(dm) || dm <= 0)
                throw SwException.InvalidArgument("end 'blind' needs depthMm > 0 (the depth of the full-diameter part, without the drill tip).",
                    "Or pass end: through_all.");
            depth = dm;
        }
        if (diameterMm is double dd && (!double.IsFinite(dd) || dd <= 0 || dd > 500))
            throw SwException.InvalidArgument($"diameterMm must be a positive diameter in mm (got {dd}).");
        // VERIFIED live: HoleWizard5 returns nothing with swEndCondThroughAll (1) whatever the depth; swEndCondThroughAllBoth (9)
        // makes the through-all hole (there is no material on the other side of the start face).
        int endType = endName == "blind" ? (int)swEndConditions_e.swEndCondBlind : (int)swEndConditions_e.swEndCondThroughAllBoth;
        int stdIndex = std switch
        {
            "iso" => (int)swWzdHoleStandards_e.swStandardISO,
            "ansi_inch" => (int)swWzdHoleStandards_e.swStandardAnsiInch,
            _ => (int)swWzdHoleStandards_e.swStandardAnsiMetric,
        };

        if (kind == "drill")
        {
            double dia = diameterMm ?? DrillSize(size)
                ?? throw SwException.InvalidArgument("A drill hole needs diameterMm (or size like \"Ø6\").");
            if (diameterMm is double given && DrillSize(size) is double fromSize && Math.Abs(given - fromSize) > 1e-9)
                throw SwException.InvalidArgument($"size '{size}' and diameterMm {given} disagree.", "Pass one of them.");
            string sizeName = std == "ansi_inch" && !string.IsNullOrWhiteSpace(size) && DrillSize(size) is null ? size!.Trim() : DrillName(dia);
            int fastener = std switch
            {
                "iso" => (int)swWzdHoleStandardFastenerTypes_e.swStandardISODrillSizes,
                "ansi_inch" => (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiInchAllDrillSizes,
                _ => (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes,
            };
            return new HolePlan(Drill(dia, depth, endType, stdIndex, fastener, sizeName, "drill"), null,
                std == "ansi_metric" ? null : $"Drill holes in the {std} standard are not verified yet (the ANSI-metric recipe is); the geometry was checked.");
        }

        if (std == "ansi_inch")
            throw new SwException(ErrorCodes.NotSupported, $"{kind} holes in ansi_inch are not supported yet.",
                "Use ansi_metric or iso with a metric size (\"M5\"), or a drill hole with diameterMm.");
        var (major, pitch) = MetricSize(size)
            ?? throw SwException.InvalidArgument($"A {kind} hole needs a metric size like \"M5\"{(kind == "tapped" ? " or \"M6x0.75\"" : "")} (got '{size}').");
        if (!Clearance.TryGetValue(major, out var cl))
            throw SwException.InvalidArgument($"Size M{F(major)} is not in the table (M1.6 to M36).", "Use a drill hole with diameterMm for other sizes.");
        double fitDia = fitName switch { "close" => cl.Close, "loose" => cl.Loose, _ => cl.Normal };
        string sizeLabel = $"M{F(major)}";

        switch (kind)
        {
            case "clearance":
            {
                double dia = diameterMm ?? fitDia;
                var call = new HoleCall
                {
                    Kind = kind, GenericType = (int)swWzdGeneralHoleTypes_e.swWzdHole, Standard = stdIndex,
                    Fastener = std == "iso" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOScrewClearances
                        : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricScrewClearances,
                    Size = sizeLabel, EndType = endType, DiameterMm = dia, DepthMm = depth, Values = Unused,
                    MinDiameterMm = diameterMm ?? cl.Close, MaxDiameterMm = diameterMm ?? cl.Loose,
                };
                return new HolePlan(call, null, null); // live-verified (diameter and faces)
            }
            case "counterbore":
            {
                if (!Counterbore.TryGetValue(major, out var cb)) throw SwException.InvalidArgument($"No counterbore size for M{F(major)}.");
                double dia = diameterMm ?? fitDia;
                var v = (double[])Unused.Clone();
                v[0] = Units.M(cb.D);     // Value1: counterbore diameter
                v[1] = Units.M(cb.Depth); // Value2: counterbore depth
                var call = new HoleCall
                {
                    Kind = kind, GenericType = (int)swWzdGeneralHoleTypes_e.swWzdCounterBore, Standard = stdIndex,
                    Fastener = std == "iso" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOSocketHeadCap
                        : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricSocketHeadCapScrew,
                    Size = sizeLabel, EndType = endType, DiameterMm = dia, DepthMm = depth, Values = v,
                    MinDiameterMm = diameterMm ?? cl.Close, MaxDiameterMm = diameterMm ?? cl.Loose, CboreDiameterMm = cb.D, CboreDepthMm = cb.Depth,
                };
                return new HolePlan(call, null, null); // live-verified (diameter and faces)
            }
            case "countersink":
            {
                if (!Countersink.TryGetValue(major, out var cs)) throw SwException.InvalidArgument($"No countersink size for M{F(major)} (M3 to M20).");
                double dia = diameterMm ?? fitDia;
                var v = (double[])Unused.Clone();
                v[0] = Units.M(cs);   // Value1: countersink diameter
                v[1] = Units.Rad(90); // Value2: countersink angle
                var call = new HoleCall
                {
                    Kind = kind, GenericType = (int)swWzdGeneralHoleTypes_e.swWzdCounterSink, Standard = stdIndex,
                    Fastener = std == "iso" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOSocketCTSKFlatHead
                        : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricFlatSocket82,
                    Size = sizeLabel, EndType = endType, DiameterMm = dia, DepthMm = depth, Values = v,
                    MinDiameterMm = diameterMm ?? cl.Close, MaxDiameterMm = diameterMm ?? cl.Loose, CsinkDiameterMm = cs, CsinkAngleDeg = 90,
                };
                return new HolePlan(call, null, null); // live-verified (diameter and faces)
            }
            default: // tapped
            {
                double p = pitch ?? CoarsePitch[major];
                double tapDrill = diameterMm ?? Math.Round(major - p, 2);
                if (tapDrill >= major) throw SwException.InvalidArgument($"The tap drill (Ø{F(tapDrill)}) must be smaller than the thread (M{F(major)}).");
                var drill = Drill(tapDrill, depth, endType, (int)swWzdHoleStandards_e.swStandardAnsiMetric,
                    (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricDrillSizes, DrillName(tapDrill), "tapped");
                if (!experimental)
                    return new HolePlan(drill, null,
                        $"Tapped M{F(major)}x{F(p)}: made as the Ø{F(tapDrill)} mm tap-drill hole without thread geometry (the Hole Wizard tapped-hole " +
                        "recipe is unverified; SWMCP_EXPERIMENTAL=1 tries it).");
                var v = (double[])Unused.Clone();
                double l = Units.M(depth ?? 2 * major);
                v[0] = l;                   // best guess, mirrors the one call that produced a tap drill: thread depth,
                v[1] = Units.M(tapDrill);   // tap-drill diameter,
                v[2] = l;                   // tap-drill depth
                var tap = drill with
                {
                    GenericType = (int)swWzdGeneralHoleTypes_e.swWzdTap,
                    Standard = stdIndex,
                    Fastener = std == "iso" ? (int)swWzdHoleStandardFastenerTypes_e.swStandardISOTappedHole
                        : (int)swWzdHoleStandardFastenerTypes_e.swStandardAnsiMetricTappedHole,
                    Size = $"M{F(major)}x{F(p)}",
                    Values = v,
                };
                return new HolePlan(tap, drill, "Tapped holes use an experimental Hole Wizard recipe; check the faces (thread depth may differ).");
            }
        }
    }

    private static HoleCall Drill(double dia, double? depth, int endType, int std, int fastener, string size, string kind) => new()
    {
        Kind = kind, GenericType = (int)swWzdGeneralHoleTypes_e.swWzdHole, Standard = std, Fastener = fastener, Size = size,
        EndType = endType, DiameterMm = dia, DepthMm = depth, Values = Unused, MinDiameterMm = dia, MaxDiameterMm = dia,
    };

    /// <summary>Hole Wizard drill-size names: "Ø6.0", "Ø6.6", "Ø6.25". The Ø must be U+00D8 (feature names contain it too).</summary>
    internal static string DrillName(double dia) => "Ø" + dia.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>"Ø6", "Ø6.0", "⌀6", "6", "6mm" → 6; anything else → null.</summary>
    internal static double? DrillSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var m = Regex.Match(size.Trim(), @"^[Øø⌀]?\s*(\d+(?:\.\d+)?)\s*(?:mm)?$", RegexOptions.IgnoreCase);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>"M5" → (5, null); "M6x0.75" → (6, 0.75); anything else → null.</summary>
    internal static (double Major, double? Pitch)? MetricSize(string? size)
    {
        if (string.IsNullOrWhiteSpace(size)) return null;
        var m = Regex.Match(size.Trim(), @"^M\s*(\d+(?:\.\d+)?)\s*(?:[x×*]\s*(\d+(?:\.\d+)?))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        double major = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        double? pitch = m.Groups[2].Success ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : null;
        if (pitch is <= 0) return null;
        return (major, pitch);
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
