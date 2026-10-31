using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// weldment_member against a live SOLIDWORKS. The oracle frame is a 400 × 300 mm rectangle on the top plane with
/// ISO square tube 40 x 40 x 4: one member per side. A member's volume over its segment's length is the profile's
/// area, the same for every member, for square-cut ends and for 90° miters (the profile is centred on the path, so a
/// miter adds outside what it cuts inside); square-cut members are exactly as long as their segment, mitered ones
/// reach 20 mm (half the profile) past each corner.
/// </summary>
public class WeldmentLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private WeldmentService Wm => new(S);
    private SketchService Sk => new(S);

    private async Task<string> RectangleAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 400, HeightMm = 300 }]);
        return doc;
    }

    private static double Area(WeldmentMember m) => m.VolumeMm3 / m.SegmentLengthMm;

    [Fact]
    public async Task Cut_list_groups_the_frames_members_by_length_with_their_properties()
    {
        // The 400 x 300 frame, mitered: two items, the 440 mm and the 340 mm members (20 mm past each segment end), two each.
        string doc = await RectangleAsync("wm_cutlist");
        await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40x40x4" });
        var r = await new CutListService(S).CutListAsync(doc, update: true);
        Assert.Equal(2, r.Items.Count);
        Assert.All(r.Items, i => Assert.Equal(2, i.Quantity));
        Assert.All(r.Items, i => Assert.Equal("weldment", i.Type));
        Assert.Equal(4, r.Bodies);
        var lengths = r.Items.Select(i => double.Parse(i.Properties.First(p => p.Key.Equals("LENGTH", StringComparison.OrdinalIgnoreCase)).Value.Split(' ')[0].Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture)).Order().ToArray(); // the value follows the document's locale
        Assert.Equal(340, lengths[0], 0.01);
        Assert.Equal(440, lengths[1], 0.01);
        Assert.Contains(r.Checks, c => c.StartsWith("every solid body of the part (4)", StringComparison.Ordinal));
        string block = await NewPartAsync("wm_cutlist_none");
        await Sk.StartAsync(block, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(block, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 40, HeightMm = 20 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = block, DepthMm = 10 });
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => new CutListService(S).CutListAsync(block, false))).Code);
    }

    [Fact]
    public async Task Square_tube_on_a_rectangle_makes_four_mitered_members_of_one_section()
    {
        string doc = await RectangleAsync("wm_miter");
        var r = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40x40x4" });
        Assert.Equal("iso / square tube / 40 x 40 x 4", r.Profile);
        Assert.Equal("miter", r.Corner);
        Assert.Equal(4, r.Members.Count);
        Assert.Equal(4, r.Model.Bodies);
        Assert.Equal([300, 300, 400, 400], r.Members.Select(m => m.SegmentLengthMm).Order().ToArray());
        foreach (var m in r.Members) Assert.Equal(m.SegmentLengthMm + 40, m.LengthMm, 0.01);
        double a = Area(r.Members[0]);
        Assert.InRange(a, 500, 600); // 40² − 32² = 576 less the profile's corner rounds
        Assert.All(r.Members, m => Assert.Equal(a, Area(m), 0.01));
        Assert.Equal(a * 1400, r.Verified.VolumeDeltaMm3, 1);
        Assert.Contains(r.Verified.Checks!, c => c.Contains("one solid body along each segment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Square_cut_members_are_as_long_as_their_segments_and_only_the_named_ones_are_made()
    {
        string doc = await RectangleAsync("wm_none");
        var r = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40 x 40 x 4", Corner = "none" });
        Assert.Equal(4, r.Members.Count);
        foreach (var m in r.Members) Assert.Equal(m.SegmentLengthMm, m.LengthMm, 0.01);
        double a = Area(r.Members[0]);
        Assert.All(r.Members, m => Assert.Equal(a, Area(m), 0.01));

        // A second member feature on two lines of a new sketch (an inner cross bar), by name.
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        var add = await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-180, 0], To = [180, 0] }]);
        string bar = add.Entities[0].Segments[0];
        var r2 = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "rectangular tube", Size = "50 x 30 x 2.6", Segments = [bar], Corner = "none" });
        var m2 = Assert.Single(r2.Members);
        Assert.Equal(360, m2.SegmentLengthMm, 1e-6);
        Assert.Equal(360, m2.LengthMm, 0.01);
        Assert.Equal(5, r2.Model.Bodies);
    }

    [Fact]
    public async Task Table_frame_from_a_3D_sketch_is_a_mitered_top_and_four_square_cut_legs()
    {
        // A 600 × 400 top at 700 mm and four legs down to the floor, all lines of one 3D sketch (model mm).
        string doc = await NewPartAsync("wm_table");
        await Sk.StartAsync(doc, null, true);
        double[][] c = [[-300, 700, -200], [300, 700, -200], [300, 700, 200], [-300, 700, 200]];
        var top = new List<SketchEntity>();
        for (int i = 0; i < 4; i++) top.Add(new SketchEntity { Kind = "line", From = c[i], To = c[(i + 1) % 4] });
        var legs = c.Select(p => new SketchEntity { Kind = "line", From = p, To = [p[0], 0, p[2]] }).ToList();
        var add = await Sk.AddAsync(doc, [.. top, .. legs]);
        var names = add.Entities.Select(e => e.Segments[0]).ToList();

        var t = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40 x 40 x 4", Segments = names[..4] });
        Assert.Equal(4, t.Members.Count);
        Assert.Equal([400, 400, 600, 600], t.Members.Select(m => m.SegmentLengthMm).Order().ToArray());
        var l = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40 x 40 x 4", Segments = names[4..], Corner = "none" });
        Assert.Equal(4, l.Members.Count);
        Assert.All(l.Members, m => Assert.Equal(700, m.LengthMm, 0.01));
        Assert.Equal(8, l.Model.Bodies);
    }

    [Fact]
    public async Task Unknown_profiles_sizes_and_segments_are_refused_with_what_there_is()
    {
        string doc = await RectangleAsync("wm_bad");
        var size = await Assert.ThrowsAsync<SwException>(() => Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "41 x 41 x 4" }));
        Assert.Equal(ErrorCodes.InvalidArgument, size.Code);
        Assert.Contains("40 x 40 x 4", size.Hint);
        var type = await Assert.ThrowsAsync<SwException>(() => Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "hexagon bar", Size = "10" }));
        Assert.Contains("square tube", type.Hint);
        var std = await Assert.ThrowsAsync<SwException>(() => Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Standard = "martian", Type = "pipe", Size = "1" }));
        Assert.Contains("iso", std.Hint);
        await Assert.ThrowsAsync<SwException>(() => Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40 x 40 x 4", Corner = "round" }));
        var seg = await Assert.ThrowsAsync<SwException>(() => Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40 x 40 x 4", Segments = ["Line99"] }));
        Assert.Equal(ErrorCodes.EntityNotFound, seg.Code);
    }
}
