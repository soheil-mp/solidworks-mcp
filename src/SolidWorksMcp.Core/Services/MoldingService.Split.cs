using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed record MoldSplitRequest
{
    public string? Doc { get; init; }
    /// <summary>+x, -x, +y (default), -y, +z or -z: the cavity opens this way.</summary>
    public string? Pull { get; init; }
    /// <summary>Shrinkage allowance in percent: the part is scaled by 1 + pct/100 first (0: no scale).</summary>
    public double ShrinkagePct { get; init; }
    /// <summary>Steel around the part on every side, mm.</summary>
    public double MarginMm { get; init; } = 10;
    public double MinDraftDeg { get; init; } = 0.5;
    public bool KeepOnFailure { get; init; }
}

public sealed class MoldSplitResult : ToolResult
{
    public required IReadOnlyList<FeatureRef> Features { get; init; }
    public required BodyRow Cavity { get; init; }
    public required BodyRow Core { get; init; }
    public double? ScaleFactor { get; init; }
    public required int PartingEdges { get; init; }
    public required double PartingLevelMm { get; init; }
    public required BoxMm Block { get; init; }
    public int UndraftedFaces { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

public sealed partial class MoldingService
{
    private sealed record Prepared(Snapshot Before, string Handle, Vec3 Pull, int Axis, string Plane, double Level, double[] Box, int Edges, double? Scale,
        double PartVolume, double[] ToolPointMm, int Undrafted);

    /// <summary>
    /// mold_split for flat parting lines: an optional shrinkage scale (InsertScale), the parting level from the edges between
    /// faces drafted towards and against the pull, a block around the part (marginMm of steel, a new body), a copy of the part
    /// subtracted from the block (the part stays), and a split of the block at the parting plane into cavity (pull side)
    /// and core. SOLIDWORKS' own tooling split (InsertMoldCoreCavitySolids) made nothing through the API after a parting line
    /// and parting surface (VERIFIED), so the same result is built from verified features. Verified: cavity + core = block −
    /// part, no overlaps, the cavity on the pull side; else everything is rolled back.
    /// </summary>
    public async Task<MoldSplitResult> SplitAsync(MoldSplitRequest q, CancellationToken ct = default)
    {
        if (q.ShrinkagePct is < 0 or > 10) throw SwException.InvalidArgument($"shrinkagePct must be 0–10 % (got {q.ShrinkagePct}).");
        double margin = Units.PositiveM(q.MarginMm, "marginMm");
        var prep = await session.RunAsync("mold_split", ctx => Prepare(ctx, q), RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(300) }, ct);
        var features = new List<string>();
        try
        {
            int axis = prep.Axis;
            double lo = prep.Box[axis], hi = prep.Box[axis + 3];
            double reach = Math.Max(hi - prep.Level, prep.Level - lo) + margin;
            string plane = prep.Plane;
            if (Math.Abs(prep.Level) > 1e-9)
            {
                var pl = await new ReferenceService(session).PlaneAsync(new RefPlaneRequest
                {
                    Doc = prep.Handle, Kind = "offset", Base = new EntityRef { Name = prep.Plane }, OffsetMm = Units.Mm(Math.Abs(prep.Level)),
                    Flip = prep.Level < 0, Name = "Parting Plane",
                }, ct);
                plane = pl.Feature.Name;
                features.Add(plane);
            }

            // The block: the part's outline plus the margin, extruded both ways from the parting plane as a new body.
            var sk = new SketchService(session);
            var start = await sk.StartAsync(prep.Handle, new EntityRef { Name = plane }, false, ct);
            var f = start.Frame!;
            var o = Vec3.From(f.OriginMm);
            var ax = Vec3.From(f.XAxis);
            var ay = Vec3.From(f.YAxis);
            var b = prep.Box;
            var corners = (from x in new[] { b[0], b[3] } from y in new[] { b[1], b[4] } from z in new[] { b[2], b[5] }
                           select Vec3.From(Units.Mm(new Vec3(x, y, z)))).ToList();
            double mm = Units.Mm(margin);
            double sx0 = corners.Min(p => (p - o).Dot(ax)) - mm, sx1 = corners.Max(p => (p - o).Dot(ax)) + mm;
            double sy0 = corners.Min(p => (p - o).Dot(ay)) - mm, sy1 = corners.Max(p => (p - o).Dot(ay)) + mm;
            await sk.AddAsync(prep.Handle, [new SketchEntity { Kind = "rectangle", Corners = [[sx0, sy0], [sx1, sy1]] }], ct);
            var block = await new FeatureService(session).ExtrudeAsync(new ExtrudeRequest
            {
                Doc = prep.Handle, End = "mid_plane", DepthMm = 2 * Units.Mm(reach), Merge = false, Name = "Mold Block",
            }, ct);
            features.Add(block.Feature.Name);

            // Points on the block's outer side face (outside the part): at the parting level, and in each half.
            int side = (axis + 1) % 3, third = (axis + 2) % 3;
            double[] OnBlock(double along)
            {
                var p = new double[3];
                p[axis] = Units.Mm(along);
                p[side] = Units.Mm(b[side + 3] + margin);
                p[third] = Units.Mm((b[third] + b[third + 3]) / 2);
                return p;
            }
            // The block split at the parting plane first: a split of a block with a void inside also counts the void as a
            // piece (VERIFIED), so the part comes out of each half afterwards.
            var bodies = new BodyService(session);
            var split = await bodies.SplitAsync(prep.Handle, new EntityRef { At = OnBlock(prep.Level) }, new EntityRef { Name = plane }, false, ct);
            features.Add(split.Feature.Name);

            // The part's shape out of each half it reaches: a copy of the part in place (the part itself stays), subtracted.
            // An indent cut makes nothing when the tool lies wholly inside the target (VERIFIED).
            foreach (double along in new[] { prep.Level + reach / 2, prep.Level - reach / 2 })
            {
                var (copy, copyBody, overlaps) = await session.RunAsync("mold_split", ctx => CopyPart(ctx, prep, OnBlock(along)),
                    RunOptions.Write(prep.Handle), ct);
                if (copy is null) continue; // the part doesn't reach into this half
                features.Add(copy);
                var cut = await bodies.CombineAsync(prep.Handle, new EntityRef { At = OnBlock(along) }, [new EntityRef { Name = copyBody }], "subtract", false, ct);
                features.Add(cut.Feature.Name);
            }

            double outline = (sx1 - sx0) * (sy1 - sy0) * 1e-6;
            return await session.RunAsync("mold_split", ctx => Finish(ctx, prep, features, outline, reach, margin),
                RunOptions.Write(prep.Handle) with { Timeout = TimeSpan.FromSeconds(300) }, ct);
        }
        catch (SwException) when (!q.KeepOnFailure)
        {
            try
            {
                await session.RunAsync("mold_split", ctx => { FeatureVerifier.Rollback(ctx, (IModelDoc2)ctx.Part(), prep.Before); return true; },
                    RunOptions.Write(prep.Handle), CancellationToken.None);
            }
            catch (SwException) { /* reported with the original error */ }
            throw;
        }
    }

    /// <summary>
    /// A copy of the part's body in place (InsertMoveCopyBody2, no motion, one copy) to subtract from the block half at
    /// <paramref name="onHalf"/>; nothing (null) when the part doesn't reach into that half.
    /// </summary>
    private static (string? Feature, string? Body, double Overlap) CopyPart(SwContext ctx, Prepared prep, double[] onHalf)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
        var names = solids.Select(BodyKit.SafeName).ToHashSet(StringComparer.Ordinal);
        var body = solids.OrderBy(b => Math.Abs((b.MassOfBody() is { Length: >= 4 } m ? m[3] : 0) - prep.PartVolume)).First();
        var at = Units.PointM(onHalf, "half");
        var half = solids.Where(b => !ReferenceEquals(b, body)).OrderBy(b => b.Faces().Min(f => Topology.DistanceToFace(f, at))).FirstOrDefault()
                   ?? throw new SwException(ErrorCodes.VerifyFailed, "The block half is missing after the split.");
        double overlap = PartFeatureService.OverlapOf(half, body);
        if (overlap <= 1e-12) return (null, null, 0);
        int before = solids.Count;
        var snap = Snapshot.Take(d);
        d.ClearSelection2(true);
        BodyKit.SelectAll(d, [new Resolved { Obj = body, Kind = EntityKind.Body, Label = "the part's body" }], 1);
        var f = (object?)d.FeatureManager.InsertMoveCopyBody2(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, 1) as IFeature;
        d.ClearSelection2(true);
        var now = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
        if (f is null || now.Count != before + 1)
        {
            if (f is not null) FeatureVerifier.Rollback(ctx, d, snap);
            throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS made no copy of the part's body in place.");
        }
        string copyName = now.Select(BodyKit.SafeName).FirstOrDefault(n => !names.Contains(n))
                          ?? throw new SwException(ErrorCodes.VerifyFailed, "The copy of the part's body has no name of its own.");
        return (f.Name, copyName, overlap);
    }

