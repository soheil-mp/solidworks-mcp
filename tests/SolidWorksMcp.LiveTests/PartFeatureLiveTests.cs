using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Revolve, chamfer, shell, Hole Wizard holes, linear and circular patterns and mirror against a live SOLIDWORKS, with
/// analytic oracles (DESIGN §6.3). Every tool has positive tests and negative ones (typed errors, rollback).
/// </summary>
public class PartFeatureLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-3;  // mm³: planar oracles
    private const double Vq = 2e-2; // mm³: oracles with cylinders and cones
    private const double HoleMm3 = Math.PI * 9 * 20; // a Ø6 hole through the 20 mm block: 565.49 mm³

    private PartFeatureService Pf => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private InspectService Ins => new(S);

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane, centred on the origin in x and z (top face at y = 20).</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    /// <summary>A disc r 40, 10 thick (y from 0 to 10), centred on the y axis.</summary>
    private async Task<string> DiscAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 40 }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        Assert.Equal(Math.PI * 1600 * 10, r.Verified.VolumeDeltaMm3, Vq);
        return doc;
    }

    /// <summary>A vertical hole through the part at model (x, z), cut from the top plane (the bottom face).</summary>
    private async Task<FeatureResult> HoleCutAsync(string doc, double x, double z, double diameterMm = 6)
    {
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [x, 0, z], DiameterMm = diameterMm }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal("removed", r.Verified.Material);
        return r;
    }

    /// <summary>A cylindrical boss r × h standing on the block's top face at model (x, z).</summary>
    private async Task<FeatureResult> BossAsync(string doc, double x, double z, double radiusMm = 5, double heightMm = 10)
    {
        await Sk.StartAsync(doc, new EntityRef { At = [x, 20, z] }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [x, 20, z], RadiusMm = radiusMm }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = heightMm });
        Assert.Equal(Math.PI * radiusMm * radiusMm * heightMm, r.Verified.VolumeDeltaMm3, Vq);
        return r;
    }

    private static void AssertBox(BoxMm? box, double[] min, double[] max, double tol = 0.01)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], box.Min[i], tol);
            Assert.Equal(max[i], box.Max[i], tol);
        }
    }

    private static void AssertFailedAndRolledBack(SwException ex) =>
        Assert.True(ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed,
            $"expected a feature failure, got {ex.ToAgentText()}");

    private async Task<int> FeatureCountAsync(string doc) => (await Ins.FeatureListAsync(doc, false, 500, 0)).Total;

    /// <summary>The x (axisIndex 0) or z (2) positions of the axes of the cylindrical faces of radius r, sorted.</summary>
    private async Task<double[]> HoleAxesAsync(string doc, int axisIndex, double radiusMm = 3)
    {
        var faces = await Ins.FaceListAsync(doc, new EntityFilter { Surface = "cylinder", RadiusMm = radiusMm }, 200, 0);
        return faces.Faces.Select(f => Math.Round(f.AxisOriginMm![axisIndex], 3)).OrderBy(v => v).ToArray();
    }

    private async Task AssertHoleAxesAsync(double[] expected, string doc, int axisIndex, double radiusMm = 3)
    {
        double[] got = await HoleAxesAsync(doc, axisIndex, radiusMm);
        Assert.Equal(expected, got);
    }

    /// <summary>Drill-tip cone (118°) volume for a hole of radius r.</summary>
    private static double TipMm3(double r) => Math.PI * r * r * (r / Math.Tan(59 * Math.PI / 180)) / 3;

    // ------------------------------------------------------------------ revolve

    [Fact]
    public async Task Revolve_tube_around_the_sketch_centerline_matches_the_oracle()
    {
        string doc = await NewPartAsync("revolve");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[10, 0], [20, 40]] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        var r = await Pf.RevolveAsync(new RevolveRequest { Doc = doc });
        Assert.Equal(Math.PI * (400 - 100) * 40, r.Verified.VolumeDeltaMm3, Vq); // 37 699.11 mm³
        Assert.Equal("added", r.Verified.Material);
        Assert.NotNull(r.ConsumedSketch);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("coaxial"));
        Assert.Contains(r.NewFaces!, f => f.Surface == "cylinder" && f.RadiusMm == 10);
        Assert.Contains(r.NewFaces!, f => f.Surface == "cylinder" && f.RadiusMm == 20);
        AssertBox(r.Model.Box, [-20, 0, -20], [20, 40, 20]);
    }

    [Fact]
    public async Task Revolve_partial_and_two_direction_angles_scale_the_volume()
    {
        string doc = await NewPartAsync("revolve_90");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[10, 0], [20, 40]] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        var r = await Pf.RevolveAsync(new RevolveRequest { Doc = doc, AngleDeg = 90 });
        Assert.Equal(Math.PI * 300 * 40 / 4, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("end caps 90° apart"));

        string doc2 = await NewPartAsync("revolve_2dir");
        await Sk.StartAsync(doc2, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc2,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[10, 0], [20, 40]] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        var r2 = await Pf.RevolveAsync(new RevolveRequest { Doc = doc2, AngleDeg = 90, Angle2Deg = 90 });
        Assert.Equal(Math.PI * 300 * 40 / 2, r2.Verified.VolumeDeltaMm3, Vq);
    }

    [Fact]
    public async Task Revolve_around_a_named_centerline_when_the_sketch_has_two()
    {
        // Best-guess path (the axis is selected with mark 16): the second centerline at x = -30 must be used, not the first.
        string doc = await NewPartAsync("revolve_axis");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        var add = await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[10, 0], [20, 40]] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
            new SketchEntity { Kind = "centerline", From = [-30, -10], To = [-30, 50] },
        ]);
        string axis = add.Entities[2].Segments[0];
        var r = await Pf.RevolveAsync(new RevolveRequest { Doc = doc, Axis = new EntityRef { Name = axis } });
        Assert.Equal(Math.PI * (50 * 50 - 40 * 40) * 40, r.Verified.VolumeDeltaMm3, Vq); // r 40..50 about x = -30
        AssertBox(r.Model.Box, [-80, 0, -50], [20, 40, 50]);
        Assert.Equal("sketch_segment", Assert.Single(r.Resolved!).Type);
    }

    [Fact]
    public async Task Revolve_cut_turns_a_groove_into_the_block()
    {
        string doc = await BlockAsync("revolve_cut");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false); // z = 0, through the middle of the block
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[5, 15], [10, 25]] },
            new SketchEntity { Kind = "centerline", From = [0, 0], To = [0, 30] },
        ]);
        var r = await Pf.RevolveAsync(new RevolveRequest { Doc = doc, Cut = true });
        // The ring r 5..10 between y = 15 and the top face at y = 20.
        Assert.Equal(-Math.PI * (100 - 25) * 5, r.Verified.VolumeDeltaMm3, Vq); // -1 178.10 mm³
        Assert.Equal("removed", r.Verified.Material);
    }

    [Fact]
    public async Task Revolve_without_a_centerline_or_angle_is_rejected()
    {
        string doc = await NewPartAsync("revolve_noaxis");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[10, 0], [20, 40]] }]);
        int before = await FeatureCountAsync(doc);
        var bad = await Assert.ThrowsAsync<SwException>(() => Pf.RevolveAsync(new RevolveRequest { Doc = doc, AngleDeg = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, bad.Code);
        var ex = await Assert.ThrowsAsync<SwException>(() => Pf.RevolveAsync(new RevolveRequest { Doc = doc }));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("centerline", ex.Message);
        Assert.Equal(before, await FeatureCountAsync(doc)); // only the (now closed) sketch
    }

    [Fact]
    public async Task Revolve_of_a_profile_crossing_the_axis_is_rolled_back()
    {
        string doc = await NewPartAsync("revolve_cross");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "rectangle", Corners = [[-10, 0], [20, 40]] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        int before = await FeatureCountAsync(doc);
        var ex = await Assert.ThrowsAsync<SwException>(() => Pf.RevolveAsync(new RevolveRequest { Doc = doc }));
        AssertFailedAndRolledBack(ex);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(0, (await Ins.ModelInfoAsync(doc)).VolumeMm3 ?? 0, V);
    }

    // ------------------------------------------------------------------ chamfer

    [Fact]
    public async Task Chamfer_equal_3mm_on_a_convex_edge_removes_450()
    {
        string doc = await BlockAsync("chamfer_equal");
        var r = await Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = [new EntityRef { At = [0, 20, 30] }], DistanceMm = 3, Name = "EdgeBreak",
        });
        Assert.Equal(-450, r.Verified.VolumeDeltaMm3, V); // ½ · 3 · 3 · 100
        Assert.Equal("Chamfer", r.Feature.Type);
        Assert.Equal("EdgeBreak", r.Feature.Name);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("legs measured on 1 edge"));
        Assert.Contains(r.NewFaces!, f => f.Surface == "plane" && Math.Abs(f.AreaMm2 - 100 * 3 * Math.Sqrt(2)) < 1e-3);
    }

    [Fact]
    public async Task Chamfer_two_distances_and_distance_angle_match_their_triangles()
    {
        string doc = await BlockAsync("chamfer_dd");
        var dd = await Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = [new EntityRef { At = [0, 20, 30] }], Type = "distance_distance", DistanceMm = 2, Distance2Mm = 4,
        });
        Assert.Equal(-400, dd.Verified.VolumeDeltaMm3, V); // ½ · 2 · 4 · 100

        string doc2 = await BlockAsync("chamfer_da");
        var da = await Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc2, Edges = [new EntityRef { At = [0, 20, -30] }], Type = "angle_distance", DistanceMm = 3, AngleDeg = 30,
        });
        // Leg 3 and the other leg 3·tan 30° (or 3·tan 60°, depending on which face the angle is measured from).
        Assert.Contains(Math.Round(-da.Verified.VolumeDeltaMm3, 1), new[] { 259.8, 779.4 });
        Assert.Contains(da.Verified.Checks!, c => c.Contains("legs measured"));
    }

    [Fact]
    public async Task Chamfer_rejects_bad_input_and_rolls_back_an_oversized_chamfer()
    {
        string doc = await BlockAsync("chamfer_bad");
        var missing = await Assert.ThrowsAsync<SwException>(() => Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = [new EntityRef { At = [0, 20, 30] }], Type = "distance_distance", DistanceMm = 2,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, missing.Code);
        var unknown = await Assert.ThrowsAsync<SwException>(() => Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = [new EntityRef { At = [0, 20, 30] }], Type = "round", DistanceMm = 2,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, unknown.Code);

        int before = await FeatureCountAsync(doc);
        var big = await Assert.ThrowsAsync<SwException>(() => Pf.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = [new EntityRef { At = [0, 20, 30] }], DistanceMm = 30, // the front face is only 20 high
        }));
        AssertFailedAndRolledBack(big);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(120000, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }

    // ------------------------------------------------------------------ shell

    [Fact]
    public async Task Shell_inward_2mm_removing_the_top_face_matches_the_oracle()
    {
        string doc = await BlockAsync("shell_in");
        var r = await Pf.ShellAsync(new ShellRequest { Doc = doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 2 });
        Assert.Equal(120000 - 96 * 56 * 18, r.Model.VolumeMm3, V); // 23 232 mm³
        Assert.Equal("Shell", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("5/5 walls"));
        AssertBox(r.Model.Box, [-50, 0, -30], [50, 20, 30]);
        Assert.Equal("face", Assert.Single(r.Resolved!).Type);
    }

    [Fact]
    public async Task Shell_outward_puts_the_walls_outside()
    {
        string doc = await BlockAsync("shell_out");
        var r = await Pf.ShellAsync(new ShellRequest
        {
            Doc = doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 2, Outward = true,
        });
        Assert.Equal(104 * 22 * 64 - 120000, r.Model.VolumeMm3, V); // 26 432 mm³: the old block is the cavity
        AssertBox(r.Model.Box, [-52, -2, -32], [52, 20, 32]);
    }

    [Fact]
    public async Task Shell_rejects_a_zero_wall_and_rolls_back_one_thicker_than_the_part()
    {
        string doc = await BlockAsync("shell_bad");
        var zero = await Assert.ThrowsAsync<SwException>(() =>
            Pf.ShellAsync(new ShellRequest { Doc = doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);

        int before = await FeatureCountAsync(doc);
        var thick = await Assert.ThrowsAsync<SwException>(() =>
            Pf.ShellAsync(new ShellRequest { Doc = doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 25 }));
        AssertFailedAndRolledBack(thick);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(120000, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }

    // ------------------------------------------------------------------ holes

    [Fact]
    public async Task Hole_drill_6x10_matches_the_cylinder_and_tip_oracle()
    {
        string doc = await BlockAsync("hole_drill");
        var r = await Pf.HoleAsync(new HoleRequest
        {
            Doc = doc, Points = [[20, 20, 10]], Type = "drill", DiameterMm = 6, End = "blind", DepthMm = 10,
        });
        Assert.Equal(-(Math.PI * 9 * 10 + TipMm3(3)), r.Verified.VolumeDeltaMm3, 0.05); // -299.73 mm³
        Assert.Equal("HoleWzd", r.Feature.Type);
        Assert.Contains("Ø", r.Feature.Name);
        var cyl = Assert.Single(r.NewFaces!, f => f.Surface == "cylinder");
        Assert.Equal(3, cyl.RadiusMm!.Value, 1e-6);
        double[] axisAt = cyl.AxisOriginMm!;
        Assert.Equal(20, axisAt[0], 1e-6);
        Assert.Equal(10, axisAt[2], 1e-6);
        Assert.Contains(r.NewFaces!, f => f.Surface == "cone");
        Assert.Contains(r.Verified.Checks!, c => c.Contains("10 mm deep"));
    }

    [Fact]
    public async Task Hole_through_all_at_two_points_makes_two_named_features()
    {
        string doc = await BlockAsync("hole_two");
        var r = await Pf.HoleAsync(new HoleRequest
        {
            Doc = doc, Points = [[-20, 20, 0], [20, 20, 0]], DiameterMm = 5, End = "through_all", Name = "Pin",
        });
        Assert.Equal(-2 * Math.PI * 6.25 * 20, r.Verified.VolumeDeltaMm3, Vq); // two Ø5 through 20: -785.40 mm³
        Assert.Equal("Pin1", r.Feature.Name);
        Assert.Equal("Pin2", Assert.Single(r.AlsoCreated!).Name);
        await AssertHoleAxesAsync([-20, 20], doc, 0, 2.5);
    }

    [Fact]
    public async Task Hole_on_an_explicit_face_projects_the_points_onto_it()
    {
        string doc = await BlockAsync("hole_face");
        var r = await Pf.HoleAsync(new HoleRequest
        {
            Doc = doc, Points = [[-30, 25, 0], [30, 25, 0]], Face = new EntityRef { At = [0, 20, 0] }, DiameterMm = 6, End = "through_all",
        });
        Assert.Equal(-2 * HoleMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Warnings!, w => w.Contains("mm off"));
        await AssertHoleAxesAsync([-30, 30], doc, 0);
    }

    [Fact]
    public async Task Hole_clearance_M5_normal_fit_is_within_the_clearance_range()
    {
        // Best-guess recipe (ANSI-metric screw clearances, fastener type 40): verify live.
        string doc = await BlockAsync("hole_clear");
        var r = await Pf.HoleAsync(new HoleRequest
        {
            Doc = doc, Points = [[0, 20, 0]], Type = "clearance", Size = "M5", Fit = "normal", End = "through_all",
        });
        var cyl = Assert.Single(r.NewFaces!, f => f.Surface == "cylinder");
        double rr = cyl.RadiusMm!.Value;
        Assert.InRange(rr, 2.65, 2.9); // Ø5.3 (close) .. Ø5.8 (loose); normal is Ø5.5
        Assert.Equal(-Math.PI * rr * rr * 20, r.Verified.VolumeDeltaMm3, Vq);
    }

    [Fact]
    public async Task Hole_counterbore_M5_has_a_wider_coaxial_bore_at_the_face()
    {
        // Best-guess recipe (counterbore Ø10 x 5.4 through Value1/Value2): verify live.
        string doc = await BlockAsync("hole_cbore");
        var r = await Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[0, 20, 0]], Type = "counterbore", Size = "M5", End = "through_all" });
        Assert.Contains(r.Verified.Checks!, c => c.Contains("c'bore"));
        var radii = r.NewFaces!.Where(f => f.Surface == "cylinder").Select(f => f.RadiusMm!.Value).OrderBy(v => v).ToList();
        Assert.True(radii.Count >= 2 && radii[^1] > radii[0], $"cylinders: {string.Join(", ", radii)}");
        Assert.Equal("removed", r.Verified.Material);
    }

    [Fact]
    public async Task Hole_countersink_M5_has_a_cone_at_the_face()
    {
        // Best-guess recipe (countersink Ø11.4 x 90° through Value1/Value2): verify live.
        string doc = await BlockAsync("hole_csink");
        var r = await Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[0, 20, 0]], Type = "countersink", Size = "M5", End = "through_all" });
        Assert.Contains(r.Verified.Checks!, c => c.Contains("c'sink"));
        Assert.Contains(r.NewFaces!, f => f.Surface == "cone");
        Assert.Equal("removed", r.Verified.Material);
    }

    [Fact]
    public async Task Hole_tapped_M6_is_made_as_its_tap_drill_without_the_experimental_flag()
    {
        Assert.SkipWhen(PartFeatureService.Experimental, "SWMCP_EXPERIMENTAL is set: the experimental tapped-hole path runs instead.");
        string doc = await BlockAsync("hole_tap");
        var r = await Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[0, 20, 0]], Type = "tapped", Size = "M6", DepthMm = 12 });
        var cyl = Assert.Single(r.NewFaces!, f => f.Surface == "cylinder");
        Assert.Equal(2.5, cyl.RadiusMm!.Value, 1e-6); // tap drill M6 x 1.0 = Ø5
        Assert.Equal(-(Math.PI * 6.25 * 12 + TipMm3(2.5)), r.Verified.VolumeDeltaMm3, 0.05);
        Assert.Contains(r.Warnings!, w => w.Contains("tap-drill"));
    }

    [Fact]
    public async Task Hole_rejects_missing_depth_points_off_the_model_curved_faces_and_overlaps()
    {
        string doc = await BlockAsync("hole_bad");
        await HoleCutAsync(doc, 0, 0, 10); // a Ø10 hole: its wall is a cylinder at r 5
        double vol = (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value;
        int before = await FeatureCountAsync(doc);

        var depth = await Assert.ThrowsAsync<SwException>(() =>
            Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[20, 20, 10]], DiameterMm = 6 }));
        Assert.Equal(ErrorCodes.InvalidArgument, depth.Code);
        var off = await Assert.ThrowsAsync<SwException>(() =>
            Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[20, 50, 10]], DiameterMm = 6, End = "through_all" }));
        Assert.Equal(ErrorCodes.EntityNotFound, off.Code);
        var curved = await Assert.ThrowsAsync<SwException>(() =>
            Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[5, 10, 0]], DiameterMm = 2, End = "through_all" }));
        Assert.Equal(ErrorCodes.InvalidArgument, curved.Code);
        var overlap = await Assert.ThrowsAsync<SwException>(() =>
            Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[20, 20, 10], [22, 20, 10]], DiameterMm = 6, End = "through_all" }));
        Assert.Equal(ErrorCodes.InvalidArgument, overlap.Code);

        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(vol, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }

    [Fact]
    public async Task Hole_failing_at_the_second_point_removes_the_first_one_too()
    {
        string doc = await BlockAsync("hole_partial");
        int before = await FeatureCountAsync(doc);
        // The second point is 30 mm above the model: no face there.
        var ex = await Assert.ThrowsAsync<SwException>(() =>
            Pf.HoleAsync(new HoleRequest { Doc = doc, Points = [[-20, 20, 0], [20, 50, 0]], DiameterMm = 6, End = "through_all" }));
        Assert.Equal(ErrorCodes.EntityNotFound, ex.Code);
        Assert.Contains("points[1]", ex.Message);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(120000, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }

    // ------------------------------------------------------------------ linear pattern

    [Fact]
    public async Task Linear_pattern_of_a_hole_along_plus_x_matches_the_oracle()
    {
        string doc = await BlockAsync("lpat_x");
        var seed = await HoleCutAsync(doc, -40, -20);
        Assert.Equal(-HoleMm3, seed.Verified.VolumeDeltaMm3, Vq);
        var r = await Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "+x", Count = 4, SpacingMm = 20,
        });
        Assert.Equal(3 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq); // -1 696.46 mm³
        Assert.Equal("LPattern", r.Feature.Type);
        Assert.Equal([1, 0, 0], r.DirectionModel!);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("expected positions: 3/3"));
        await AssertHoleAxesAsync([-40, -20, 0, 20], doc, 0);
    }

    [Fact]
    public async Task Linear_pattern_toward_minus_x_runs_the_requested_way()
    {
        string doc = await BlockAsync("lpat_minus_x");
        var seed = await HoleCutAsync(doc, 40, -20);
        var r = await Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "-x", Count = 3, SpacingMm = 30,
        });
        Assert.Equal(2 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Equal([-1, 0, 0], r.DirectionModel!);
        await AssertHoleAxesAsync([-20, 10, 40], doc, 0);
    }

    [Fact]
    public async Task Linear_pattern_in_two_directions_fills_the_grid()
    {
        string doc = await BlockAsync("lpat_grid");
        var seed = await HoleCutAsync(doc, -40, -20);
        var r = await Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "+x", Count = 3, SpacingMm = 20,
            Direction2 = "+z", Count2 = 2, Spacing2Mm = 30,
        });
        Assert.Equal(5 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("expected positions: 5/5"));
        await AssertHoleAxesAsync([-40, -40, -20, -20, 0, 0], doc, 0);
        await AssertHoleAxesAsync([-20, -20, -20, 10, 10, 10], doc, 2);
    }

    [Fact]
    public async Task Linear_pattern_along_an_edge_reference()
    {
        string doc = await BlockAsync("lpat_edge");
        var seed = await HoleCutAsync(doc, -40, 0);
        var r = await Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], DirectionRef = new EntityRef { Type = "edge", At = [0, 20, 30] }, Direction = "+x",
            Count = 3, SpacingMm = 25,
        });
        Assert.Equal(2 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Equal([1, 0, 0], r.DirectionModel!);
        await AssertHoleAxesAsync([-40, -15, 10], doc, 0);
        Assert.Equal("edge", Assert.Single(r.Resolved!).Type);
    }

    [Fact]
    public async Task Linear_pattern_of_a_boss_adds_its_volume_per_instance()
    {
        string doc = await BlockAsync("lpat_boss");
        var seed = await BossAsync(doc, -40, 0);
        var r = await Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "+x", Count = 3, SpacingMm = 25,
        });
        Assert.Equal(2 * Math.PI * 25 * 10, r.Verified.VolumeDeltaMm3, Vq); // 1 570.80 mm³
        Assert.Equal("added", r.Verified.Material);
        await AssertHoleAxesAsync([-40, -15, 10], doc, 0, 5);
    }

    [Fact]
    public async Task Linear_pattern_rejects_bad_input_and_rolls_back_instances_off_the_part()
    {
        string doc = await BlockAsync("lpat_bad");
        var seed = await HoleCutAsync(doc, -40, -20);
        var none = await Assert.ThrowsAsync<SwException>(() => Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = ["NoSuchFeature"], Direction = "+x", Count = 3, SpacingMm = 20,
        }));
        Assert.Equal(ErrorCodes.EntityNotFound, none.Code);
        var one = await Assert.ThrowsAsync<SwException>(() => Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "+x", Count = 1, SpacingMm = 20,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, one.Code);
        var diagonal = await Assert.ThrowsAsync<SwException>(() => Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "1,0,1", Count = 3, SpacingMm = 20,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, diagonal.Code);

        int before = await FeatureCountAsync(doc);
        double vol = (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value;
        var away = await Assert.ThrowsAsync<SwException>(() => Pf.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Direction = "+z", Count = 2, SpacingMm = 200, // lands outside the block
        }));
        AssertFailedAndRolledBack(away);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(vol, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }

    // ------------------------------------------------------------------ circular pattern

    [Fact]
    public async Task Circular_pattern_of_a_hole_around_the_disc_face_matches_the_oracle()
    {
        string doc = await DiscAsync("cpat");
        var seed = await HoleCutAsync(doc, 30, 0);
        Assert.Equal(-Math.PI * 9 * 10, seed.Verified.VolumeDeltaMm3, Vq);
        var r = await Pf.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Axis = new EntityRef { Type = "face", At = [40, 5, 0] }, Count = 6,
        });
        Assert.Equal(5 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq); // -1 413.72 mm³
        Assert.Equal("CirPattern", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("expected positions: 5/5"));
        // Six holes 60° apart on r 30: x = 30 cos(k·60°).
        await AssertHoleAxesAsync([-30, -15, -15, 15, 15, 30], doc, 0);
    }

    [Fact]
    public async Task Circular_pattern_over_90_degrees_around_a_circular_edge()
    {
        string doc = await DiscAsync("cpat_edge");
        var seed = await HoleCutAsync(doc, 30, 0);
        var r = await Pf.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Axis = new EntityRef { Type = "edge", At = [40, 10, 0] }, Count = 3,
            TotalAngleDeg = 90,
        });
        Assert.Equal(2 * seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("expected positions: 2/2"));
        // Instances at 45° and 90° from the seed (either sense): |x| = 30 cos 45° and 0.
        var xs = (await HoleAxesAsync(doc, 0)).Select(Math.Abs).OrderBy(v => v).ToArray();
        Assert.Equal([0, Math.Round(30 * Math.Cos(Math.PI / 4), 3), 30], xs);
    }

    [Fact]
    public async Task Circular_pattern_rejects_a_planar_axis_one_instance_and_overlapping_angles()
    {
        string doc = await DiscAsync("cpat_bad");
        var seed = await HoleCutAsync(doc, 30, 0);
        int before = await FeatureCountAsync(doc);
        var planar = await Assert.ThrowsAsync<SwException>(() => Pf.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Axis = new EntityRef { Type = "face", At = [0, 10, 0] }, Count = 4,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, planar.Code);
        var one = await Assert.ThrowsAsync<SwException>(() => Pf.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Axis = new EntityRef { Type = "face", At = [40, 5, 0] }, Count = 1,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, one.Code);
        var wrap = await Assert.ThrowsAsync<SwException>(() => Pf.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = [seed.Feature.Name], Axis = new EntityRef { Type = "face", At = [40, 5, 0] }, Count = 10,
            TotalAngleDeg = 60, EqualSpacing = false,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, wrap.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
    }

    // ------------------------------------------------------------------ mirror

    [Fact]
    public async Task Mirror_of_a_hole_about_the_right_plane_is_symmetric()
    {
        string doc = await BlockAsync("mirror_hole");
        var seed = await HoleCutAsync(doc, -30, -10);
        Assert.True(Math.Abs((await Ins.ModelInfoAsync(doc)).CenterOfMassMm![0]) > 1e-3); // off-centre before the mirror
        var r = await Pf.MirrorAsync(new MirrorRequest { Doc = doc, Features = [seed.Feature.Name], Plane = new EntityRef { Name = "right" } });
        Assert.Equal(seed.Verified.VolumeDeltaMm3, r.Verified.VolumeDeltaMm3, Vq);
        Assert.Equal("MirrorPattern", r.Feature.Type);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("reflected"));
        await AssertHoleAxesAsync([-30, 30], doc, 0);
        Assert.Equal(0, (await Ins.ModelInfoAsync(doc)).CenterOfMassMm![0], 1e-4);
    }

    [Fact]
    public async Task Mirror_of_a_boss_about_a_planar_face_extends_the_part()
    {
        string doc = await BlockAsync("mirror_face");
        // A 20 x 10 x 20 boss flush with the +x side face (x = 50).
        await Sk.StartAsync(doc, new EntityRef { At = [40, 20, 0] }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[30, 20, -10], [50, 20, 10]] }]);
        var boss = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        Assert.Equal(4000, boss.Verified.VolumeDeltaMm3, V);
        var r = await Pf.MirrorAsync(new MirrorRequest
        {
            Doc = doc, Features = [boss.Feature.Name], Plane = new EntityRef { Type = "face", At = [50, 10, 0] },
        });
        Assert.Equal(4000, r.Verified.VolumeDeltaMm3, V);
        AssertBox(r.Model.Box, [-50, 0, -30], [70, 30, 30]);
        Assert.Equal(1, r.Model.Bodies);
    }

    [Fact]
    public async Task Mirror_into_the_body_or_about_a_curved_face_is_rejected()
    {
        string doc = await BlockAsync("mirror_bad");
        var boss = await BossAsync(doc, -30, 0);
        var curved = await Assert.ThrowsAsync<SwException>(() => Pf.MirrorAsync(new MirrorRequest
        {
            Doc = doc, Features = [boss.Feature.Name], Plane = new EntityRef { Type = "face", At = [-25, 25, 0] },
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, curved.Code);
        var empty = await Assert.ThrowsAsync<SwException>(() => Pf.MirrorAsync(new MirrorRequest
        {
            Doc = doc, Features = [], Plane = new EntityRef { Name = "right" },
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, empty.Code);

        int before = await FeatureCountAsync(doc);
        double vol = (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value;
        // Mirrored about the top face, the boss lands inside the block: no new material.
        var inside = await Assert.ThrowsAsync<SwException>(() => Pf.MirrorAsync(new MirrorRequest
        {
            Doc = doc, Features = [boss.Feature.Name], Plane = new EntityRef { Type = "face", At = [0, 20, 20] },
        }));
        AssertFailedAndRolledBack(inside);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(vol, (await Ins.ModelInfoAsync(doc)).VolumeMm3!.Value, V);
    }
}
