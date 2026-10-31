using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>feature_pattern_curve options.</summary>
public sealed record CurvePatternRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<string>? Features { get; init; }
    /// <summary>An edge or a sketch segment the instances follow.</summary>
    public EntityRef? Curve { get; init; }
    /// <summary>Instances, the seed included.</summary>
    public int Count { get; init; }
    /// <summary>Distance between instances along the curve; null: spread equally over the whole curve.</summary>
    public double? SpacingMm { get; init; }
    public bool Reverse { get; init; }
    /// <summary>tangent (default: instances turn with the curve) | seed (they keep the seed's orientation).</summary>
    public string? Align { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_pattern_fill options: holes (or seed features) filling a face.</summary>
public sealed record FillPatternRequest
{
    public string? Doc { get; init; }
    /// <summary>The flat face to fill.</summary>
    public EntityRef? Face { get; init; }
    /// <summary>perforation (default: staggered rows) | square | circular | polygon.</summary>
    public string? Layout { get; init; }
    public double SpacingMm { get; init; }
    /// <summary>Distance kept from the face's boundary.</summary>
    public double? MarginMm { get; init; }
    /// <summary>The seed: a round hole of this diameter, cut through at the face's interior point, or <see cref="Features"/>.</summary>
    public double? HoleDiameterMm { get; init; }
    public IReadOnlyList<string>? Features { get; init; }
    /// <summary>perforation and square: a straight edge giving the rows' direction (default: an edge of the face).</summary>
    public EntityRef? DirectionRef { get; init; }
    /// <summary>perforation: the stagger angle between rows (default 60°).</summary>
    public double? StaggerDeg { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class PartFeatureService
{
    private static readonly FeatureSpec CurveSpec = new("feature_pattern_curve", ["CurvePattern", "CrvPattern"], VolumeRule.Change);
    private static readonly FeatureSpec FillSpec = new("feature_pattern_fill", ["APattern", "FillPattern"], VolumeRule.Change); // a fill pattern is an "APattern" (VERIFIED)

    // ------------------------------------------------------------------ curve-driven

    public Task<FeatureResult> CurvePatternAsync(CurvePatternRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_pattern_curve", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (q.Count < 2 || q.Count > 1000) throw SwException.InvalidArgument($"count must be 2..1000, the seed included (got {q.Count}).");
            double? spacing = q.SpacingMm is double sm ? Units.PositiveM(sm, "spacingMm") : null;
            string align = (q.Align ?? "tangent").Trim().ToLowerInvariant();
            if (align is not ("tangent" or "seed")) throw SwException.InvalidArgument($"align '{q.Align}' is not supported.", "Use tangent or seed.");
            NoOpenSketch(d);
            var seeds = Seeds(d, q.Features, "features");
            var curve = EntityResolver.Resolve(ctx, d, q.Curve, "curve", EntityKind.Edge, EntityKind.SketchSegment);
            var seedAxes = Axes(seeds.SelectMany(FeatureFaces));
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            int k = q.Count - 1;
            const string hint = "The instances must land on the body (cuts) or touch it (bosses): check count, spacing and reverse.";
            // SOLIDWORKS picks the curve's sense itself; a sense that produced nothing usable is retried reversed once.
            for (int attempt = 0; ; attempt++)
            {
                bool reverse = q.Reverse ^ (attempt == 1);
                // The definition takes the selection (seeds mark 4, the curve mark 1), as the API's examples do.
                d.ClearSelection2(true);
                SelectSeeds(d, seeds, 4);
                if (!curve.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {curve.Label}.");
                var data = (object?)d.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmCurvePattern) as ICurveDrivenPatternFeatureData
                           ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS gave no curve-driven pattern definition.");
                data.D1InstanceCount = q.Count;
                data.D1IsEqualSpaced = spacing is null;
                data.D1Spacing = spacing ?? CurveLength(curve.Obj) / (q.Count - 1);
                data.D1ReverseDirection = reverse;
                data.D1CurveMethod = (int)swCurveDrivenPatternCurveMethod_e.swCurvePatternTransformCurve;
                data.D1AlignmentMethod = align == "seed"
                    ? (int)swCurveDrivenPatternAlignment_e.swCurvePatternAlignToSeed
                    : (int)swCurveDrivenPatternAlignment_e.swCurvePatternTangentToCurve;
                var api = (object?)d.FeatureManager.CreateFeature(data);
                d.ClearSelection2(true);
                try
                {
                    var o = Verify(ctx, d, before, CurveSpec, (main, _, checks) => InstanceCount(main, seedAxes, k, checks), q.KeepOnFailure, api, hint);
                    if (reverse != q.Reverse) o = o with { Checks = [.. o.Checks, "the curve's other sense (the asked one put nothing on the body)"] };
                    Rename(ctx, o.Main, q.Name);
                    return FeatureVerifier.Result(ctx, d, o, resolved: [curve.Info("curve")]);
                }
                catch (SwException ex) when (attempt == 0 && !q.KeepOnFailure && !NewFeatures(d, before) &&
                                             ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed)
                {
                    // rolled back by the verifier: try the other sense
                }
            }
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);

    // ------------------------------------------------------------------ fill

    /// <summary>
    /// The fill pattern's own seed cut always comes out at its 8 mm default, whatever Diameter says, also when set
    /// afterwards (VERIFIED), so holeDiameterMm cuts a real seed hole through the face at an interior point (sketch circle,
    /// through-all cut) and the fill copies that feature. A fill that fails takes the seed with it.
    /// </summary>
    public async Task<FeatureResult> FillPatternAsync(FillPatternRequest q, CancellationToken ct = default)
    {
        if ((q.HoleDiameterMm is null) == (q.Features is not { Count: > 0 }))
            throw SwException.InvalidArgument("Give either holeDiameterMm (round holes cut through) or features (seeds to copy).");
        if (q.HoleDiameterMm is not double dia) return await FillAsync(q, null, null, ct);
        double r = Units.PositiveM(dia, "holeDiameterMm") / 2;
        Units.PositiveM(q.SpacingMm, "spacingMm");
        var (handle, at, away) = await session.RunAsync("feature_pattern_fill", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            NoOpenSketch(d);
            var face = EntityResolver.Resolve(ctx, d, q.Face, "face", EntityKind.Face);
            var f = (IFace2)face.Obj;
            if (Topology.Describe(f) is not { Surface: "plane", Normal: Vec3 n })
                throw SwException.InvalidArgument($"{face.Label} is not flat; a fill pattern fills a flat face.");
            var p = Topology.InteriorPoint(f);
            // A second point on the face, clear of the seed hole, to find the face again afterwards.
            var u = (Math.Abs(n.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0)).Cross(n).Normalized();
            var q2 = Enumerable.Range(1, 40).Select(k => p + u * (r + k * 0.001)).Concat(Enumerable.Range(1, 40).Select(k => p - u * (r + k * 0.001)))
                         .FirstOrDefault(x => Topology.DistanceToFace(f, x) < 1e-9);
            if (q2 == default) throw SwException.InvalidArgument($"{face.Label} is too small for a Ø{dia} mm seed hole.");
            return (ctx.DocEntry.Handle, Units.Mm(p), Units.Mm(q2));
        }, RunOptions.Write(q.Doc), ct);
        var sk = new SketchService(session);
        await sk.StartAsync(handle, new EntityRef { Type = "face", At = at }, false, ct);
        await sk.AddAsync(handle, [new SketchEntity { Kind = "circle", Center = at, DiameterMm = dia }], ct);
        string seed = (await new FeatureService(session).ExtrudeAsync(new ExtrudeRequest { Doc = handle, Cut = true, End = "through_all" }, ct)).Feature.Name;
        try
        {
            return await FillAsync(q with { Doc = handle, Face = new EntityRef { Type = "face", At = away }, Features = [seed], HoleDiameterMm = null }, r, seed, ct);
        }
        catch (SwException)
        {
            try { await new FeatureManagementService(session).DeleteAsync(handle, [seed], deleteChildren: false, deleteAbsorbed: true, ct: CancellationToken.None); }
            catch (SwException) { /* reported with the original error */ }
            throw;
        }
    }

    private Task<FeatureResult> FillAsync(FillPatternRequest q, double? holeR, string? seedHole, CancellationToken ct) =>
        session.RunAsync("feature_pattern_fill", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            double spacing = Units.PositiveM(q.SpacingMm, "spacingMm");
            double margin = q.MarginMm is double mm ? (mm >= 0 ? Units.M(mm) : throw SwException.InvalidArgument("marginMm must be ≥ 0.")) : 0;
            int layout = (q.Layout ?? "perforation").Trim().ToLowerInvariant() switch
            {
                "perforation" => (int)swPatternLayoutType_e.swPatternLayoutPerforation,
                "square" => (int)swPatternLayoutType_e.swPatternLayoutSquare,
                "circular" => (int)swPatternLayoutType_e.swPatternLayoutCircular,
                "polygon" => (int)swPatternLayoutType_e.swPatternLayoutPolygon,
                _ => throw SwException.InvalidArgument($"layout '{q.Layout}' is not supported.", "Use perforation, square, circular or polygon."),
            };
            NoOpenSketch(d);
            var face = EntityResolver.Resolve(ctx, d, q.Face, "face", EntityKind.Face);
            var fg = Topology.Describe((IFace2)face.Obj);
            if (fg.Surface != "plane") throw SwException.InvalidArgument($"{face.Label} is a {fg.Surface}; a fill pattern fills a flat face.");
            var seeds = q.Features is { Count: > 0 } ? Seeds(d, q.Features, "features") : [];
            // Rows run along a straight edge: the one given, else the face's longest.
            object? direction = null;
            if (layout is (int)swPatternLayoutType_e.swPatternLayoutPerforation or (int)swPatternLayoutType_e.swPatternLayoutSquare)
            {
                direction = q.DirectionRef is not null
                    ? EntityResolver.Resolve(ctx, d, q.DirectionRef, "directionRef", EntityKind.Edge).Obj
                    : ((IFace2)face.Obj).Edges().Where(e => Topology.Describe(e).Curve == "line").OrderByDescending(e => Topology.Describe(e).Length).FirstOrDefault()
                      ?? throw SwException.InvalidArgument($"{face.Label} has no straight edge for the rows' direction.", "Pass directionRef (a straight edge).");
            }
            // The definition takes the selection (seeds mark 4, the rows' edge mark 1, the boundary face mark 16384), as the
            // API's example does.
            d.ClearSelection2(true);
            if (seeds.Count > 0) SelectSeeds(d, seeds, 4);
            if (direction is not null && !((IEntity)direction).Select4(true, MarkData(d, 1)))
                throw new SwException(ErrorCodes.FeatureFailed, "Could not select the rows' edge.");
            if (!face.Select(d, true, 16384)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {face.Label}.");
            var data = (object?)d.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmFillPattern) as IFillPatternFeatureData
                       ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS gave no fill pattern definition.");
            data.PatternLayoutType = layout;
            data.LayoutSpacingType = (int)swPatternLayoutSpacingType_e.swPatternLayoutTargetSpacing;
            data.InstanceSpacing = spacing;
            data.LoopSpacing = spacing;
            data.Margins = margin;
            if (layout == (int)swPatternLayoutType_e.swPatternLayoutPerforation) data.StaggerAngle = Units.Rad(q.StaggerDeg ?? 60);
            data.FeaturesToPatternType = (int)swFeaturesToPatternType_e.swFeaturesToPatternSelectedFeatures;
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var api = (object?)d.FeatureManager.CreateFeature(data);
            d.ClearSelection2(true);
            var o = Verify(ctx, d, before, FillSpec, (main, after, checks) => FillIntent(main, holeR, before, after, checks), q.KeepOnFailure, api,
                "The holes must fit inside the face less its margin: check spacing, margin and size.");
            if (seedHole is not null) o = o with { Checks = [.. o.Checks, $"plus the seed hole {seedHole} at the face's interior point"] };
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: [face.Info("face")]);
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);

    /// <summary>
    /// Tier A for round-hole fills: the holes are counted by their axes and each removes the same volume (the volume
    /// change is a whole number of holes); the count is reported.
    /// </summary>
    private static IEnumerable<string> FillIntent(IFeature main, double? holeR, Snapshot before, Snapshot after, List<string> checks)
    {
        var faces = FeatureFaces(main);
        if (holeR is not double r) { checks.Add($"{faces.Count} face(s) in the pattern"); yield break; }
        var axes = Axes(faces.Where(f => Topology.Describe(f) is { Surface: "cylinder", Radius: double fr } && Math.Abs(fr - r) < 1e-7));
        if (axes.Count == 0) { yield return $"no round holes of Ø{Units.Mm(2 * r)} mm in the pattern"; yield break; }
        double removed = before.Volume - after.Volume, each = removed / axes.Count;
        if (removed <= 0) { yield return "the holes removed no material"; yield break; }
        checks.Add($"{axes.Count} hole(s) of Ø{Units.Mm(2 * r)} mm, {Units.Mm3(each)} mm³ each");
    }

    /// <summary>Tier B for curve patterns of round seeds: (count − 1) × the seed's axes are new.</summary>
    private static IEnumerable<string> InstanceCount(IFeature main, List<(Vec3 P, Vec3 U)> seedAxes, int k, List<string> checks)
    {
        if (seedAxes.Count == 0) { checks.Add("the seed has no round faces: instances not counted"); yield break; }
        var axes = Axes(FeatureFaces(main));
        if (axes.Count != seedAxes.Count * k) { yield return $"{axes.Count} instance axes, not {seedAxes.Count * k} ({k} instance(s) of the seed's {seedAxes.Count})"; yield break; }
        checks.Add($"{k} instance(s): axes through {string.Join("; ", axes.Take(6).Select(a => $"[{TopoQuery.Fmt(Units.Mm(a.P))}]"))}");
    }

    /// <summary>A selection mark for IEntity.Select4.</summary>
    private static SelectData MarkData(IModelDoc2 d, int mark)
    {
        var sd = d.Sel().CreateSelectData();
        sd.Mark = mark;
        return sd;
    }

    /// <summary>The length of an edge or a sketch segment, m.</summary>
    private static double CurveLength(object curve) => curve switch
    {
        IEdge e => Topology.Describe(e).Length,
        ISketchSegment s => s.GetLength(),
        _ => 0,
    };

    /// <summary>The distinct axis lines of the cylindrical faces (a point on each, its direction), in model space.</summary>
    private static List<(Vec3 P, Vec3 U)> Axes(IEnumerable<IFace2> faces)
    {
        var list = new List<(Vec3 P, Vec3 U)>();
        foreach (var f in faces)
        {
            if (Topology.Describe(f) is not { Surface: "cylinder", Axis: Vec3 u, AxisOrigin: Vec3 o }) continue;
            // The axis point nearest the model origin: one point per line, whichever face of it reports.
            var p = o - u * o.Dot(u);
            if (!list.Any(a => a.U.IsParallelTo(u, 1e-6) && ((p - a.P) - a.U * (p - a.P).Dot(a.U)).Length < 1e-7)) list.Add((p, u));
        }
        return list;
    }
}