    private static Prepared Prepare(SwContext ctx, MoldSplitRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
        if (solids.Count != 1) throw SwException.InvalidArgument($"mold_split needs a part with one solid body (it has {solids.Count}).");
        var pull = Direction(q.Pull);
        int axis = Math.Abs(pull.X) > 0.99 ? 0 : Math.Abs(pull.Y) > 0.99 ? 1 : Math.Abs(pull.Z) > 0.99 ? 2
            : throw SwException.InvalidArgument("pull must be along x, y or z (+x, -y, …).");
        string planeAlias = axis switch { 0 => "right", 1 => "top", _ => "front" };
        var planeFeat = (IFeature)EntityResolver.Resolve(ctx, d, new EntityRef { Name = planeAlias }, "pull", EntityKind.Plane).Obj;

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        double? scale = null;
        try
        {
            if (q.ShrinkagePct > 0)
            {
                double s = 1 + q.ShrinkagePct / 100;
                double v0 = before.Volume;
                d.ClearSelection2(true);
                var sc = (object?)d.FeatureManager.InsertScale((short)swScaleType_e.swScaleAboutOrigin, true, s, s, s) as IFeature;
                if (sc is null)
                {
                    BodyKit.SelectAll(d, [new Resolved { Obj = solids[0], Kind = EntityKind.Body, Label = "the part's body" }], 0);
                    sc = (object?)d.FeatureManager.InsertScale((short)swScaleType_e.swScaleAboutOrigin, true, s, s, s) as IFeature
                         ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS made no Scale feature (shrinkage).");
                }
                d.ClearSelection2(true);
                double v1 = Snapshot.Take(d).Volume;
                if (Math.Abs(v1 - v0 * s * s * s) > 1e-6 * v1)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The scaled part is {Units.Mm3(v1)} mm³, expected {Units.Mm3(v0 * s * s * s)} mm³ (× {s}³).");
                scale = s;
            }
            var body = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false)[0];
            double partVolume = body.MassOfBody() is { Length: >= 4 } m ? m[3] : Snapshot.Take(d).Volume;

            // The parting line: the edges where a face drafted towards the pull meets one drafted against it.
            var cls = new Dictionary<string, int>();
            IFace2? biggest = null;
            foreach (var f in body.Faces())
            {
                var a = Samples(f).Select(x => DraftDeg(x.N, pull)).ToList();
                if (a.Count == 0) continue;
                cls[ctx.Entities.IdFor(d, f, 'F', "face")] = a.Min() >= q.MinDraftDeg ? 1 : a.Max() <= -q.MinDraftDeg ? -1 : 0;
                if (biggest is null || f.GetArea() > biggest.GetArea()) biggest = f;
            }
            int Cls(IFace2 f) => cls.TryGetValue(ctx.Entities.IdFor(d, f, 'F', "face"), out int c) ? c : 0;
            var edges = body.Edges().Where(e =>
            {
                var adj = ComArgs.Objects<IFace2>((object?)e.GetTwoAdjacentFaces2()).Where(x => x is not null).ToList();
                return adj.Count == 2 && Cls(adj[0]) * Cls(adj[1]) == -1;
            }).ToList();
            int undrafted = cls.Count(kv => kv.Value == 0);
            if (edges.Count == 0)
                throw SwException.InvalidArgument($"No edge separates faces drafted towards and against the pull ({undrafted} face(s) have no draft).",
                    "Draft the side walls first (feature_draft; model_check {kind:'draft'} lists the faces).");
            if (undrafted > 0)
                ctx.Warn($"{undrafted} face(s) have less than {q.MinDraftDeg}° of draft (model_check {{kind:'draft'}} lists them).");
            double sign = axis == 0 ? Math.Sign(pull.X) : axis == 1 ? Math.Sign(pull.Y) : Math.Sign(pull.Z);
            var levels = edges.Select(e => Topology.Describe(e)).SelectMany(g => new[] { g.Start, g.End }).Select(p => p.Dot(pull) * sign).ToList();
            if (levels.Max() - levels.Min() > 1e-6)
                throw new SwException(ErrorCodes.NotSupported, "The parting line is not flat (a stepped parting line).",
                    "mold_split splits at a flat parting plane; split stepped parts by hand.");
            return new Prepared(before, ctx.DocEntry.Handle, pull, axis, planeFeat.Name, levels.Average(), body.Box(), edges.Count, scale, partVolume,
                Units.Mm(Topology.InteriorPoint(biggest!)), undrafted);
        }
        catch (SwException) when (!q.KeepOnFailure)
        {
            FeatureVerifier.Rollback(ctx, d, before);
            throw;
        }
    }

    private static MoldSplitResult Finish(SwContext ctx, Prepared prep, List<string> features, double outline, double reach, double margin)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Select(x => BodyKit.Geom(ctx, d, x)).ToList();
        var partBody = solids.OrderBy(g => Math.Abs(g.Volume - prep.PartVolume)).First();
        if (Math.Abs(partBody.Volume - prep.PartVolume) > 1e-6 * prep.PartVolume)
            throw new SwException(ErrorCodes.VerifyFailed, "The part's body changed during the split.");
        var tools = solids.Where(g => !ReferenceEquals(g, partBody)).OrderByDescending(g => g.Center.Dot(prep.Pull)).ToList();
        if (tools.Count != 2) throw new SwException(ErrorCodes.VerifyFailed, $"The split made {tools.Count} mold bod(ies), not a cavity and a core.");
        var cavity = tools[0];
        var core = tools[1];
        var checks = new List<string>();
        double block = outline * 2 * reach;
        double sum = cavity.Volume + core.Volume;
        if (Math.Abs(sum - (block - prep.PartVolume)) > 1e-6 * block)
            throw new SwException(ErrorCodes.VerifyFailed, $"Cavity + core = {Units.Mm3(sum)} mm³, block − part = {Units.Mm3(block - prep.PartVolume)} mm³.");
        checks.Add($"cavity + core = block − part = {Units.Mm3(sum)} mm³");
        foreach (var (n1, b1, n2, b2) in new[] { ("cavity", cavity.Body, "core", core.Body), ("cavity", cavity.Body, "part", partBody.Body), ("core", core.Body, "part", partBody.Body) })
        {
            double ov = PartFeatureService.OverlapOf(b1, b2);
            if (ov > 1e-9 * block) throw new SwException(ErrorCodes.VerifyFailed, $"The {n1} and the {n2} overlap by {Units.Mm3(ov)} mm³.");
        }
        checks.Add("no overlap between cavity, core and part");
        if (cavity.Center.Dot(prep.Pull) <= core.Center.Dot(prep.Pull)) throw new SwException(ErrorCodes.VerifyFailed, "The cavity is not on the pull side.");
        checks.Add($"cavity on the pull side [{BodyKit.Dir(prep.Pull)}], core opposite; parting plane at {Units.Mm(prep.Level)} mm");
        if (prep.Scale is double s) checks.Add($"part scaled × {s} about the origin for shrinkage first");
        Rename(cavity.Body, "Cavity");
        Rename(core.Body, "Core");
        int axis = prep.Axis;
        var b = prep.Box;
        return new MoldSplitResult
        {
            Doc = ctx.Title(d),
            Features = features.Select(n => EntityResolver.FeatureByName(d, n)).Where(x => x is not null)
                .Select(x => new FeatureRef { Name = x!.Name, Type = x.GetTypeName2() }).ToList(),
            Cavity = BodyKit.Row(BodyKit.Geom(ctx, d, cavity.Body)),
            Core = BodyKit.Row(BodyKit.Geom(ctx, d, core.Body)),
            ScaleFactor = prep.Scale,
            PartingEdges = prep.Edges,
            PartingLevelMm = Units.Mm(prep.Level),
            Block = BoxMm.FromMeters(Enumerable.Range(0, 6).Select(i =>
                i % 3 == axis ? (i < 3 ? prep.Level - reach : prep.Level + reach) : (i < 3 ? b[i] - margin : b[i] + margin)).ToArray()),
            UndraftedFaces = prep.Undrafted,
            Checks = checks,
        };
    }

    private static void Rename(IBody2 b, string name)
    {
        try { b.Name = name; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* the names are a convenience */ }
    }
}
