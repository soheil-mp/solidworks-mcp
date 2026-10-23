using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Bodies (move/copy, delete/keep, combine, split), surfaces (extrude, revolve, planar, offset, knit, thicken) and sheet
/// metal (base flange, edge flange, flatten, flat DXF/DWG) against a live SOLIDWORKS, with analytic oracles (DESIGN §6.3).
/// Every tool has positive tests and negative ones (typed errors, nothing left behind). Tests of best-guess recipes say so
/// in a comment: they are the ones to look at first when they fail.
/// </summary>
public class BodySheetMetalLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-3;  // mm³: planar oracles
    private const double Vq = 2e-2; // mm³ / mm²: oracles with cylinders
    private const double CubeMm3 = 40 * 40 * 40;  // 64 000
    private const double SmallMm3 = 20 * 20 * 20; //  8 000
    private const double OverlapMm3 = 10 * 20 * 20; // the small cube at x = 20 is half inside the big one: 4 000

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_sheet_{Guid.NewGuid():N}");

    private BodyService Bs => new(S);
    private SurfaceService Ss => new(S);
    private SheetMetalService Sm => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private InspectService Ins => new(S);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(_dir);
    }

    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); } // closes the test documents (and with them the saved part files)
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ fixtures

    private static EntityRef At(double x, double y, double z) => new() { At = [x, y, z] };

    private static EntityRef Query(object where, int expect) => new() { Where = JsonSerializer.SerializeToElement(where), Expect = expect };

    /// <summary>A 40 mm cube (x, z in [-20, 20], y in [0, 40]); with <paramref name="smallX"/> also a separate 20 mm cube centred at (smallX, 10, 0).</summary>
    private async Task<string> CubesAsync(string name, double? smallX = null)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 40, HeightMm = 40 }]);
        var big = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 40 });
        Assert.Equal(CubeMm3, big.Verified.VolumeDeltaMm3, V);
        if (smallX is double x)
        {
            await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
            await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [x, 0, 0], WidthMm = 20, HeightMm = 20 }]);
            var small = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20, Merge = false });
            Assert.Equal(SmallMm3, small.Verified.VolumeDeltaMm3, V);
            Assert.Equal(2, small.Model.Bodies);
        }
        return doc;
    }

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane, centred on the origin in x and z.</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    /// <summary>A 50 (x) × 30 (z) rectangle centred on the origin, sketched on the top plane and left open.</summary>
    private async Task RectangleSketchAsync(string doc, double widthMm = 50, double heightMm = 30)
    {
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = widthMm, HeightMm = heightMm }]);
    }

    /// <summary>An open 50 x 30 x 20 tube surface (four walls, y in [0, 20]): area 2·(50 + 30)·20 = 3200 mm².</summary>
    private async Task<BodyResult> TubeAsync(string doc)
    {
        await RectangleSketchAsync(doc);
        var r = await Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(3200, NewArea(r), Vq);
        return r;
    }

    /// <summary>A 50 x 30 x 2 sheet-metal plate (y in [-2, 0], thickened against the top plane's normal).</summary>
    private async Task<string> SheetAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await RectangleSketchAsync(doc);
        await Sk.FinishAsync(doc);
        var r = await Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 2, BendRadiusMm = 1 });
        Assert.Equal(3000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    private static double NewArea(BodyResult r) => r.NewFaces!.Sum(f => f.AreaMm2);

    private static void AssertBox(BoxMm? box, double[] min, double[] max, double tol = 0.01)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], box.Min[i], tol);
            Assert.Equal(max[i], box.Max[i], tol);
        }
    }

    private static void AssertFailed(SwException ex) =>
        Assert.True(ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed,
            $"expected a feature failure, got {ex.ToAgentText()}");

    private async Task<int> FeatureCountAsync(string doc) => (await Ins.FeatureListAsync(doc, false, 500, 0)).Total;

    private async Task<double> VolumeAsync(string doc) => (await Ins.ModelInfoAsync(doc)).VolumeMm3 ?? 0;

    private async Task<string> SavedSheetAsync(string name)
    {
        string doc = await SheetAsync(name);
        var saved = await new DocumentService(S).SaveAsync(doc, "as", Path.Combine(_dir, $"{name}.sldprt"), overwrite: false);
        Assert.True(File.Exists(saved.SavedTo));
        return doc;
    }

    // ================================================================== body_move

    [Fact]
    public async Task Move_by_10_in_x_shifts_the_box_by_10()
    {
        // VERIFIED recipe (translation).
        string doc = await CubesAsync("move");
        var r = await Bs.MoveAsync(new BodyMoveRequest { Doc = doc, Bodies = [At(-20, 20, 0)], TranslateMm = [10, 0, 0] });
        AssertBox(r.Model.Box, [-10, 0, -20], [30, 40, 20]);
        Assert.Equal(CubeMm3, r.Model.VolumeMm3, V);
        Assert.Equal(1, r.Model.Bodies);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("box shifted by [10, 0, 0]"));
        Assert.Equal("body", Assert.Single(r.Resolved!).Type);
        Assert.Single(r.Bodies!);
    }

    [Fact]
    public async Task Move_with_two_copies_makes_bodies_at_one_and_two_steps()
    {
        string doc = await CubesAsync("move_copies");
        var r = await Bs.MoveAsync(new BodyMoveRequest { Doc = doc, Bodies = [At(-20, 20, 0)], TranslateMm = [50, 0, 0], Copies = 2 });
        Assert.Equal(3, r.Model.Bodies);
        Assert.Equal(3 * CubeMm3, r.Model.VolumeMm3, V);
        AssertBox(r.Model.Box, [-20, 0, -20], [120, 40, 20]); // copies at +50 and +100
        Assert.Equal(2, r.Verified.BodyCountDelta);
    }

    [Fact]
    public async Task Move_rotates_90_degrees_about_y_through_the_origin()
    {
        // Best guess: the angle order and sense of InsertMoveCopyBody2's rotation (a wrong reading is redone).
        string doc = await NewPartAsync("move_rotate");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [20, 0, 0], WidthMm = 20, HeightMm = 10 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 }); // x in [10, 30], y in [0, 10], z in [-5, 5]
        var r = await Bs.MoveAsync(new BodyMoveRequest
        {
            Doc = doc, Bodies = [At(30, 5, 0)], Rotate = new BodyRotation { Axis = [0, 1, 0], AngleDeg = 90 },
        });
        // Right-hand rule about +y: (x, y, z) -> (z, y, -x).
        AssertBox(r.Model.Box, [-5, 0, -30], [5, 10, -10]);
        Assert.Equal(2000, r.Model.VolumeMm3, V);
    }

    [Fact]
    public async Task Move_rejects_a_zero_motion_bad_copies_and_a_missing_body()
    {
        string doc = await CubesAsync("move_bad");
        int before = await FeatureCountAsync(doc);
        var zero = await Assert.ThrowsAsync<SwException>(() => Bs.MoveAsync(new BodyMoveRequest { Doc = doc, Bodies = [At(-20, 20, 0)], TranslateMm = [0, 0, 0] }));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);
        var copies = await Assert.ThrowsAsync<SwException>(() =>
            Bs.MoveAsync(new BodyMoveRequest { Doc = doc, Bodies = [At(-20, 20, 0)], TranslateMm = [10, 0, 0], Copies = -1 }));
        Assert.Equal(ErrorCodes.InvalidArgument, copies.Code);
        var missing = await Assert.ThrowsAsync<SwException>(() =>
            Bs.MoveAsync(new BodyMoveRequest { Doc = doc, Bodies = [At(500, 500, 500)], TranslateMm = [10, 0, 0] }));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(CubeMm3, await VolumeAsync(doc), V);
    }

    // ================================================================== body_delete

    [Fact]
    public async Task Delete_removes_the_listed_body_and_reports_its_name()
    {
        // VERIFIED recipe (delete).
        string doc = await CubesAsync("delete", smallX: 60); // disjoint: the small cube spans x 50..70
        var r = await Bs.DeleteAsync(doc, [At(70, 10, 0)], "delete", false);
        Assert.Equal(1, r.Model.Bodies);
        Assert.Equal(CubeMm3, r.Model.VolumeMm3, V);
        Assert.Equal(-1, r.Verified.BodyCountDelta);
        Assert.Equal("Boss-Extrude2", Assert.Single(r.RemovedBodies!));
        AssertBox(r.Model.Box, [-20, 0, -20], [20, 40, 20]);
    }

    [Fact]
    public async Task Keep_only_keeps_the_listed_body()
    {
        string doc = await CubesAsync("keep", smallX: 60);
        var r = await Bs.DeleteAsync(doc, [At(70, 10, 0)], "keep_only", false);
        Assert.Equal(1, r.Model.Bodies);
        Assert.Equal(SmallMm3, r.Model.VolumeMm3, V);
        Assert.Equal("Boss-Extrude1", Assert.Single(r.RemovedBodies!));
    }

    [Fact]
    public async Task Delete_rejects_keeping_everything_and_an_unknown_mode()
    {
        string doc = await CubesAsync("delete_bad", smallX: 60);
        int before = await FeatureCountAsync(doc);
        var all = await Assert.ThrowsAsync<SwException>(() => Bs.DeleteAsync(doc, [At(-20, 20, 0), At(70, 10, 0)], "keep_only", false));
        Assert.Equal(ErrorCodes.InvalidArgument, all.Code);
        var mode = await Assert.ThrowsAsync<SwException>(() => Bs.DeleteAsync(doc, [At(70, 10, 0)], "erase", false));
        Assert.Equal(ErrorCodes.InvalidArgument, mode.Code);
        var none = await Assert.ThrowsAsync<SwException>(() => Bs.DeleteAsync(doc, [], "delete", false));
        Assert.Equal(ErrorCodes.InvalidArgument, none.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(CubeMm3 + SmallMm3, await VolumeAsync(doc), V);
    }

    // ================================================================== body_combine

    [Fact]
    public async Task Combine_subtract_removes_the_overlap()
    {
        // VERIFIED recipe (main mark 1, tools mark 2).
        string doc = await CubesAsync("subtract", smallX: 20);
        var r = await Bs.CombineAsync(doc, At(-20, 20, 0), [At(30, 10, 0)], "subtract", false);
        Assert.Equal(CubeMm3 - OverlapMm3, r.Model.VolumeMm3, V); // 60 000
        Assert.Equal(1, r.Model.Bodies);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("removed 4000 mm³"));
        Assert.Equal("main on mark 1, tools on mark 2", r.Method);
        Assert.Equal("Boss-Extrude2", Assert.Single(r.RemovedBodies!));
    }

    [Fact]
    public async Task Combine_add_unites_overlapping_bodies()
    {
        // UNRESOLVED recipe: the variants are tried in order; 'method' tells which one worked.
        string doc = await CubesAsync("add", smallX: 20);
        var r = await Bs.CombineAsync(doc, At(-20, 20, 0), [At(30, 10, 0)], "add", false);
        Assert.Equal(CubeMm3 + SmallMm3 - OverlapMm3, r.Model.VolumeMm3, V); // 68 000
        Assert.Equal(1, r.Model.Bodies);
        Assert.NotNull(r.Method);
        AssertBox(r.Model.Box, [-20, 0, -20], [30, 40, 20]);
    }

    [Fact]
    public async Task Combine_common_keeps_the_intersection()
    {
        // UNRESOLVED recipe (as add).
        string doc = await CubesAsync("common", smallX: 20);
        var r = await Bs.CombineAsync(doc, At(-20, 20, 0), [At(30, 10, 0)], "common", false);
        Assert.Equal(OverlapMm3, r.Model.VolumeMm3, V);
        Assert.Equal(1, r.Model.Bodies);
        AssertBox(r.Model.Box, [10, 0, -10], [20, 20, 10]);
    }

    [Fact]
    public async Task Combine_rejects_bad_input_and_rolls_back_a_subtract_that_misses()
    {
        string doc = await CubesAsync("combine_bad", smallX: 60);
        int before = await FeatureCountAsync(doc);
        var same = await Assert.ThrowsAsync<SwException>(() => Bs.CombineAsync(doc, At(-20, 20, 0), [At(-20, 20, 0)], "subtract", false));
        Assert.Equal(ErrorCodes.InvalidArgument, same.Code);
        var op = await Assert.ThrowsAsync<SwException>(() => Bs.CombineAsync(doc, At(-20, 20, 0), [At(70, 10, 0)], "merge", false));
        Assert.Equal(ErrorCodes.InvalidArgument, op.Code);
        // The small cube is 30 mm away: nothing to subtract.
        var miss = await Assert.ThrowsAsync<SwException>(() => Bs.CombineAsync(doc, At(-20, 20, 0), [At(70, 10, 0)], "subtract", false));
        AssertFailed(miss);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Equal(CubeMm3 + SmallMm3, await VolumeAsync(doc), V);
    }

    // ================================================================== body_split

    [Fact]
    public async Task Split_by_the_right_plane_halves_the_centred_block()
    {
        // UNTESTED recipe (PreSplitBody2 / PostSplitBody2).
        string doc = await BlockAsync("split");
        var r = await Bs.SplitAsync(doc, null, new EntityRef { Name = "right" }, false);
        Assert.Equal(2, r.Model.Bodies);
        Assert.Equal(120000, r.Model.VolumeMm3, V);
        Assert.Equal(2, r.Bodies!.Count);
        Assert.All(r.Bodies, b => Assert.Equal(60000, b.VolumeMm3!.Value, V));
        Assert.NotNull(r.Method);
    }

    [Fact]
    public async Task Split_rejects_a_plane_that_misses_the_body_and_an_unknown_tool()
    {
        string doc = await BlockAsync("split_bad");
        int before = await FeatureCountAsync(doc);
        // The top plane (y = 0) only touches the block's bottom face.
        var miss = await Assert.ThrowsAsync<SwException>(() => Bs.SplitAsync(doc, null, new EntityRef { Name = "top" }, false));
        Assert.Equal(ErrorCodes.InvalidArgument, miss.Code);
        var unknown = await Assert.ThrowsAsync<SwException>(() => Bs.SplitAsync(doc, null, new EntityRef { Name = "NoSuchPlane" }, false));
        Assert.Equal(ErrorCodes.EntityNotFound, unknown.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
        Assert.Single((await Ins.ModelInfoAsync(doc)).Bodies!);
    }

    // ================================================================== surface_extrude

    [Fact]
    public async Task Surface_extrude_of_a_closed_rectangle_is_an_open_tube()
    {
        // VERIFIED recipe (on the open sketch).
        string doc = await NewPartAsync("surf_extrude");
        var r = await TubeAsync(doc);
        Assert.Equal("ExtruRefSurface", r.Feature.Type);
        Assert.Equal(4, r.NewFaceCount);
        Assert.Equal(1, r.Model.Bodies);
        Assert.Equal(0, r.Model.VolumeMm3, V);
        AssertBox(r.Model.Box, [-25, 0, -15], [25, 20, 15]);
        Assert.Equal("surface", Assert.Single(r.Bodies!).Type);
        Assert.NotNull(r.ConsumedSketch);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("spans 0..20"));
    }

    [Fact]
    public async Task Surface_extrude_reverse_and_both_sides_of_an_open_line()
    {
        string doc = await NewPartAsync("surf_extrude_dir");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-25, 0], To = [25, 0] }]);
        var rev = await Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 20, Direction = "reverse" });
        Assert.Equal(1000, NewArea(rev), V); // 50 x 20
        AssertBox(rev.Model.Box, [-25, -20, 0], [25, 0, 0]);
        Assert.Equal([0, -1, 0], rev.DirectionModel!);

        string doc2 = await NewPartAsync("surf_extrude_mid");
        await Sk.StartAsync(doc2, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc2, [new SketchEntity { Kind = "line", From = [-25, 0], To = [25, 0] }]);
        await Sk.FinishAsync(doc2); // the closed-sketch path (re-opened or selected)
        var mid = await Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc2, DepthMm = 20, BothSides = true });
        AssertBox(mid.Model.Box, [-25, -10, 0], [25, 10, 0]);
    }

    [Fact]
    public async Task Surface_extrude_rejects_a_zero_depth_an_in_plane_direction_and_no_sketch()
    {
        string doc = await NewPartAsync("surf_extrude_bad");
        var nothing = await Assert.ThrowsAsync<SwException>(() => Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 10 }));
        Assert.Equal(ErrorCodes.InvalidArgument, nothing.Code);
        await RectangleSketchAsync(doc);
        var zero = await Assert.ThrowsAsync<SwException>(() => Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);
        var inPlane = await Assert.ThrowsAsync<SwException>(() => Ss.ExtrudeAsync(new SurfaceExtrudeRequest { Doc = doc, DepthMm = 10, Direction = "+x" }));
        Assert.Equal(ErrorCodes.InvalidArgument, inPlane.Code);
        Assert.Null((await Ins.ModelInfoAsync(doc)).OpenSketch); // the sketch was finished, not left open
        Assert.Empty((await Ins.ModelInfoAsync(doc)).Bodies ?? []);
    }

    // ================================================================== surface_revolve

    [Fact]
    public async Task Surface_revolve_of_a_line_around_the_centerline_is_a_cylinder()
    {
        // UNTESTED recipe (InsertRevolvedRefSurface, then fallbacks).
        string doc = await NewPartAsync("surf_revolve");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "line", From = [10, 0], To = [10, 40] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        var r = await Ss.RevolveAsync(new SurfaceRevolveRequest { Doc = doc });
        Assert.Equal(2 * Math.PI * 10 * 40, NewArea(r), Vq); // 2 513.27 mm²
        Assert.Contains(r.NewFaces!, f => f.Surface == "cylinder" && Math.Abs(f.RadiusMm!.Value - 10) < 1e-6);
        AssertBox(r.Model.Box, [-10, 0, -10], [10, 40, 10]);
        Assert.Equal(0, r.Model.VolumeMm3, V);
        Assert.NotNull(r.Method);
    }

    [Fact]
    public async Task Surface_revolve_by_90_degrees_is_a_quarter()
    {
        string doc = await NewPartAsync("surf_revolve_90");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc,
        [
            new SketchEntity { Kind = "line", From = [10, 0], To = [10, 40] },
            new SketchEntity { Kind = "centerline", From = [0, -10], To = [0, 50] },
        ]);
        var r = await Ss.RevolveAsync(new SurfaceRevolveRequest { Doc = doc, AngleDeg = 90 });
        Assert.Equal(2 * Math.PI * 10 * 40 / 4, NewArea(r), Vq);
    }

    [Fact]
    public async Task Surface_revolve_rejects_no_centerline_and_a_zero_angle()
    {
        string doc = await NewPartAsync("surf_revolve_bad");
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [10, 0], To = [10, 40] }]);
        var angle = await Assert.ThrowsAsync<SwException>(() => Ss.RevolveAsync(new SurfaceRevolveRequest { Doc = doc, AngleDeg = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, angle.Code);
        var axis = await Assert.ThrowsAsync<SwException>(() => Ss.RevolveAsync(new SurfaceRevolveRequest { Doc = doc }));
        Assert.Equal(ErrorCodes.InvalidArgument, axis.Code);
        Assert.Contains("centerline", axis.Message);
        Assert.Empty((await Ins.ModelInfoAsync(doc)).Bodies ?? []);
    }

    // ================================================================== surface_planar

    [Fact]
    public async Task Planar_surface_fills_a_closed_rectangle()
    {
        // UNTESTED recipe (IModelDoc2.InsertPlanarRefSurface).
        string doc = await NewPartAsync("planar");
        await RectangleSketchAsync(doc);
        var r = await Ss.PlanarAsync(doc, null, null, false);
        Assert.Equal(1500, NewArea(r), V);
        var face = Assert.Single(r.NewFaces!);
        Assert.Equal("plane", face.Surface);
        Assert.Equal(1, Math.Abs(face.Normal![1]), 1e-9);
        AssertBox(r.Model.Box, [-25, 0, -15], [25, 0, 15]);
    }

    [Fact]
    public async Task Planar_surface_from_edges_caps_the_tube()
    {
        string doc = await NewPartAsync("planar_edges");
        await TubeAsync(doc);
        var r = await Ss.PlanarAsync(doc, null, [Query(new { extreme = "+y" }, 4)], false);
        Assert.Equal(1500, NewArea(r), V);
        Assert.Equal(2, r.Model.Bodies);
        Assert.Equal(20, Assert.Single(r.NewFaces!).PickMm[1], 1e-6);
        Assert.Equal(4, r.Resolved!.Count);
    }

    [Fact]
    public async Task Planar_surface_rejects_an_open_profile_and_both_inputs()
    {
        string doc = await NewPartAsync("planar_bad");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-25, 0], To = [25, 0] }]);
        var open = await Assert.ThrowsAsync<SwException>(() => Ss.PlanarAsync(doc, null, null, false));
        Assert.Equal(ErrorCodes.InvalidArgument, open.Code);
        var both = await Assert.ThrowsAsync<SwException>(() => Ss.PlanarAsync(doc, "Sketch1", [At(0, 0, 0)], false));
        Assert.Equal(ErrorCodes.InvalidArgument, both.Code);
        Assert.Empty((await Ins.ModelInfoAsync(doc)).Bodies ?? []);
    }

    // ================================================================== surface_offset

    [Fact]
    public async Task Offset_surface_copies_the_top_face_5mm_up_or_down()
    {
        // UNTESTED recipe (IModelDoc2.InsertOffsetSurface).
        string doc = await BlockAsync("offset");
        var up = await Ss.OffsetAsync(doc, [At(0, 20, 0)], 5, false, false);
        var face = Assert.Single(up.NewFaces!);
        Assert.Equal(6000, face.AreaMm2, V);
        Assert.Equal(25, face.PickMm[1], 1e-6);
        Assert.Equal(120000, up.Model.VolumeMm3, V);
        Assert.Equal(2, up.Model.Bodies);

        var down = await Ss.OffsetAsync(doc, [At(0, 20, 0)], 5, true, false);
        Assert.Equal(15, Assert.Single(down.NewFaces!).PickMm[1], 1e-6);
    }

    [Fact]
    public async Task Offset_surface_rejects_a_negative_distance_and_no_faces()
    {
        string doc = await BlockAsync("offset_bad");
        int before = await FeatureCountAsync(doc);
        var neg = await Assert.ThrowsAsync<SwException>(() => Ss.OffsetAsync(doc, [At(0, 20, 0)], -1, false, false));
        Assert.Equal(ErrorCodes.InvalidArgument, neg.Code);
        var none = await Assert.ThrowsAsync<SwException>(() => Ss.OffsetAsync(doc, [], 5, false, false));
        Assert.Equal(ErrorCodes.InvalidArgument, none.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
    }

    // ================================================================== surface_knit

    [Fact]
    public async Task Knit_of_the_tube_and_both_caps_forms_a_50x30x20_solid()
    {
        // UNTESTED recipe (IFeatureManager.InsertSewRefSurface).
        string doc = await NewPartAsync("knit_solid");
        await TubeAsync(doc);
        await Ss.PlanarAsync(doc, null, [Query(new { extreme = "+y" }, 4)], false);
        await Ss.PlanarAsync(doc, null, [Query(new { extreme = "-y" }, 4)], false);
        var r = await Ss.KnitAsync(doc, [At(25, 10, 0), At(0, 20, 0), At(0, 0, 0)], true, false);
        Assert.True(r.SolidFormed);
        Assert.Equal(30000, r.Model.VolumeMm3, V);
        Assert.Contains(r.Bodies!, b => b.Type == "solid" && Math.Abs(b.VolumeMm3!.Value - 30000) < V);
    }

    [Fact]
    public async Task Knit_of_the_tube_and_one_cap_is_one_open_surface()
    {
        string doc = await NewPartAsync("knit_open");
        await TubeAsync(doc);
        await Ss.PlanarAsync(doc, null, [Query(new { extreme = "+y" }, 4)], false);
        var r = await Ss.KnitAsync(doc, [At(25, 10, 0), At(0, 20, 0)], false, false);
        Assert.False(r.SolidFormed);
        Assert.Equal(1, r.Model.Bodies);
        var faces = await Ins.FaceListAsync(doc, new EntityFilter { Body = Assert.Single(r.Bodies!).Id }, 50, 0);
        Assert.Equal(5, faces.Total);
        Assert.Equal(4700, faces.Faces.Sum(f => f.AreaMm2), Vq); // 3200 + 1500
    }

    [Fact]
    public async Task Knit_rejects_one_surface_and_a_solid_body()
    {
        string doc = await NewPartAsync("knit_bad");
        await TubeAsync(doc);
        var one = await Assert.ThrowsAsync<SwException>(() => Ss.KnitAsync(doc, [At(25, 10, 0)], false, false));
        Assert.Equal(ErrorCodes.InvalidArgument, one.Code);
        string block = await BlockAsync("knit_bad_solid");
        var solid = await Assert.ThrowsAsync<SwException>(() => Ss.KnitAsync(block, [At(0, 20, 0), At(50, 10, 0)], false, false));
        Assert.Equal(ErrorCodes.InvalidArgument, solid.Code);
    }

    // ================================================================== surface_thicken

    [Fact]
    public async Task Thicken_a_50x30_planar_surface_by_2_gives_3000()
    {
        // VERIFIED recipe (SURFACEBODY on mark 1).
        string doc = await NewPartAsync("thicken");
        await RectangleSketchAsync(doc);
        await Ss.PlanarAsync(doc, null, null, false);
        var r = await Ss.ThickenAsync(doc, At(0, 0, 0), 2, "one", false);
        Assert.Equal(3000, r.Verified.VolumeDeltaMm3, V);
        Assert.Equal(2, r.Model.Box!.Size[1], 1e-6);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("ΔV = 1500 mm² x 2 mm"));
        Assert.Contains(r.Bodies!, b => b.Type == "solid");
    }

    [Fact]
    public async Task Thicken_other_side_and_both_sides()
    {
        string doc = await NewPartAsync("thicken_sides");
        await RectangleSketchAsync(doc);
        await Ss.PlanarAsync(doc, null, null, false);
        var one = await Ss.ThickenAsync(doc, At(0, 0, 0), 2, "one", false);
        double side1 = one.Model.Box!.Min[1] + one.Model.Box.Max[1]; // +2 or -2: which side "one" is

        string doc2 = await NewPartAsync("thicken_other");
        await RectangleSketchAsync(doc2);
        await Ss.PlanarAsync(doc2, null, null, false);
        var other = await Ss.ThickenAsync(doc2, At(0, 0, 0), 2, "other", false);
        Assert.Equal(3000, other.Verified.VolumeDeltaMm3, V);
        Assert.Equal(-side1, other.Model.Box!.Min[1] + other.Model.Box.Max[1], 1e-6);

        string doc3 = await NewPartAsync("thicken_both");
        await RectangleSketchAsync(doc3);
        await Ss.PlanarAsync(doc3, null, null, false);
        var both = await Ss.ThickenAsync(doc3, At(0, 0, 0), 2, "both", false);
        Assert.Contains(Math.Round(both.Verified.VolumeDeltaMm3, 3), new[] { 3000.0, 6000.0 });
        Assert.True(both.Model.Box!.Min[1] < -1e-6 && both.Model.Box.Max[1] > 1e-6, "both sides straddle the sheet");
    }

    [Fact]
    public async Task Thicken_rejects_a_solid_a_zero_thickness_and_an_unknown_side()
    {
        string block = await BlockAsync("thicken_bad");
        var solid = await Assert.ThrowsAsync<SwException>(() => Ss.ThickenAsync(block, At(0, 20, 0), 2, "one", false));
        Assert.Equal(ErrorCodes.InvalidArgument, solid.Code);
        string doc = await NewPartAsync("thicken_bad2");
        await RectangleSketchAsync(doc);
        await Ss.PlanarAsync(doc, null, null, false);
        var zero = await Assert.ThrowsAsync<SwException>(() => Ss.ThickenAsync(doc, At(0, 0, 0), 0, "one", false));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);
        var side = await Assert.ThrowsAsync<SwException>(() => Ss.ThickenAsync(doc, At(0, 0, 0), 2, "up", false));
        Assert.Equal(ErrorCodes.InvalidArgument, side.Code);
        Assert.Equal(0, await VolumeAsync(doc), V);
    }

    // ================================================================== sheetmetal_base_flange

    [Fact]
    public async Task Base_flange_from_a_50x30_rectangle_2mm_is_3000()
    {
        // VERIFIED recipe (closed, selected sketch).
        string doc = await NewPartAsync("base_flange");
        await RectangleSketchAsync(doc);
        var r = await Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 2, BendRadiusMm = 1 });
        Assert.Equal(3000, r.Verified.VolumeDeltaMm3, V);
        Assert.Equal("SMBaseFlange", r.Feature.Type);
        Assert.Contains(r.AlsoCreated!, f => f.Type == "SheetMetal");
        Assert.Contains(r.AlsoCreated!, f => f.Type == "FlatPattern");
        AssertBox(r.Model.Box, [-25, -2, -15], [25, 0, 15]); // thickened against the top plane's normal
        Assert.NotNull(r.Sheet);
        Assert.Equal(2, r.Sheet.ThicknessMm, 1e-6);
        Assert.Equal(1, r.Sheet.BendRadiusMm, 1e-6);
        Assert.Equal(0.5, r.Sheet.KFactor!.Value, 1e-6);
        Assert.NotNull(r.ConsumedSketch);
    }

    [Fact]
    public async Task Base_flange_reverse_thickens_along_the_sketch_normal()
    {
        string doc = await NewPartAsync("base_flange_rev");
        await RectangleSketchAsync(doc);
        var r = await Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 2, BendRadiusMm = 1, Reverse = true });
        Assert.Equal(3000, r.Verified.VolumeDeltaMm3, V);
        AssertBox(r.Model.Box, [-25, 0, -15], [25, 2, 15]);
    }

    [Fact]
    public async Task Base_flange_with_k_factor_0_4_reads_it_back()
    {
        // Best guess: a K-factor custom bend allowance when the part's default (0.5) differs.
        string doc = await NewPartAsync("base_flange_k");
        await RectangleSketchAsync(doc);
        var r = await Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 1.5, BendRadiusMm = 2, KFactor = 0.4 });
        Assert.Equal(1500 * 1.5, r.Verified.VolumeDeltaMm3, V);
        Assert.Equal(0.4, r.Sheet!.KFactor!.Value, 1e-6);
        Assert.Equal("k_factor", r.Sheet.BendAllowance);
    }

    [Fact]
    public async Task Base_flange_rejects_an_open_profile_and_bad_parameters()
    {
        string doc = await NewPartAsync("base_flange_bad");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-25, 0], To = [25, 0] }]);
        var open = await Assert.ThrowsAsync<SwException>(() => Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 2, BendRadiusMm = 1 }));
        Assert.Equal(ErrorCodes.InvalidArgument, open.Code);
        var thick = await Assert.ThrowsAsync<SwException>(() => Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 0, BendRadiusMm = 1 }));
        Assert.Equal(ErrorCodes.InvalidArgument, thick.Code);
        var k = await Assert.ThrowsAsync<SwException>(() => Sm.BaseFlangeAsync(new BaseFlangeRequest { Doc = doc, ThicknessMm = 2, BendRadiusMm = 1, KFactor = 1.5 }));
        Assert.Equal(ErrorCodes.InvalidArgument, k.Code);
        Assert.Empty((await Ins.ModelInfoAsync(doc)).Bodies ?? []);
    }

    // ================================================================== sheetmetal_edge_flange

    [Fact]
    public async Task Edge_flange_20mm_on_the_30mm_edge_adds_about_length_x_edge_x_thickness()
    {
        // UNRESOLVED recipe: profile sketches from InsertSketchForEdgeFlange first; 'method' tells which variant worked.
        string doc = await SheetAsync("edge_flange");
        var r = await Sm.EdgeFlangeAsync(new EdgeFlangeRequest { Doc = doc, Edges = [new EntityRef { Type = "edge", At = [25, 0, 0] }], LengthMm = 20 });
        Assert.InRange(r.Verified.VolumeDeltaMm3, 20 * 30 * 2 - 240, 20 * 30 * 2 + 240); // the bend region: ± (r + 1.5 t)·30·t
        Assert.True(r.Experimental);
        Assert.NotNull(r.Method);
        Assert.InRange(r.Model.Box!.Max[1], 14, 22); // the flange stands up from the top face (y = 0)
        Assert.Equal(1, r.Model.Bodies);
    }

    [Fact]
    public async Task Hems_fold_the_30mm_edge_back_closed_open_teardrop_and_rolled()
    {
        var edge = new EntityRef { Type = "edge", At = [25, 0, 0] };
        string doc = await SheetAsync("hem_closed");
        var c = await Sm.HemAsync(new HemRequest { Doc = doc, Edges = [edge], LengthMm = 10 });
        Assert.InRange(c.Verified.VolumeDeltaMm3, 30 * 2 * 10 * 0.5, 30 * 2 * (10 + Math.PI + 4)); // edge x t x (length + bend)
        Assert.Contains(c.Verified.Checks!, x => x.Contains("hem type closed read back", StringComparison.Ordinal));
        Assert.Equal(1, c.Model.Bodies);

        string doc2 = await SheetAsync("hem_open");
        var o = await Sm.HemAsync(new HemRequest { Doc = doc2, Edges = [edge], Type = "open", LengthMm = 8, GapMm = 1.5 });
        Assert.Contains(o.Verified.Checks!, x => x.Contains("hem type open read back", StringComparison.Ordinal));
        Assert.True(o.Verified.VolumeDeltaMm3 > 0);

        string doc3 = await SheetAsync("hem_tear");
        var t = await Sm.HemAsync(new HemRequest { Doc = doc3, Edges = [edge], Type = "teardrop", RadiusMm = 1, Position = "outside" });
        Assert.Contains(t.Verified.Checks!, x => x.Contains("hem type teardrop read back", StringComparison.Ordinal));

        string doc4 = await SheetAsync("hem_rolled");
        var r = await Sm.HemAsync(new HemRequest { Doc = doc4, Edges = [edge], Type = "rolled", RadiusMm = 2 });
        Assert.Contains(r.Verified.Checks!, x => x.Contains("hem type rolled read back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sketched_bend_across_the_plate_lifts_the_moving_side()
    {
        // The line x = 10 across the 50 x 30 plate's top face: the larger side (x < 10, 35 mm) stays, the 15 mm side bends up.
        string doc = await SheetAsync("bend90");
        var r = await Sm.BendAsync(new SketchedBendRequest { Doc = doc, FromMm = [10, 0, -15], ToMm = [10, 0, 15] });
        Assert.Equal(3000, r.Model.VolumeMm3, 3000 * 0.05);
        Assert.InRange(r.Model.Box!.Max[0], 10, 14); // nothing reaches past the bend any more
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("the bent side reaches", StringComparison.Ordinal));
        Assert.Equal(1, r.Model.Bodies);

        // 45° with the other side fixed.
        string doc2 = await SheetAsync("bend45");
        var r2 = await Sm.BendAsync(new SketchedBendRequest { Doc = doc2, FromMm = [0, 0, -15], ToMm = [0, 0, 15], FixedAtMm = [15, 0, 0], AngleDeg = 45 });
        Assert.InRange(r2.Model.Box!.Min[0], -25, -15); // the x < 0 side folded in towards the line
        Assert.Contains(r2.Verified.Checks!, c => c.StartsWith("volume kept", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sketched_bends_off_the_sheet_or_at_its_edge_are_refused()
    {
        string doc = await SheetAsync("bend_bad");
        int features = await FeatureCountAsync(doc);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.BendAsync(new SketchedBendRequest { Doc = doc, FromMm = [0, 0, -15], ToMm = [0, 0, -15] }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.BendAsync(new SketchedBendRequest { Doc = doc, FromMm = [0, 0, -15], ToMm = [0, 0, 15], AngleDeg = 180 }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.BendAsync(new SketchedBendRequest { Doc = doc, FromMm = [0, 0, -15], ToMm = [0, 0, 15], FixedAtMm = [0, 0, 5] }))).Code); // on the line
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.BendAsync(new SketchedBendRequest { Doc = doc, FromMm = [0, 0, -15], ToMm = [0, 0, 15], Position = "sideways" }))).Code);
        Assert.Equal(features, await FeatureCountAsync(doc));
    }

    [Fact]
    public async Task Hems_without_their_sizes_on_bad_edges_or_plain_parts_are_refused()
    {
        string doc = await SheetAsync("hem_bad");
        var edge = new EntityRef { Type = "edge", At = [25, 0, 0] };
        int features = await FeatureCountAsync(doc);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Sm.HemAsync(new HemRequest { Doc = doc, Edges = [edge] }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Sm.HemAsync(new HemRequest { Doc = doc, Edges = [edge], Type = "open", LengthMm = 5 }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Sm.HemAsync(new HemRequest { Doc = doc, Edges = [edge], Type = "wavy", LengthMm = 5 }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Sm.HemAsync(new HemRequest { Doc = doc, Edges = [edge], LengthMm = 5, RadiusMm = 1 }))).Code);
        // The edge across the thickness (along y at a corner) is not a sheet-face edge.
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.HemAsync(new HemRequest { Doc = doc, Edges = [new EntityRef { Type = "edge", At = [25, -1, 15] }], LengthMm = 5 }))).Code);
        Assert.Equal(features, await FeatureCountAsync(doc));
        string plain = await BlockAsync("hem_plain");
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Sm.HemAsync(new HemRequest { Doc = plain, Edges = [new EntityRef { Type = "edge", At = [0, 20, 30] }], LengthMm = 5 }))).Code);
    }

    [Fact]
    public async Task Edge_flange_rejects_a_thickness_edge_a_plain_part_and_a_zero_length()
    {
        string doc = await SheetAsync("edge_flange_bad");
        int before = await FeatureCountAsync(doc);
        var across = await Assert.ThrowsAsync<SwException>(() =>
            Sm.EdgeFlangeAsync(new EdgeFlangeRequest { Doc = doc, Edges = [new EntityRef { Type = "edge", At = [25, -1, 15] }], LengthMm = 20 }));
        Assert.Equal(ErrorCodes.InvalidArgument, across.Code);
        var zero = await Assert.ThrowsAsync<SwException>(() =>
            Sm.EdgeFlangeAsync(new EdgeFlangeRequest { Doc = doc, Edges = [new EntityRef { Type = "edge", At = [25, 0, 0] }], LengthMm = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, zero.Code);
        Assert.Equal(before, await FeatureCountAsync(doc));
        string block = await BlockAsync("edge_flange_plain");
        var plain = await Assert.ThrowsAsync<SwException>(() =>
            Sm.EdgeFlangeAsync(new EdgeFlangeRequest { Doc = block, Edges = [new EntityRef { Type = "edge", At = [0, 20, 30] }], LengthMm = 20 }));
        Assert.Equal(ErrorCodes.InvalidArgument, plain.Code);
    }

    // ================================================================== sheetmetal_flatten

    [Fact]
    public async Task Flatten_and_fold_switch_the_flat_pattern_and_report_the_blank()
    {
        // VERIFIED recipe (FlatPattern suppression).
        string doc = await SheetAsync("flatten");
        var flat = await Sm.FlattenAsync(doc, true);
        Assert.True(flat.Flat);
        Assert.False(flat.Previous);
        Assert.True(flat.Changed);
        Assert.Equal([50, 30], flat.FlatSizeMm!);
        Assert.Equal(2, flat.ThicknessMm!.Value, 1e-6);
        Assert.Equal(3000, flat.VolumeMm3, V);

        var folded = await Sm.FlattenAsync(doc, false);
        Assert.False(folded.Flat);
        Assert.True(folded.Changed);
        Assert.Null(folded.FlatSizeMm);
        var again = await Sm.FlattenAsync(doc, false);
        Assert.False(again.Changed); // idempotent
    }

    [Fact]
    public async Task Flatten_of_a_flanged_part_unfolds_the_flange()
    {
        // Needs the (experimental) edge flange: the flat blank is longer than the 50 mm plate.
        string doc = await SheetAsync("flatten_flange");
        await Sm.EdgeFlangeAsync(new EdgeFlangeRequest { Doc = doc, Edges = [new EntityRef { Type = "edge", At = [25, 0, 0] }], LengthMm = 20 });
        var flat = await Sm.FlattenAsync(doc, true);
        Assert.True(flat.Flat);
        Assert.InRange(flat.FlatSizeMm![0], 60, 72);
        Assert.Equal(30, flat.FlatSizeMm[1], 0.01);
        Assert.Equal(2, flat.ThicknessMm!.Value, 1e-6);
        var folded = await Sm.FlattenAsync(doc, false);
        Assert.True(folded.Box!.Max[1] > 10); // the flange stands up again
    }

    [Fact]
    public async Task Flatten_rejects_a_part_without_sheet_metal()
    {
        string block = await BlockAsync("flatten_plain");
        var ex = await Assert.ThrowsAsync<SwException>(() => Sm.FlattenAsync(block, true));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    // ================================================================== sheetmetal_export_flat

    [Fact]
    public async Task Export_flat_dxf_of_the_saved_plate_is_the_50x30_blank()
    {
        // VERIFIED recipe (ExportToDWG2, sheet-metal action, geometry + bend lines).
        string doc = await SavedSheetAsync("export_flat");
        var r = await Sm.ExportFlatAsync(doc, Path.Combine(_dir, "plate_flat.dxf"), true, false);
        var fi = new FileInfo(r.Path);
        Assert.True(fi.Exists);
        Assert.Equal(fi.Length, r.SizeBytes);
        Assert.Equal("dxf", r.Format);
        Assert.Equal("DXF", r.Details.Header);
        Assert.Contains(r.Details.Checks!, c => c.StartsWith("new file written by this call", StringComparison.Ordinal));
        Assert.Contains(r.Details.Checks!, c => c.Contains("= the flat blank"));
        var lines = File.ReadLines(r.Path).Select(l => l.Trim()).Where(l => l.Length > 0).Take(2).ToArray();
        Assert.Equal(["0", "SECTION"], lines);
        Assert.Equal([50, 30], r.Dxf!.Size!.OrderByDescending(x => x).Select(x => Math.Round(x, 3)).ToArray());
        Assert.Equal([50, 30], r.FlatSizeMm!);

        var again = await Sm.ExportFlatAsync(doc, Path.Combine(_dir, "plate_flat.dxf"), false, true);
        Assert.True(again.Replaced);
        Assert.Null(again.BackupPath); // this server wrote the old file
    }

    [Fact]
    public async Task Export_flat_dwg_has_a_dwg_header()
    {
        string doc = await SavedSheetAsync("export_flat_dwg");
        var r = await Sm.ExportFlatAsync(doc, Path.Combine(_dir, "plate_flat.dwg"), true, false);
        Assert.Equal("DWG", r.Details.Header);
        Assert.StartsWith("AC10", File.ReadAllText(r.Path)[..6]);
    }

    [Fact]
    public async Task Export_flat_refuses_an_unsaved_part_an_existing_file_and_other_formats()
    {
        string unsaved = await SheetAsync("export_flat_unsaved");
        var notSaved = await Assert.ThrowsAsync<SwException>(() => Sm.ExportFlatAsync(unsaved, Path.Combine(_dir, "unsaved.dxf"), true, false));
        Assert.Equal(ErrorCodes.Dirty, notSaved.Code);
        Assert.Contains("doc_save", notSaved.Hint);

        string doc = await SavedSheetAsync("export_flat_exists");
        string file = Path.Combine(_dir, "exists.dxf");
        File.WriteAllText(file, "not a dxf");
        var exists = await Assert.ThrowsAsync<SwException>(() => Sm.ExportFlatAsync(doc, file, true, false));
        Assert.Equal(ErrorCodes.IoError, exists.Code);
        Assert.Equal("not a dxf", File.ReadAllText(file)); // untouched
        var step = await Assert.ThrowsAsync<SwException>(() => Sm.ExportFlatAsync(doc, Path.Combine(_dir, "plate.step"), true, false));
        Assert.Equal(ErrorCodes.InvalidArgument, step.Code);

        string block = await BlockAsync("export_flat_plain");
        await new DocumentService(S).SaveAsync(block, "as", Path.Combine(_dir, "plain.sldprt"), overwrite: false);
        var plain = await Assert.ThrowsAsync<SwException>(() => Sm.ExportFlatAsync(block, Path.Combine(_dir, "plain.dxf"), true, false));
        Assert.Equal(ErrorCodes.InvalidArgument, plain.Code);
    }
}
