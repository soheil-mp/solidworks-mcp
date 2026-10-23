using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of sheetmetal_bend: a sketched bend along a line across the sheet's face.</summary>
public sealed record SketchedBendRequest
{
    public string? Doc { get; init; }
    /// <summary>The bend line's two ends on the sheet's face, model mm.</summary>
    public double[]? FromMm { get; init; }
    public double[]? ToMm { get; init; }
    /// <summary>A point on the face on the side that stays put; default: the larger side.</summary>
    public double[]? FixedAtMm { get; init; }
    public double AngleDeg { get; init; } = 90;
    public double? RadiusMm { get; init; }
    /// <summary>centerline | material_inside (default) | material_outside | bend_outside.</summary>
    public string? Position { get; init; }
    public bool Flip { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class SheetMetalService
{
    private static readonly FeatureSpec BendSpec = new("sheetmetal_bend", ["SM3dBend", "SketchBend"], VolumeRule.Any, RequireFaces: false);

    public Task<SheetMetalResult> BendAsync(SketchedBendRequest q, CancellationToken ct = default)
    {
        var a = Units.PointM(q.FromMm, "fromMm");
        var b = Units.PointM(q.ToMm, "toMm");
        if (a.DistanceTo(b) < 1e-4) throw SwException.InvalidArgument("fromMm and toMm must be at least 0.1 mm apart.");
        if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg >= 180)
            throw SwException.InvalidArgument($"angleDeg must be between 0 and 180 (exclusive; got {q.AngleDeg}).");
        if (q.RadiusMm is double rr && (!double.IsFinite(rr) || rr < 0)) throw SwException.InvalidArgument($"radiusMm must be 0 or positive (got {rr}).");
        int position = BodyKit.Norm(q.Position) switch
        {
            "centerline" or "center" => 0,
            null or "material_inside" or "inside" => 1,
            "material_outside" or "outside" => 2,
            "bend_outside" => 3,
            _ => throw SwException.InvalidArgument($"Unknown position '{q.Position}'.", "Use centerline, material_inside (default), material_outside or bend_outside."),
        };
        Vec3? fixedAt = q.FixedAtMm is null ? null : Units.PointM(q.FixedAtMm, "fixedAtMm");
        return session.RunAsync("sheetmetal_bend", ctx => Bend(ctx, q, a, b, fixedAt, position), RunOptions.Write(q.Doc) with { Timeout = Long }, ct);
    }

    private static SheetMetalResult Bend(SwContext ctx, SketchedBendRequest q, Vec3 a, Vec3 b, Vec3? fixedAt, int position)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        var smFeature = SheetMetalFeature(d)
                        ?? throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a sheet-metal part (no SheetMetal feature).", "Start with sheetmetal_base_flange.");
        var sheet = ReadSheet(smFeature) ?? throw new SwException(ErrorCodes.Internal, $"Could not read the sheet-metal parameters of '{smFeature.Name}'.");
        double t = Units.M(sheet.ThicknessMm);
        if (FlatPatterns(d).Any(f => !f.IsSuppressed()))
            throw new SwException(ErrorCodes.InvalidArgument, $"'{ctx.Title(d)}' is flattened.", "Fold it first with sheetmetal_flatten {flat:false}.");

        // The planar sheet face the line lies on.
        var mid = (a + b) / 2;
        var rf = EntityResolver.Resolve(ctx, d, new EntityRef { Type = "face", At = Units.Mm(mid) }, "fromMm/toMm", EntityKind.Face);
        var face = (IFace2)rf.Obj;
        var fg = Topology.Describe(face);
        if (fg is not { Surface: "plane", Normal: Vec3 n })
            throw SwException.InvalidArgument($"The bend line's midpoint is on {rf.Label}, which is not planar; put the line on the sheet's flat face.");
        var u = (b - a).Normalized();
        foreach (var (p, arg) in new[] { (a, "fromMm"), (b, "toMm") })
            if (Math.Abs(n.Dot(p - mid)) > BodyKit.PosTol)
                throw SwException.InvalidArgument($"{arg} is off the plane of {rf.Label}; both ends must lie on the same face.");
        // The side that stays: given, or the side whose part of the face reaches farther from the line.
        var side = n.Cross(u).Normalized();
        double reachPlus = 0, reachMinus = 0;
        foreach (var e in face.Edges())
        {
            var g = Topology.Describe(e);
            foreach (var p in new[] { g.Start, g.End, g.Mid })
            {
                double s = (p - mid).Dot(side);
                reachPlus = Math.Max(reachPlus, s);
                reachMinus = Math.Max(reachMinus, -s);
            }
        }
        Vec3 fix;
        if (fixedAt is Vec3 f0)
        {
            if (Math.Abs(n.Dot(f0 - mid)) > BodyKit.PosTol) throw SwException.InvalidArgument("fixedAtMm must lie on the same face as the bend line.");
            if (Math.Abs((f0 - mid).Dot(side)) < 1e-4) throw SwException.InvalidArgument("fixedAtMm is on the bend line; put it on the side that should stay.");
            fix = f0;
        }
        else
        {
            double sgn = reachPlus >= reachMinus ? 1 : -1;
            fix = mid + side * (sgn * Math.Max(reachPlus, reachMinus) / 2); // halfway across the larger side
        }
        double moving = (fix - mid).Dot(side) > 0 ? reachMinus : reachPlus;
        if (moving < 1e-4) throw SwException.InvalidArgument("The bend line is at the face's edge: nothing is left on the moving side.");

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        // The bend sketch: one line on the face.
        d.ClearSelection2(true);
        if (!rf.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {rf.Label} to sketch the bend line on.");
        var sm = d.SketchManager;
        sm.InsertSketch(true);
        if (sm.ActiveSketch is not ISketch sketch)
            throw new SwException(ErrorCodes.FeatureFailed, $"No sketch opened on {rf.Label}.");
        string sketchName = ((IFeature)sketch).Name;
        try
        {
            var m2s = new Xform(ComArgs.Doubles((object?)sketch.ModelToSketchTransform.ArrayData));
            var sa = m2s.ApplyPoint(a);
            var sb = m2s.ApplyPoint(b);
            if (sm.CreateLine(sa.X, sa.Y, 0, sb.X, sb.Y, 0) is null)
                throw new SwException(ErrorCodes.FeatureFailed, "The bend line could not be drawn.");
        }
        finally
        {
            sm.InsertSketch(true);
        }
        d.ClearSelection2(true);
        var sketchFeat = EntityResolver.FeatureByName(d, sketchName)
                         ?? throw new SwException(ErrorCodes.Internal, $"The bend sketch '{sketchName}' could not be found.");
        bool selected = sketchFeat.Select2(false, 0) &&
                        d.Extension.SelectByRay(fix.X + n.X * 0.005, fix.Y + n.Y * 0.005, fix.Z + n.Z * 0.005, -n.X, -n.Y, -n.Z, 0.0005,
                            (int)swSelectType_e.swSelFACES, true, 0, 0);
        if (!selected || d.Sel().GetSelectedObjectCount2(-1) != 2)
        {
            BodyKit.CleanUp(ctx, d, before);
            throw new SwException(ErrorCodes.FeatureFailed, "The bend sketch and the fixed side could not be selected.", "Pass fixedAtMm on the sheet's face, away from its edges.");
        }
        bool useDefault = q.RadiusMm is null;
        double r = q.RadiusMm is double rr ? Units.M(rr) : Units.M(sheet.BendRadiusMm);
        object? api = d.FeatureManager.InsertSheetMetal3dBend(Units.Rad(q.AngleDeg), useDefault, useDefault ? 0 : r, q.Flip, (short)position, null);
        d.ClearSelection2(true);

        var o = BodyKit.Verify(ctx, d, before, BendSpec, (_, after, checks) => BendIntent(before, after, n, moving, Units.Rad(q.AngleDeg), t, r, checks), q.KeepOnFailure, api,
            "The line must cross the sheet's face from edge to edge (or end on its edges), away from other bends; try the other fixed side.", guessedType: true);
        return Result(ctx, d, o, ReadSheet(SheetMetalFeature(d)), sketchName, [rf.Info("face")]);
    }

    /// <summary>
    /// Tier B of a sketched bend: the volume barely changes (bending keeps the material, within the bend allowance) and
    /// the moving side lifts off the face's plane by about its length × sin(angle).
    /// </summary>
    private static List<string> BendIntent(Snapshot before, Snapshot after, Vec3 n, double moving, double angle, double t, double r, List<string> checks)
    {
        var problems = new List<string>();
        double dv = after.Volume - before.Volume;
        if (Math.Abs(dv) > Math.Max(0.05 * before.Volume, 1e-12))
            problems.Add($"the volume changed by {Units.Mm3(dv)} mm³ (a bend keeps the material)");
        else checks.Add($"volume kept (ΔV {Units.Mm3(dv)} mm³)");
        if (before.Box is { Length: 6 } b0 && after.Box is { Length: 6 } b1)
        {
            double up = Math.Max(Reach(b1, n) - Reach(b0, n), Reach(b1, -n) - Reach(b0, -n));
            double want = moving * Math.Sin(angle);
            if (up < want - (r + 2 * t) - BodyKit.PosTol || up > moving + r + 2 * t + BodyKit.PosTol)
                problems.Add($"the part reaches {Units.Mm(up)} mm farther off the face, expected about {Units.Mm(want)} mm");
            else checks.Add($"the bent side reaches {Units.Mm(up)} mm off the face (≈ {Units.Mm(moving)} mm x sin {Units.Deg(angle)}°)");
        }
        return problems;
    }
}
