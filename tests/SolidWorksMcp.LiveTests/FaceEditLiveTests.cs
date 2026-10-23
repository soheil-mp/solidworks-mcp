using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// face_edit (direct editing: delete and heal, offset, move) against a live SOLIDWORKS. The oracle part is a
/// 100 (x) × 20 (y) × 60 (z) block on the top plane, centred on the origin in x and z, with a Ø10 hole through it along
/// y at the origin: 120 000 − π·25·20 mm³. Healing the hole restores the solid block; offsets and moves of planes and
/// cylinders have exact volumes.
/// </summary>
public class FaceEditLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 2e-2; // mm³: oracles with cylinders
    private const double Block = 100 * 20 * 60;
    private static readonly double Hole = Math.PI * 25 * 20;

    private FaceEditService Fe => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private InspectService Ins => new(S);

    private static EntityRef Query(object where, int expect) => new() { Where = JsonSerializer.SerializeToElement(where), Expect = expect };

    private static readonly EntityRef HoleFace = Query(new { surface = "cylinder" }, 1);
    private static readonly EntityRef TopFace = Query(new { surface = "plane", normal = "+y" }, 1);

    private async Task<string> BlockWithHoleAsync(string name, bool hole = true)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        if (hole)
        {
            await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
            await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 5 }]);
            var cut = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
            Assert.Equal(Block - Hole, cut.Model.VolumeMm3, V);
        }
        return doc;
    }

    private async Task<ModelInfoResult> InfoAsync(string doc) => await Ins.ModelInfoAsync(doc);

    private async Task<List<FaceInfo>> CylindersAsync(string doc) =>
        (await Ins.FaceListAsync(doc, new EntityFilter { Surface = "cylinder" }, 100, 0)).Faces.ToList();

    [Fact]
    public async Task Delete_with_patch_fills_the_through_hole_and_the_block_is_whole_again()
    {
        string doc = await BlockWithHoleAsync("fe_delete");
        var r = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "delete", Faces = [HoleFace] });
        Assert.Equal(Block, r.Model.VolumeMm3, V);
        Assert.Equal(Hole, r.Verified.VolumeDeltaMm3, V);
        Assert.Empty(await CylindersAsync(doc));
        Assert.Contains(r.Verified.Checks, c => c.Contains("gone from where they were", StringComparison.Ordinal));
        var info = await InfoAsync(doc);
        Assert.Equal(1, info.Bodies!.Count(b => b.Type == "solid"));
        Assert.Equal(6, info.Bodies!.Single().Faces);
    }

    [Fact]
    public async Task Delete_heals_a_hole_in_an_imported_STEP_body_without_history()
    {
        string doc = await BlockWithHoleAsync("fe_step");
        string dir = Path.Combine(Path.GetTempPath(), $"swmcp_fe_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "block.step");
            await new ExportService(S).ExportAsync(new ExportRequest { Doc = doc, Path = file });
            var opened = await new OpenService(S).OpenAsync(file, false, null);
            string imp = opened.Document.Handle;
            Track(imp);
            Assert.Equal(Block - Hole, (await InfoAsync(imp)).VolumeMm3!.Value, 0.1); // the STEP round trip
            var r = await Fe.EditAsync(new FaceEditRequest { Doc = imp, Op = "delete", Faces = [HoleFace] });
            Assert.Equal(Block, r.Model.VolumeMm3, 0.1);
            Assert.Empty(await CylindersAsync(imp));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Delete_without_healing_leaves_an_open_surface_body()
    {
        string doc = await BlockWithHoleAsync("fe_open", hole: false);
        var r = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "delete", Faces = [TopFace], Heal = "none" });
        Assert.Contains(r.Verified.Checks, c => c.Contains("delete only leaves the body open", StringComparison.Ordinal));
        var info = await InfoAsync(doc);
        Assert.DoesNotContain(info.Bodies!, b => b.Type == "solid");
        Assert.Equal(5, info.Bodies!.Single().Faces);
    }

    [Fact]
    public async Task Offset_raises_the_top_face_and_shrinks_the_hole()
    {
        string doc = await BlockWithHoleAsync("fe_offset");
        // The top face 5 mm out: the block is 25 mm high (the hole goes through it, so it grows too).
        var top = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "offset", Faces = [TopFace], DistanceMm = 5 });
        Assert.Equal(100 * 25 * 60 - Math.PI * 25 * 25, top.Model.VolumeMm3, V);
        Assert.Equal(25, top.Model.Box!.Max[1], 3);
        // The hole face 2 mm out of the material is 2 mm towards its axis: r 5 → 3.
        var hole = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "offset", Faces = [HoleFace], DistanceMm = 2 });
        Assert.Equal(100 * 25 * 60 - Math.PI * 9 * 25, hole.Model.VolumeMm3, V);
        Assert.Equal(3, Assert.Single(await CylindersAsync(doc)).RadiusMm!.Value, 6);
        // Negative: into the material, back to r 4.
        var back = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "offset", Faces = [HoleFace], DistanceMm = -1 });
        Assert.Equal(4, Assert.Single(await CylindersAsync(doc)).RadiusMm!.Value, 6);
        Assert.Contains(back.Verified.Checks, c => c.Contains("inward", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Move_translates_the_hole_and_the_top_face()
    {
        string doc = await BlockWithHoleAsync("fe_move");
        var r = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "move", Faces = [HoleFace], TranslateMm = [20, 0, 10] });
        Assert.Equal(Block - Hole, r.Model.VolumeMm3, V);
        var cyl = Assert.Single(await CylindersAsync(doc));
        Assert.Equal(5, cyl.RadiusMm!.Value, 6);
        Assert.Equal(20, cyl.AxisOriginMm![0], 3);
        Assert.Equal(10, cyl.AxisOriginMm![2], 3);
        // A plane moved along its normal: the top face 4 mm up.
        var t = await Fe.EditAsync(new FaceEditRequest { Doc = doc, Op = "move", Faces = [TopFace], TranslateMm = [0, 4, 0] });
        Assert.Equal(100 * 24 * 60 - Math.PI * 25 * 24, t.Model.VolumeMm3, V);
    }

    [Fact]
    public async Task Requests_that_cant_work_are_refused_or_rolled_back()
    {
        string doc = await BlockWithHoleAsync("fe_bad");
        async Task<string> Refused(FaceEditRequest q) => (await Assert.ThrowsAsync<SwException>(() => Fe.EditAsync(q))).Code;
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "twist", Faces = [HoleFace] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "offset", Faces = [HoleFace] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "offset", Faces = [HoleFace], DistanceMm = 0 }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "move", Faces = [HoleFace] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "move", Faces = [HoleFace], TranslateMm = [0, 0, 0] }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "offset", Faces = [HoleFace], DistanceMm = 1, Heal = "fill" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "delete", Faces = [HoleFace], Heal = "glue" }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new() { Doc = doc, Op = "delete", Faces = [] }));
        int features = (await Ins.FeatureListAsync(doc, false, 500, 0)).Total;

        // Moving the hole 60 mm puts it off the block: SOLIDWORKS can't, and nothing is left behind.
        var code = await Refused(new() { Doc = doc, Op = "move", Faces = [HoleFace], TranslateMm = [60, 0, 0] });
        Assert.True(code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed, code);
        Assert.Equal(features, (await Ins.FeatureListAsync(doc, false, 500, 0)).Total);
        Assert.Equal(Block - Hole, (await InfoAsync(doc)).VolumeMm3!.Value, V);
    }
}
