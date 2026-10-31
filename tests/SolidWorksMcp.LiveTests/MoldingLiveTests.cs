using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Moldability checks and the mold split against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): draft angles of
/// drafted walls, a side hole as an undercut, a shelled box's walls, and a drafted block split into cavity and core.
/// Oracles: exact angles and wall thicknesses, cavity + core = block − part.
/// </summary>
public class MoldingLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double Deg = 1e-6;
    private MoldingService M => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);

    /// <summary>A 60 × 40 block, 20 high (x ±30, z ±20, y 0..20).</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 60, HeightMm = 40 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        return doc;
    }

    private static EntityRef[] Sides() =>
        [new() { At = [30, 10, 0] }, new() { At = [-30, 10, 0] }, new() { At = [0, 10, 20] }, new() { At = [0, 10, -20] }];

    private async Task DraftSidesAsync(string doc, double deg) =>
        await new AdvancedFeatureService(S).DraftAsync(new DraftRequest { Doc = doc, Faces = Sides(), Neutral = new EntityRef { At = [0, 0, 0] }, AngleDeg = deg });

    [Fact]
    public async Task Draft_reads_the_walls_angle_and_flags_walls_without_draft()
    {
        string doc = await BlockAsync("mold_draft");
        var plain = await M.CheckAsync(doc, "draft", "+y", 1, null);
        Assert.Equal(4, plain.Counts["needs_draft"]);
        Assert.Equal(1, plain.Counts["positive"]); // the top
        Assert.Equal(1, plain.Counts["negative"]); // the bottom
        Assert.All(plain.Faces.Take(4), f => Assert.Equal(0, f.MaxDraftDeg!.Value, Deg));

        await DraftSidesAsync(doc, 3);
        var r = await M.CheckAsync(doc, "draft", "+y", 1, null);
        Assert.Equal(0, r.Counts["needs_draft"]);
        var walls = r.Faces.Where(f => Math.Abs(Math.Abs(f.MinDraftDeg!.Value) - 3) < 1e-4).ToList();
        Assert.Equal(4, walls.Count);
        Assert.All(walls, f => Assert.Equal(f.MinDraftDeg!.Value, f.MaxDraftDeg!.Value, Deg));
        var strict = await M.CheckAsync(doc, "draft", "+y", 5, null); // 3° is less than the 5° asked for
        Assert.Equal(4, strict.Counts["needs_draft"]);
    }

    [Fact]
    public async Task A_side_hole_is_an_undercut_and_a_vertical_one_is_not()
    {
        string doc = await BlockAsync("mold_undercut");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [15, 0], DiameterMm = 6 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        var clean = await M.CheckAsync(doc, "undercut", "+y", 1, null);
        Assert.Equal(0, clean.Counts["undercut_faces"]);

        // A Ø8 hole along x through the block at y = 10: its lower half faces up into its upper half.
        await Sk.StartAsync(doc, new EntityRef { Name = "right" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [-10, 10], DiameterMm = 8 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all_both" });
        var r = await M.CheckAsync(doc, "undercut", "+y", 1, null);
        Assert.True(r.Counts["undercut_faces"] >= 1);
        Assert.All(r.Faces, f => Assert.Equal("cylinder", f.Surface));
        Assert.All(r.Faces, f => Assert.Equal(10, f.AtMm![1], 4.01)); // on the side hole (y 6..14)
    }

    [Fact]
    public async Task A_shelled_box_has_its_wall_thickness_everywhere()
    {
        string doc = await BlockAsync("mold_wall");
        await new PartFeatureService(S).ShellAsync(new ShellRequest { Doc = doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 2 });
        var r = await M.CheckAsync(doc, "thickness", null, 1, 2.5);
        Assert.Equal(2, r.MinThicknessMm!.Value, 1e-3);
        Assert.True(r.Counts["thin_faces"] >= 5, $"thin faces {r.Counts["thin_faces"]}"); // four walls and the floor
        var ok = await M.CheckAsync(doc, "thickness", null, 1, 1.5);
        Assert.Equal(0, ok.Counts["thin_faces"]);
    }

    [Fact]
    public async Task A_drafted_block_splits_into_cavity_and_core_that_fill_the_block()
    {
        string doc = await BlockAsync("mold_split");
        await DraftSidesAsync(doc, 3);
        var r = await M.SplitAsync(new MoldSplitRequest { Doc = doc, Pull = "+y", MarginMm = 10, ShrinkagePct = 0.5 });
        Assert.Equal(1.005, r.ScaleFactor!.Value, 1e-12);
        Assert.Equal(4, r.PartingEdges);
        Assert.Contains(r.Checks, c => c.StartsWith("cavity + core = block − part", StringComparison.Ordinal));
        Assert.Contains(r.Checks, c => c.StartsWith("no overlap", StringComparison.Ordinal));
        Assert.True(r.Cavity.Box!.Max[1] > r.Core.Box!.Max[1]); // the cavity above, the core below
        Assert.Equal(r.PartingLevelMm, r.Cavity.Box.Min[1], 1e-6);
        Assert.Equal(r.PartingLevelMm, r.Core.Box!.Max[1], 1e-6);
        Assert.True(r.Block.Size[0] >= 80 && r.Block.Size[2] >= 60); // 10 mm of steel around the 60 × 40 part
    }
}
