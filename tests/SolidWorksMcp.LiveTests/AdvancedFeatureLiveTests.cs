using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Sweep, loft, helix, draft, rib and cosmetic thread against a live SOLIDWORKS, with analytic oracles (DESIGN §6.3).
/// The sweep, loft and helix recipes are verified (docs/solidworks-api-notes.md §4); the tests marked "best guess"
/// exercise unverified API paths (cut sweep and loft, circular profile, twist, draft marks, rib, cosmetic thread).
/// Every tool also has negative tests (typed errors, rollback).
/// </summary>
public class AdvancedFeatureLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-2;  // mm³: planar oracles
    private const double Vq = 2e-2; // mm³: cylinders
    private const double L = 1e-3;  // mm: positions read back from curves

    private AdvancedFeatureService Af => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private InspectService Ins => new(S);
    private ReferenceService Ref => new(S);

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane, centered in x and z: x ∈ [-50, 50], y ∈ [0, 20], z ∈ [-30, 30].</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await SketchAsync(doc, Named("top"), new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 });
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    /// <summary>An L bracket in the front plane, 40 deep (z ∈ [-20, 20]): a 60 × 10 base and a 10 × 60 upright, inner corner at (10, 10).</summary>
    private async Task<string> BracketAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await SketchAsync(doc, Named("front"), new SketchEntity
        {
            Kind = "polyline", Points = [[0, 0], [60, 0], [60, 10], [10, 10], [10, 60], [0, 60]], Closed = true,
        });
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, End = "mid_plane", DepthMm = 40 });
        Assert.Equal((600 + 500) * 40, r.Verified.VolumeDeltaMm3, V); // 44 000 mm³
        return doc;
    }

    /// <summary>Draws the entities in a new sketch on <paramref name="on"/>; finishes it unless <paramref name="leaveOpen"/>. Returns its name.</summary>
    private async Task<string> SketchAsync(string doc, EntityRef on, SketchEntity entity, bool leaveOpen = false) =>
        await SketchAsync(doc, on, [entity], leaveOpen);

    private async Task<string> SketchAsync(string doc, EntityRef on, SketchEntity[] entities, bool leaveOpen = false)
    {
        var s = await Sk.StartAsync(doc, on, false);
        await Sk.AddAsync(doc, entities);
        if (!leaveOpen) await Sk.FinishAsync(doc);
        return s.Sketch;
    }

    /// <summary>A circle of radius 8 at the origin of the top plane and a 4-turn, 5 mm pitch helix on it (start at (0, 0, 8): verified).</summary>
    private async Task<HelixResult> SpringHelixAsync(string doc)
    {
        string circle = await SketchAsync(doc, Named("top"), Circle([0, 0], 8));
        var h = await Af.HelixAsync(new HelixRequest { Doc = doc, Sketch = circle, DefinedBy = "pitch_revolutions", PitchMm = 5, Revolutions = 4 });
        Assert.Equal("Helix", h.Feature.Type);
        return h;
    }

    private static EntityRef Named(string name) => new() { Name = name };
    private static SketchEntity Line(double[] from, double[] to) => new() { Kind = "line", From = from, To = to };
    private static SketchEntity Circle(double[] center, double radiusMm) => new() { Kind = "circle", Center = center, RadiusMm = radiusMm };
    private static SketchEntity Square(double sizeMm) => new() { Kind = "rectangle", Center = [0, 0], WidthMm = sizeMm, HeightMm = sizeMm };

    private async Task<int> FeatureCountAsync(string doc) => (await Ins.FeatureListAsync(doc, false, 500, 0)).Total;

    private async Task<double> VolumeAsync(string doc) => (await Ins.ModelInfoAsync(doc)).VolumeMm3 ?? 0;

    private static void AssertFailedAndRolledBack(SwException ex) =>
        Assert.True(ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed,
            $"expected a feature failure, got {ex.ToAgentText()}");

    private static void AssertBox(BoxMm? box, double[] min, double[] max, double tol = 0.01)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], box.Min[i], tol);
            Assert.Equal(max[i], box.Max[i], tol);
        }
    }

    private static void Near(double[] expected, double[]? actual, double tol = L)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], tol);
    }

    /// <summary>Distance of a model point from the y axis (the axis of the helices here).</summary>
    private static double FromYAxis(double[] p) => Math.Sqrt(p[0] * p[0] + p[2] * p[2]);

    // ------------------------------------------------------------------ sweep

    [Fact]
    public async Task Sweep_r5_circle_along_a_50mm_line_is_area_times_length()
    {
        // Verified recipe: profile mark 1, path mark 4; V = 3926.99 (notes §4). The profile sketch is left open: the tool closes it.
        string doc = await NewPartAsync("sweep_line");
        string path = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 50]));     // model (0,0,0) → (0,50,0)
        string profile = await SketchAsync(doc, Named("top"), Circle([0, 0], 5), leaveOpen: true);
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(path), Name = "Rod" });
        Assert.Equal(Math.PI * 25 * 50, r.Verified.VolumeDeltaMm3, Vq); // 3 926.99 mm³
        Assert.Equal("Sweep", r.Feature.Type);
        Assert.Equal("Rod", r.Feature.Name);
        Assert.Equal(profile, r.ConsumedSketch);
        Near([0, 1, 0], r.DirectionModel, 1e-9);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("volume = profile area x path length"));
        AssertBox(r.Model.Box, [-5, 0, -5], [5, 50, 5]);
        Assert.Equal(2, r.Resolved!.Count); // profile + path echoed
    }

    [Fact]
    public async Task Sweep_of_a_slot_along_a_line_is_exact_through_its_start_face()
    {
        // A slot's half circles have no length-decided sense, so the area computed from the sketch is inexact; the sweep's
        // start face (SOLIDWORKS' own area) keeps the check exact: (30 x 10 + π 5²) x 40 = 15 141.59 mm³.
        string doc = await NewPartAsync("sweep_slot");
        string path = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 40]));
        string profile = await SketchAsync(doc, Named("top"), new SketchEntity { Kind = "slot", From = [-15, 0], To = [15, 0], WidthMm = 10 });
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(path) });
        Assert.Equal((30 * 10 + Math.PI * 25) * 40, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("volume = profile area x path length"));
        AssertBox(r.Model.Box, [-20, 0, -5], [20, 40, 5]);
    }

    [Fact]
    public async Task Sweep_spring_along_a_helix_matches_pappus()
    {
        // Verified recipe (notes §4, Helix): circle r 1 on a plane through the helix start that contains the axis.
        string doc = await NewPartAsync("sweep_spring");
        var h = await SpringHelixAsync(doc);
        Near([0, 0, 8], h.Helix.StartMm);
        string profile = await SketchAsync(doc, Named("right"), Circle(h.Helix.StartMm, 1)); // right plane: x = 0 holds the start
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(h.Feature.Name) });
        Assert.Equal(2 * Math.PI * Math.PI * 1 * 8 * 4, r.Model.VolumeMm3, 0.5); // Pappus 631.65 mm³ (live: 631.54)
        Assert.Equal("added", r.Verified.Material);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("x profile area x path length"));
        AssertBox(r.Model.Box, [-9, -1, -9], [9, 21, 9], 0.1);
    }

    [Fact]
    public async Task Sweep_circular_profile_along_a_line_needs_no_profile_sketch()
    {
        // Best guess: InsertProtrusionSwept4 with CircularProfile = true, the path alone on mark 4.
        string doc = await NewPartAsync("sweep_circular");
        string path = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 50]));
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Path = Named(path), CircularProfileDiameterMm = 10 });
        Assert.Equal(Math.PI * 25 * 50, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("circular profile Ø10 mm read back"));
        AssertBox(r.Model.Box, [-5, 0, -5], [5, 50, 5]);
    }

    [Fact]
    public async Task Sweep_cut_along_the_top_face_removes_half_a_cylinder()
    {
        // Best guess: InsertCutSwept4 (19 args). The r 5 circle centered on the top face at x = 0 is swept to x = 60, past the end.
        string doc = await BlockAsync("sweep_cut");
        string path = await SketchAsync(doc, Named("front"), Line([0, 20], [60, 20]));     // model (0,20,0) → (60,20,0)
        string profile = await SketchAsync(doc, Named("right"), Circle([0, 20, 0], 5));
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(path), Cut = true });
        Assert.Equal(-Math.PI * 25 * 50 / 2, r.Verified.VolumeDeltaMm3, Vq); // half of r 5 x 50 inside the block: -1 963.50 mm³
        Assert.Equal("removed", r.Verified.Material);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("not compared"));
    }

    [Fact]
    public async Task Sweep_with_a_twist_keeps_the_volume_and_twists_the_sides()
    {
        // Best guess: TwistCtrlOption = constant twist along the path (8), TwistAngle in radians.
        string doc = await NewPartAsync("sweep_twist");
        string path = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 50]));
        string profile = await SketchAsync(doc, Named("top"), new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 20, HeightMm = 10 });
        var r = await Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(path), TwistDeg = 90 });
        Assert.Equal(200 * 50, r.Verified.VolumeDeltaMm3, 0.5); // Cavalieri: every section is the 20 x 10 rectangle
        Assert.Contains(r.NewFaces!, f => f.Surface != "plane");  // the twisted sides are not planar
    }

    [Fact]
    public async Task Sweep_rejects_missing_or_conflicting_profiles_and_open_profiles()
    {
        string doc = await NewPartAsync("sweep_bad");
        string path = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 50]));
        string open = await SketchAsync(doc, Named("top"), Line([-5, 0], [5, 0]));
        string profile = await SketchAsync(doc, Named("top"), Circle([0, 0], 5));
        int before = await FeatureCountAsync(doc);

        var noPath = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile) }));
        Assert.Equal(ErrorCodes.InvalidArgument, noPath.Code);
        var noProfile = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Path = Named(path) }));
        Assert.Equal(ErrorCodes.InvalidArgument, noProfile.Code);
        var both = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest
        {
            Doc = doc, Profile = Named(profile), Path = Named(path), CircularProfileDiameterMm = 4,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, both.Code);
        var openProfile = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(open), Path = Named(path) }));
        Assert.Equal(ErrorCodes.InvalidArgument, openProfile.Code);
        Assert.Contains("closed", openProfile.Message);
        var same = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(profile) }));
        Assert.Equal(ErrorCodes.InvalidArgument, same.Code);
        var plane = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named("top") }));
        Assert.Equal(ErrorCodes.InvalidArgument, plane.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
    }

    [Fact]
    public async Task Sweep_with_the_profile_away_from_the_helix_start_is_rolled_back()
    {
        // The notes' failure mode: a profile that isn't at the path's start sweeps only part of it (or nothing).
        // The front plane (z = 0) holds neither end of the helix; the circle sits where the helix crosses it a quarter turn in.
        string doc = await NewPartAsync("sweep_partial");
        var h = await SpringHelixAsync(doc);
        double x = h.Helix.CounterClockwise == false ? -8 : 8;
        string profile = await SketchAsync(doc, Named("front"), Circle([x, 1.25, 0], 1));
        int before = await FeatureCountAsync(doc);
        var ex = await Assert.ThrowsAsync<SwException>(() => Af.SweepAsync(new SweepRequest { Doc = doc, Profile = Named(profile), Path = Named(h.Feature.Name) }));
        AssertFailedAndRolledBack(ex);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(0, await VolumeAsync(doc), V);
    }

    // ------------------------------------------------------------------ loft

    [Fact]
    public async Task Loft_between_a_40_and_a_20_square_30_apart_is_a_frustum()
    {
        // Verified recipe: profiles on mark 1 in order. Frustum: h/3 (A1 + A2 + √(A1·A2)) = 10 · (1600 + 400 + 800).
        string doc = await NewPartAsync("loft_frustum");
        string bottom = await SketchAsync(doc, Named("top"), Square(40));
        var plane = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = Named("top"), OffsetMm = 30 });
        string top = await SketchAsync(doc, Named(plane.Feature.Name), Square(20));
        var r = await Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(bottom), Named(top)] });
        Assert.Equal(28000, r.Verified.VolumeDeltaMm3, 0.05);
        Assert.Equal("Blend", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("pass through all 2 profiles"));
        AssertBox(r.Model.Box, [-20, 0, -20], [20, 30, 20]);
        Assert.Equal(2, r.Resolved!.Count);
    }

    [Fact]
    public async Task Loft_cut_makes_a_frustum_pocket_in_the_block()
    {
        // Best guess: InsertCutBlend (12 args). A 40 x 40 square on the top face (y = 20) to a 20 x 20 one at y = 10.
        string doc = await BlockAsync("loft_cut");
        string mouth = await SketchAsync(doc, new EntityRef { At = [0, 20, 0] }, Square(40));
        var plane = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = Named("top"), OffsetMm = 10 });
        string floor = await SketchAsync(doc, Named(plane.Feature.Name), Square(20));
        var r = await Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(mouth), Named(floor)], Cut = true });
        Assert.Equal(-10.0 / 3 * (1600 + 400 + 800), r.Verified.VolumeDeltaMm3, 0.05); // -9 333.33 mm³
        Assert.Equal("removed", r.Verified.Material);
    }

    [Fact]
    public async Task Loft_rejects_one_profile_open_profiles_and_a_closed_loft_of_two()
    {
        string doc = await NewPartAsync("loft_bad");
        string bottom = await SketchAsync(doc, Named("top"), Square(40));
        var plane = await Ref.PlaneAsync(new RefPlaneRequest { Doc = doc, Kind = "offset", Base = Named("top"), OffsetMm = 30 });
        string top = await SketchAsync(doc, Named(plane.Feature.Name), Square(20));
        string line = await SketchAsync(doc, Named("front"), Line([0, 0], [0, 30]));
        int before = await FeatureCountAsync(doc);

        var one = await Assert.ThrowsAsync<SwException>(() => Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(bottom)] }));
        Assert.Equal(ErrorCodes.InvalidArgument, one.Code);
        var closed = await Assert.ThrowsAsync<SwException>(() => Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(bottom), Named(top)], Closed = true }));
        Assert.Equal(ErrorCodes.InvalidArgument, closed.Code);
        var open = await Assert.ThrowsAsync<SwException>(() => Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(bottom), Named(line)] }));
        Assert.Equal(ErrorCodes.InvalidArgument, open.Code);
        var hole = await Assert.ThrowsAsync<SwException>(() => Af.LoftAsync(new LoftRequest { Doc = doc, Profiles = [Named(bottom), null!] }));
        Assert.Equal(ErrorCodes.InvalidArgument, hole.Code); // a JSON null inside the array
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(0, await VolumeAsync(doc), V);
    }

    // ------------------------------------------------------------------ helix

    [Fact]
    public async Task Helix_by_pitch_and_revolutions_starts_on_the_circle_and_reports_its_geometry()
    {
        // Verified recipe and start point (notes §4): a top-plane circle's helix starts on the sketch's -Y side, model +Z.
        string doc = await NewPartAsync("helix");
        var h = await SpringHelixAsync(doc);
        Near([0, 0, 8], h.Helix.StartMm);
        Near([0, 20, 8], h.Helix.EndMm);
        Near([0, 0, 0], h.Helix.AxisOriginMm);
        Near([0, 1, 0], h.Helix.Axis, 1e-9);
        Assert.Equal(8, h.Helix.RadiusMm, 1e-6);
        Assert.Equal(20, h.Helix.HeightMm, L);
        Assert.Equal(5, h.Helix.PitchMm, 1e-9);
        Assert.Equal(4, h.Helix.Revolutions, 1e-9);
        Assert.Equal(4 * Math.Sqrt(Math.Pow(2 * Math.PI * 8, 2) + 25), h.Helix.LengthMm, 0.05); // 202.05 mm
        Assert.Equal(1, Math.Abs(h.Helix.ProfilePlaneNormal![0]), 1e-9);                        // the right plane through the start
        Assert.Contains(h.Verified.Checks!, c => c == "starts on the circle");
        Assert.NotNull(h.ConsumedSketch);
        Assert.Equal(0, await VolumeAsync(doc), V);
    }

    [Fact]
    public async Task Helix_clockwise_flips_the_turning_sense_and_reverse_climbs_down()
    {
        // Best guess for what the Clockwised and Reversed flags mean (the argument order is verified), one flag at a time.
        string doc = await NewPartAsync("helix_flags");
        var ccw = await SpringHelixAsync(doc); // circle at the origin, climbing +y
        string c2 = await SketchAsync(doc, Named("top"), Circle([40, 0], 8));
        var cw = await Af.HelixAsync(new HelixRequest { Doc = doc, Sketch = c2, PitchMm = 5, Revolutions = 4, Clockwise = true });
        Near([0, 1, 0], cw.Helix.Axis, 1e-9);
        Assert.NotNull(ccw.Helix.CounterClockwise);
        Assert.NotNull(cw.Helix.CounterClockwise);
        Assert.NotEqual(ccw.Helix.CounterClockwise, cw.Helix.CounterClockwise); // both about +y

        string c3 = await SketchAsync(doc, Named("top"), Circle([80, 0], 8));
        var down = await Af.HelixAsync(new HelixRequest { Doc = doc, Sketch = c3, DefinedBy = "height_pitch", HeightMm = 30, PitchMm = 6, Reverse = true });
        Assert.Equal(5, down.Helix.Revolutions, 1e-9);
        Assert.Equal(30, down.Helix.HeightMm, L);
        Near([0, -1, 0], down.Helix.Axis, 1e-9);
        Assert.Equal(-30, down.Helix.EndMm![1], L);
        Assert.Equal(8, Math.Sqrt(Math.Pow(down.Helix.EndMm[0] - 80, 2) + Math.Pow(down.Helix.EndMm[2], 2)), L);
        Assert.Equal(0, await VolumeAsync(doc), V);
    }

    [Fact]
    public async Task Tapered_helix_widens_by_height_times_tan_taper()
    {
        // Best guess: Tapered + Outward + TaperAngle.
        string doc = await NewPartAsync("helix_taper");
        string circle = await SketchAsync(doc, Named("top"), Circle([0, 0], 8));
        var h = await Af.HelixAsync(new HelixRequest
        {
            Doc = doc, Sketch = circle, DefinedBy = "height_revolutions", HeightMm = 20, Revolutions = 4, TaperDeg = 5,
        });
        Assert.Equal(8, FromYAxis(h.Helix.StartMm), L);
        Assert.Equal(8 + 20 * Math.Tan(5 * Math.PI / 180), FromYAxis(h.Helix.EndMm!), 0.01); // 9.750 mm
        Assert.Contains(h.Verified.Checks!, c => c.StartsWith("end radius"));
    }

    [Fact]
    public async Task Spiral_is_flat_and_moves_one_pitch_per_turn()
    {
        // Best guess: definedBy 3 (spiral) with pitch = the radial gap between turns.
        string doc = await NewPartAsync("spiral");
        string circle = await SketchAsync(doc, Named("top"), Circle([0, 0], 8));
        var h = await Af.HelixAsync(new HelixRequest { Doc = doc, Sketch = circle, DefinedBy = "spiral", PitchMm = 2, Revolutions = 3 });
        Assert.Equal(0, h.Helix.EndMm![1], L);
        Assert.Contains(Math.Round(FromYAxis(h.Helix.EndMm), 2), new[] { 14.0, 2.0 });
    }

    [Fact]
    public async Task Helix_rejects_bad_sketches_and_parameters()
    {
        string doc = await NewPartAsync("helix_bad");
        string two = await SketchAsync(doc, Named("top"), [Circle([0, 0], 8), Circle([30, 0], 5)]);
        string rect = await SketchAsync(doc, Named("front"), Square(10));
        string circle = await SketchAsync(doc, Named("right"), Circle([0, 0], 8));
        int before = await FeatureCountAsync(doc);

        async Task<string> Code(HelixRequest q) => (await Assert.ThrowsAsync<SwException>(() => Af.HelixAsync(q with { Doc = doc }))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = two, PitchMm = 5, Revolutions = 4 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = rect, PitchMm = 5, Revolutions = 4 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = circle, PitchMm = 5 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = circle, DefinedBy = "turns", PitchMm = 5, Revolutions = 4 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = circle, PitchMm = 5, Revolutions = 4, HeightMm = 25 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new HelixRequest { Sketch = circle, DefinedBy = "spiral", PitchMm = 2, Revolutions = 3, TaperDeg = 5 }));
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(new HelixRequest { Sketch = "NoSuchSketch", PitchMm = 5, Revolutions = 4 }));
        Assert.Equal(before, await FeatureCountAsync(doc));
    }

    // ------------------------------------------------------------------ draft

    /// <summary>The 100 × 60 × 20 block with its four sides drafted by t = tan(angle) about the bottom face: inward (sign -1) or outward (+1).</summary>
    private static double DraftedBlock(double t, int sign) => 100 * 60 * 20 + sign * t * (100 + 60) * 20 * 20 + 4.0 / 3 * t * t * 20 * 20 * 20;

    private static EntityRef[] SideFaces() =>
        [new() { At = [50, 10, 0] }, new() { At = [-50, 10, 0] }, new() { At = [0, 10, 30] }, new() { At = [0, 10, -30] }];

    [Fact]
    public async Task Draft_of_the_four_sides_by_5_degrees_about_the_bottom_face_tilts_each_face()
    {
        // Best guess: InsertMultiFaceDraft with the neutral face on mark 1 and the faces on mark 2 (the call retries without marks).
        string doc = await BlockAsync("draft");
        var r = await Af.DraftAsync(new DraftRequest { Doc = doc, Faces = SideFaces(), Neutral = new EntityRef { At = [0, 0, 0] }, AngleDeg = 5 });
        Assert.Equal("Draft", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("4 planar face(s) turned 5°"));
        double t = Math.Tan(5 * Math.PI / 180);
        Assert.Contains(Math.Round(r.Model.VolumeMm3, 1), new[] { Math.Round(DraftedBlock(t, -1), 1), Math.Round(DraftedBlock(t, +1), 1) });
        Assert.Equal(5, r.Resolved!.Count); // neutral + 4 faces
    }

    [Fact]
    public async Task Draft_flip_tapers_the_other_way()
    {
        // Best guess: FlipDir reverses the pull direction, so one draft adds what the other removes (to first order).
        string a = await BlockAsync("draft_a");
        var r1 = await Af.DraftAsync(new DraftRequest { Doc = a, Faces = SideFaces(), Neutral = new EntityRef { At = [0, 0, 0] }, AngleDeg = 3 });
        string b = await BlockAsync("draft_b");
        var r2 = await Af.DraftAsync(new DraftRequest { Doc = b, Faces = SideFaces(), Neutral = new EntityRef { At = [0, 0, 0] }, AngleDeg = 3, Flip = true });
        double t = Math.Tan(3 * Math.PI / 180);
        var got = new[] { Math.Round(r1.Model.VolumeMm3, 1), Math.Round(r2.Model.VolumeMm3, 1) }.OrderBy(v => v).ToArray();
        Assert.Equal(new[] { Math.Round(DraftedBlock(t, -1), 1), Math.Round(DraftedBlock(t, +1), 1) }, got);
    }

    [Fact]
    public async Task Draft_rejects_bad_angles_and_faces_parallel_to_the_neutral_face()
    {
        string doc = await BlockAsync("draft_bad");
        var bottom = new EntityRef { At = [0, 0, 0] };
        int before = await FeatureCountAsync(doc);
        async Task<string> Code(DraftRequest q) => (await Assert.ThrowsAsync<SwException>(() => Af.DraftAsync(q with { Doc = doc }))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new DraftRequest { Faces = SideFaces(), Neutral = bottom, AngleDeg = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new DraftRequest { Faces = SideFaces(), Neutral = bottom, AngleDeg = 90 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new DraftRequest { Faces = [new() { At = [0, 20, 0] }], Neutral = bottom, AngleDeg = 5 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new DraftRequest { Faces = [bottom], Neutral = bottom, AngleDeg = 5 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new DraftRequest { Faces = SideFaces(), AngleDeg = 5 }));
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(120000, await VolumeAsync(doc), V);
    }

    // ------------------------------------------------------------------ rib

    [Fact]
    public async Task Rib_5mm_fills_the_corner_of_an_L_bracket()
    {
        // Best guess: IFeatureManager.InsertRib (10 args) on the selected sketch. The line from (10,40) to (40,10) closes the
        // triangle (10,10)-(10,40)-(40,10) with the walls: 450 mm² x 5 mm.
        string doc = await BracketAsync("rib");
        string rib = await SketchAsync(doc, Named("front"), Line([10, 40], [40, 10]));
        var r = await Af.RibAsync(new RibRequest { Doc = doc, Sketch = rib, ThicknessMm = 5 });
        Assert.Equal("Rib", r.Feature.Type);
        Assert.Equal(2250, r.Verified.VolumeDeltaMm3, V);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("side faces 5 mm apart, centered on the sketch plane"));
        AssertBox(r.Model.Box, [0, 0, -20], [60, 60, 20]);
        Assert.Equal(rib, r.ConsumedSketch);
    }

    [Fact]
    public async Task Rib_on_one_side_of_the_sketch_has_the_same_volume()
    {
        // Best guess: Is2Sided = false puts the thickness on one side of the sketch plane.
        string doc = await BracketAsync("rib_one_side");
        string rib = await SketchAsync(doc, Named("front"), Line([10, 40], [40, 10]));
        var r = await Af.RibAsync(new RibRequest { Doc = doc, Sketch = rib, ThicknessMm = 5, BothSides = false });
        Assert.Equal(2250, r.Verified.VolumeDeltaMm3, V);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("on one side of the sketch plane"));
    }

    [Fact]
    public async Task Rib_rejects_a_zero_thickness_and_rolls_back_a_rib_that_meets_no_wall()
    {
        string doc = await BracketAsync("rib_bad");
        string rib = await SketchAsync(doc, Named("front"), Line([10, 40], [40, 10]));
        var zero = await Assert.ThrowsAsync<SwException>(() => Af.RibAsync(new RibRequest { Doc = doc, Sketch = rib, ThicknessMm = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);
        // Far from the bracket: neither the line, its extension (y = 100) nor its in-plane normal (±y at x 100..130) meets it.
        string away = await SketchAsync(doc, Named("front"), Line([100, 100], [130, 100]));
        int before = await FeatureCountAsync(doc);
        var ex = await Assert.ThrowsAsync<SwException>(() => Af.RibAsync(new RibRequest { Doc = doc, Sketch = away, ThicknessMm = 5 }));
        AssertFailedAndRolledBack(ex);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(44000, await VolumeAsync(doc), V);
    }

    // ------------------------------------------------------------------ cosmetic thread

    /// <summary>The block with a Ø5 hole (an M6 tap drill) through it along y at x = z = 0.</summary>
    private async Task<string> TapDrillBlockAsync(string name)
    {
        string doc = await BlockAsync(name);
        await SketchAsync(doc, Named("top"), new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 5 });
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal(-Math.PI * 6.25 * 20, r.Verified.VolumeDeltaMm3, Vq);
        return doc;
    }

    [Fact]
    public async Task Cosmetic_thread_M6_on_a_tap_drill_hole_reads_back_its_diameter_and_depth()
    {
        // Best guess: InsertCosmeticThread3 with the circular edge selected, standard none (-2) and the major diameter.
        string doc = await TapDrillBlockAsync("thread");
        double vol = await VolumeAsync(doc);
        var r = await Af.CosmeticThreadAsync(new CosmeticThreadRequest
        {
            Doc = doc, Edge = new EntityRef { Type = "edge", At = [2.5, 20, 0] }, Size = "M6", DepthMm = 10,
        });
        Assert.Equal("CosmeticThread", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("hole thread Ø6 mm, 10 mm deep"));
        Assert.Equal(vol, r.Model.VolumeMm3, V); // an annotation: no geometry
        Assert.Equal("edge", Assert.Single(r.Resolved!).Type);
    }

    [Fact]
    public async Task Cosmetic_thread_through_the_hole_with_an_explicit_diameter()
    {
        string doc = await TapDrillBlockAsync("thread_through");
        var r = await Af.CosmeticThreadAsync(new CosmeticThreadRequest
        {
            Doc = doc, Edge = new EntityRef { Type = "edge", At = [2.5, 0, 0] }, DiameterMm = 6, ThroughAll = true,
        });
        Assert.Contains(r.Verified.Checks!, c => c.Contains("through"));
    }

    [Fact]
    public async Task Cosmetic_thread_rejects_straight_edges_and_a_missing_or_double_end()
    {
        string doc = await TapDrillBlockAsync("thread_bad");
        var rim = new EntityRef { Type = "edge", At = [2.5, 20, 0] };
        int before = await FeatureCountAsync(doc);
        async Task<string> Code(CosmeticThreadRequest q) => (await Assert.ThrowsAsync<SwException>(() => Af.CosmeticThreadAsync(q with { Doc = doc }))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new CosmeticThreadRequest { Edge = new EntityRef { Type = "edge", At = [0, 20, 30] }, Size = "M6", DepthMm = 10 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new CosmeticThreadRequest { Edge = rim, Size = "M6" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new CosmeticThreadRequest { Edge = rim, Size = "M6", DepthMm = 10, ThroughAll = true }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new CosmeticThreadRequest { Edge = rim, Size = "quarter-twenty", DepthMm = 10 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(new CosmeticThreadRequest { Edge = rim, Standard = "unobtainium", Size = "M6", DepthMm = 10 }));
        Assert.Equal(before, await FeatureCountAsync(doc));
    }
}

/// <summary>The COM-free rules of the advanced-feature tools (AdvancedFeatureLogic). These run without SOLIDWORKS.</summary>
public class AdvancedFeatureLogicTests
{
    private static Vec3 P(double x, double y) => new(x, y, 0);

    private static string Code(Action a) => Assert.Throws<SwException>(a).Code;

    // ------------------------------------------------------------------ chains and areas

    [Fact]
    public void A_rectangle_drawn_with_mixed_directions_is_one_loop()
    {
        var segs = new List<AdvancedFeatureLogic.Seg>
        {
            AdvancedFeatureLogic.Seg.Line(P(0, 0), P(0.04, 0)),
            AdvancedFeatureLogic.Seg.Line(P(0.04, 0.02), P(0.04, 0)),
            AdvancedFeatureLogic.Seg.Line(P(0.04, 0.02), P(0, 0.02)),
            AdvancedFeatureLogic.Seg.Line(P(0, 0), P(0, 0.02)),
        };
        var set = AdvancedFeatureLogic.Chains(segs);
        var loop = Assert.Single(set.Chains);
        Assert.True(loop.Closed);
        Assert.False(set.Branched);
        Assert.Equal(0.12, loop.Length, 12);
        var (area, exact) = AdvancedFeatureLogic.RegionArea(set);
        Assert.Equal(8e-4, area, 12);
        Assert.True(exact);
    }

    [Fact]
    public void A_hole_inside_a_rectangle_is_subtracted()
    {
        var segs = new List<AdvancedFeatureLogic.Seg>
        {
            AdvancedFeatureLogic.Seg.Line(P(-0.05, -0.03), P(0.05, -0.03)),
            AdvancedFeatureLogic.Seg.Line(P(0.05, -0.03), P(0.05, 0.03)),
            AdvancedFeatureLogic.Seg.Line(P(0.05, 0.03), P(-0.05, 0.03)),
            AdvancedFeatureLogic.Seg.Line(P(-0.05, 0.03), P(-0.05, -0.03)),
            AdvancedFeatureLogic.Seg.Circle(P(0, 0), 0.01),
        };
        var set = AdvancedFeatureLogic.Chains(segs);
        Assert.Equal(2, set.Loops.Count());
        Assert.Equal(0.006 - Math.PI * 1e-4, AdvancedFeatureLogic.RegionArea(set).Area, 12);
    }

    [Fact]
    public void Two_separate_circles_add_up()
    {
        var set = AdvancedFeatureLogic.Chains([AdvancedFeatureLogic.Seg.Circle(P(0, 0), 0.005), AdvancedFeatureLogic.Seg.Circle(P(0.03, 0), 0.002)]);
        Assert.Equal(Math.PI * (25e-6 + 4e-6), AdvancedFeatureLogic.RegionArea(set).Area, 12);
    }

    [Fact]
    public void A_slot_of_two_lines_and_two_clockwise_half_circles_has_its_exact_area()
    {
        var segs = new List<AdvancedFeatureLogic.Seg>
        {
            AdvancedFeatureLogic.Seg.Line(P(0, 0.005), P(0.03, 0.005)),
            AdvancedFeatureLogic.Seg.Arc(P(0.03, 0), 0.005, P(0.03, 0.005), P(0.03, -0.005), ccw: false),
            AdvancedFeatureLogic.Seg.Line(P(0.03, -0.005), P(0, -0.005)),
            AdvancedFeatureLogic.Seg.Arc(P(0, 0), 0.005, P(0, -0.005), P(0, 0.005), ccw: false),
        };
        var set = AdvancedFeatureLogic.Chains(segs);
        Assert.True(Assert.Single(set.Chains).Closed);
        Assert.Equal(0.03 * 0.01 + Math.PI * 25e-6, AdvancedFeatureLogic.RegionArea(set).Area, 12);
        Assert.Equal(0.06 + Math.PI * 0.01, set.Chains[0].Length, 12);
    }

    [Fact]
    public void An_open_chain_reports_its_ends_and_branches_are_detected()
    {
        var set = AdvancedFeatureLogic.Chains([AdvancedFeatureLogic.Seg.Line(P(1, 0), P(1, 1)), AdvancedFeatureLogic.Seg.Line(P(0, 0), P(1, 0))]);
        var chain = Assert.Single(set.Chains);
        Assert.False(chain.Closed);
        var ends = new[] { chain.Start, chain.End };
        Assert.Contains(P(0, 0), ends);
        Assert.Contains(P(1, 1), ends);
        Assert.Equal(2, chain.Length, 12);

        var tee = AdvancedFeatureLogic.Chains([
            AdvancedFeatureLogic.Seg.Line(P(0, 0), P(1, 0)), AdvancedFeatureLogic.Seg.Line(P(0, 0), P(0, 1)), AdvancedFeatureLogic.Seg.Line(P(0, 0), P(-1, 0))]);
        Assert.True(tee.Branched);
    }

    [Fact]
    public void Arc_direction_follows_the_measured_length()
    {
        double r = 0.01;
        Assert.True(AdvancedFeatureLogic.ArcCcw(P(0, 0), r, P(r, 0), P(0, r), Math.PI * r / 2, rotationDir: -1));
        Assert.False(AdvancedFeatureLogic.ArcCcw(P(0, 0), r, P(r, 0), P(0, r), 3 * Math.PI * r / 2, rotationDir: 1));
        // A half circle is ambiguous by length: the reported direction decides.
        Assert.True(AdvancedFeatureLogic.ArcCcw(P(0, 0), r, P(r, 0), P(-r, 0), Math.PI * r, rotationDir: 1));
        Assert.False(AdvancedFeatureLogic.ArcCcw(P(0, 0), r, P(r, 0), P(-r, 0), Math.PI * r, rotationDir: -1));
        Assert.Equal(Math.PI / 2, AdvancedFeatureLogic.Seg.Sweep(0, Math.PI / 2, true), 12);
        Assert.Equal(-3 * Math.PI / 2, AdvancedFeatureLogic.Seg.Sweep(0, Math.PI / 2, false), 12);
        Assert.Equal(2 * Math.PI, AdvancedFeatureLogic.Seg.Sweep(1, 1, true), 12);
    }

    [Fact]
    public void A_half_circle_has_no_length_decided_sense_and_makes_the_area_inexact()
    {
        double r = 0.005;
        Assert.True(AdvancedFeatureLogic.ArcSenseKnown(P(0, 0), r, P(r, 0), P(0, r), Math.PI * r / 2));
        Assert.False(AdvancedFeatureLogic.ArcSenseKnown(P(0, 0), r, P(r, 0), P(-r, 0), Math.PI * r));
        Assert.False(AdvancedFeatureLogic.ArcSenseKnown(P(0, 0), r, P(r, 0), P(0, r), 0)); // no length: unknown

        // A slot whose half circles' sense is a guess: the area may still come out right, but it is not trusted, and the
        // guessed arcs offer only their end points (on the contour whichever way they turn) as sample points.
        var half = AdvancedFeatureLogic.Seg.Arc(P(0.03, 0), r, P(0.03, r), P(0.03, -r), ccw: false, senseKnown: false);
        Assert.False(half.Exact);
        Assert.Equal(new[] { P(0.03, r), P(0.03, -r) }, half.Samples);
        var segs = new List<AdvancedFeatureLogic.Seg>
        {
            AdvancedFeatureLogic.Seg.Line(P(0, r), P(0.03, r)),
            half,
            AdvancedFeatureLogic.Seg.Line(P(0.03, -r), P(0, -r)),
            AdvancedFeatureLogic.Seg.Arc(P(0, 0), r, P(0, -r), P(0, r), ccw: false, senseKnown: false),
        };
        var (area, exact) = AdvancedFeatureLogic.RegionArea(AdvancedFeatureLogic.Chains(segs));
        Assert.False(exact);
        Assert.Equal(0.03 * 0.01 + Math.PI * r * r, area, 12);
    }

    // ------------------------------------------------------------------ sweep volume

    [Fact]
    public void Sweep_volume_is_exact_on_a_straight_path_and_catches_partial_sweeps()
    {
        double a = Math.PI * 25e-6, l = 0.05;
        Assert.Null(AdvancedFeatureLogic.SweepVolume(a * l, a, l, 1, true, true, true, false, false, false).Problem);
        Assert.NotNull(AdvancedFeatureLogic.SweepVolume(0.9 * a * l, a, l, 1, true, true, true, false, false, false).Problem);
        // A profile tilted 30° to the path: the prism is cos 30° as tall.
        Assert.Null(AdvancedFeatureLogic.SweepVolume(a * l * Math.Cos(Math.PI / 6), a, l, Math.Cos(Math.PI / 6), true, true, true, false, false, false).Problem);
        // Curved paths: about area x length passes, a quarter of it fails, 70 % fails only when the profile is off the path's ends.
        Assert.Null(AdvancedFeatureLogic.SweepVolume(0.995 * a * l, a, l, 1, false, true, true, false, false, false).Problem);
        Assert.Contains("only part", AdvancedFeatureLogic.SweepVolume(0.25 * a * l, a, l, 1, false, true, true, false, false, false).Problem);
        Assert.Null(AdvancedFeatureLogic.SweepVolume(0.7 * a * l, a, l, 1, false, true, true, false, false, false).Problem);
        Assert.NotNull(AdvancedFeatureLogic.SweepVolume(0.7 * a * l, a, l, 1, false, true, true, false, false, true).Problem);
        // Merged, guided or unknown-area sweeps are not compared.
        Assert.Contains("not compared", AdvancedFeatureLogic.SweepVolume(0.1 * a * l, a, l, 1, true, true, false, false, false, false).Check);
        Assert.Contains("not compared", AdvancedFeatureLogic.SweepVolume(0.1 * a * l, a, l, 1, true, true, true, true, false, false).Check);
        Assert.Contains("not compared", AdvancedFeatureLogic.SweepVolume(0.1 * a * l, null, l, 1, true, true, true, false, false, false).Check);
    }

    // ------------------------------------------------------------------ helix

    [Fact]
    public void Helix_modes_derive_the_third_value()
    {
        var pr = AdvancedFeatureLogic.PlanHelix("pitch_revolutions", 5, 4, null, 0, null);
        Assert.Equal((0, 0.02, 0.005, 4.0), (pr.DefinedBy, pr.HeightM, pr.PitchM, pr.Revolutions));
        var hr = AdvancedFeatureLogic.PlanHelix("height_revolutions", null, 4, 20, 0, null);
        Assert.Equal(1, hr.DefinedBy);
        Assert.Equal(0.005, hr.PitchM, 15);
        var hp = AdvancedFeatureLogic.PlanHelix("Height-Pitch", 6, null, 30, 0, null);
        Assert.Equal(2, hp.DefinedBy);
        Assert.Equal(5, hp.Revolutions, 12);
        var sp = AdvancedFeatureLogic.PlanHelix("spiral", 2, 3, null, 90, null);
        Assert.Equal((3, 0.0), (sp.DefinedBy, sp.HeightM));
        Assert.Equal(Math.PI / 2, sp.StartRad, 12);
        var tp = AdvancedFeatureLogic.PlanHelix(null, 5, 4, 20, 0, -5); // a consistent height is fine
        Assert.True(tp.Tapered);
        Assert.False(tp.Outward);
        Assert.Equal(5 * Math.PI / 180, tp.TaperRad, 12);
    }

    [Theory]
    [InlineData("pitch_revolutions", 5.0, null, null, 0.0, null)]     // revolutions missing
    [InlineData("pitch_revolutions", 5.0, 4.0, 25.0, 0.0, null)]      // height disagrees with pitch x revolutions
    [InlineData("turns", 5.0, 4.0, null, 0.0, null)]                  // unknown mode
    [InlineData("spiral", 2.0, 3.0, 10.0, 0.0, null)]                 // a spiral has no height
    [InlineData("spiral", 2.0, 3.0, null, 0.0, 5.0)]                  // nor a taper
    [InlineData("pitch_revolutions", -5.0, 4.0, null, 0.0, null)]     // negative pitch
    [InlineData("pitch_revolutions", 5.0, 2000.0, null, 0.0, null)]   // too many turns
    [InlineData("pitch_revolutions", 5.0, 4.0, null, 400.0, null)]    // start angle
    [InlineData("pitch_revolutions", 5.0, 4.0, null, 0.0, 95.0)]      // taper
    public void Bad_helix_parameters_are_refused(string mode, double? pitch, double? revs, double? height, double start, double? taper) =>
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanHelix(mode, pitch, revs, height, start, taper)));

    [Fact]
    public void Helix_length_matches_the_closed_form_and_spirals_are_longer_than_their_mean_circles()
    {
        Assert.Equal(4 * Math.Sqrt(Math.Pow(2 * Math.PI * 0.008, 2) + 0.005 * 0.005), AdvancedFeatureLogic.HelixLength(0.008, 0.008, 0.02, 4), 12);
        double spiral = AdvancedFeatureLogic.HelixLength(0.008, 0.014, 0, 3);
        Assert.InRange(spiral, 2 * Math.PI * 3 * 0.011, 2 * Math.PI * 3 * 0.011 * 1.01);
        Assert.True(AdvancedFeatureLogic.HelixLength(0.008, 0.0098, 0.02, 4) > AdvancedFeatureLogic.HelixLength(0.008, 0.008, 0.02, 4));
    }

    [Fact]
    public void Helix_read_back_compares_only_the_defining_values()
    {
        var plan = AdvancedFeatureLogic.PlanHelix("pitch_revolutions", 5, 4, null, 0, null);
        Assert.Empty(AdvancedFeatureLogic.HelixReadBack(plan, 0, 0, 0.005, 4)); // the height is derived, not compared
        Assert.Contains(AdvancedFeatureLogic.HelixReadBack(plan, 0, 0.02, 0.006, 4), w => w.StartsWith("pitch"));
        Assert.Contains(AdvancedFeatureLogic.HelixReadBack(plan, 1, 0.02, 0.005, 4), w => w.StartsWith("definedBy"));
    }

    // ------------------------------------------------------------------ draft

    [Fact]
    public void A_side_face_drafted_about_the_bottom_face_is_found_by_its_hinge()
    {
        var n0 = Vec3.UnitX;           // the +x side wall of a block, at x = 0.05
        var m = new Vec3(0, -1, 0);    // the bottom face's outward normal (the pull direction)
        var hinge = AdvancedFeatureLogic.PlaneIntersection(n0, 0.05, m, 0);
        Assert.NotNull(hinge);
        var (hp, hd) = hinge.Value;
        Assert.Equal(0.05, hp.X, 12);
        Assert.Equal(0, hp.Y, 12);
        Assert.True(hd.IsParallelTo(Vec3.UnitZ));
        Assert.Null(AdvancedFeatureLogic.PlaneIntersection(Vec3.UnitY, 0.02, m, 0)); // the top face is parallel to it

        double a = 5 * Math.PI / 180, tol = 0.01 * Math.PI / 180;
        var drafted = new Vec3(Math.Cos(a), Math.Sin(a), 0);
        var planes = new List<(Vec3 N, Vec3 P)>
        {
            (new Vec3(Math.Cos(4 * Math.PI / 180), Math.Sin(4 * Math.PI / 180), 0), new Vec3(0.05, 0, 0.01)), // 4°: wrong angle
            (new Vec3(Math.Cos(a), 0, Math.Sin(a)), new Vec3(0.05, 0, 0.01)),                                   // turned about y
            (drafted, new Vec3(0.06, 0, 0)),                                                                    // not through the hinge
            (drafted, new Vec3(0.05, 0, 0.02)),                                                                 // the drafted wall
        };
        Assert.Equal(3, AdvancedFeatureLogic.MatchDraftedFace(n0, hp, hd, m, a, planes, tol, 2e-6));
        Assert.Equal(-1, AdvancedFeatureLogic.MatchDraftedFace(n0, hp, hd, m, a, planes.Take(3).ToList(), tol, 2e-6));
        Assert.Equal(5, Units.Deg(AdvancedFeatureLogic.AngleToPull(drafted, Vec3.UnitY)), 9);
        Assert.Equal(0, AdvancedFeatureLogic.AngleToPull(Vec3.UnitX, Vec3.UnitY), 12);
    }

    // ------------------------------------------------------------------ cosmetic thread

    [Fact]
    public void Thread_plans_map_standards_sizes_and_ends()
    {
        var custom = AdvancedFeatureLogic.PlanThread(null, null, 6, 10, false);
        Assert.Equal((AdvancedFeatureLogic.NoStandard, 6.0, 0, 10.0), (custom.Standard, custom.DiameterMm!.Value, custom.EndType, custom.DepthMm!.Value));
        var m6 = AdvancedFeatureLogic.PlanThread(null, "M6", null, null, true);
        Assert.Equal((6.0, 1.0, 2), (m6.NominalMm!.Value, m6.PitchMm!.Value, m6.EndType));
        Assert.Equal(0.75, AdvancedFeatureLogic.PlanThread(null, "M6x0.75", null, 8, false).PitchMm);
        var iso = AdvancedFeatureLogic.PlanThread("ISO", "M6", null, 10, false);
        Assert.Equal((8, "M6x1.0", "Machine Threads"), (iso.Standard, iso.SizeName, iso.StandardType));
        Assert.Equal(1, AdvancedFeatureLogic.PlanThread("ansi-metric", "M8x1.25", null, 10, false).Standard);
    }

    [Fact]
    public void Bad_thread_requests_are_refused()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread(null, "M6", null, 10, true)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread(null, "M6", null, null, false)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread(null, "M6", null, 0, false)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread("acme", "M6", null, 10, false)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread(null, "1/4-20", null, 10, false)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread("iso", null, 6, 10, false)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => AdvancedFeatureLogic.PlanThread(null, null, -6, 10, false)));
    }

    [Fact]
    public void Thread_diameter_is_major_in_holes_and_minor_on_shafts()
    {
        var m6 = AdvancedFeatureLogic.PlanThread(null, "M6", null, 10, false);
        Assert.Equal((6.0, true), AdvancedFeatureLogic.ThreadDiameter(m6, 5)); // Ø5 tap drill
        var (minor, hole) = AdvancedFeatureLogic.ThreadDiameter(m6, 6);         // Ø6 shaft
        Assert.False(hole);
        Assert.Equal(6 - 1.082532, minor!.Value, 4);
        Assert.Equal((8.0, true), AdvancedFeatureLogic.ThreadDiameter(AdvancedFeatureLogic.PlanThread(null, null, 8, 10, false), 6.8));
    }
}
