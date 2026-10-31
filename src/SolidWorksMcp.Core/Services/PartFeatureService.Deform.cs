using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed record DeformRequest
{
    public string? Doc { get; init; }
    /// <summary>wrap | dome | indent.</summary>
    public string Kind { get; init; } = "";
    /// <summary>wrap: the closed sketch to wrap.</summary>
    public string? Sketch { get; init; }
    /// <summary>wrap: the faces it wraps onto; dome: the face that gets the dome.</summary>
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>wrap: emboss | deboss | scribe.</summary>
    public string? Type { get; init; }
    /// <summary>wrap: emboss height or deboss depth; dome: height; indent: wall thickness.</summary>
    public double? DepthMm { get; init; }
    public bool Reverse { get; init; }
    /// <summary>dome: an elliptical dome (height = the semi-axis) instead of a spherical one.</summary>
    public bool Elliptical { get; init; }
    /// <summary>indent: the body that gets indented.</summary>
    public EntityRef? Body { get; init; }
    /// <summary>indent: a point on the tool body (the region that indents).</summary>
    public EntityRef? Tool { get; init; }
    public double? ClearanceMm { get; init; }
    /// <summary>indent: cut the tool's shape out of the body instead of forming a wall around it.</summary>
    public bool Cut { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class PartFeatureService
{
    private static readonly FeatureSpec WrapSpec = new("feature_deform", ["Emboss", "Wrap", "WrapSketch", "Deboss", "Engrave", "Scribe"], VolumeRule.Any);
    private static readonly FeatureSpec DomeSpec = new("feature_deform", ["Dome"], VolumeRule.Change);
    private static readonly FeatureSpec IndentSpec = new("feature_deform", ["Punch", "Indent"], VolumeRule.Change);

    public Task<FeatureResult> DeformAsync(DeformRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_deform", ctx => (q.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "wrap" => Wrap(ctx, q),
            "dome" => Dome(ctx, q),
            "indent" => Indent(ctx, q),
            var k => throw SwException.InvalidArgument($"kind must be wrap, dome or indent (got '{k}')."),
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>
    /// InsertWrapFeature2: faces mark 1, sketch mark 4. Tier B: emboss adds and deboss removes material, at most the faces'
    /// area × depth; scribe adds faces only. The live tests check exact volumes on a plane and on a cylinder.
    /// </summary>
    private static FeatureResult Wrap(SwContext ctx, DeformRequest q)
    {
        var d = (IModelDoc2)ctx.Part();
        NoOpenSketch(d);
        int type = (q.Type ?? "emboss").Trim().ToLowerInvariant() switch
        {
            "emboss" => (int)swWrapSketchType_e.swWrapSketchType_Emboss,
            "deboss" or "engrave" => (int)swWrapSketchType_e.swWrapSketchType_Engrave,
            "scribe" => (int)swWrapSketchType_e.swWrapSketchType_Scribe,
            var other => throw SwException.InvalidArgument($"type must be emboss, deboss or scribe (got '{other}')."),
        };
        double t = type == (int)swWrapSketchType_e.swWrapSketchType_Scribe ? 0.00001 : Units.PositiveM(q.DepthMm ?? 0, "depthMm");
        var sketch = Sketch(d, q.Sketch);
        var faces = EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face);
        double faceArea = faces.Sum(f => ((IFace2)f.Obj).GetArea());

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        object? api = null;
        foreach (bool rev in new[] { q.Reverse, !q.Reverse })
        {
            d.ClearSelection2(true);
            foreach (var f in faces) if (!f.Select(d, true, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {f.Label}.");
            if (!sketch.Select2(true, 4)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketch.Name}'.");
            api = (object?)d.FeatureManager.InsertWrapFeature2(type, t, rev, (int)swWrapMethods_e.swWrapMethods_Analytical, 5);
            if (api is not null || type == (int)swWrapSketchType_e.swWrapSketchType_Scribe) break;
        }
        d.ClearSelection2(true);
        var o = Verify(ctx, d, before, WrapSpec, (_, after, checks) =>
        {
            double dv = after.Volume - before.Volume;
            var problems = new List<string>();
            if (type == (int)swWrapSketchType_e.swWrapSketchType_Scribe)
            {
                if (Math.Abs(dv) > 1e-12) problems.Add($"a scribe changed the volume by {Units.Mm3(dv)} mm³");
                else if (after.Faces <= before.Faces) problems.Add("the scribe split no faces");
                else checks.Add($"scribe: {after.Faces - before.Faces} more face(s), volume unchanged");
                return problems;
            }
            bool emboss = type == (int)swWrapSketchType_e.swWrapSketchType_Emboss;
            if (emboss ? dv <= 0 : dv >= 0) problems.Add($"{(emboss ? "an emboss" : "a deboss")} changed the volume by {Units.Mm3(dv)} mm³");
            else if (Math.Abs(dv) > faceArea * t * 1.05) problems.Add($"|ΔV| {Units.Mm3(Math.Abs(dv))} mm³ exceeds the faces' area × depth");
            else checks.Add($"{(emboss ? "embossed" : "debossed")} {Units.Mm3(Math.Abs(dv))} mm³ at {Units.Mm(t)} mm");
            return problems;
        }, q.KeepOnFailure, api, "Wrap a closed 2D sketch on a plane tangent to (or parallel with) the faces; planar or cylindrical/conical faces.");
        Rename(ctx, o.Main, q.Name);
        if (sketch.Name is { } s) ctx.DocEntry.LastSketch = null;
        return FeatureVerifier.Result(ctx, d, o, sketch.Name, null, faces.Select(f => f.Info("faces")).ToList());
    }

    /// <summary>
    /// InsertDome on one face (mark 1). Tier B: a convex dome adds material (concave with reverse removes it); on a circular
    /// planar face a spherical dome adds exactly the cap πh(3a² + h²)/6.
    /// </summary>
    private static FeatureResult Dome(SwContext ctx, DeformRequest q)
    {
        var d = (IModelDoc2)ctx.Part();
        NoOpenSketch(d);
        double h = Units.PositiveM(q.DepthMm ?? 0, "depthMm");
        var face = EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face) is [var one] ? one
            : throw SwException.InvalidArgument("kind 'dome' takes one face in 'faces'.");
        var f = (IFace2)face.Obj;
        var g = Topology.Describe(f);
        if (g.Surface != "plane") throw SwException.InvalidArgument($"{face.Label} is a {g.Surface} face; a dome goes on a planar face.");
        // A circular face (one circular edge): the spherical cap's volume.
        var loops = ComArgs.Objects<ILoop2>((object?)f.GetLoops());
        double? cap = null;
        if (!q.Elliptical && loops.Count == 1 && ComArgs.Objects<IEdge>((object?)loops[0].GetEdges()) is [var e] && Topology.Describe(e) is { Curve: "circle", Radius: double a })
            cap = Math.PI * h * (3 * a * a + h * h) / 6;

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        if (!face.Select(d, false, 1)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {face.Label}.");
        d.InsertDome(h, q.Reverse, q.Elliptical);
        d.ClearSelection2(true);
        var o = Verify(ctx, d, before, DomeSpec, (_, after, checks) =>
        {
            double dv = after.Volume - before.Volume;
            if (q.Reverse ? dv >= 0 : dv <= 0) return [$"the {(q.Reverse ? "concave" : "convex")} dome changed the volume by {Units.Mm3(dv)} mm³"];
            if (cap is double c && Math.Abs(Math.Abs(dv) - c) > 1e-4 * c)
                return [$"|ΔV| is {Units.Mm3(Math.Abs(dv))} mm³, the spherical cap {Units.Mm3(c)} mm³"];
            checks.Add(cap is double c2 ? $"|ΔV| = the spherical cap πh(3a²+h²)/6 = {Units.Mm3(c2)} mm³" : $"ΔV {Units.Mm3(dv)} mm³");
            return [];
        }, q.KeepOnFailure, true, "A dome needs a planar face; the height must fit the face.");
        Rename(ctx, o.Main, q.Name);
        return FeatureVerifier.Result(ctx, d, o, resolved: [face.Info("faces")]);
    }

    /// <summary>
    /// InsertIndent: target body mark 1, tool body region (a face of the tool at the point) mark 4. Tier B: a cut takes away
    /// the tool's overlap (clearance 0: exactly the intersection volume); an indent changes the target's volume.
    /// </summary>
    private static FeatureResult Indent(SwContext ctx, DeformRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        NoOpenSketch(d);
        var target = BodyKit.ResolveBody(ctx, d, q.Body, "body", BodyKind.Solid);
        var tool = EntityResolver.Resolve(ctx, d, q.Tool, "tool", EntityKind.Face);
        var toolBody = (IBody2)(object)((IFace2)tool.Obj).GetBody();
        var tb = (IBody2)target.Obj;
        if (ReferenceEquals(toolBody, tb) || BodyKit.SafeName(toolBody) == BodyKit.SafeName(tb))
            throw SwException.InvalidArgument("The tool point is on the target body; pick a point on the other body.");
        // A cut ignores the wall, but SOLIDWORKS makes nothing with a zero one (VERIFIED): 0.1 mm then.
        double t = q.Cut ? 1e-4 : Units.PositiveM(q.DepthMm ?? 0, "depthMm");
        double clr = q.ClearanceMm is double c ? (c >= 0 && double.IsFinite(c) ? Units.M(c) : throw SwException.InvalidArgument("clearanceMm must be ≥ 0.")) : 0;
        double overlap = OverlapOf(tb, toolBody);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        BodyKit.SelectAll(d, [target], 1);
        if (!tool.Select(d, true, 4)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {tool.Label}.");
        var api = (object?)d.FeatureManager.InsertIndent(t, clr, false, true, q.Cut, false);
        d.ClearSelection2(true);
        var o = Verify(ctx, d, before, IndentSpec, (_, after, checks) =>
        {
            // The target body is renamed after the feature; the tool body stays as it was, so the part's change is the target's.
            double dv = after.Volume - before.Volume;
            if (q.Cut && clr == 0 && overlap > 0 && Math.Abs(-dv - overlap) > 1e-4 * overlap)
                return [$"the cut removed {Units.Mm3(-dv)} mm³; the tool overlaps {Units.Mm3(overlap)} mm³"];
            if (Math.Abs(dv) < 1e-12) return ["the target's volume didn't change"];
            checks.Add(q.Cut && clr == 0 ? $"the cut removed the overlap, {Units.Mm3(overlap)} mm³" : $"the target's volume changed {Units.Mm3(dv)} mm³");
            return [];
        }, q.KeepOnFailure, api, "The tool body must overlap the target body (two bodies in one part).");
        Rename(ctx, o.Main, q.Name);
        return FeatureVerifier.Result(ctx, d, o, resolved: [target.Info("body"), tool.Info("tool")]);
    }

    /// <summary>The volume two solid bodies share (a temporary intersect of copies).</summary>
    internal static double OverlapOf(IBody2 a, IBody2 b)
    {
        try
        {
            var ca = (IBody2)(object)a.Copy();
            var cb = (IBody2)(object)b.Copy();
            var res = ComArgs.Objects<IBody2>((object?)ca.Operations2((int)swBodyOperationType_e.SWBODYINTERSECT, cb, out int err));
            return err == 0 ? res.Sum(x => x.MassOfBody() is { Length: >= 4 } m ? m[3] : 0) : 0;
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return 0; }
    }

    private static IFeature Sketch(IModelDoc2 d, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw SwException.InvalidArgument("'sketch' is required (the closed sketch to wrap).");
        var f = EntityResolver.FeatureByName(d, name.Trim()) ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.");
        if (f.GetTypeName2() != "ProfileFeature") throw SwException.InvalidArgument($"'{f.Name}' is a {f.GetTypeName2()}, not a 2D sketch.");
        return f;
    }
}
