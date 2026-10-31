using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed record RefCurveRequest
{
    public string? Doc { get; init; }
    /// <summary>projected | composite | xyz.</summary>
    public string Kind { get; init; } = "";
    /// <summary>projected: the sketch to project.</summary>
    public string? Sketch { get; init; }
    /// <summary>projected: the faces it is projected onto (along the sketch's normal).</summary>
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>projected: a second sketch (the curve is where both sketches' extrusions meet).</summary>
    public string? OntoSketch { get; init; }
    /// <summary>composite: edges or sketch segments joined end to end.</summary>
    public IReadOnlyList<EntityRef>? Entities { get; init; }
    /// <summary>xyz: points [x,y,z] mm the curve passes through.</summary>
    public double[][]? Points { get; init; }
    public string? Name { get; init; }
}

public sealed class CurveResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
    public required double LengthMm { get; init; }
    public required int Segments { get; init; }
    public double[]? StartMm { get; init; }
    public double[]? EndMm { get; init; }
    public bool? Closed { get; init; }
    public required Verified Verified { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed partial class ReferenceService
{
    private const double CurveTol = 1e-6; // m: points on projected and interpolated curves
    private static readonly string[] CurveKinds = ["projected", "composite", "xyz"];
    private static readonly FeatureSpec ProjectedSpec = new("ref_curve", ["RefCurve", "ProjectedCurve", "ProjCurve"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec CompositeSpec = new("ref_curve", ["CompositeCurve", "CompCurve"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec XyzSpec = new("ref_curve", ["CurveInFile", "CurveThroughXYZPoints", "XYZCurve"], VolumeRule.Any, RequireFaces: false);

    /// <summary>
    /// ref_curve: a projected curve (CreateDefinition(swFmRefCurve): faces mark 1, sketch 2, second sketch 4), a composite
    /// curve (InsertCompositeCurve, mark 1) or a curve through points (InsertCurveFileBegin/Point/End). Tier B: the curve's
    /// segments (IReferenceCurve.GetSegments) lie on the faces, add up to the joined lengths, or pass through every point.
    /// </summary>
    public Task<CurveResult> CurveAsync(RefCurveRequest q, CancellationToken ct = default) =>
        session.RunAsync("ref_curve", ctx =>
        {
            string k = Kind(q.Kind, CurveKinds);
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            var resolved = new List<ResolvedInfo>();
            var fm = d.FeatureManager;
            FeatureSpec spec;
            Func<object?> create;
            Func<List<IEdge>, List<string>, List<string>> expect;
            string hint;
            switch (k)
            {
                case "projected":
                {
                    var sk = SketchByName(d, q.Sketch, "sketch");
                    bool onFaces = q.Faces is { Count: > 0 };
                    if (onFaces == !string.IsNullOrWhiteSpace(q.OntoSketch))
                        throw SwException.InvalidArgument("kind 'projected' needs either 'faces' (sketch onto faces) or 'ontoSketch' (sketch on sketch).");
                    var faces = onFaces ? EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face) : [];
                    for (int i = 0; i < faces.Count; i++) resolved.Add(faces[i].Info($"faces[{i}]"));
                    var onto = onFaces ? null : SketchByName(d, q.OntoSketch, "ontoSketch");
                    var boxes = onFaces ? null : new[] { SketchBox(sk), SketchBox(onto!) };
                    spec = ProjectedSpec;
                    hint = onFaces ? "The faces must lie in front of (or behind) the sketch along its normal." : "The two sketches must be on intersecting planes and overlap.";
                    bool reverse = false;
                    create = () =>
                    {
                        // Projected onto faces, the direction follows the sketch normal: try the other way if nothing came out.
                        foreach (bool rev in new[] { reverse, !reverse })
                        {
                            d.ClearSelection2(true);
                            foreach (var f in faces) if (!f.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {f.Label}.");
                            if (!sk.Select2(true, 2)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sk.Name}'.");
                            if (onto is not null && !onto.Select2(true, 4)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{onto.Name}'.");
                            if ((object?)fm.CreateDefinition((int)swFeatureNameID_e.swFmRefCurve) is not IProjectionCurveFeatureData data)
                                throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS returned no projection-curve definition.");
                            data.Reverse = rev;
                            if ((object?)fm.CreateFeature(data) is IFeature made) return made;
                            if (onto is not null) break;
                        }
                        return null;
                    };
                    expect = (segs, checks) =>
                    {
                        var problems = new List<string>();
                        var pts = segs.SelectMany(Samples).ToList();
                        if (onFaces)
                        {
                            var off = pts.Where(p => !faces.Any(f => Topology.DistanceToFace((IFace2)f.Obj, p) < CurveTol)).ToList();
                            if (off.Count > 0) problems.Add($"{off.Count} curve point(s) are off the faces, e.g. [{Fmt(Units.Mm(off[0]))}] mm");
                            else checks.Add($"every sampled point lies on the {faces.Count} face(s)");
                        }
                        else
                        {
                            var outside = pts.Where(p => !InSketchBox(sk, boxes![0], p) || !InSketchBox(onto!, boxes[1], p)).ToList();
                            if (outside.Count > 0) problems.Add($"{outside.Count} curve point(s) project outside the sketches, e.g. [{Fmt(Units.Mm(outside[0]))}] mm");
                            else checks.Add($"every sampled point projects into both sketches");
                        }
                        return problems;
                    };
                    break;
                }
                case "composite":
                {
                    var items = EntityResolver.ResolveAll(ctx, d, q.Entities, "entities", EntityKind.Edge, EntityKind.SketchSegment);
                    for (int i = 0; i < items.Count; i++) resolved.Add(items[i].Info($"entities[{i}]"));
                    double total = items.Sum(r => r.Obj is IEdge e ? Topology.Describe(e).Length : ((ISketchSegment)r.Obj).GetLength());
                    spec = CompositeSpec;
                    hint = "The entities must connect end to end (edges or sketch segments).";
                    create = () =>
                    {
                        d.ClearSelection2(true);
                        foreach (var r in items) if (!r.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");
                        return d.InsertCompositeCurve() ? true : null;
                    };
                    expect = (segs, checks) =>
                    {
                        double len = segs.Sum(e => Topology.Describe(e).Length);
                        if (Math.Abs(len - total) > CurveTol * Math.Max(1, items.Count)) return [$"the curve is {Mm(len)} mm, its {items.Count} piece(s) add up to {Mm(total)} mm"];
                        checks.Add($"length {Mm(len)} mm = the {items.Count} joined piece(s)");
                        return [];
                    };
                    break;
                }
                default: // xyz
                {
                    if (q.Points is not { Length: >= 2 }) throw SwException.InvalidArgument("kind 'xyz' needs 'points': at least 2 points [x, y, z] (model mm).");
                    var pts = q.Points.Select((p, i) => Units.PointM(p, $"points[{i}]")).ToList();
                    for (int i = 1; i < pts.Count; i++)
                        if (pts[i].DistanceTo(pts[i - 1]) < 1e-7) throw SwException.InvalidArgument($"points[{i}] repeats points[{i - 1}].");
                    double chords = pts.Zip(pts.Skip(1), (a, b) => a.DistanceTo(b)).Sum();
                    spec = XyzSpec;
                    hint = "Give distinct points in order along the curve.";
                    create = () =>
                    {
                        d.ClearSelection2(true);
                        d.InsertCurveFileBegin();
                        foreach (var p in pts) d.InsertCurveFilePoint(p.X, p.Y, p.Z);
                        return d.InsertCurveFileEnd() ? true : null;
                    };
                    expect = (segs, checks) =>
                    {
                        var problems = new List<string>();
                        for (int i = 0; i < pts.Count; i++)
                        {
                            double gap = segs.Min(e => Topology.DistanceToEdge(e, pts[i]));
                            if (gap > CurveTol) problems.Add($"points[{i}] is {Mm(gap)} mm off the curve");
                        }
                        double len = segs.Sum(e => Topology.Describe(e).Length);
                        if (len < chords - CurveTol) problems.Add($"the curve ({Mm(len)} mm) is shorter than its chords ({Mm(chords)} mm)");
                        if (problems.Count == 0) checks.Add($"passes through all {pts.Count} points; {Mm(len)} mm long (chords {Mm(chords)} mm)");
                        return problems;
                    };
                    break;
                }
            }

            var before = Snapshot.Take(d);
            List<IEdge> segments = [];
            var o = WithHint(hint, () =>
            {
                object? api = create();
                d.ClearSelection2(true);
                return BodyKit.Verify(ctx, d, before, spec, (main, _, checks) =>
                {
                    segments = Segments(main);
                    if (segments.Count == 0) return [$"'{main.Name}' has no curve segments"];
                    return expect(segments, checks);
                }, false, api, hint, guessedType: true);
            });
            Rename(ctx, o.Main, q.Name);
            var fr = FeatureVerifier.Result(ctx, d, o, resolved: resolved.Count == 0 ? null : resolved);
            var ends = ChainEnds(segments);
            return new CurveResult
            {
                Doc = fr.Doc,
                Feature = fr.Feature,
                AlsoCreated = fr.AlsoCreated,
                LengthMm = Mm(segments.Sum(e => Topology.Describe(e).Length)),
                Segments = segments.Count,
                StartMm = ends is var (s, _) ? Clean(Units.Mm(s)) : null,
                EndMm = ends is var (_, e2) ? Clean(Units.Mm(e2)) : null,
                Closed = ends is null && segments.Count > 0 ? true : null,
                Verified = fr.Verified,
                Resolved = fr.Resolved,
            };
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>The curve segments of a reference-curve feature (IReferenceCurve.GetSegments; not selectable).</summary>
    private static List<IEdge> Segments(IFeature f)
    {
        if (f.Specific() is not IReferenceCurve rc) return [];
        try { return ComArgs.Objects<IEdge>((object?)rc.GetSegments()); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return []; }
    }

    /// <summary>Start, middle and end points of an edge.</summary>
    private static IEnumerable<Vec3> Samples(IEdge e)
    {
        var g = Topology.Describe(e);
        yield return g.Start;
        yield return g.Mid;
        yield return g.End;
    }

    /// <summary>The two free ends of a chain of segments, or null when it closes.</summary>
    private static (Vec3, Vec3)? ChainEnds(List<IEdge> segs)
    {
        var ends = segs.Select(Topology.Describe).Where(g => !g.Closed).SelectMany(g => new[] { g.Start, g.End }).ToList();
        var free = ends.Where(p => ends.Count(x => x.DistanceTo(p) < 1e-6) == 1).ToList();
        return free.Count == 2 ? (free[0], free[1]) : null;
    }

    private static IFeature SketchByName(IModelDoc2 d, string? name, string arg)
    {
        if (string.IsNullOrWhiteSpace(name)) throw SwException.InvalidArgument($"'{arg}' is required (a sketch name, e.g. Sketch2).");
        var f = EntityResolver.FeatureByName(d, name.Trim())
                ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.", "feature_list shows the sketches.");
        if (f.GetTypeName2() is not ("ProfileFeature" or "3DProfileFeature"))
            throw SwException.InvalidArgument($"'{f.Name}' is a {f.GetTypeName2()}, not a sketch.");
        return f;
    }

    /// <summary>The sketch's 2D extents in its own coordinates (min x, min y, max x, max y), m.</summary>
    private static double[] SketchBox(IFeature f)
    {
        var sk = (ISketch)(object)f.GetSpecificFeature2();
        var pts = ComArgs.Objects<ISketchPoint>((object?)sk.GetSketchPoints2()).Select(p => new Vec3(p.X, p.Y, p.Z)).ToList();
        foreach (var seg in ComArgs.Objects<ISketchSegment>((object?)sk.GetSketchSegments()))
            if (seg is ISketchArc arc && (object?)arc.GetCenterPoint2() is ISketchPoint c)
            {
                double r = arc.GetRadius();
                pts.Add(new Vec3(c.X - r, c.Y - r, 0));
                pts.Add(new Vec3(c.X + r, c.Y + r, 0));
            }
        return pts.Count == 0 ? [0, 0, 0, 0] : [pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y)];
    }

    private static bool InSketchBox(IFeature f, double[] box, Vec3 modelPoint)
    {
        var sk = (ISketch)(object)f.GetSpecificFeature2();
        var p = SketchService.SketchToModel(sk).Inverse().ApplyPoint(modelPoint);
        return p.X >= box[0] - CurveTol && p.Y >= box[1] - CurveTol && p.X <= box[2] + CurveTol && p.Y <= box[3] + CurveTol;
    }
}
