using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== requests

/// <summary>feature_sweep options.</summary>
public sealed record SweepRequest
{
    public string? Doc { get; init; }
    /// <summary>The closed profile sketch (leave null with <see cref="CircularProfileDiameterMm"/>).</summary>
    public EntityRef? Profile { get; init; }
    /// <summary>The path: a sketch, a model edge or a curve feature (helix).</summary>
    public EntityRef? Path { get; init; }
    public IReadOnlyList<EntityRef>? Guides { get; init; }
    public bool Cut { get; init; }
    public double? TwistDeg { get; init; }
    public bool Merge { get; init; } = true;
    /// <summary>Sweep a circle of this diameter (centered on the path) instead of a profile sketch.</summary>
    public double? CircularProfileDiameterMm { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_loft options.</summary>
public sealed record LoftRequest
{
    public string? Doc { get; init; }
    /// <summary>Closed profile sketches or faces, in loft order.</summary>
    public IReadOnlyList<EntityRef>? Profiles { get; init; }
    public IReadOnlyList<EntityRef>? Guides { get; init; }
    public bool Cut { get; init; }
    /// <summary>Close the loft from the last profile back to the first (3 profiles or more).</summary>
    public bool Closed { get; init; }
    public bool Merge { get; init; } = true;
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_helix options.</summary>
public sealed record HelixRequest
{
    public string? Doc { get; init; }
    /// <summary>The sketch with one circle (default: the open sketch, else the last finished one).</summary>
    public string? Sketch { get; init; }
    /// <summary>pitch_revolutions | height_revolutions | height_pitch | spiral.</summary>
    public string? DefinedBy { get; init; } = "pitch_revolutions";
    public double? PitchMm { get; init; }
    public double? Revolutions { get; init; }
    public double? HeightMm { get; init; }
    public double StartAngleDeg { get; init; }
    public bool Clockwise { get; init; }
    /// <summary>Climb against the sketch normal.</summary>
    public bool Reverse { get; init; }
    /// <summary>Taper angle: positive = outward (the radius grows), negative = inward.</summary>
    public double? TaperDeg { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_draft options.</summary>
public sealed record DraftRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>The neutral plane or planar face the drafted faces hinge on.</summary>
    public EntityRef? Neutral { get; init; }
    public double AngleDeg { get; init; }
    public bool Flip { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_rib options.</summary>
public sealed record RibRequest
{
    public string? Doc { get; init; }
    /// <summary>The rib's (open) sketch (default: the open sketch, else the last finished one).</summary>
    public string? Sketch { get; init; }
    public double ThicknessMm { get; init; }
    /// <summary>Thickness centered on the sketch (true) or on one side of it.</summary>
    public bool BothSides { get; init; } = true;
    /// <summary>Extend the rib material to the other side of the sketch.</summary>
    public bool FlipMaterial { get; init; }
    /// <summary>Extrude the rib normal to the sketch plane instead of parallel to it.</summary>
    public bool NormalToSketch { get; init; }
    public double? DraftDeg { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>feature_thread_cosmetic options.</summary>
public sealed record CosmeticThreadRequest
{
    public string? Doc { get; init; }
    /// <summary>The circular edge where the thread starts (a hole's rim, a shaft's end).</summary>
    public EntityRef? Edge { get; init; }
    /// <summary>none (default: custom diameter) | ansi_metric | ansi_inch | iso | din | jis | bsi | gb | ks | is | as.</summary>
    public string? Standard { get; init; }
    /// <summary>"M6" or "M6x1.0" (derives the diameter), or the size as the standard names it.</summary>
    public string? Size { get; init; }
    public double? DiameterMm { get; init; }
    public double? DepthMm { get; init; }
    public bool ThroughAll { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

// ====================================================================== results

/// <summary>Where a helix (or spiral) lies, read back from the curve SOLIDWORKS built (mm, unit vectors).</summary>
public sealed class HelixInfo
{
    /// <summary>The start point: a sweep profile must lie there (on a plane through it, see <see cref="ProfilePlaneNormal"/>).</summary>
    public required double[] StartMm { get; init; }
    public double[]? EndMm { get; init; }
    /// <summary>The circle's center: a point on the axis.</summary>
    public required double[] AxisOriginMm { get; init; }
    /// <summary>The axis, pointing the way the helix climbs (a spiral: the sketch normal).</summary>
    public required double[] Axis { get; init; }
    public double RadiusMm { get; init; }
    public double HeightMm { get; init; }
    public double PitchMm { get; init; }
    public double Revolutions { get; init; }
    /// <summary>Measured curve length.</summary>
    public double LengthMm { get; init; }
    /// <summary>True when it turns counter-clockwise seen from the tip of <see cref="Axis"/> (a right-handed helix).</summary>
    public bool? CounterClockwise { get; init; }
    /// <summary>Normal of the plane through the start that contains the axis (the usual sweep-profile plane).</summary>
    public double[]? ProfilePlaneNormal { get; init; }
}

/// <summary>Result of feature_helix: the curve feature, its geometry and the verification evidence.</summary>
public sealed class HelixResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    public string? ConsumedSketch { get; init; }
    public required HelixInfo Helix { get; init; }
    public required Verified Verified { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
}

// ====================================================================== service

/// <summary>
/// Advanced part features: sweep, loft, helix, draft, rib and cosmetic thread. Every call is verified — Tier A by
/// <see cref="FeatureVerifier"/> (a new feature of the right type, no rebuild error, faces, the volume rule) and Tier B
/// by the intent checks here (swept volume against profile area × path length, the loft passing through its
/// profiles, the helix's start, height and length, drafted faces tilted about their hinge, rib side faces, the
/// thread read back) — and rolled back on failure. Verified recipes (docs/solidworks-api-notes.md §4): the sweep
/// (profile mark 1, path 4, guides 2), the loft (profiles mark 1 in order, guides 2) and the helix. Best guesses (see
/// the tests): cut sweeps and lofts, circular profiles, twist, the draft's marks, the rib and the cosmetic thread.
/// </summary>
public sealed class AdvancedFeatureService(SwSession session)
{
    private const double PosTol = 2e-6;     // m: positions read back from SOLIDWORKS geometry
    private const double OnFaceTol = 1e-5;  // m: loft-profile points on the loft's faces (0.01 mm)
    private const int ProfileMark = 1, GuideMark = 2, PathMark = 4; // verified (sweep and loft)
    private const int NeutralMark = 1, DraftFaceMark = 2;            // best reading; the call retries without marks

    private static readonly FeatureSpec SweepSpec = new("feature_sweep", ["Sweep"], VolumeRule.Increase);
    private static readonly FeatureSpec SweepCutSpec = new("feature_sweep", ["SweepCut", "ICE"], VolumeRule.Decrease);
    private static readonly FeatureSpec LoftSpec = new("feature_loft", ["Blend"], VolumeRule.Increase);
    private static readonly FeatureSpec LoftCutSpec = new("feature_loft", ["BlendCut", "ICE"], VolumeRule.Decrease);
    private static readonly FeatureSpec HelixSpec = new("feature_helix", ["Helix"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec DraftSpec = new("feature_draft", ["Draft"], VolumeRule.Change);
    private static readonly FeatureSpec RibSpec = new("feature_rib", ["Rib"], VolumeRule.Increase);
    private static readonly FeatureSpec ThreadSpec = new("feature_thread_cosmetic", ["CosmeticThread"], VolumeRule.Any, RequireFaces: false);

    private const string SweepHint = "Put the profile on a plane through the path's start point (feature_helix returns startMm and profilePlaneNormal), " +
                                     "keep it small enough for the path's bends, and make sure a cut sweep runs through material.";
    private const string LoftHint = "Profiles must be closed, in order, on separate planes (or faces), with matching start points; a cut loft must run through material.";
    private const string HelixHint = "The sketch needs one circle; check the pitch, the revolutions and the height (definedBy).";
    private const string DraftHint = "The faces must meet the neutral plane or face (side walls about a bottom face); a smaller angle or fewer faces may help.";
    private const string RibHint = "The rib sketch (usually one line) must lie in a plane through the part, with its ends on or inside the walls it joins.";
    private const string ThreadHint = "Pick the circular rim of a hole or shaft; the depth must fit the cylinder.";

    private TimeSpan Long => TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120));

    // ================================================================== sweep

    public Task<FeatureResult> SweepAsync(SweepRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_sweep", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            double? dia = q.CircularProfileDiameterMm is double cd ? Units.PositiveM(cd, "circularProfileDiameterMm") : null;
            if (dia is null && q.Profile is null)
                throw SwException.InvalidArgument("Pass 'profile' (a closed sketch, e.g. {name:'Sketch2'}) or circularProfileDiameterMm.");
            if (dia is not null && q.Profile is not null)
                throw SwException.InvalidArgument("Pass either 'profile' or circularProfileDiameterMm, not both.");
            if (q.Path is null)
                throw SwException.InvalidArgument("'path' is required: a sketch {name:'Sketch1'}, an edge {type:'edge',at:[x,y,z]} or a curve {name:'Helix/Spiral1'}.");
            double twist = 0;
            if (q.TwistDeg is double tw)
            {
                if (!double.IsFinite(tw) || Math.Abs(tw) > 36000)
                    throw SwException.InvalidArgument($"twistDeg must be a finite angle of at most 36000 degrees (got {F(tw)}).");
                twist = Units.Rad(tw);
            }
            if (twist != 0 && dia is not null)
            {
                ctx.Warn("twistDeg has no effect on a circular profile; it was ignored.");
                twist = 0;
            }
            if (q.Guides is { Count: > 20 }) throw SwException.InvalidArgument($"At most 20 guide curves (got {q.Guides.Count}).");
            NoNullRefs(q.Guides, "guides");

            // Features need closed sketches: the server's open sketch (often the profile just drawn) is closed first when
            // the call names it, so every reference below is resolved in the final state of the model.
            CloseServerSketch(ctx, d, RefNames([q.Profile, q.Path, .. q.Guides ?? []]));
            var profRef = q.Profile is null ? null : EntityResolver.Resolve(ctx, d, q.Profile, "profile", EntityKind.Sketch);
            var pathRef = EntityResolver.Resolve(ctx, d, q.Path, "path", EntityKind.Sketch, EntityKind.Edge, EntityKind.Feature);
            var guideRefs = q.Guides is { Count: > 0 }
                ? EntityResolver.ResolveAll(ctx, d, q.Guides, "guides", EntityKind.Sketch, EntityKind.Edge, EntityKind.Feature)
                : [];
            var resolved = new List<ResolvedInfo>();
            if (profRef is not null) resolved.Add(profRef.Info("profile"));
            resolved.Add(pathRef.Info("path"));
            for (int i = 0; i < guideRefs.Count; i++) resolved.Add(guideRefs[i].Info($"guides[{i}]"));

            var profile = profRef is null ? null : ProfileOf(profRef, "profile");
            var path = PathOf(d, pathRef, "path");
            var guides = guideRefs.Select((g, i) => PathOf(d, g, $"guides[{i}]")).ToList();
            if (profile is not null && SameName(path.Sketch, profile.Feature.Name))
                throw SwException.InvalidArgument($"The profile and the path are the same sketch ('{profile.Feature.Name}').",
                    "Draw the path and the profile in two sketches.");
            if (profile is not null && guides.Any(g => SameName(g.Sketch, profile.Feature.Name)))
                throw SwException.InvalidArgument($"The profile '{profile.Feature.Name}' is also a guide curve.");

            // SOLIDWORKS sweeps from where the profile meets the path: a profile plane through neither end of an open
            // path usually means a partial sweep (docs: Helix), which the volume check below then catches.
            bool offStart = false;
            if (profile is not null && !path.Closed && path.Start is Vec3 s0 && path.End is Vec3 s1)
            {
                double gap = Math.Min(Math.Abs((s0 - profile.Origin).Dot(profile.Normal)), Math.Abs((s1 - profile.Origin).Dot(profile.Normal)));
                if (gap > PosTol)
                {
                    offStart = true;
                    ctx.Warn($"Neither end of the path lies in the profile's plane (the nearer end is {F(Units.Mm(gap))} mm from it); " +
                             "SOLIDWORKS sweeps from where the profile meets the path, which may cover only part of it.");
                }
            }

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var picks = new List<Pick>();
            if (profile is not null) picks.Add(new Pick(profRef!.Label, (doc, m) => SelectSketch(doc, profile.Feature, m), ProfileMark));
            picks.Add(new Pick(path.Label, path.Select, PathMark));
            picks.AddRange(guides.Select(g => new Pick(g.Label, g.Select, GuideMark)));
            SelectAll(d, picks);
            bool twisted = twist != 0;
            int twistType = twisted ? (int)swTwistControlType_e.swTwistControlConstantTwistAlongPath : (int)swTwistControlType_e.swTwistControlFollowPath;
            int dir1 = (int)swSweepDirection_e.swSweepDirection1;
            var fm = d.FeatureManager;
            object? api;
            if (!q.Cut)
                api = fm.InsertProtrusionSwept4(false, false, twistType, false, false, 0, 0, false, 0, 0, 0, 0, q.Merge, true, true,
                    twist, true, dia is not null, dia ?? 0, dir1);
            else if (dia is null)
                api = fm.InsertCutSwept4(false, false, (short)twistType, false, false, 0, 0, false, 0, 0, 0, 0, true, true, twist, true,
                    false, false, false);
            else
                api = fm.InsertCutSwept5(false, false, twistType, false, false, 0, 0, false, 0, 0, 0, 0, true, true, 0, true,
                    false, false, false, true, dia.Value, dir1);
            d.ClearSelection2(true);

            var o = Verify(ctx, d, before, q.Cut ? SweepCutSpec : SweepSpec,
                (main, after, checks) => SweepIntent(ctx, main, before, after, q, profile, path, dia, twisted, guides.Count > 0, offStart, checks),
                q.KeepOnFailure, api, SweepHint);
            Consumed(ctx, profile?.Feature.Name, path.Sketch);
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, profile?.Feature.Name, path.Straight ? path.Dir : null, resolved);
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    /// <summary>
    /// Tier B: the circular profile reads back, and the swept volume (when nothing merged with it) matches profile area ×
    /// path length — exactly on a straight path, roughly on a curved one (a partial sweep fails). The profile area is
    /// SOLIDWORKS' own: the sweep's start face in the profile plane; the area computed from the sketch is the fallback.
    /// </summary>
    private static List<string> SweepIntent(SwContext ctx, IFeature main, Snapshot before, Snapshot after, SweepRequest q, ProfileInfo? profile,
        PathInfo path, double? dia, bool twisted, bool guides, bool offStart, List<string> checks)
    {
        var problems = new List<string>();
        if (dia is double dd)
        {
            if (main.Definition() is ISweepFeatureData data)
            {
                if (!data.CircularProfile) problems.Add("the sweep reads back without a circular profile");
                else if (Math.Abs(data.CircularProfileDiameter - dd) > 1e-9)
                    problems.Add($"the circular profile reads back as Ø{F(Units.Mm(data.CircularProfileDiameter))} mm, not Ø{F(Units.Mm(dd))} mm");
                else checks.Add($"circular profile Ø{F(Units.Mm(dd))} mm read back");
            }
            else checks.Add("circular profile not read back (no feature data)");
        }
        double? cap = dia is null && profile is not null ? StartCapArea(main, profile) : null;
        if (cap is double ca && profile is { AreaExact: true, Area: double sa } && Math.Abs(ca - sa) > 1e-3 * sa)
            ctx.Warn($"The sweep's start face is {F(Units.Mm2(ca))} mm², but the profile sketch encloses {F(Units.Mm2(sa))} mm² " +
                     "(SOLIDWORKS may have used only some of its contours).");
        double? area = dia is double d2 ? Math.PI * d2 * d2 / 4 : cap ?? profile?.Area;
        bool exact = dia is not null || cap is not null || profile?.AreaExact == true;
        double cos = 1;
        if (path.Straight && path.Dir is Vec3 u && profile is not null) cos = Math.Abs(u.Dot(profile.Normal));
        bool isolated = !q.Cut && (!q.Merge || before.SolidBodies == 0 || after.SolidBodies > before.SolidBodies);
        var v = AdvancedFeatureLogic.SweepVolume(after.Volume - before.Volume, area, path.Length, cos, path.Straight, exact, isolated, guides,
            twisted && cos < 1 - 1e-9, offStart);
        if (v.Problem is not null) problems.Add(v.Problem);
        else checks.Add(v.Check);
        return problems;
    }

    /// <summary>A closed 2D profile sketch: its plane, points on its contours (model space) and, when computable, its area (holes subtracted).</summary>
    private sealed record ProfileInfo(IFeature Feature, ISketch Sketch, Vec3 Origin, Vec3 Normal, IReadOnlyList<Vec3> Points, double? Area, bool AreaExact);

    /// <summary>
    /// The total area of the sweep's planar faces that lie in the profile's plane and touch the profile's contours: the
    /// start face, i.e. the section SOLIDWORKS actually swept (an end face in the same plane, after whole helix turns, is
    /// away from the profile). Null when there is none (a closed path has no start face).
    /// </summary>
    private static double? StartCapArea(IFeature main, ProfileInfo profile)
    {
        if (profile.Points.Count == 0) return null;
        double area = 0;
        foreach (var f in FeatureFaces(main))
        {
            var g = Topology.Describe(f);
            if (g.Surface != "plane" || g.Normal is not Vec3 n || !n.IsParallelTo(profile.Normal, 1e-6) || PointOn(f) is not Vec3 p) continue;
            if (Math.Abs((p - profile.Origin).Dot(profile.Normal)) > PosTol) continue;
            if (profile.Points.Any(x => Topology.DistanceToFace(f, x) < OnFaceTol)) area += f.GetArea();
        }
        return area > 0 ? area : null;
    }

    private static ProfileInfo ProfileOf(Resolved r, string arg)
    {
        var feat = (IFeature)r.Obj;
        if (feat.Specific() is not ISketch sk) throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is not a sketch.");
        if (sk.Is3D())
            throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is a 3D sketch; a profile must be planar.", "Draw it with sketch_start on a plane or a planar face.");
        var contours = SketchService.Contours(sk);
        if (contours.ClosedContours == 0 || contours.OpenContours > 0)
            throw SwException.InvalidArgument(
                $"'{arg}' ({r.Label}) is not a closed profile ({contours.Status}{(contours.Problem is null ? "" : ", " + contours.Problem)}).",
                "A profile is closed (circle, rectangle, closed polyline) without loose segments; make helper lines construction.");
        var s2m = SketchService.SketchToModel(sk);
        var origin = s2m.ApplyPoint(Vec3.Zero);
        var normal = s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized();
        var segs = SketchSegs(sk, out bool exactShapes);
        var set = AdvancedFeatureLogic.Chains(segs);
        double? area = null;
        bool exact = false;
        if (segs.Count > 0 && !set.Branched && !set.Open.Any())
        {
            (double a, bool ex) = AdvancedFeatureLogic.RegionArea(set);
            area = a;
            exact = ex && exactShapes;
        }
        var points = Spread(segs.SelectMany(s => s.Samples).ToList(), 24).Select(s2m.ApplyPoint).ToList();
        return new ProfileInfo(feat, sk, origin, normal, points, area, exact);
    }

    /// <summary>A sweep path or guide curve: how to select it, its length, ends and (if straight) direction, in model space.</summary>
    private sealed record PathInfo(string Label, Func<IModelDoc2, int, bool> Select, double Length, bool Straight, Vec3? Dir,
        Vec3? Start, Vec3? End, bool Closed, string? Sketch);

    private static PathInfo PathOf(IModelDoc2 d, Resolved r, string arg)
    {
        switch (r.Obj)
        {
            case IEdge e:
            {
                var g = Topology.Describe(e);
                return new PathInfo(r.Label, (doc, m) => r.Select(doc, true, m), g.Length, g.Curve == "line", g.Direction,
                    g.Closed ? null : g.Start, g.Closed ? null : g.End, g.Closed, null);
            }
            case IFeature f when r.Kind == EntityKind.Sketch && f.Specific() is ISketch sk:
            {
                var segs = SketchSegs(sk, out _);
                if (segs.Count == 0) throw SwException.InvalidArgument($"'{arg}' ({r.Label}) has no segments (construction geometry doesn't count).");
                var set = AdvancedFeatureLogic.Chains(segs);
                if (set.Branched || set.Chains.Count != 1)
                    throw SwException.InvalidArgument(
                        $"'{arg}' ({r.Label}) must be one connected chain of segments; it has {set.Chains.Count} chain(s){(set.Branched ? " with branches" : "")}.",
                        "Use one sketch per path or guide (lines, arcs and splines joined end to end).");
                var chain = set.Chains[0];
                var xf = sk.Is3D() ? Xform.Identity : SketchService.SketchToModel(sk);
                Vec3? a = chain.Closed ? null : xf.ApplyPoint(chain.Start);
                Vec3? b = chain.Closed ? null : xf.ApplyPoint(chain.End);
                bool straight = segs.Count == 1 && segs[0].Kind == "line";
                Vec3? dir = straight && a is Vec3 pa && b is Vec3 pb ? (pb - pa).Normalized() : null;
                return new PathInfo(r.Label, (doc, m) => SelectSketch(doc, f, m), chain.Length, straight, dir, a, b, chain.Closed, f.Name);
            }
            case IFeature f when f.Specific() is IReferenceCurve rc:
            {
                var edges = rc.AdvCurveEdges();
                if (edges.Count == 0) throw new SwException(ErrorCodes.VerifyFailed, $"'{arg}' ({r.Label}) reports no curve segments.");
                var geoms = edges.Select(Topology.Describe).ToList();
                var set = AdvancedFeatureLogic.Chains(geoms.Select(g => AdvancedFeatureLogic.Seg.Chord(g.Start, g.End, g.Length)).ToList());
                var chain = set.Chains.Count == 1 ? set.Chains[0] : null;
                bool closed = chain?.Closed == true || (geoms.Count == 1 && geoms[0].Closed);
                // The curve's first segment starts where a helix starts (verified); the other open end is its end.
                var start = rc.AdvFirstSegment()?.StartVertex()?.Point() is { Length: >= 3 } sp ? Vec3.From(sp) : chain?.Start ?? geoms[0].Start;
                Vec3? end = chain is null || closed ? null : start.DistanceTo(chain.Start) < start.DistanceTo(chain.End) ? chain.End : chain.Start;
                bool straight = geoms.Count == 1 && geoms[0].Curve == "line";
                string name = f.Name;
                return new PathInfo(r.Label,
                    (doc, m) => doc.Extension.SelectByID2(name, "REFERENCECURVES", 0, 0, 0, true, m, null, 0) || f.Select2(true, m),
                    geoms.Sum(g => g.Length), straight, straight ? geoms[0].Direction : null, closed ? null : start, end, closed, null);
            }
        }
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; a path or guide is a sketch, an edge or a curve feature (helix, composite curve).");
    }

    // ================================================================== loft

    public Task<FeatureResult> LoftAsync(LoftRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_loft", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            int n = q.Profiles?.Count ?? 0;
            if (n < 2)
                throw SwException.InvalidArgument($"'profiles' needs at least 2 entries, in loft order (got {n}).",
                    "Pass closed sketches {name:'Sketch1'} or faces {type:'face',at:[x,y,z]}, one per section.");
            if (n > 50) throw SwException.InvalidArgument($"At most 50 profiles (got {n}).");
            if (q.Closed && n < 3) throw SwException.InvalidArgument("A closed loft needs at least 3 profiles.", "Leave 'closed' false for two profiles.");
            if (q.Guides is { Count: > 20 }) throw SwException.InvalidArgument($"At most 20 guide curves (got {q.Guides.Count}).");
            NoNullRefs(q.Profiles, "profiles");
            NoNullRefs(q.Guides, "guides");
            CloseServerSketch(ctx, d, RefNames([.. q.Profiles!, .. q.Guides ?? []])); // the last profile is often still open
            var profs = EntityResolver.ResolveAll(ctx, d, q.Profiles, "profiles", EntityKind.Sketch, EntityKind.Face);
            if (profs.Count < 2) throw SwException.InvalidArgument("'profiles' resolved to fewer than 2 different profiles.");
            var guideRefs = q.Guides is { Count: > 0 }
                ? EntityResolver.ResolveAll(ctx, d, q.Guides, "guides", EntityKind.Sketch, EntityKind.Edge, EntityKind.Feature)
                : [];
            var resolved = profs.Select((p, i) => p.Info($"profiles[{i}]")).Concat(guideRefs.Select((g, i) => g.Info($"guides[{i}]"))).ToList();

            var sections = profs.Select((p, i) => (Label: $"profiles[{i}] ({p.Label})", Points: LoftProfilePoints(p, $"profiles[{i}]"))).ToList();
            var guides = guideRefs.Select((g, i) => PathOf(d, g, $"guides[{i}]")).ToList();
            var sketches = profs.Where(p => p.Kind == EntityKind.Sketch).Select(p => ((IFeature)p.Obj).Name).ToList();

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var picks = profs.Select(p => new Pick(p.Label,
                    p.Kind == EntityKind.Sketch ? (doc, m) => SelectSketch(doc, (IFeature)p.Obj, m) : (doc, m) => p.Select(doc, true, m), ProfileMark))
                .Concat(guides.Select(g => new Pick(g.Label, g.Select, GuideMark))).ToList();
            SelectAll(d, picks);
            var fm = d.FeatureManager;
            object? api = q.Cut
                ? fm.InsertCutBlend(q.Closed, true, false, 1, 0, 0, false, 0, 0, 0, true, true)
                : fm.InsertProtrusionBlend2(q.Closed, true, false, 1, 0, 0, 1, 1, true, true, false, 0, 0, 0, q.Merge, true, true, 0);
            d.ClearSelection2(true);

            var o = Verify(ctx, d, before, q.Cut ? LoftCutSpec : LoftSpec,
                (main, after, checks) => LoftIntent(ctx, main, before, after, sections, q, checks), q.KeepOnFailure, api, LoftHint);
            Consumed(ctx, [.. sketches]);
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, sketches.Count == 0 ? null : string.Join(", ", sketches), null, resolved);
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    /// <summary>
    /// Tier B: the loft's faces pass through every profile (each section's boundary lies on them). A boss that merged
    /// with existing material may have lost faces inside it, so there a miss is a warning, not a failure.
    /// </summary>
    private static List<string> LoftIntent(SwContext ctx, IFeature main, Snapshot before, Snapshot after,
        List<(string Label, List<Vec3> Points)> sections, LoftRequest q, List<string> checks)
    {
        if (q.Cut)
        {
            checks.Add("profiles not checked (a cut keeps only what lies inside the body)");
            return [];
        }
        var faces = FeatureFaces(main);
        var misses = new List<string>();
        foreach (var (label, pts) in sections)
        {
            double worst = 0;
            foreach (var p in pts)
                worst = Math.Max(worst, faces.Count == 0 ? double.MaxValue : faces.Min(f => Topology.DistanceToFace(f, p)));
            if (worst > OnFaceTol) misses.Add($"{label} lies up to {F(Units.Mm(worst))} mm off the loft's faces");
        }
        if (misses.Count == 0)
        {
            checks.Add($"the loft's faces pass through all {sections.Count} profiles");
            return [];
        }
        bool merged = q.Merge && before.SolidBodies > 0 && after.SolidBodies <= before.SolidBodies;
        if (!merged) return misses;
        ctx.Warn("The loft merged with existing material and " + string.Join("; ", misses) + " (faces inside the body are gone; check the shape).");
        checks.Add("profiles partly checked (merged with existing material)");
        return [];
    }

    /// <summary>Points on a loft profile in model space (at most 24): segment ends and midpoints, or a face's edge points.</summary>
    private static List<Vec3> LoftProfilePoints(Resolved r, string arg)
    {
        switch (r.Obj)
        {
            case IFeature f when r.Kind == EntityKind.Sketch && f.Specific() is ISketch sk:
            {
                var contours = SketchService.Contours(sk);
                if (contours.ClosedContours == 0)
                    throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is not a closed profile ({contours.Status}).",
                        "Loft profiles are closed sketches or faces; open curves go in 'guides'.");
                var xf = sk.Is3D() ? Xform.Identity : SketchService.SketchToModel(sk);
                return Spread(SketchSegs(sk, out _).SelectMany(s => s.Samples).ToList(), 24).Select(xf.ApplyPoint).ToList();
            }
            case IFace2 face:
                return Spread(face.Edges().Select(Topology.Describe).SelectMany(g => new[] { g.Start, g.Mid }).ToList(), 24);
        }
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; a loft profile is a closed sketch or a face.");
    }

    // ================================================================== helix

    public Task<HelixResult> HelixAsync(HelixRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_helix", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var plan = AdvancedFeatureLogic.PlanHelix(q.DefinedBy, q.PitchMm, q.Revolutions, q.HeightMm, q.StartAngleDeg, q.TaperDeg);
            var (feat, sk) = SketchArg(ctx, d, q.Sketch, "helix");
            if (sk.Is3D()) throw SwException.InvalidArgument($"'{feat.Name}' is a 3D sketch; a helix needs a 2D sketch with one circle.");
            var segs = sk.Segments().Where(s => !s.ConstructionGeometry).ToList();
            var circles = segs.Where(s => s.GetType() == (int)swSketchSegments_e.swSketchARC && s is ISketchArc a && a.IsCircle() != 0).ToList();
            if (circles.Count != 1 || segs.Count != 1)
                throw SwException.InvalidArgument(
                    $"Sketch '{feat.Name}' must contain exactly one circle (it has {circles.Count} circle(s) and {segs.Count - circles.Count} other segment(s)).",
                    "A helix takes its axis and radius from a sketch with a single circle; make other geometry construction.");
            var arc = (ISketchArc)circles[0];
            double r = arc.GetRadius();
            var cp = arc.SkCenter() ?? throw new SwException(ErrorCodes.Internal, $"Could not read the center of the circle in '{feat.Name}'.");
            var s2m = SketchService.SketchToModel(sk);
            var center = s2m.ApplyPoint(new Vec3(cp.X, cp.Y, 0));
            var normal = s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized();
            if (plan.Tapered && !plan.Outward && plan.HeightM * Math.Tan(plan.TaperRad) >= r)
                throw SwException.InvalidArgument($"An inward taper of {F(Units.Deg(plan.TaperRad))}° over {F(Units.Mm(plan.HeightM))} mm reaches the axis (radius {F(Units.Mm(r))} mm).",
                    "Use a smaller taper or height.");

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            if (!feat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{feat.Name}'.");
            d.InsertHelix(q.Reverse, q.Clockwise, plan.Tapered, plan.Outward, plan.DefinedBy, plan.HeightM, plan.PitchM, plan.Revolutions,
                plan.TaperRad, plan.StartRad); // void: the new feature is found by the snapshot difference
            d.ClearSelection2(true);

            HelixInfo? info = null;
            var o = Verify(ctx, d, before, HelixSpec, (main, _, checks) =>
            {
                var (problems, h) = HelixIntent(main, plan, center, normal, r, checks);
                info = h;
                return problems;
            }, q.KeepOnFailure, null, HelixHint);
            Consumed(ctx, feat.Name);
            Rename(ctx, o.Main, q.Name);
            var fr = FeatureVerifier.Result(ctx, d, o, feat.Name);
            return new HelixResult
            {
                Doc = fr.Doc,
                Feature = fr.Feature,
                ConsumedSketch = feat.Name,
                Helix = info ?? throw new SwException(ErrorCodes.Internal, "The helix geometry was not read."),
                Verified = fr.Verified,
                Dimensions = fr.Dimensions is { Count: > 0 } dims ? dims : null,
            };
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>
    /// Tier B: the curve starts on the circle (verified behaviour), ends at the height along the axis (a spiral stays in
    /// the sketch plane), has the expected end radius and length, and its definition reads back.
    /// </summary>
    private static (List<string> Problems, HelixInfo? Info) HelixIntent(IFeature main, AdvancedFeatureLogic.HelixPlan plan, Vec3 c, Vec3 n,
        double r, List<string> checks)
    {
        if (main.Specific() is not IReferenceCurve rc) return (["the helix reports no curve"], null);
        var edges = rc.AdvCurveEdges();
        if (edges.Count == 0) return (["the helix has no curve segments"], null);
        var problems = new List<string>();
        var first = rc.AdvFirstSegment() ?? edges[0];
        var geoms = edges.Select(Topology.Describe).ToList();
        var start = first.StartVertex()?.Point() is { Length: >= 3 } sp ? Vec3.From(sp) : Topology.Describe(first).Start;
        var set = AdvancedFeatureLogic.Chains(geoms.Select(g => AdvancedFeatureLogic.Seg.Chord(g.Start, g.End, g.Length)).ToList());
        var chain = set.Chains.Count == 1 ? set.Chains[0] : null;
        var end = chain is null ? geoms[^1].End : start.DistanceTo(chain.Start) < start.DistanceTo(chain.End) ? chain.End : chain.Start;
        double length = geoms.Sum(g => g.Length);

        var radial0 = Radial(start - c, n);
        if (Math.Abs(radial0.Length - r) > PosTol || Math.Abs((start - c).Dot(n)) > PosTol)
            problems.Add($"it starts at [{Pt(start)}] mm, not on the circle r {F(Units.Mm(r))} mm about [{Pt(c)}]");
        else checks.Add("starts on the circle");

        bool spiral = plan.Mode == "spiral";
        double climb = (end - c).Dot(n);
        double rEnd = Radial(end - c, n).Length;
        var expectEnd = new List<double>();
        if (spiral)
        {
            if (Math.Abs(climb) > PosTol) problems.Add($"the spiral is not flat: its end is {F(Units.Mm(climb))} mm off the sketch plane");
            expectEnd.Add(r + plan.Revolutions * plan.PitchM);
            if (r - plan.Revolutions * plan.PitchM > 0) expectEnd.Add(r - plan.Revolutions * plan.PitchM);
        }
        else
        {
            if (Math.Abs(Math.Abs(climb) - plan.HeightM) > 5 * PosTol)
                problems.Add($"its end lies {F(Units.Mm(Math.Abs(climb)))} mm along the axis, expected the height {F(Units.Mm(plan.HeightM))} mm");
            else checks.Add($"height {F(Units.Mm(plan.HeightM))} mm");
            if (!plan.Tapered) expectEnd.Add(r);
            else
            {
                double dr = plan.HeightM * Math.Tan(plan.TaperRad);
                expectEnd.Add(r + dr);
                if (r - dr > 0) expectEnd.Add(r - dr);
            }
        }
        double? r1 = expectEnd.Count == 0 ? null : expectEnd.MinBy(x => Math.Abs(x - rEnd));
        if (r1 is double want && Math.Abs(rEnd - want) > 5 * PosTol)
            problems.Add($"its end is {F(Units.Mm(rEnd))} mm from the axis, expected {string.Join(" or ", expectEnd.Select(x => F(Units.Mm(x))))} mm");
        else if (r1 is double ok && (spiral || plan.Tapered)) checks.Add($"end radius {F(Units.Mm(ok))} mm");

        double expectLength = AdvancedFeatureLogic.HelixLength(r, r1 ?? r, spiral ? 0 : plan.HeightM, plan.Revolutions);
        if (expectLength > 0 && Math.Abs(length - expectLength) > 0.01 * expectLength)
            problems.Add($"its length is {F(Units.Mm(length))} mm, expected about {F(Units.Mm(expectLength))} mm");
        else checks.Add($"length {F(Units.Mm(length))} mm (expected {F(Units.Mm(expectLength))} mm)");

        if (main.Definition() is IHelixFeatureData hd)
        {
            var wrong = AdvancedFeatureLogic.HelixReadBack(plan, hd.DefinedBy, hd.Height, hd.Pitch, hd.Revolution);
            if (wrong.Count > 0) problems.Add("its definition reads back " + string.Join(", ", wrong));
            else checks.Add("definition read back");
        }
        else checks.Add("definition not read back (no feature data)");

        var axis = spiral || climb >= 0 ? n : -n;
        bool? ccw = TurnsCounterClockwise(first, start, c, axis);
        var info = new HelixInfo
        {
            StartMm = Units.Mm(start),
            EndMm = Units.Mm(end),
            AxisOriginMm = Units.Mm(c),
            Axis = Units.Unit(axis),
            RadiusMm = Units.Mm(r),
            HeightMm = Units.Mm(Math.Abs(climb)),
            PitchMm = Units.Mm(plan.PitchM),
            Revolutions = Math.Round(plan.Revolutions, 6),
            LengthMm = Units.Mm(length),
            CounterClockwise = ccw,
            ProfilePlaneNormal = radial0.Length > 1e-12 ? Units.Unit(axis.Cross(radial0.Normalized()).Normalized()) : null,
        };
        return (problems, info);
    }

    /// <summary>The turning sense about <paramref name="axis"/> just after the start (null when it can't be evaluated).</summary>
    private static bool? TurnsCounterClockwise(IEdge first, Vec3 start, Vec3 c, Vec3 axis)
    {
        var cp = first.CurveParams();
        if (cp.Length < 8) return null;
        double t0 = cp[6], t1 = cp[7];
        bool fromT0 = Vec3.From(cp, 0).DistanceTo(start) <= Vec3.From(cp, 3).DistanceTo(start);
        var p = first.EvalAt(fromT0 ? t0 + 0.01 * (t1 - t0) : t1 - 0.01 * (t1 - t0));
        if (p.Length < 3) return null;
        double s = Radial(start - c, axis).Cross(Radial(Vec3.From(p) - c, axis)).Dot(axis);
        return Math.Abs(s) < 1e-15 ? null : s > 0;
    }

    // ================================================================== draft

    public Task<FeatureResult> DraftAsync(DraftRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_draft", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (!double.IsFinite(q.AngleDeg) || q.AngleDeg <= 0 || q.AngleDeg >= 90)
                throw SwException.InvalidArgument($"angleDeg must be greater than 0 and less than 90 (got {F(q.AngleDeg)}).");
            double ang = Units.Rad(q.AngleDeg);
            if (q.Faces is null || q.Faces.Count == 0)
                throw SwException.InvalidArgument("Pass 'faces' to draft, e.g. [{at:[50,10,0]}] or a 'where' query for the side walls.");
            if (q.Neutral is null)
                throw SwException.InvalidArgument("'neutral' is required: the plane {name:'top'} or planar face {at:[x,y,z]} the faces hinge on.");
            NoNullRefs(q.Faces, "faces");
            NoOpenSketch(d);
            var neutral = EntityResolver.Resolve(ctx, d, q.Neutral, "neutral", EntityKind.Plane, EntityKind.Face);
            var (q0, m) = PlaneOf(neutral, "neutral");
            var faces = EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face);
            if (faces.Count > 200) throw SwException.InvalidArgument($"At most 200 faces per draft (got {faces.Count}).");
            var targets = new List<DraftTarget>();
            for (int i = 0; i < faces.Count; i++)
            {
                var f = (IFace2)faces[i].Obj;
                if (neutral.Obj is IFace2 nf && SameFace(nf, f))
                    throw SwException.InvalidArgument($"faces[{i}] ({faces[i].Label}) is the neutral face itself.", "Draft the faces that meet it (the side walls).");
                var g = Topology.Describe(f);
                if (g.Surface != "plane" || g.Normal is not Vec3 n0 || PointOn(f) is not Vec3 p0)
                {
                    targets.Add(new DraftTarget(faces[i].Label, null, Vec3.Zero, Vec3.Zero));
                    continue;
                }
                var hinge = AdvancedFeatureLogic.PlaneIntersection(n0, n0.Dot(p0), m, m.Dot(q0));
                if (hinge is not { } line)
                    throw SwException.InvalidArgument($"faces[{i}] ({faces[i].Label}) is parallel to the neutral plane, so it can't be drafted about it.",
                        "Draft the faces that meet the neutral plane or face (e.g. the side walls about the bottom face).");
                targets.Add(new DraftTarget(faces[i].Label, n0, line.Point, line.Dir));
            }
            var resolved = new List<ResolvedInfo> { neutral.Info("neutral") };
            resolved.AddRange(faces.Take(12).Select((f, i) => f.Info($"faces[{i}]")));

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            FeatureOutcome Attempt(bool marks)
            {
                var picks = new List<Pick> { new(neutral.Label, (doc, mk) => neutral.Select(doc, true, mk), marks ? NeutralMark : 0) };
                picks.AddRange(faces.Select(f => new Pick(f.Label, (doc, mk) => f.Select(doc, true, mk), marks ? DraftFaceMark : 0)));
                SelectAll(d, picks);
                object? api = d.FeatureManager.InsertMultiFaceDraft(ang, q.Flip, false, (int)swDraftFacePropagationType_e.swFacePropNone, false, false);
                d.ClearSelection2(true);
                return Verify(ctx, d, before, DraftSpec, (main, _, checks) => DraftIntent(main, targets, m, ang, checks), q.KeepOnFailure, api, DraftHint);
            }

            FeatureOutcome o;
            try
            {
                o = Attempt(marks: true);
                o = o with { Checks = [.. o.Checks, $"selection: neutral mark {NeutralMark}, faces mark {DraftFaceMark}"] };
            }
            catch (SwException ex) when (!q.KeepOnFailure && IsFeatureFailure(ex) && NothingNew(d, before))
            {
                // The marks for InsertMultiFaceDraft are a best reading: retry once with plain selection order (neutral first).
                try
                {
                    o = Attempt(marks: false);
                    o = o with { Checks = [.. o.Checks, "selection: neutral first, then the faces (no marks)"] };
                }
                catch (SwException ex2) when (IsFeatureFailure(ex2))
                {
                    throw new SwException(ex.Code, $"{ex.Message} (Also tried without selection marks: {Clip(ex2.Message, 300)})", ex.Hint, ex);
                }
            }
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: resolved);
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    /// <summary>A face to draft: its label and, for planar faces, its normal and its hinge line on the neutral plane.</summary>
    private sealed record DraftTarget(string Label, Vec3? N0, Vec3 HingePoint, Vec3 HingeDir);

    /// <summary>Tier B: each planar face reappears turned by the draft angle about its hinge line (within 0.01°).</summary>
    private static List<string> DraftIntent(IFeature main, List<DraftTarget> targets, Vec3 m, double ang, List<string> checks)
    {
        var planes = new List<(Vec3 N, Vec3 P)>();
        foreach (var f in FeatureFaces(main))
        {
            var g = Topology.Describe(f);
            if (g.Surface == "plane" && g.Normal is Vec3 n && PointOn(f) is Vec3 p) planes.Add((n, p));
        }
        var problems = new List<string>();
        int ok = 0, narrow = 0, curved = 0;
        foreach (var t in targets)
        {
            if (t.N0 is not Vec3 n0) { curved++; continue; }
            int k = AdvancedFeatureLogic.MatchDraftedFace(n0, t.HingePoint, t.HingeDir, m, ang, planes, Units.Rad(0.01), PosTol);
            if (k < 0)
            {
                string seen = planes.Count == 0 ? "none"
                    : string.Join(", ", planes.Select(x => F(Units.Deg(AdvancedFeatureLogic.AngleToPull(x.N, m))) + "°").Distinct().Take(6));
                problems.Add($"{t.Label}: no drafted face turned {F(Units.Deg(ang))}° about its line on the neutral plane (the draft's planar faces make {seen} with the pull direction)");
                continue;
            }
            ok++;
            if (planes[k].N.Dot(m) > 0) narrow++;
        }
        if (problems.Count > 0) return problems;
        if (ok > 0)
        {
            string sense = narrow == ok ? $"; the body narrows along [{Dir(m)}]" : narrow == 0 ? $"; the body widens along [{Dir(m)}]" : "";
            checks.Add($"{ok} planar face(s) turned {F(Units.Deg(ang))}° about their line on the neutral plane{sense}");
        }
        if (curved > 0) checks.Add($"{curved} non-planar face(s) not angle-checked");
        return problems;
    }

    // ================================================================== rib

    public Task<FeatureResult> RibAsync(RibRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_rib", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var part = ctx.Part();
            double t = Units.PositiveM(q.ThicknessMm, "thicknessMm");
            double draft = 0;
            if (q.DraftDeg is double dd)
            {
                if (!double.IsFinite(dd) || dd < 0 || dd >= 45) throw SwException.InvalidArgument($"draftDeg must be between 0 and 45 (got {F(dd)}).");
                draft = Units.Rad(dd);
            }
            if (part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Count == 0)
                throw SwException.InvalidArgument("A rib needs a solid body to attach to.", "Make the walls first (feature_extrude).");
            var (feat, sk) = SketchArg(ctx, d, q.Sketch, "rib");
            if (sk.Is3D()) throw SwException.InvalidArgument($"'{feat.Name}' is a 3D sketch; a rib needs a 2D sketch.");
            var segs = sk.Segments().Where(s => !s.ConstructionGeometry).ToList();
            if (segs.Count == 0) throw SwException.InvalidArgument($"Sketch '{feat.Name}' has no segments (construction geometry doesn't count).");
            var s2m = SketchService.SketchToModel(sk);
            var origin = s2m.ApplyPoint(Vec3.Zero);
            var normal = s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized();
            (Vec3 P, Vec3 U)? line = null;
            if (segs.Count == 1 && segs[0] is ISketchLine sl && sl.SkStart() is { } a && sl.SkEnd() is { } b)
            {
                var pa = s2m.ApplyPoint(new Vec3(a.X, a.Y, 0));
                var pb = s2m.ApplyPoint(new Vec3(b.X, b.Y, 0));
                if (pa.DistanceTo(pb) > 1e-9) line = (pa, (pb - pa).Normalized());
            }

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            FeatureOutcome Attempt(bool flip)
            {
                d.ClearSelection2(true);
                if (!feat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{feat.Name}'.");
                d.FeatureManager.InsertRib(q.BothSides, false, t, 0, flip, draft > 0, false, draft, q.NormalToSketch, false); // void
                d.ClearSelection2(true);
                return Verify(ctx, d, before, RibSpec, (main, after, checks) => RibIntent(ctx, main, before, after, origin, normal, line, t, q, draft > 0, checks),
                    q.KeepOnFailure, null, RibHint);
            }

            FeatureOutcome o;
            try
            {
                o = Attempt(q.FlipMaterial);
            }
            catch (SwException ex) when (!q.KeepOnFailure && IsFeatureFailure(ex) && NothingNew(d, before))
            {
                // Which side the material goes to is SOLIDWORKS' choice relative to the sketch; a side that meets no wall fails.
                try
                {
                    o = Attempt(!q.FlipMaterial);
                }
                catch (SwException ex2) when (IsFeatureFailure(ex2))
                {
                    throw new SwException(ex.Code, $"{ex.Message} (The other material side failed too: {Clip(ex2.Message, 300)})", ex.Hint, ex);
                }
                ctx.Warn("The rib met no wall on the requested material side; it was made toward the other side (flipMaterial reversed).");
                o = o with { Checks = [.. o.Checks, "material side flipped"] };
            }
            Consumed(ctx, feat.Name);
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, feat.Name);
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    /// <summary>
    /// Tier B: the thickness reads back, and the rib's side faces lie thicknessMm apart — centered on the sketch plane
    /// (or on one side of it); for a rib normal to the sketch, about the sketch line. A rib that grew the part's box is
    /// reported (it should end at the walls).
    /// </summary>
    private static List<string> RibIntent(SwContext ctx, IFeature main, Snapshot before, Snapshot after, Vec3 origin, Vec3 n, (Vec3 P, Vec3 U)? line,
        double t, RibRequest q, bool drafted, List<string> checks)
    {
        var problems = new List<string>();
        if (main.Definition() is IRibFeatureData2 data)
        {
            if (Math.Abs(data.Thickness - t) > 1e-9) problems.Add($"its thickness reads back as {F(Units.Mm(data.Thickness))} mm, not {F(Units.Mm(t))} mm");
            else if (data.IsTwoSided != q.BothSides) problems.Add($"it reads back {(data.IsTwoSided ? "two-sided" : "one-sided")}, not {(q.BothSides ? "two-sided" : "one-sided")}");
            else checks.Add($"thickness {F(Units.Mm(t))} mm read back");
        }
        else checks.Add("thickness not read back (no feature data)");

        Vec3? w = n;
        if (q.NormalToSketch) w = line is { } ln ? n.Cross(ln.U).Normalized() : (Vec3?)null;
        var at = q.NormalToSketch && line is { } l2 ? l2.P : origin;
        string about = q.NormalToSketch ? "line" : "plane";
        if (drafted) checks.Add("side faces not checked (drafted rib)");
        else if (w is not Vec3 dir) checks.Add("side faces not checked (a rib normal to the sketch with more than one straight line)");
        else
        {
            var offsets = new List<double>();
            foreach (var f in FeatureFaces(main))
            {
                var g = Topology.Describe(f);
                if (g.Surface == "plane" && g.Normal is Vec3 fn && fn.IsParallelTo(dir, 1e-6) && PointOn(f) is Vec3 p) offsets.Add((p - at).Dot(dir));
            }
            bool Has(double x) => offsets.Any(o => Math.Abs(o - x) < PosTol);
            bool ok = q.BothSides ? Has(t / 2) && Has(-t / 2) : Has(0) && (Has(t) || Has(-t));
            if (!ok)
                problems.Add($"no side faces {(q.BothSides ? $"±{F(Units.Mm(t / 2))} mm" : $"0 and {F(Units.Mm(t))} mm")} from the sketch {about} " +
                             $"(its faces parallel to it are at {(offsets.Count == 0 ? "none" : string.Join(", ", offsets.Select(x => F(Units.Mm(x))).Distinct().Take(6)))} mm)");
            else checks.Add($"side faces {F(Units.Mm(t))} mm apart, {(q.BothSides ? "centered on" : "on one side of")} the sketch {about}");
        }
        double grew = Growth(before.Box, after.Box);
        if (grew > PosTol) ctx.Warn($"The rib grew the part's extents by {F(Units.Mm(grew))} mm; check that it ends at the walls you meant.");
        else checks.Add("within the part's previous extents");
        return problems;
    }

    // ================================================================== cosmetic thread

    public Task<FeatureResult> CosmeticThreadAsync(CosmeticThreadRequest q, CancellationToken ct = default) =>
        session.RunAsync("feature_thread_cosmetic", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            var plan = AdvancedFeatureLogic.PlanThread(q.Standard, q.Size, q.DiameterMm, q.DepthMm, q.ThroughAll);
            if (q.Edge is null)
                throw SwException.InvalidArgument("'edge' is required: the circular edge where the thread starts, e.g. {type:'edge',at:[x,y,z]} on a hole's rim.");
            NoOpenSketch(d);
            var edge = EntityResolver.Resolve(ctx, d, q.Edge, "edge", EntityKind.Edge);
            var g = Topology.Describe((IEdge)edge.Obj);
            if (g.Curve != "circle" || g.Radius is not double re)
                throw SwException.InvalidArgument($"'edge' ({edge.Label}) is a {g.Curve} edge; a cosmetic thread starts on a circular edge.",
                    "Pick the rim of a hole or the end edge of a shaft.");
            var (dia, hole) = AdvancedFeatureLogic.ThreadDiameter(plan, Units.Mm(2 * re));
            if (dia is not double diaMm)
                throw SwException.InvalidArgument("Pass diameterMm, or a metric size like \"M6\" to derive it.");

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            SelectAll(d, [new Pick(edge.Label, (doc, m) => edge.Select(doc, true, m), 0)]);
            object? api = d.FeatureManager.InsertCosmeticThread3(plan.Standard, plan.StandardType, plan.SizeName, Units.M(diaMm), plan.EndType,
                plan.DepthMm is double dm ? Units.M(dm) : 0, "");
            d.ClearSelection2(true);
            var o = Verify(ctx, d, before, ThreadSpec, (main, _, checks) => ThreadIntent(main, plan, diaMm, hole, checks), q.KeepOnFailure, api, ThreadHint);
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: [edge.Info("edge")]);
        }, RunOptions.Write(q.Doc), ct);

    /// <summary>Tier B (an annotation has no geometry): the thread's diameter, end condition and depth read back.</summary>
    private static List<string> ThreadIntent(IFeature main, AdvancedFeatureLogic.ThreadPlan plan, double diaMm, bool hole, List<string> checks)
    {
        if (main.Definition() is not ICosmeticThreadFeatureData data)
        {
            checks.Add("thread not read back (no feature data)");
            return [];
        }
        var problems = new List<string>();
        double got = Units.Mm(data.Diameter);
        if (plan.Standard == AdvancedFeatureLogic.NoStandard && Math.Abs(got - diaMm) > 1e-4)
            problems.Add($"its diameter reads back as {F(got)} mm, not {F(diaMm)} mm");
        if (data.EndCondition != plan.EndType)
            problems.Add($"its end condition reads back as {data.EndCondition}, not {(plan.DepthMm is null ? "through" : "blind")}");
        else if (plan.DepthMm is double dm && Math.Abs(Units.Mm(data.BlindDepth) - dm) > 1e-4)
            problems.Add($"its depth reads back as {F(Units.Mm(data.BlindDepth))} mm, not {F(dm)} mm");
        if (problems.Count == 0)
            checks.Add($"{(hole ? "hole" : "shaft")} thread Ø{F(got)} mm, {(plan.DepthMm is double d2 ? $"{F(d2)} mm deep" : "through")} read back");
        return problems;
    }

    // ================================================================== shared helpers

    /// <summary>One reference to select for an API call, with its mark.</summary>
    private sealed record Pick(string Label, Func<IModelDoc2, int, bool> Select, int Mark);

    /// <summary>Selects every pick in order with its mark and reads the selection back (count and marks).</summary>
    private static void SelectAll(IModelDoc2 d, IReadOnlyList<Pick> picks)
    {
        d.ClearSelection2(true);
        var sel = d.Sel();
        for (int i = 0; i < picks.Count; i++)
        {
            var p = picks[i];
            string? problem = null;
            if (!p.Select(d, p.Mark) || sel.GetSelectedObjectCount2(-1) != i + 1) problem = $"Could not select {p.Label}.";
            else if (sel.GetSelectedObjectMark(i + 1) is int got && got != p.Mark) problem = $"{p.Label} was selected with mark {got} instead of {p.Mark}.";
            if (problem is null) continue;
            d.ClearSelection2(true);
            throw new SwException(ErrorCodes.FeatureFailed, problem, "Check the reference (feature_list, face_list, edge_list); the same entity can't be used twice.");
        }
    }

    /// <summary>
    /// Tier A plus the tool's own hint. FeatureVerifier rolls back top-level features; anything left (a sub-feature
    /// such as a cosmetic thread listed under its hole) is deleted here so a failed call leaves nothing behind.
    /// </summary>
    private static FeatureOutcome Verify(SwContext ctx, IModelDoc2 d, Snapshot before, FeatureSpec spec,
        Func<IFeature, Snapshot, List<string>, IEnumerable<string>>? intent, bool keep, object? api, string hint)
    {
        try
        {
            return FeatureVerifier.Check(ctx, d, before, spec, intent, keep, api);
        }
        catch (SwException ex)
        {
            string extra = keep ? "" : DeleteLeftovers(d, before);
            if (extra.Length == 0 && ex.Hint is not null) throw;
            throw new SwException(ex.Code, extra.Length == 0 ? ex.Message : $"{ex.Message} {extra}", ex.Hint ?? hint, ex);
        }
    }

    private static string DeleteLeftovers(IModelDoc2 d, Snapshot before)
    {
        var left = Snapshot.Take(d).Features.Where(kv => !before.Features.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        if (left.Count == 0) return "";
        var names = left.Select(f => f.Name).ToList();
        d.ClearSelection2(true);
        foreach (var f in left) f.Select2(true, 0);
        d.Extension.DeleteSelection2(0); // the features only (an absorbed sketch that existed before stays)
        d.ClearSelection2(true);
        return NothingNew(d, before)
            ? $"Then deleted the remaining {string.Join(", ", names)}; nothing is left behind."
            : $"Could not delete {string.Join(", ", names)} (feature_list shows them).";
    }

    private static bool IsFeatureFailure(SwException ex) =>
        ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed;

    /// <summary>True when the tree holds no feature created since <paramref name="before"/> (a rollback was complete).</summary>
    private static bool NothingNew(IModelDoc2 d, Snapshot before) => Snapshot.Take(d).Features.Keys.All(before.Features.ContainsKey);

    /// <summary>
    /// Features need closed sketches: the server's open sketch is closed when the call uses it; another open sketch
    /// (the user's, or one this call doesn't use) is an error.
    /// </summary>
    private static void CloseServerSketch(SwContext ctx, IModelDoc2 d, IReadOnlyCollection<string> uses)
    {
        var sm = d.SketchManager;
        if (sm.ActiveSketch is not ISketch open) return;
        string name = ((IFeature)open).Name;
        var entry = ctx.DocEntry;
        if (entry.ServerSketch != name)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open, but this server didn't open it.", "Ask the user to exit the sketch first.");
        if (!uses.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open.", "Finish it with sketch_finish first.");
        if (open.Is3D()) sm.Insert3DSketch(true);
        else sm.InsertSketch(true);
        if (sm.ActiveSketch is not null)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' did not close.", "Check dialog_list; a dialog may be open.");
        entry.ServerSketch = null;
        entry.LastSketch = name;
    }

    /// <summary>The sketch named by a helix or rib: the given one, else the server's open sketch (closed now), else the last finished one.</summary>
    private static (IFeature Feature, ISketch Sketch) SketchArg(SwContext ctx, IModelDoc2 d, string? name, string what)
    {
        var entry = ctx.DocEntry;
        if (string.IsNullOrWhiteSpace(name) && d.SketchManager.ActiveSketch is ISketch open && entry.ServerSketch == ((IFeature)open).Name)
            name = ((IFeature)open).Name;
        if (string.IsNullOrWhiteSpace(name)) name = entry.LastSketch;
        if (string.IsNullOrWhiteSpace(name))
            throw SwException.InvalidArgument($"No sketch for the {what}.", "Pass 'sketch' (e.g. \"Sketch1\"), or draw one with sketch_start / sketch_add first.");
        name = name.Trim();
        CloseServerSketch(ctx, d, [name]);
        var feat = EntityResolver.FeatureByName(d, name)
                   ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.", "feature_list shows the sketches.");
        if (feat.Specific() is not ISketch sk || EntityResolver.FeatureKind(feat) != EntityKind.Sketch)
            throw SwException.InvalidArgument($"'{name}' is a {feat.GetTypeName2()}, not a sketch.");
        return (feat, sk);
    }

    /// <summary>INVALID_ARGUMENT for a null entry in a list of references (JSON null inside the array).</summary>
    private static void NoNullRefs(IReadOnlyList<EntityRef?>? refs, string arg)
    {
        if (refs is null) return;
        for (int i = 0; i < refs.Count; i++)
            if (refs[i] is null) throw SwException.InvalidArgument($"{arg}[{i}] is null.", "Pass an entity reference, e.g. {name:'Sketch1'} or {at:[x,y,z]}.");
    }

    /// <summary>The names the references give (a sketch the call uses is named, e.g. {name:'Sketch2'}).</summary>
    private static List<string> RefNames(IEnumerable<EntityRef?> refs) =>
        refs.Where(r => !string.IsNullOrWhiteSpace(r?.Name)).Select(r => r!.Name!.Trim()).ToList();

    /// <summary>
    /// Selects a sketch feature with a mark the way the verified sweep and loft recipes do (SelectByID2 by name with type
    /// SKETCH at the origin); IFeature.Select2 is the fallback (a 3D sketch has another selection type).
    /// </summary>
    private static bool SelectSketch(IModelDoc2 doc, IFeature sketch, int mark) =>
        doc.Extension.SelectByID2(sketch.Name, "SKETCH", 0, 0, 0, true, mark, null, 0) || sketch.Select2(true, mark);

    /// <summary>A consumed sketch is no longer "the last sketch" that defaults the next feature.</summary>
    private static void Consumed(SwContext ctx, params string?[] sketches)
    {
        var entry = ctx.DocEntry;
        if (entry.LastSketch is string last && sketches.Any(s => SameName(s, last))) entry.LastSketch = null;
    }

    private static bool SameName(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A sketch's non-construction segments as chain pieces (2D: sketch coordinates; 3D: model). Lines, arcs, circles
    /// and full ellipses are exact; splines (fit points), partial ellipses and 3D arcs are approximations.
    /// </summary>
    private static List<AdvancedFeatureLogic.Seg> SketchSegs(ISketch sk, out bool exactShapes)
    {
        bool is3D = sk.Is3D();
        var list = new List<AdvancedFeatureLogic.Seg>();
        exactShapes = true;
        foreach (var s in sk.Segments())
        {
            if (s.ConstructionGeometry) continue;
            double len = s.GetLength();
            switch (s.GetType())
            {
                case (int)swSketchSegments_e.swSketchLINE when s is ISketchLine l && l.SkStart() is { } a && l.SkEnd() is { } b:
                    list.Add(AdvancedFeatureLogic.Seg.Line(P(a), P(b), len));
                    break;
                case (int)swSketchSegments_e.swSketchARC when s is ISketchArc arc && arc.SkCenter() is { } c:
                {
                    double r = arc.GetRadius();
                    if (arc.IsCircle() != 0)
                    {
                        list.Add(AdvancedFeatureLogic.Seg.Circle(P(c), r));
                        if (is3D) exactShapes = false;
                        break;
                    }
                    if (arc.SkStart() is not { } a0 || arc.SkEnd() is not { } a1) { exactShapes = false; break; }
                    if (is3D)
                    {
                        list.Add(AdvancedFeatureLogic.Seg.Chord(P(a0), P(a1), len));
                        exactShapes = false;
                        break;
                    }
                    // The length tells the sense of any arc but a half circle; there GetRotationDir (1 = counter-clockwise) is a
                    // reading of the API help, so such an arc makes the area inexact and gives only its end points as samples.
                    bool known = AdvancedFeatureLogic.ArcSenseKnown(P(c), r, P(a0), P(a1), len);
                    bool ccw = AdvancedFeatureLogic.ArcCcw(P(c), r, P(a0), P(a1), len, arc.GetRotationDir());
                    list.Add(AdvancedFeatureLogic.Seg.Arc(P(c), r, P(a0), P(a1), ccw, known));
                    if (!known) exactShapes = false;
                    break;
                }
                case (int)swSketchSegments_e.swSketchELLIPSE when s is ISketchEllipse e && e.SkCenter() is { } ec && e.SkMajor() is { } em && e.SkMinor() is { } en:
                {
                    var st = e.SkStart();
                    var nd = e.SkEnd();
                    bool full = st is null || nd is null || P(st).DistanceTo(P(nd)) <= AdvancedFeatureLogic.ChainTol;
                    if (full && !is3D) list.Add(AdvancedFeatureLogic.Seg.Ellipse(P(ec), P(em), P(en).DistanceTo(P(ec))));
                    else
                    {
                        exactShapes = false;
                        if (st is not null && nd is not null) list.Add(AdvancedFeatureLogic.Seg.Chord(P(st), P(nd), len));
                    }
                    break;
                }
                case (int)swSketchSegments_e.swSketchSPLINE when s is ISketchSpline sp:
                {
                    exactShapes = false;
                    var pts = sp.SkFitPoints().Select(P).ToList();
                    if (pts.Count >= 2) list.Add(AdvancedFeatureLogic.Seg.Polyline(pts, len));
                    break;
                }
                default:
                    exactShapes = false; // text, parabolas, …: their area and ends are not read
                    break;
            }
        }
        return list;
    }

    private static Vec3 P(ISketchPoint p) => new(p.X, p.Y, p.Z);

    /// <summary>At most <paramref name="max"/> items, evenly spread over the list.</summary>
    private static List<Vec3> Spread(List<Vec3> pts, int max)
    {
        if (pts.Count <= max) return pts;
        return Enumerable.Range(0, max).Select(i => pts[(int)((long)i * (pts.Count - 1) / (max - 1))]).ToList();
    }

    /// <summary>How far the model box grew in any direction (m; 0 when it didn't or a box is missing).</summary>
    private static double Growth(double[]? before, double[]? after)
    {
        if (before is not { Length: 6 } b || after is not { Length: 6 } a) return 0;
        double g = 0;
        for (int i = 0; i < 3; i++) g = Math.Max(g, Math.Max(b[i] - a[i], a[i + 3] - b[i + 3]));
        return g;
    }

    /// <summary>A plane (point, unit normal) from a reference plane or a planar face.</summary>
    private static (Vec3 Q, Vec3 M) PlaneOf(Resolved r, string arg)
    {
        switch (r.Obj)
        {
            case IFeature f when f.Specific() is IRefPlane rp:
            {
                var t = ComArgs.Doubles((object?)rp.Transform?.ArrayData);
                if (t.Length < 12) throw new SwException(ErrorCodes.Internal, $"Could not read the frame of {r.Label}.");
                return (new Vec3(t[9], t[10], t[11]), new Vec3(t[6], t[7], t[8]).Normalized()); // normal = the transform's Z row
            }
            case IFace2 face:
            {
                var g = Topology.Describe(face);
                if (g.Surface != "plane" || g.Normal is not Vec3 n || PointOn(face) is not Vec3 p)
                    throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is a {g.Surface} face; it needs a plane or a planar face.");
                return (p, n);
            }
        }
        throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is not a plane or a planar face.");
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

    /// <summary>Some point on a face (the closest to the origin).</summary>
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

    private static string F(double v) => AdvancedFeatureLogic.F(v);
}

// ====================================================================== COM-free logic

/// <summary>
/// The COM-free rules of the advanced-feature tools: sketch chains and profile areas, helix parameters and length,
/// the sweep-volume verdict, the drafted-face match and cosmetic-thread sizes. Tested offline (AdvancedFeatureLogicTests).
/// </summary>
internal static class AdvancedFeatureLogic
{
    /// <summary>m: sketch end points closer than this are one point.</summary>
    public const double ChainTol = 1e-7;

    /// <summary>swCosmeticStandardType_e.swStandardType_StandardNone: a custom diameter.</summary>
    public const int NoStandard = -2;

    internal static string F(double v) => (v == 0 ? 0 : v).ToString("0.###", CultureInfo.InvariantCulture);

    internal static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    // ------------------------------------------------------------------ chains and profile areas

    /// <summary>
    /// One sketch segment as a chain piece: its ends A → B, sample points from A to B, its length, and
    /// ½∫(x dy − y dx) along it in the sketch's XY plane (the shoelace / Green's-theorem area term).
    /// </summary>
    internal sealed class Seg
    {
        public required string Kind { get; init; }
        public required Vec3 A { get; init; }
        public required Vec3 B { get; init; }
        public bool Closed { get; init; }
        public double Green { get; init; }
        public double Length { get; init; }
        public bool Exact { get; init; } = true;
        public required IReadOnlyList<Vec3> Samples { get; init; }

        public static Seg Line(Vec3 a, Vec3 b, double? length = null) => new()
        {
            Kind = "line", A = a, B = b, Green = 0.5 * (a.X * b.Y - b.X * a.Y), Length = length ?? a.DistanceTo(b), Samples = [a, (a + b) / 2, b],
        };

        /// <summary>
        /// A circular arc in the XY plane about <paramref name="c"/> from <paramref name="a"/> to <paramref name="b"/>. With
        /// <paramref name="senseKnown"/> false (a half circle, whose sense its length can't tell) the arc counts as inexact
        /// and only its end points are sample points, so a wrong guess can't fail a check.
        /// </summary>
        public static Seg Arc(Vec3 c, double r, Vec3 a, Vec3 b, bool ccw, bool senseKnown = true)
        {
            double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X), a1 = Math.Atan2(b.Y - c.Y, b.X - c.X);
            double sweep = Sweep(a0, a1, ccw);
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 32)));
            var pts = new List<Vec3>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double t = a0 + sweep * i / n;
                pts.Add(new Vec3(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t), c.Z));
            }
            double e = a0 + sweep;
            double green = 0.5 * (r * (c.X * (Math.Sin(e) - Math.Sin(a0)) - c.Y * (Math.Cos(e) - Math.Cos(a0))) + r * r * sweep);
            return new Seg
            {
                Kind = "arc", A = a, B = b, Green = green, Length = r * Math.Abs(sweep), Exact = senseKnown, Samples = senseKnown ? pts : [a, b],
            };
        }

        /// <summary>A full circle, counter-clockwise.</summary>
        public static Seg Circle(Vec3 c, double r)
        {
            var pts = Enumerable.Range(0, 65).Select(i => new Vec3(c.X + r * Math.Cos(Math.PI * i / 32), c.Y + r * Math.Sin(Math.PI * i / 32), c.Z)).ToList();
            return new Seg { Kind = "circle", A = pts[0], B = pts[0], Closed = true, Green = Math.PI * r * r, Length = 2 * Math.PI * r, Samples = pts };
        }

        /// <summary>A full ellipse in the XY plane: center, a point at the end of the major axis, the minor radius.</summary>
        public static Seg Ellipse(Vec3 c, Vec3 majorPoint, double b)
        {
            var u = majorPoint - c;
            double a = u.Length;
            var ux = a > 0 ? u / a : Vec3.UnitX;
            var uy = new Vec3(-ux.Y, ux.X, 0);
            var pts = Enumerable.Range(0, 65).Select(i =>
            {
                double t = Math.PI * i / 32;
                return c + ux * (a * Math.Cos(t)) + uy * (b * Math.Sin(t));
            }).ToList();
            double h = Math.Pow(a - b, 2) / Math.Pow(a + b, 2);
            double length = Math.PI * (a + b) * (1 + 3 * h / (10 + Math.Sqrt(4 - 3 * h))); // Ramanujan
            return new Seg { Kind = "ellipse", A = pts[0], B = pts[0], Closed = true, Green = Math.PI * a * b, Length = length, Samples = pts };
        }

        /// <summary>A curve known only by its ends and length (3D arcs, partial ellipses, reference-curve edges).</summary>
        public static Seg Chord(Vec3 a, Vec3 b, double length) => new()
        {
            Kind = "curve", A = a, B = b, Green = 0.5 * (a.X * b.Y - b.X * a.Y), Length = length, Exact = false, Samples = [a, b],
        };

        /// <summary>A curve approximated by points (a spline's fit points); closed when the ends meet.</summary>
        public static Seg Polyline(IReadOnlyList<Vec3> pts, double? length = null)
        {
            double g = 0, l = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                g += 0.5 * (pts[i].X * pts[i + 1].Y - pts[i + 1].X * pts[i].Y);
                l += pts[i].DistanceTo(pts[i + 1]);
            }
            bool closed = pts.Count > 2 && pts[0].DistanceTo(pts[^1]) <= ChainTol;
            return new Seg { Kind = "spline", A = pts[0], B = pts[^1], Closed = closed, Green = g, Length = length ?? l, Exact = false, Samples = [.. pts] };
        }

        /// <summary>Signed sweep from angle a0 to a1: in (0, 2π] counter-clockwise, in [−2π, 0) clockwise (equal angles: a full turn).</summary>
        public static double Sweep(double a0, double a1, bool ccw)
        {
            double d = a1 - a0;
            d -= 2 * Math.PI * Math.Floor(d / (2 * Math.PI)); // [0, 2π)
            if (ccw) return d < 1e-12 ? 2 * Math.PI : d;
            return d < 1e-12 ? -2 * Math.PI : d - 2 * Math.PI;
        }
    }

    /// <summary>Segments joined end to end, each possibly traversed backwards.</summary>
    internal sealed class Chain
    {
        public required IReadOnlyList<(Seg Seg, bool Reversed)> Parts { get; init; }
        public bool Closed { get; init; }
        public Vec3 Start => Parts[0].Reversed ? Parts[0].Seg.B : Parts[0].Seg.A;
        public Vec3 End => Parts[^1].Reversed ? Parts[^1].Seg.A : Parts[^1].Seg.B;
        public double Length => Parts.Sum(p => p.Seg.Length);
        public double SignedArea => Parts.Sum(p => p.Reversed ? -p.Seg.Green : p.Seg.Green);
        public bool Exact => Parts.All(p => p.Seg.Exact);

        /// <summary>The sample points in traversal order (junctions once).</summary>
        public List<Vec3> Points()
        {
            var list = new List<Vec3>();
            foreach (var (s, rev) in Parts)
                foreach (var p in rev ? s.Samples.Reverse() : s.Samples)
                    if (list.Count == 0 || list[^1].DistanceTo(p) > ChainTol) list.Add(p);
            return list;
        }
    }

    internal sealed record ChainSet(IReadOnlyList<Chain> Chains, bool Branched)
    {
        public IEnumerable<Chain> Loops => Chains.Where(c => c.Closed);
        public IEnumerable<Chain> Open => Chains.Where(c => !c.Closed);
    }

    /// <summary>Joins segments into chains by their end points (closed loops and open chains); Branched when a point joins more than two ends.</summary>
    public static ChainSet Chains(IReadOnlyList<Seg> segs, double tol = ChainTol)
    {
        var chains = new List<Chain>();
        foreach (var loop in segs.Where(x => x.Closed)) chains.Add(new Chain { Parts = [(loop, false)], Closed = true });
        var free = segs.Where(x => !x.Closed).ToList();
        var ends = free.SelectMany(x => new[] { x.A, x.B }).ToList();
        bool branched = ends.Any(p => ends.Count(x => x.DistanceTo(p) <= tol) > 2);
        var used = new bool[free.Count];

        int? From(Vec3 p, out bool startsAtP)
        {
            for (int j = 0; j < free.Count; j++)
            {
                if (used[j]) continue;
                if (free[j].A.DistanceTo(p) <= tol) { startsAtP = true; return j; }
                if (free[j].B.DistanceTo(p) <= tol) { startsAtP = false; return j; }
            }
            startsAtP = false;
            return null;
        }

        for (int i = 0; i < free.Count; i++)
        {
            if (used[i]) continue;
            used[i] = true;
            var parts = new LinkedList<(Seg, bool)>();
            parts.AddLast((free[i], false));
            Vec3 head = free[i].A, tail = free[i].B;
            bool closed = head.DistanceTo(tail) <= tol;
            while (!closed && From(tail, out bool s) is int j)
            {
                used[j] = true;
                parts.AddLast((free[j], !s));
                tail = s ? free[j].B : free[j].A;
                closed = tail.DistanceTo(head) <= tol;
            }
            while (!closed && From(head, out bool s) is int j)
            {
                used[j] = true;
                parts.AddFirst((free[j], s));
                head = s ? free[j].B : free[j].A;
                closed = head.DistanceTo(tail) <= tol;
            }
            chains.Add(new Chain { Parts = [.. parts], Closed = closed });
        }
        return new ChainSet(chains, branched);
    }

    /// <summary>
    /// The area the closed loops enclose (sketch XY), holes subtracted: a loop inside an odd number of others is a hole.
    /// Exact when every piece is (lines, arcs, circles, full ellipses).
    /// </summary>
    public static (double Area, bool Exact) RegionArea(ChainSet set)
    {
        var loops = set.Loops.ToList();
        var polys = loops.Select(l => l.Points()).ToList();
        double area = 0;
        for (int i = 0; i < loops.Count; i++)
        {
            var probe = polys[i].Count >= 2 ? (polys[i][0] + polys[i][1]) / 2 : polys[i][0];
            int depth = 0;
            for (int j = 0; j < loops.Count; j++)
                if (j != i && InsidePolygon(probe, polys[j])) depth++;
            area += Math.Abs(loops[i].SignedArea) * (depth % 2 == 0 ? 1 : -1);
        }
        return (area, loops.All(l => l.Exact));
    }

    /// <summary>Even-odd point-in-polygon test in the XY plane.</summary>
    public static bool InsidePolygon(Vec3 p, IReadOnlyList<Vec3> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// Which way a sketch arc runs from its start to its end: the sense whose length r·|sweep| matches the measured
    /// length (anything but a half circle), else the reported rotation direction (+1 = counter-clockwise).
    /// </summary>
    public static bool ArcCcw(Vec3 c, double r, Vec3 a, Vec3 b, double length, int rotationDir) =>
        ArcSenseKnown(c, r, a, b, length) ? ArcLengthCcw(c, r, a, b, length) : rotationDir >= 0;

    /// <summary>True when the measured length tells an arc's sense: the two ways round differ in length (not a half circle).</summary>
    public static bool ArcSenseKnown(Vec3 c, double r, Vec3 a, Vec3 b, double length)
    {
        var (lc, lw) = ArcLengths(c, r, a, b);
        return length > 0 && Math.Abs(lc - lw) > 1e-9 + 1e-6 * length;
    }

    private static bool ArcLengthCcw(Vec3 c, double r, Vec3 a, Vec3 b, double length)
    {
        var (lc, lw) = ArcLengths(c, r, a, b);
        return Math.Abs(lc - length) < Math.Abs(lw - length);
    }

    /// <summary>The lengths from a to b counter-clockwise and clockwise about c.</summary>
    private static (double Ccw, double Cw) ArcLengths(Vec3 c, double r, Vec3 a, Vec3 b)
    {
        double a0 = Math.Atan2(a.Y - c.Y, a.X - c.X), a1 = Math.Atan2(b.Y - c.Y, b.X - c.X);
        return (r * Math.Abs(Seg.Sweep(a0, a1, true)), r * Math.Abs(Seg.Sweep(a0, a1, false)));
    }

    // ------------------------------------------------------------------ sweep

    internal sealed record VolumeVerdict(string? Problem, string Check);

    /// <summary>
    /// Tier B of a sweep from its volume change. An isolated boss (nothing merged with it) has exactly the swept volume:
    /// profile area × path length on a straight path (× cos of the profile's tilt to it), about that on a curved one.
    /// Much less means SOLIDWORKS swept only part of the path (a profile away from the path's start; notes: Helix).
    /// </summary>
    public static VolumeVerdict SweepVolume(double dv, double? area, double length, double cos, bool straight, bool exactArea, bool isolated,
        bool guides, bool twistedTilted, bool offStart)
    {
        if (!isolated) return new(null, "swept volume not compared (the sweep merges with or cuts existing material)");
        if (guides) return new(null, "swept volume not compared (guide curves reshape the section)");
        if (area is not double a || a <= 0 || length <= 0) return new(null, "swept volume not compared (profile area not computed)");
        double expect = a * length * (straight ? cos : 1);
        if (expect <= 0) return new(null, "swept volume not compared (the profile is parallel to the path)");
        double ratio = dv / expect;
        string vs = $"{F(Units.Mm3(dv))} mm³ vs profile area {F(Units.Mm2(a))} mm² x path length {F(Units.Mm(length))} mm" +
                    (straight && cos < 1 - 1e-9 ? $" x cos {cos.ToString("0.####", CultureInfo.InvariantCulture)}" : "");
        if (straight && exactArea && !twistedTilted)
            return Math.Abs(ratio - 1) > 1e-3
                ? new($"the swept volume is {vs} = {F(Units.Mm3(expect))} mm³", "")
                : new(null, $"volume = profile area x path length ({vs})");
        if (ratio < 0.5 || (offStart && ratio < 0.9))
            return new($"the sweep covers only part of the path: {vs} (ratio {ratio.ToString("0.###", CultureInfo.InvariantCulture)})", "");
        return new(null, $"volume {ratio.ToString("0.###", CultureInfo.InvariantCulture)} x profile area x path length ({vs})");
    }

    // ------------------------------------------------------------------ helix

    /// <summary>InsertHelix arguments (meters, radians) and how the helix was defined.</summary>
    internal sealed record HelixPlan(string Mode, int DefinedBy, double HeightM, double PitchM, double Revolutions, double StartRad, bool Tapered,
        bool Outward, double TaperRad);

    /// <summary>
    /// Validates the helix parameters for <paramref name="definedBy"/> (swHelixDefinedBy_e: 0 pitch and revolutions, 1
    /// height and revolutions, 2 height and pitch, 3 spiral) and derives the third value. A value the mode doesn't use
    /// is accepted only when it agrees.
    /// </summary>
    public static HelixPlan PlanHelix(string? definedBy, double? pitchMm, double? revolutions, double? heightMm, double startAngleDeg, double? taperDeg)
    {
        string mode = Norm(definedBy) switch
        {
            null or "pitch_revolutions" or "pitch_and_revolutions" or "pitch_revolution" => "pitch_revolutions",
            "height_revolutions" or "height_and_revolutions" or "height_revolution" => "height_revolutions",
            "height_pitch" or "height_and_pitch" => "height_pitch",
            "spiral" => "spiral",
            _ => throw SwException.InvalidArgument($"Unknown definedBy '{definedBy}'.", "Use pitch_revolutions, height_revolutions, height_pitch or spiral."),
        };
        double? pitch = Positive(pitchMm, "pitchMm"), revs = Positive(revolutions, "revolutions"), height = Positive(heightMm, "heightMm");
        SwException Need(string what) => SwException.InvalidArgument($"definedBy '{mode}' needs {what}.");
        void Agree(double? given, double derived, string name, string formula)
        {
            if (given is double g && Math.Abs(g - derived) > 1e-6 * Math.Max(1, derived))
                throw SwException.InvalidArgument($"{name} {F(g)} disagrees with {formula} = {F(derived)} for definedBy '{mode}'.", $"Leave {name} out.");
        }
        double h, p, n;
        switch (mode)
        {
            case "pitch_revolutions":
                p = pitch ?? throw Need("pitchMm and revolutions");
                n = revs ?? throw Need("pitchMm and revolutions");
                h = p * n;
                Agree(height, h, "heightMm", "pitchMm x revolutions");
                break;
            case "height_revolutions":
                h = height ?? throw Need("heightMm and revolutions");
                n = revs ?? throw Need("heightMm and revolutions");
                p = h / n;
                Agree(pitch, p, "pitchMm", "heightMm / revolutions");
                break;
            case "height_pitch":
                h = height ?? throw Need("heightMm and pitchMm");
                p = pitch ?? throw Need("heightMm and pitchMm");
                n = h / p;
                Agree(revs, n, "revolutions", "heightMm / pitchMm");
                break;
            default:
                p = pitch ?? throw Need("pitchMm (the gap between turns) and revolutions");
                n = revs ?? throw Need("pitchMm (the gap between turns) and revolutions");
                if (height is not null) throw SwException.InvalidArgument("heightMm doesn't apply to a spiral (it is flat).", "Leave heightMm out.");
                h = 0;
                break;
        }
        if (n > 1000) throw SwException.InvalidArgument($"At most 1000 revolutions (got {F(n)}).");
        if (n < 0.01) throw SwException.InvalidArgument($"At least 0.01 revolutions (got {F(n)}).");
        if (h > 100_000) throw SwException.InvalidArgument($"The height may be at most 100000 mm (got {F(h)}).");
        if (!double.IsFinite(startAngleDeg) || Math.Abs(startAngleDeg) > 360)
            throw SwException.InvalidArgument($"startAngleDeg must be between -360 and 360 (got {F(startAngleDeg)}).");
        if (taperDeg is double td && (!double.IsFinite(td) || Math.Abs(td) >= 89))
            throw SwException.InvalidArgument($"taperDeg must be between -89 and 89 (got {F(td)}).");
        bool tapered = taperDeg is double t && t != 0;
        if (tapered && mode == "spiral") throw SwException.InvalidArgument("A spiral can't be tapered.", "Leave taperDeg out.");
        int code = mode switch { "pitch_revolutions" => 0, "height_revolutions" => 1, "height_pitch" => 2, _ => 3 };
        return new HelixPlan(mode, code, Units.M(h), Units.M(p), n, Units.Rad(startAngleDeg), tapered, taperDeg > 0,
            tapered ? Units.Rad(Math.Abs(taperDeg!.Value)) : 0);
    }

    private static double? Positive(double? v, string name) => v switch
    {
        null => null,
        double x when double.IsFinite(x) && x > 0 => x,
        double x => throw SwException.InvalidArgument($"'{name}' must be positive (got {F(x)})."),
    };

    /// <summary>
    /// Length of a helix or spiral whose radius goes linearly from <paramref name="r0"/> to <paramref name="r1"/> over
    /// <paramref name="n"/> turns while it rises <paramref name="h"/> (Simpson's rule; exact for a constant radius:
    /// n·√((2πr)² + pitch²)).
    /// </summary>
    public static double HelixLength(double r0, double r1, double h, double n)
    {
        const int N = 2000; // even
        double dr = r1 - r0;
        double Speed(double s)
        {
            double rho = r0 + dr * s;
            return Math.Sqrt(dr * dr + Math.Pow(2 * Math.PI * n * rho, 2) + h * h);
        }
        double sum = Speed(0) + Speed(1);
        for (int i = 1; i < N; i++) sum += Speed((double)i / N) * (i % 2 == 1 ? 4 : 2);
        return sum / (3 * N);
    }

    /// <summary>What a helix's definition reads back differently from the plan (only the values its mode defines).</summary>
    public static List<string> HelixReadBack(HelixPlan plan, int definedBy, double heightM, double pitchM, double revolutions)
    {
        var wrong = new List<string>();
        bool Off(double got, double want) => Math.Abs(got - want) > 1e-9 + 1e-6 * Math.Abs(want);
        if (definedBy != plan.DefinedBy) wrong.Add($"definedBy {definedBy} (expected {plan.DefinedBy})");
        bool useHeight = plan.Mode is "height_revolutions" or "height_pitch";
        bool usePitch = plan.Mode is "pitch_revolutions" or "height_pitch" or "spiral";
        bool useRevs = plan.Mode is "pitch_revolutions" or "height_revolutions" or "spiral";
        if (useHeight && Off(heightM, plan.HeightM)) wrong.Add($"height {F(Units.Mm(heightM))} mm (expected {F(Units.Mm(plan.HeightM))})");
        if (usePitch && Off(pitchM, plan.PitchM)) wrong.Add($"pitch {F(Units.Mm(pitchM))} mm (expected {F(Units.Mm(plan.PitchM))})");
        if (useRevs && Off(revolutions, plan.Revolutions)) wrong.Add($"{F(revolutions)} revolutions (expected {F(plan.Revolutions)})");
        return wrong;
    }

    // ------------------------------------------------------------------ draft

    /// <summary>The angle (radians) between a face with unit normal <paramref name="n"/> and the pull direction <paramref name="m"/>: 0 for a wall parallel to it.</summary>
    public static double AngleToPull(Vec3 n, Vec3 m) => Math.Asin(Math.Clamp(Math.Abs(n.Dot(m)), 0, 1));

    /// <summary>The line where two planes n·x = h meet (a point on it and its unit direction), or null when they are parallel.</summary>
    public static (Vec3 Point, Vec3 Dir)? PlaneIntersection(Vec3 na, double ha, Vec3 nb, double hb)
    {
        var dir = na.Cross(nb);
        double l2 = dir.Dot(dir);
        if (l2 < 1e-18) return null;
        var p = (nb.Cross(dir) * ha + dir.Cross(na) * hb) / l2;
        return (p, dir.Normalized());
    }

    /// <summary>
    /// The planar face (normal, point) that is the face with normal <paramref name="n0"/> drafted by
    /// <paramref name="angle"/>: turned about its hinge line (where it met the neutral plane) by the angle, or set at the
    /// angle to the pull direction <paramref name="m"/>, and still through the hinge line. -1 when none is.
    /// </summary>
    public static int MatchDraftedFace(Vec3 n0, Vec3 hingePoint, Vec3 hingeDir, Vec3 m, double angle, IReadOnlyList<(Vec3 N, Vec3 P)> planes,
        double angTol, double posTol)
    {
        var h = hingeDir.Normalized();
        for (int i = 0; i < planes.Count; i++)
        {
            var (n1, p1) = planes[i];
            double tilt = Math.Acos(Math.Clamp(n0.Dot(n1), -1, 1));
            if (Math.Abs(tilt - angle) > angTol && Math.Abs(AngleToPull(n1, m) - angle) > angTol) continue;
            if (Math.Abs(n1.Dot(h)) > Math.Sin(angTol)) continue;     // turned about the hinge line
            if (Math.Abs(n1.Dot(hingePoint - p1)) > posTol) continue; // still through it
            return i;
        }
        return -1;
    }

    // ------------------------------------------------------------------ cosmetic thread

    /// <summary>InsertCosmeticThread3 arguments: standard (swCosmeticStandardType_e), its type and size names, end type (swCosmeticEndConditions_e).</summary>
    internal sealed record ThreadPlan(int Standard, string StandardName, string StandardType, string SizeName, double? DiameterMm, double? NominalMm,
        double? PitchMm, int EndType, double? DepthMm);

    public static ThreadPlan PlanThread(string? standard, string? size, double? diameterMm, double? depthMm, bool throughAll)
    {
        string std = Norm(standard) ?? "none";
        int code = std switch
        {
            "none" or "custom" => NoStandard,
            "ansi_inch" => 0, "ansi_metric" => 1, "bsi" => 2, "din" => 4, "iso" => 8, "jis" => 9, "gb" => 13, "ks" => 14, "is" => 15, "as" => 16,
            _ => throw SwException.InvalidArgument($"Unknown standard '{standard}'.",
                "Use none (the default: a custom diameter), ansi_metric, ansi_inch, iso, din, jis, bsi, gb, ks, is or as."),
        };
        if (depthMm is double dm)
        {
            if (!double.IsFinite(dm) || dm <= 0) throw SwException.InvalidArgument($"depthMm must be positive (got {F(dm)}).");
            if (throughAll) throw SwException.InvalidArgument("Pass depthMm or throughAll, not both.");
        }
        else if (!throughAll) throw SwException.InvalidArgument("Pass depthMm (the thread length) or throughAll: true.");
        if (diameterMm is double dd && (!double.IsFinite(dd) || dd <= 0 || dd > 2000))
            throw SwException.InvalidArgument($"diameterMm must be a positive diameter in mm (got {F(dd)}).");
        var metric = HolePlan.MetricSize(size);
        double? nominal = metric?.Major;
        double? pitch = metric is { } ms ? ms.Pitch ?? (HolePlan.CoarsePitch.TryGetValue(ms.Major, out double cp) ? cp : (double?)null) : null;
        string sizeName = "";
        if (code != NoStandard)
        {
            if (string.IsNullOrWhiteSpace(size))
                throw SwException.InvalidArgument($"standard '{std}' needs 'size' as the standard names it, e.g. \"M6x1.0\".", "Or use standard none with diameterMm.");
            sizeName = metric is { } m2 && pitch is double p2 ? $"M{F(m2.Major)}x{p2.ToString("0.0##", CultureInfo.InvariantCulture)}" : size.Trim();
        }
        else if (diameterMm is null && metric is null)
            throw SwException.InvalidArgument(string.IsNullOrWhiteSpace(size) ? "Pass diameterMm or a metric size like \"M6\"."
                : $"size '{size}' is not a metric size like \"M6\" or \"M6x0.75\".", "Pass diameterMm for other threads.");
        return new ThreadPlan(code, std, code == NoStandard ? "" : "Machine Threads", sizeName, diameterMm, nominal, pitch,
            throughAll ? 2 : 0, depthMm);
    }

    /// <summary>
    /// The thread diameter (mm) on an edge: the given one, else from the metric size — a hole (its edge smaller than the
    /// nominal size) gets the major diameter, a shaft the basic minor diameter d − 1.082532·P (ISO 724).
    /// </summary>
    public static (double? Diameter, bool Hole) ThreadDiameter(ThreadPlan plan, double edgeDiameterMm)
    {
        if (plan.DiameterMm is double d) return (d, d > edgeDiameterMm);
        if (plan.NominalMm is not double nom) return (null, false);
        if (edgeDiameterMm < nom - 1e-6) return (nom, true);
        return (Math.Round(nom - 1.082532 * (plan.PitchMm ?? 0), 4), false);
    }
}
