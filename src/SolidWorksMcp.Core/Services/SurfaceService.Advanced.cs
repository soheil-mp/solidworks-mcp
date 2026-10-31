using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed record SurfaceFillRequest
{
    public string? Doc { get; init; }
    /// <summary>The boundary: edges forming a closed loop.</summary>
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>contact (default) or tangent: how the patch meets the faces along the boundary.</summary>
    public string? Continuity { get; init; }
    /// <summary>Merge the patch with the surfaces around it.</summary>
    public bool Merge { get; init; }
    /// <summary>Try to form a solid when the patch closes a volume.</summary>
    public bool Solid { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed record SurfaceTrimRequest
{
    public string? Doc { get; init; }
    /// <summary>trim | extend | untrim.</summary>
    public string Kind { get; init; } = "trim";
    /// <summary>trim: the trim tool, a plane, a sketch or a face of a surface body.</summary>
    public EntityRef? Tool { get; init; }
    /// <summary>trim (mutual): surfaces that trim each other, instead of a tool.</summary>
    public IReadOnlyList<EntityRef>? Surfaces { get; init; }
    /// <summary>trim: points [x,y,z] mm on the pieces to keep (or use RemoveAt).</summary>
    public IReadOnlyList<double[]>? KeepAt { get; init; }
    public IReadOnlyList<double[]>? RemoveAt { get; init; }
    /// <summary>extend: edges of a surface; untrim: edges to extend.</summary>
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>extend: faces whose edges extend; untrim: the face whose holes (or boundary) are filled.</summary>
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    public double? DistanceMm { get; init; }
    /// <summary>extend: linearly (tangent) instead of along the surface's own shape.</summary>
    public bool Linear { get; init; }
    /// <summary>untrim a face: holes (default), outer or all edges.</summary>
    public string? Edge { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class SurfaceService
{
    private static readonly FeatureSpec FillSpec = new("surface_fill", ["FillRefSurface", "FillSurface", "FilledSurface"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec TrimSpec = new("surface_trim", ["TrimRefSurface", "SurfaceTrim", "TrimSurface"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec ExtendSpec = new("surface_trim", ["ExtendRefSurface", "SurfaceExtend", "ExtendSurface"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec UntrimSpec = new("surface_trim", ["UnTrimRefSurf", "UntrimRefSurface", "UntrimSurface"], VolumeRule.Any, RequireFaces: false);

    // ================================================================== surface_fill

    /// <summary>
    /// A patch over a closed loop of edges (InsertFillSurface2; boundary edges selected with mark 1 for contact, 513 for
    /// tangent). Tier B: a new or merged surface (or a solid with <see cref="SurfaceFillRequest.Solid"/>); the patch's
    /// faces touch every boundary edge; a planar loop's patch has the loop's own area.
    /// </summary>
    public Task<BodyResult> FillAsync(SurfaceFillRequest q, CancellationToken ct = default) =>
        session.RunAsync("surface_fill", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            int mark = (BodyKit.Norm(q.Continuity) ?? "contact") switch
            {
                "contact" => 1,
                "tangent" => 513,
                var c => throw SwException.InvalidArgument($"continuity must be contact or tangent (got '{c}')."),
            };
            var edges = EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge);
            var mids = edges.Select(e => Topology.Describe((IEdge)e.Obj).Mid).ToList();
            var pts = edges.SelectMany(e => Topology.Describe((IEdge)e.Obj) is var g ? new[] { g.Start, g.End } : []).ToList();
            if (!Closed(pts))
                throw SwException.InvalidArgument("The edges don't form a closed loop.", "Pass every edge around the opening (edge_list {where:{ofFace…}} helps).");
            var plane = PlaneThrough(edges.SelectMany(e => Topology.Describe((IEdge)e.Obj) is var g ? new[] { g.Start, g.End, g.Mid } : []).ToList());
            double? planarArea = plane is var (q0, n) && edges.All(e => Topology.Describe((IEdge)e.Obj).Curve == "line") ? LoopArea(edges, q0, n) : null;
            var geomsBefore = BodyKit.AllGeoms(ctx, part, d);
            int solidsBefore = geomsBefore.Count(g => g.Solid);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            BodyKit.SelectAll(d, edges, mark);
            int options = (int)swFeatureFillSurfaceOptions_e.swOptimizeSurface
                          | (q.Solid ? (int)swFeatureFillSurfaceOptions_e.swTryToFormSolid : 0)
                          | (q.Merge || q.Solid ? (int)swFeatureFillSurfaceOptions_e.swMergeResult : 0);
            var api = (object?)d.FeatureManager.InsertFillSurface2(2, options, ComArgs.Empty, ComArgs.Empty, ComArgs.Empty, ComArgs.Empty);
            d.ClearSelection2(true);
            var o = BodyKit.Verify(ctx, d, before, FillSpec, (main, after, checks) =>
            {
                var problems = new List<string>();
                var faces = main.BdFaces().ToList();
                var all = part.AllBodies(visibleOnly: false).SelectMany(b => b.Faces()).ToList();
                var far = mids.Where(m => !all.Any(f => Topology.DistanceToFace(f, m) < BodyKit.PosTol)).ToList();
                if (far.Count > 0) problems.Add($"{far.Count} boundary edge(s) are not on any face afterwards");
                if (q.Solid && after.SolidBodies <= solidsBefore) problems.Add("no solid was formed (the surfaces don't enclose a volume?)");
                double area = faces.Sum(f => f.GetArea());
                if (faces.Count > 0 && planarArea is double pa && !q.Solid && !q.Merge && Math.Abs(area - pa) > 1e-4 * pa + 1e-10)
                    problems.Add($"the patch is {Units.Mm2(area)} mm², the planar loop {Units.Mm2(pa)} mm²");
                if (problems.Count == 0)
                {
                    checks.Add($"patch over {edges.Count} boundary edge(s){(faces.Count > 0 ? $", {Units.Mm2(area)} mm²" : "")}");
                    if (planarArea is double p2 && !q.Solid && !q.Merge) checks.Add($"equals the planar loop's area {Units.Mm2(p2)} mm²");
                    if (q.Solid) checks.Add($"a solid of {Units.Mm3(after.Volume)} mm³ was formed");
                }
                return problems;
            }, q.KeepOnFailure, api, "The edges must form a closed loop; a tangent patch needs faces along every edge.", guessedType: true);
            return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), edges.Select(e => e.Info("edges")).ToList(),
                solidFormed: q.Solid ? Snapshot.Take(d).SolidBodies > solidsBefore : null);
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>Every endpoint shared by an even number of edge ends (a closed chain or loops).</summary>
    private static bool Closed(List<Vec3> ends)
    {
        var left = new List<Vec3>(ends);
        while (left.Count > 0)
        {
            var p = left[0];
            int same = left.Count(x => x.DistanceTo(p) < 1e-6);
            if (same % 2 != 0) return false;
            left.RemoveAll(x => x.DistanceTo(p) < 1e-6);
        }
        return true;
    }

    /// <summary>The area inside a planar loop of straight edges (shoelace in the plane).</summary>
    private static double LoopArea(List<Resolved> edges, Vec3 q0, Vec3 n)
    {
        var u = (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n).Normalized();
        var v = n.Cross(u);
        var segs = edges.Select(e => Topology.Describe((IEdge)e.Obj)).Select(g => (g.Start, g.End)).ToList();
        var chain = new List<Vec3> { segs[0].Start, segs[0].End };
        segs.RemoveAt(0);
        while (segs.Count > 0)
        {
            var last = chain[^1];
            int i = segs.FindIndex(s => s.Start.DistanceTo(last) < 1e-6 || s.End.DistanceTo(last) < 1e-6);
            if (i < 0) break;
            var s = segs[i];
            chain.Add(s.Start.DistanceTo(last) < 1e-6 ? s.End : s.Start);
            segs.RemoveAt(i);
        }
        double a = 0;
        for (int k = 0; k + 1 < chain.Count; k++)
        {
            var p = chain[k] - q0;
            var r = chain[k + 1] - q0;
            a += p.Dot(u) * r.Dot(v) - r.Dot(u) * p.Dot(v);
        }
        return Math.Abs(a) / 2;
    }

    // ================================================================== surface_trim

    public Task<BodyResult> TrimAsync(SurfaceTrimRequest q, CancellationToken ct = default) =>
        (BodyKit.Norm(q.Kind) ?? "trim") switch
        {
            "trim" => session.RunAsync("surface_trim", ctx => Trim(ctx, q), RunOptions.Write(q.Doc), ct),
            "extend" => session.RunAsync("surface_trim", ctx => Extend(ctx, q), RunOptions.Write(q.Doc), ct),
            "untrim" => session.RunAsync("surface_trim", ctx => Untrim(ctx, q), RunOptions.Write(q.Doc), ct),
            var k => throw SwException.InvalidArgument($"kind must be trim, extend or untrim (got '{k}')."),
        };

    /// <summary>
    /// PreTrimSurface (standard with a tool, or mutual between surfaces), the pieces picked by a ray onto each point
    /// (keepAt, or removeAt with BRemovePicked), PostTrimSurface. Tier B: kept points still on a surface, removed ones not,
    /// and the surface area smaller.
    /// </summary>
    private static BodyResult Trim(SwContext ctx, SurfaceTrimRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        bool mutual = q.Surfaces is { Count: > 0 };
        if (mutual == (q.Tool is not null))
            throw SwException.InvalidArgument("Give either 'tool' (a plane, sketch or surface that cuts) or 'surfaces' (surfaces trimming each other).");
        bool keep = q.KeepAt is { Count: > 0 };
        if (keep == (q.RemoveAt is { Count: > 0 }))
            throw SwException.InvalidArgument("Give either 'keepAt' or 'removeAt': points [x,y,z] mm on the pieces.");
        var picks = (keep ? q.KeepAt! : q.RemoveAt!).Select((p, i) => Units.PointM(p, $"{(keep ? "keepAt" : "removeAt")}[{i}]")).ToList();

        List<Resolved> tools;
        if (mutual) tools = BodyKit.ResolveBodies(ctx, d, q.Surfaces, "surfaces", BodyKind.Surface);
        else
        {
            var t = EntityResolver.Resolve(ctx, d, q.Tool, "tool", EntityKind.Plane, EntityKind.Sketch, EntityKind.Face);
            tools = t.Obj is IFace2 f && (object?)f.GetBody() is IBody2 tb
                ? [new Resolved { Obj = tb, Kind = EntityKind.Body, Label = $"surface body '{BodyKit.SafeName(tb)}'" }]
                : [t];
        }
        var surfaces = part.Bodies(swBodyType_e.swSheetBody, visibleOnly: false).SelectMany(b => b.Faces()).ToList();
        var pickFaces = picks.Select((p, i) => surfaces.Where(f => Topology.DistanceToFace(f, p) < EntityResolver.DefaultTolMm / 1000)
                                                  .OrderBy(f => Topology.DistanceToFace(f, p)).FirstOrDefault()
                                              ?? throw new SwException(ErrorCodes.EntityNotFound,
                                                  $"No surface face at [{BodyKit.Pt(p)}] ({(keep ? "keepAt" : "removeAt")}[{i}]).",
                                                  "Pick points on the surface pieces (face_list shows surfaces' faces).")).ToList();
        var geomsBefore = BodyKit.AllGeoms(ctx, part, d);
        double areaBefore = geomsBefore.Where(g => !g.Solid).Sum(g => g.Area);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        var fm = d.FeatureManager;
        d.ClearSelection2(true);
        if (!mutual) BodyKit.SelectAll(d, tools, 0);
        else BodyKit.SelectAll(d, tools, 0);
        fm.PreTrimSurface(mutual, true, false, !keep);
        for (int i = 0; i < picks.Count; i++) PickPiece(d, pickFaces[i], picks[i], $"{(keep ? "keepAt" : "removeAt")}[{i}]");
        var api = (object?)fm.PostTrimSurface(true);
        d.ClearSelection2(true);
        var o = BodyKit.Verify(ctx, d, before, TrimSpec, (main, after, checks) =>
        {
            var problems = new List<string>();
            var faces = part.Bodies(swBodyType_e.swSheetBody, visibleOnly: false).SelectMany(b => b.Faces()).ToList();
            bool On(Vec3 p) => faces.Any(f => Topology.DistanceToFace(f, p) < BodyKit.PosTol);
            for (int i = 0; i < picks.Count; i++)
                if (On(picks[i]) != keep)
                    problems.Add($"[{BodyKit.Pt(picks[i])}] is {(keep ? "no longer" : "still")} on a surface");
            double area = BodyKit.SurfaceGeoms(ctx, part, d).Sum(g => g.Area);
            if (area >= areaBefore - 1e-10) problems.Add("the surfaces got no smaller");
            if (problems.Count == 0)
                checks.Add($"{(keep ? "kept" : "removed")} the piece(s) at {string.Join(", ", picks.Select(p => $"[{BodyKit.Pt(p)}]"))}; surface area {Units.Mm2(areaBefore)} → {Units.Mm2(area)} mm²");
            return problems;
        }, q.KeepOnFailure, api, "The tool must cross the surface; pick points on the pieces (not on the tool).", guessedType: true);
        return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), tools.Select(t => t.Info(mutual ? "surfaces" : "tool")).ToList());
    }

    /// <summary>
    /// Selects the surface piece at <paramref name="p"/> the way SOLIDWORKS' example does: the surface body picked at the
    /// point (SelectByID2 "SURFACEBODY" with model coordinates), else a short ray onto the face along its normal.
    /// </summary>
    private static void PickPiece(IModelDoc2 d, IFace2 face, Vec3 p, string arg)
    {
        var sel = d.Sel();
        int count = sel.GetSelectedObjectCount2(-1);
        bool Grew() => sel.GetSelectedObjectCount2(-1) == count + 1;
        if (d.Extension.SelectByID2("", "SURFACEBODY", p.X, p.Y, p.Z, true, 0, null, 0) && Grew()) return;
        var g = Topology.Describe(face);
        var n = (g.Normal ?? Topology.OutwardNormal(face, g, p)).Normalized();
        foreach (var dir in new[] { n, -n })
        {
            var s = p + dir * 0.001;
            if (d.Extension.SelectByRay(s.X, s.Y, s.Z, -dir.X, -dir.Y, -dir.Z, 1e-5, (int)swSelectType_e.swSelSURFACEBODIES, true, 0, 0) && Grew()) return;
        }
        throw new SwException(ErrorCodes.FeatureFailed, $"Could not pick the surface at {arg} [{BodyKit.Pt(p)}].");
    }

    /// <summary>InsertExtendSurface on the selected edges (or faces). Tier B: the surface grows; straight edges of a planar face by length × distance.</summary>
    private static BodyResult Extend(SwContext ctx, SurfaceTrimRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        double dist = Units.PositiveM(q.DistanceMm ?? 0, "distanceMm");
        bool byEdges = q.Edges is { Count: > 0 };
        if (byEdges == (q.Faces is { Count: > 0 })) throw SwException.InvalidArgument("Give either 'edges' (of a surface) or 'faces' to extend.");
        var items = byEdges ? EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge) : EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face);
        // A straight edge on a planar face grows the face by length × distance (linear or natural: the same for planes).
        double? expect = null;
        if (byEdges && items.All(e => EdgeOnPlanarFace((IEdge)e.Obj)))
            expect = items.Sum(e => Topology.Describe((IEdge)e.Obj) is { Curve: "line" } g ? g.Start.DistanceTo(g.End) * dist : double.NaN);
        var geomsBefore = BodyKit.AllGeoms(ctx, part, d);
        double areaBefore = geomsBefore.Where(g => !g.Solid).Sum(g => g.Area);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        BodyKit.SelectAll(d, items, 0);
        d.InsertExtendSurface(q.Linear, 0, dist);
        d.ClearSelection2(true);
        var o = BodyKit.Verify(ctx, d, before, ExtendSpec, (main, after, checks) =>
        {
            var problems = new List<string>();
            double area = BodyKit.SurfaceGeoms(ctx, part, d).Sum(g => g.Area);
            double grew = area - areaBefore;
            if (grew <= 1e-10) problems.Add("the surface got no bigger");
            else if (expect is double e && double.IsFinite(e) && Math.Abs(grew - e) > 1e-4 * e + 1e-10)
                problems.Add($"the surface grew {Units.Mm2(grew)} mm², expected {Units.Mm2(e)} mm² (edge length × distance)");
            if (problems.Count == 0)
                checks.Add($"surface area grew {Units.Mm2(grew)} mm²{(expect is double x && double.IsFinite(x) ? " = edge length × distance" : "")}");
            return problems;
        }, q.KeepOnFailure, true, "Extend edges of a surface body (not of a solid), or its faces.", guessedType: true);
        return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), items.Select(e => e.Info(byEdges ? "edges" : "faces")).ToList());
    }

    private static bool EdgeOnPlanarFace(IEdge e) =>
        ComArgs.Objects<IFace2>((object?)e.GetTwoAdjacentFaces2()).Where(f => f is not null).All(f => Topology.Describe(f).Surface == "plane");

    /// <summary>
    /// InsertUntrimSurface2 on one face (its holes, its outer boundary or all edges, merged into the face) or on edges
    /// (extended). Tier B: the surface grows; holes filled on a planar face add exactly the holes' area.
    /// </summary>
    private static BodyResult Untrim(SwContext ctx, SurfaceTrimRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        bool byEdges = q.Edges is { Count: > 0 };
        if (byEdges == (q.Faces is { Count: > 0 })) throw SwException.InvalidArgument("Give either 'faces' (one face to untrim) or 'edges'.");
        int faceType = (BodyKit.Norm(q.Edge) ?? "holes") switch
        {
            "holes" or "internal" => (int)swFaceUntrimType_e.swFaceUntrimTypeInternalEdges,
            "outer" or "external" => (int)swFaceUntrimType_e.swFaceUntrimTypeExternalEdges,
            "all" => (int)swFaceUntrimType_e.swFaceUntrimTypeAllEdges,
            var x => throw SwException.InvalidArgument($"edge must be holes, outer or all (got '{x}')."),
        };
        var items = byEdges ? EntityResolver.ResolveAll(ctx, d, q.Edges, "edges", EntityKind.Edge) : EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face);
        if (!byEdges && items.Count != 1) throw SwException.InvalidArgument("Untrim one face at a time.");
        double dist = q.DistanceMm is double mm ? Units.PositiveM(mm, "distanceMm") : 0;
        // Holes of a planar face: their area exactly (inner loops of straight lines and circles).
        double? expect = !byEdges && faceType == (int)swFaceUntrimType_e.swFaceUntrimTypeInternalEdges && Topology.Describe((IFace2)items[0].Obj).Surface == "plane"
            ? HoleArea((IFace2)items[0].Obj) : null;
        var geomsBefore = BodyKit.AllGeoms(ctx, part, d);
        double areaBefore = geomsBefore.Where(g => !g.Solid).Sum(g => g.Area);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        BodyKit.SelectAll(d, items, 0);
        var api = (object?)d.FeatureManager.InsertUntrimSurface2(faceType, (int)swEdgeUntrimType_e.swEdgeUntrimTypeExtendEdges, dist, true, false);
        d.ClearSelection2(true);
        var o = BodyKit.Verify(ctx, d, before, UntrimSpec, (main, after, checks) =>
        {
            var problems = new List<string>();
            double grew = BodyKit.SurfaceGeoms(ctx, part, d).Sum(g => g.Area) - areaBefore;
            if (grew <= 1e-10) problems.Add("the surface got no bigger");
            else if (expect is double e && e > 0 && Math.Abs(grew - e) > 1e-4 * e + 1e-10)
                problems.Add($"the surface grew {Units.Mm2(grew)} mm², the holes' area is {Units.Mm2(e)} mm²");
            if (problems.Count == 0) checks.Add($"surface area grew {Units.Mm2(grew)} mm²{(expect is > 0 ? " = the holes' area" : "")}");
            return problems;
        }, q.KeepOnFailure, api, "Untrim works on surface bodies: a face with holes, or edges to extend.", guessedType: true);
        return BodyKit.Result(ctx, d, o, BodyKit.NewOrChanged(geomsBefore, BodyKit.AllGeoms(ctx, part, d)), items.Select(e => e.Info(byEdges ? "edges" : "faces")).ToList());
    }

    /// <summary>The area inside a planar face's inner loops (polygons of lines, or circles); null for other curves.</summary>
    private static double? HoleArea(IFace2 face)
    {
        var g = Topology.Describe(face);
        if (g.Normal is not Vec3 n) return null;
        double total = 0;
        foreach (var loop in ComArgs.Objects<ILoop2>((object?)face.GetLoops()))
        {
            if (loop.IsOuter()) continue;
            var edges = ComArgs.Objects<IEdge>((object?)loop.GetEdges()).Select(Topology.Describe).ToList();
            if (edges.Count == 1 && edges[0] is { Curve: "circle", Radius: double r }) { total += Math.PI * r * r; continue; }
            if (edges.Any(e => e.Curve != "line")) return null;
            var q0 = edges[0].Start;
            var u = (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n).Normalized();
            var v = n.Cross(u);
            double a = edges.Sum(e => (e.Start - q0).Dot(u) * (e.End - q0).Dot(v) - (e.End - q0).Dot(u) * (e.Start - q0).Dot(v));
            total += Math.Abs(a) / 2;
        }
        return total;
    }
}
