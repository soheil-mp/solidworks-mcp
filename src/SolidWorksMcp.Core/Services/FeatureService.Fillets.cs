using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>feature_fillet options: a constant, variable-radius, face or full-round fillet.</summary>
public sealed record FilletRequest
{
    public string? Doc { get; init; }
    /// <summary>constant (default) | variable | face | full_round.</summary>
    public string? Kind { get; init; }
    public IReadOnlyList<EntityRef>? Edges { get; init; }
    /// <summary>constant: faces whose edges are all rounded; face: the first face set; full_round: the first side.</summary>
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>face: the second face set; full_round: the second side.</summary>
    public IReadOnlyList<EntityRef>? Faces2 { get; init; }
    /// <summary>full_round: the faces between the sides, rounded away completely.</summary>
    public IReadOnlyList<EntityRef>? CenterFaces { get; init; }
    public double? RadiusMm { get; init; }
    /// <summary>variable: the radius at each end of the edge, the first at <see cref="StartAt"/>.</summary>
    public IReadOnlyList<double>? RadiiMm { get; init; }
    /// <summary>variable: a point at (or near) the end that takes the first radius; default: the edge's start.</summary>
    public double[]? StartAt { get; init; }
    public bool Propagate { get; init; } = true;
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>split_line options: a sketch projected onto faces, splitting them.</summary>
public sealed record SplitLineRequest
{
    public string? Doc { get; init; }
    /// <summary>The sketch (default: the open sketch, else the last one finished).</summary>
    public string? Sketch { get; init; }
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>both (default) | normal | reverse: project both ways, or one way along/against the sketch normal.</summary>
    public string? Direction { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class FeatureService
{
    // Type names VERIFIED: a projected split line is a "PLine", a variable-radius fillet a "VarFillet".
    private static readonly FeatureSpec SplitLineSpec = new("feature_split_line", ["PLine", "SplitLine"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec VarFilletSpec = new("feature_fillet", ["VarFillet", "Fillet"], VolumeRule.Change);

    // ------------------------------------------------------------------ fillet kinds

    public Task<FeatureResult> FilletAsync(FilletRequest q, CancellationToken ct = default)
    {
        string kind = (q.Kind ?? "constant").Trim().ToLowerInvariant().Replace(' ', '_');
        bool Any<T>(IReadOnlyList<T>? l) => l is { Count: > 0 };
        void Not(bool given, string what)
        {
            if (given) throw SwException.InvalidArgument($"'{what}' doesn't apply to kind '{kind}'.");
        }
        switch (kind)
        {
            case "constant":
                Not(Any(q.Faces2), "faces2"); Not(Any(q.CenterFaces), "centerFaces"); Not(Any(q.RadiiMm), "radiiMm"); Not(q.StartAt is not null, "startAt");
                return FilletAsync(q.Doc, q.Edges, q.Faces, q.RadiusMm ?? throw SwException.InvalidArgument("radiusMm is required."), q.Propagate, q.Name, q.KeepOnFailure, ct);
            case "face":
                Not(Any(q.Edges), "edges"); Not(Any(q.CenterFaces), "centerFaces"); Not(Any(q.RadiiMm), "radiiMm"); Not(q.StartAt is not null, "startAt");
                if (!Any(q.Faces) || !Any(q.Faces2)) throw SwException.InvalidArgument("A face fillet needs 'faces' and 'faces2' (the two face sets it blends).");
                break;
            case "full_round":
                Not(Any(q.Edges), "edges"); Not(Any(q.RadiiMm), "radiiMm"); Not(q.RadiusMm is not null, "radiusMm"); Not(q.StartAt is not null, "startAt");
                if (!Any(q.Faces) || !Any(q.CenterFaces) || !Any(q.Faces2))
                    throw SwException.InvalidArgument("A full-round fillet needs 'faces' (one side), 'centerFaces' (rounded away) and 'faces2' (the other side).");
                break;
            case "variable":
                Not(Any(q.Faces), "faces"); Not(Any(q.Faces2), "faces2"); Not(Any(q.CenterFaces), "centerFaces"); Not(q.RadiusMm is not null, "radiusMm");
                if (q.Edges is not { Count: 1 } || q.RadiiMm is not { Count: 2 })
                    throw SwException.InvalidArgument("A variable fillet takes one edge and radiiMm with the radius at each end, e.g. [2, 6].");
                break;
            default:
                throw SwException.InvalidArgument($"kind '{q.Kind}' is not supported.", "Use constant, variable, face or full_round.");
        }
        return session.RunAsync("feature_fillet", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (d.SketchManager.ActiveSketch is ISketch open)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
            return kind switch
            {
                "face" => FaceFillet(ctx, d, q),
                "full_round" => FullRound(ctx, d, q),
                _ => VariableFillet(ctx, d, q),
            };
        }, RunOptions.Write(q.Doc), ct);
    }

    private static List<Resolved> Faces(SwContext ctx, IModelDoc2 d, IReadOnlyList<EntityRef> refs, string arg, List<ResolvedInfo> info)
    {
        var fs = EntityResolver.ResolveAll(ctx, d, refs, arg, EntityKind.Face);
        info.AddRange(fs.Select(x => x.Info(arg)));
        return fs;
    }

    private static FeatureResult FaceFillet(SwContext ctx, IModelDoc2 d, FilletRequest q)
    {
        double r = Units.PositiveM(q.RadiusMm ?? throw SwException.InvalidArgument("radiusMm is required."), "radiusMm");
        var info = new List<ResolvedInfo>();
        var set1 = Faces(ctx, d, q.Faces!, "faces", info);
        var set2 = Faces(ctx, d, q.Faces2!, "faces2", info);
        var before = Snapshot.Take(d);
        var data = NewFilletData(d, swSimpleFilletType_e.swFaceFillet);
        data.DefaultRadius = r;
        data.PropagateToTangentFaces = q.Propagate;
        data.SetFaces((int)swSimpleFilletWhichFaces_e.swFaceFilletSet1, ComArgs.DispatchArray(set1.Select(x => x.Obj)));
        data.SetFaces((int)swSimpleFilletWhichFaces_e.swFaceFilletSet2, ComArgs.DispatchArray(set2.Select(x => x.Obj)));
        var api = (object?)d.FeatureManager.CreateFeature(data);
        d.ClearSelection2(true);
        var o = FeatureVerifier.Check(ctx, d, before, FilletSpec, (main, after, checks) => FilletIntent(main, r, checks), q.KeepOnFailure, api);
        Rename(ctx, o.Main, q.Name);
        return FeatureVerifier.Result(ctx, d, o, resolved: info);
    }

    private static FeatureResult FullRound(SwContext ctx, IModelDoc2 d, FilletRequest q)
    {
        var info = new List<ResolvedInfo>();
        var side1 = Faces(ctx, d, q.Faces!, "faces", info);
        var center = Faces(ctx, d, q.CenterFaces!, "centerFaces", info);
        var side2 = Faces(ctx, d, q.Faces2!, "faces2", info);
        // Between parallel flat sides the round is a half cylinder of half their distance.
        double? radius = null;
        if (side1.Count == 1 && side2.Count == 1 && Topology.Describe((IFace2)side1[0].Obj) is { Surface: "plane", Normal: Vec3 n1 }
            && Topology.Describe((IFace2)side2[0].Obj) is { Surface: "plane", Normal: Vec3 n2 } && n1.IsParallelTo(n2, 1e-6))
            radius = Math.Abs((Topology.InteriorPoint((IFace2)side1[0].Obj) - Topology.InteriorPoint((IFace2)side2[0].Obj)).Dot(n1)) / 2;
        var before = Snapshot.Take(d);
        var data = NewFilletData(d, swSimpleFilletType_e.swFullRoundFillet);
        data.PropagateToTangentFaces = q.Propagate;
        data.SetFaces((int)swSimpleFilletWhichFaces_e.swFullRoundFilletSet1, ComArgs.DispatchArray(side1.Select(x => x.Obj)));
        data.SetFaces((int)swSimpleFilletWhichFaces_e.swFullRoundFilletCenterSet, ComArgs.DispatchArray(center.Select(x => x.Obj)));
        data.SetFaces((int)swSimpleFilletWhichFaces_e.swFullRoundFilletSet2, ComArgs.DispatchArray(side2.Select(x => x.Obj)));
        var api = (object?)d.FeatureManager.CreateFeature(data);
        d.ClearSelection2(true);
        var o = FeatureVerifier.Check(ctx, d, before, FilletSpec,
            (main, after, checks) => radius is double rr ? FilletIntent(main, rr, checks) : NoCheck(checks, "the sides aren't parallel planes: the radius is not checked"),
            q.KeepOnFailure, api);
        Rename(ctx, o.Main, q.Name);
        return FeatureVerifier.Result(ctx, d, o, resolved: info);
    }

    private static FeatureResult VariableFillet(SwContext ctx, IModelDoc2 d, FilletRequest q)
    {
        double r0 = Units.PositiveM(q.RadiiMm![0], "radiiMm[0]"), r1 = Units.PositiveM(q.RadiiMm[1], "radiiMm[1]");
        var info = new List<ResolvedInfo>();
        var edge = EntityResolver.Resolve(ctx, d, q.Edges![0], "edges[0]", EntityKind.Edge);
        info.Add(edge.Info("edges[0]"));
        var e = (IEdge)edge.Obj;
        var g = Topology.Describe(e);
        if (g.Closed) throw SwException.InvalidArgument("A variable fillet needs an edge with two ends, not a closed one.");
        // The API takes the radii in the edge's own order (start vertex first).
        var (start, end) = (g.Start, g.End);
        bool swap = q.StartAt is not null && Units.PointM(q.StartAt, "startAt") is var sa && sa.DistanceTo(end) < sa.DistanceTo(start);
        double rs = swap ? r1 : r0, re = swap ? r0 : r1;
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        if (!edge.Select(d, false, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {edge.Label}.");
        int options = (int)(swFeatureFilletOptions_e.swFeatureFilletKeepFeatures | swFeatureFilletOptions_e.swFeatureFilletVarRadiusType);
        if (q.Propagate) options |= (int)swFeatureFilletOptions_e.swFeatureFilletPropagate;
        object? api = d.FeatureManager.FeatureFillet3(options, Math.Min(rs, re), 0, 0, (int)swFeatureFilletType_e.swFeatureFilletType_VariableRadius, 0, 0,
            new[] { rs, re }, new double[2], new double[2], 0.0, 0.0, 0.0, 0.0);
        d.ClearSelection2(true);
        var o = FeatureVerifier.Check(ctx, d, before, VarFilletSpec, (main, after, checks) => VariableIntent(main, start, end, rs, re, checks), q.KeepOnFailure, api);
        Rename(ctx, o.Main, q.Name);
        return FeatureVerifier.Result(ctx, d, o, resolved: info);
    }

    /// <summary>
    /// Tier B for a variable fillet: the round is farther from the old sharp corner at the end with the bigger radius
    /// (for any edge angle the cut-back grows with the radius), so the radii went to the ends asked for.
    /// </summary>
    private static IEnumerable<string> VariableIntent(IFeature main, Vec3 start, Vec3 end, double rs, double re, List<string> checks)
    {
        var faces = ComArgs.Objects<IFace2>((object?)main.GetFaces());
        if (faces.Count == 0) { yield return "the fillet has no faces"; yield break; }
        double ds = faces.Min(f => Topology.DistanceToFace(f, start)), de = faces.Min(f => Topology.DistanceToFace(f, end));
        if (Math.Abs(rs - re) < 1e-9) { checks.Add("equal radii at both ends"); yield break; }
        if ((rs > re) != (ds > de))
            yield return $"the bigger radius is at the wrong end (corner distances {Units.Mm(ds)} mm at the r {Units.Mm(rs)} end, {Units.Mm(de)} mm at the r {Units.Mm(re)} end)";
        else checks.Add($"r {Units.Mm(rs)} mm at [{TopoQuery.Fmt(Units.Mm(start))}], r {Units.Mm(re)} mm at [{TopoQuery.Fmt(Units.Mm(end))}]");
    }

    private static IEnumerable<string> NoCheck(List<string> checks, string note)
    {
        checks.Add(note);
        yield break;
    }

    private static ISimpleFilletFeatureData2 NewFilletData(IModelDoc2 d, swSimpleFilletType_e type)
    {
        var data = (object?)d.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmFillet) as ISimpleFilletFeatureData2
                   ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS gave no fillet definition.");
        if (!data.Initialize((int)type)) throw new SwException(ErrorCodes.FeatureFailed, $"The fillet definition didn't take the type {type}.");
        return data;
    }

    // ------------------------------------------------------------------ split line

    public Task<FeatureResult> SplitLineAsync(SplitLineRequest q, CancellationToken ct = default)
    {
        string dir = (q.Direction ?? "both").Trim().ToLowerInvariant();
        if (dir is not ("both" or "normal" or "reverse")) throw SwException.InvalidArgument($"direction '{q.Direction}' is not supported.", "Use both, normal or reverse.");
        if (q.Faces is not { Count: > 0 }) throw SwException.InvalidArgument("'faces' must name the faces to split.");
        return session.RunAsync("feature_split_line", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var entry = ctx.DocEntry;
            var (sketchFeat, sketch) = ProfileSketch(ctx, d, q.Sketch);
            if (sketch.Is3D()) throw SwException.InvalidArgument("A split line projects a 2D sketch.");
            var info = new List<ResolvedInfo>();
            var faces = Faces(ctx, d, q.Faces, "faces", info);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            if (!sketchFeat.Select2(false, 4)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketchFeat.Name}'.");
            foreach (var f in faces)
                if (!f.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {f.Label}.");
            d.InsertSplitLineProject(dir != "both", dir == "normal");
            d.ClearSelection2(true);
            var o = FeatureVerifier.Check(ctx, d, before, SplitLineSpec, (main, after, checks) => SplitIntent(before, after, checks), q.KeepOnFailure);
            entry.LastSketch = null; // consumed
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, sketchFeat.Name, resolved: info);
        }, RunOptions.Write(q.Doc), ct);
    }

    /// <summary>Tier A: a split line adds faces and changes neither the volume nor the area.</summary>
    private static IEnumerable<string> SplitIntent(Snapshot before, Snapshot after, List<string> checks)
    {
        if (after.Faces <= before.Faces) yield return $"the face count stayed {after.Faces}: nothing was split (does the sketch project onto the faces?)";
        else checks.Add($"faces {before.Faces} → {after.Faces}");
        if (Math.Abs(after.Volume - before.Volume) > 1e-9 * Math.Max(before.Volume, 1e-12)) yield return "the volume changed";
        if (Math.Abs(after.Area - before.Area) > 1e-9 * Math.Max(before.Area, 1e-12)) yield return "the area changed";
        else checks.Add("volume and area unchanged");
    }
}
