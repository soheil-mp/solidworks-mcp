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
/// weldment_finish works on a T on the front plane: a 400 mm beam along x through the origin and a 300 mm post up from
/// its middle, both of that tube (outer corners rounded r 8, so each flat side is 24 mm wide). The beam's top is the
/// plane y = 20; the post's sides are x = ±20.
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

    /// <summary>The T: the beam (Line1) and the post (Line2) as two members without corner treatment; the post's foot sits inside the beam.</summary>
    private async Task<(string Doc, string Beam, string Post)> TeeAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "line", From = [-200, 0], To = [200, 0] }, new SketchEntity { Kind = "line", From = [0, 0], To = [0, 300] }]);
        var beam = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Type = "square tube", Size = "40x40x4", Segments = ["Line1"], Corner = "none" });
        var post = await Wm.MemberAsync(new WeldmentMemberRequest { Doc = doc, Sketch = "Sketch1", Type = "square tube", Size = "40x40x4", Segments = ["Line2"], Corner = "none" });
        return (doc, beam.Members[0].Body, post.Members[0].Body);
    }

    private Task<WeldmentFinishResult> TrimPostAsync(string doc, string beam, string post, double gapMm = 0) =>
        Wm.FinishAsync(new WeldmentFinishRequest { Doc = doc, Kind = "trim", Bodies = [new EntityRef { Name = post }], Boundary = [new EntityRef { Name = beam }], GapMm = gapMm });

    private static void AssertBox(double[]? box, double[] min, double[] max, double tol = 0.01)
    {
        Assert.NotNull(box);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(min[i], box[i], tol);
            Assert.Equal(max[i], box[i + 3], tol);
        }
    }

    [Fact]
    public async Task Trim_cuts_the_posts_foot_back_to_the_beams_top_or_a_weld_gap_above_it()
    {
        var (doc, beam, post) = await TeeAsync("wf_trim");
        var r = await TrimPostAsync(doc, beam, post);
        Assert.Equal("WeldCornerFeat", r.Feature.Type);
        var p = Assert.Single(r.Bodies);
        Assert.Equal(post, p.PreviousName); // a trimmed body takes the trim's name
        Assert.Equal(r.Feature.Name, p.Name);
        AssertBox(p.BoxMm, [-20, 20, -20], [20, 300, 20]); // the foot now stands on the beam's top
        Assert.Equal(p.VolumeBeforeMm3!.Value * 280 / 300, p.VolumeMm3, 0.5);
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("corner trim, 1 bod(ies) to trim and 1 boundar(ies) read back", StringComparison.Ordinal));

        var (doc2, beam2, post2) = await TeeAsync("wf_gap");
        var g = await TrimPostAsync(doc2, beam2, post2, gapMm: 2);
        Assert.Equal(22, g.Bodies[0].BoxMm![1], 0.01);
    }

    [Fact]
    public async Task End_caps_close_the_beams_ends_and_an_inward_cap_goes_inside_the_posts_top()
    {
        var (doc, _, post) = await TeeAsync("wf_cap");
        var r = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "end_cap", Faces = [new EntityRef { At = [-200, 0, 18] }, new EntityRef { At = [200, 0, 18] }], ThicknessMm = 5,
        });
        Assert.Equal("EndCap", r.Feature.Type);
        Assert.Equal(2, r.Bodies.Count);
        var caps = r.Bodies.OrderBy(b => b.BoxMm![0]).ToList();
        AssertBox(caps[0].BoxMm, [-205, -20, -20], [-200, 20, 20]); // beyond the ends: the beam keeps its 400 mm
        AssertBox(caps[1].BoxMm, [200, -20, -20], [205, 20, 20]);
        Assert.All(caps, c => Assert.Equal(40 * 40 * 5, c.VolumeMm3, 0.5)); // a plain 40 x 40 plate: the rounded corners aren't followed
        Assert.Contains(r.Verified.Checks!, c => c.StartsWith("2 cap(s), each a 5 mm plate beyond its end face", StringComparison.Ordinal));

        var i = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "end_cap", Faces = [new EntityRef { At = [0, 300, 18] }], ThicknessMm = 5, OffsetMm = 2, Direction = "inward",
        });
        var ic = Assert.Single(i.Bodies);
        AssertBox(ic.BoxMm, [-18, 295, -18], [18, 300, 18]); // 2 mm in from the outer edges, in the post's last 5 mm
        Assert.Equal(36 * 36 * 5, ic.VolumeMm3, 0.5);
        Assert.Contains(i.Verified.Checks!, c => c.StartsWith("1 cap(s), each a 5 mm plate in the member's last 5 mm", StringComparison.Ordinal));

        // Internal: in the 32 mm bore, 1 mm in from its walls and 3 mm down from the end.
        var (doc2, _, _) = await TeeAsync("wf_cap_in");
        var n = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc2, Kind = "end_cap", Faces = [new EntityRef { At = [0, 300, 18] }], ThicknessMm = 5, OffsetMm = 1, Direction = "internal", InsetMm = 3,
        });
        AssertBox(Assert.Single(n.Bodies).BoxMm, [-15, 292, -15], [15, 297, 15]);
        Assert.Equal(30 * 30 * 5, n.Bodies[0].VolumeMm3, 0.5);
    }

    [Fact]
    public async Task Gussets_fill_the_corners_of_post_and_beam_with_triangles_where_they_are_asked()
    {
        var (doc, beam, post) = await TeeAsync("wf_gusset");
        await TrimPostAsync(doc, beam, post);
        var r = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "gusset", Faces = [new EntityRef { At = [60, 20, 0] }, new EntityRef { At = [20, 100, 0] }], ThicknessMm = 5, LegsMm = [50, 50],
        });
        Assert.Equal("Gusset", r.Feature.Type);
        var g = Assert.Single(r.Bodies);
        Assert.Equal(0.5 * 50 * 50 * 5, g.VolumeMm3, 1);
        AssertBox(g.BoxMm, [20, 20, -2.5], [70, 70, 2.5]); // centred on the joint, legs along the beam's top and the post's side

        // Unequal legs: 60 along the beam's top (faces[0]), 40 up the post.
        var u = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "gusset", Faces = [new EntityRef { At = [-60, 20, 0] }, new EntityRef { At = [-20, 100, 0] }], ThicknessMm = 5, LegsMm = [60, 40],
        });
        var ug = Assert.Single(u.Bodies);
        Assert.Equal(0.5 * 60 * 40 * 5, ug.VolumeMm3, 1);
        AssertBox(ug.BoxMm, [-80, 20, -2.5], [-20, 60, 2.5]);

        // At the start of the joint (its +z end, z = 12), thickened inwards from there.
        var s = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "gusset", Faces = [new EntityRef { At = [60, 20, 10] }, new EntityRef { At = [20, 100, 10] }], ThicknessMm = 5, LegsMm = [50, 50],
            Location = "start", Side = "inner",
        });
        AssertBox(Assert.Single(s.Bodies).BoxMm, [20, 20, 7], [70, 70, 12]);
    }

    [Fact]
    public async Task Weld_beads_run_where_the_post_meets_the_beam_whole_intermittent_or_all_around()
    {
        var (doc, beam, post) = await TeeAsync("wf_bead");
        await TrimPostAsync(doc, beam, post);
        var r = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "bead", Faces = [new EntityRef { At = [-60, 20, 0] }], Faces2 = [new EntityRef { At = [-20, 100, 0] }], SizeMm = 4,
        });
        Assert.Equal("CosmeticWeldBead", r.Feature.Type);
        Assert.Equal(24, r.BeadLengthMm!.Value, 0.01); // the post's flat side meets the beam's flat top over 24 mm
        Assert.Equal(0, r.Verified.VolumeDeltaMm3, 1e-6); // cosmetic: no solid
        Assert.Contains(r.Verified.Checks!, c => c.Contains("where the face sets meet", StringComparison.Ordinal));

        // 5 mm beads every 10 mm along the other side's 24 mm: at 0, 10 and 20 mm, the last one cut to 4 mm.
        var i = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = "bead", Faces = [new EntityRef { At = [20, 100, 0] }], Faces2 = [new EntityRef { At = [60, 20, 0] }], SizeMm = 3, LengthMm = 5, PitchMm = 10,
        });
        Assert.Equal(14, i.BeadLengthMm!.Value, 0.01);

        // All around the post's foot: its four flats and four rounded corners.
        var (doc2, beam2, post2) = await TeeAsync("wf_bead_around");
        await TrimPostAsync(doc2, beam2, post2);
        var a = await Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc2, Kind = "bead", Faces = [new EntityRef { At = [-20, 100, 0] }], Faces2 = [new EntityRef { At = [-60, 20, 0] }], SizeMm = 4, Side = "all_around",
        });
        Assert.InRange(a.BeadLengthMm!.Value, 4 * 24, 4 * 24 + 2 * Math.PI * 8);
        Assert.Contains(a.Verified.Checks!, c => c.Contains("all_around read back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finishing_requests_that_cant_work_are_refused_and_leave_the_part_as_it_was()
    {
        var (doc, _, post) = await TeeAsync("wf_bad");
        int features = (await new InspectService(S).FeatureListAsync(doc, false, 500, 0)).Total;
        async Task<string> Refused(WeldmentFinishRequest q) => (await Assert.ThrowsAsync<SwException>(() => Wm.FinishAsync(q with { Doc = doc }))).Code;
        // The beam's top and bottom are parallel; a rounded corner of the beam is no end face; a body can't trim itself.
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new WeldmentFinishRequest
        {
            Kind = "gusset", Faces = [new EntityRef { At = [60, 20, 0] }, new EntityRef { At = [60, -20, 0] }], ThicknessMm = 5, LegsMm = [50, 50],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new WeldmentFinishRequest
        {
            Kind = "end_cap", Faces = [new EntityRef { At = [-100, 12 + 8 * Math.Sqrt(0.5), 12 + 8 * Math.Sqrt(0.5)] }], ThicknessMm = 5,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new WeldmentFinishRequest
        {
            Kind = "trim", Bodies = [new EntityRef { Name = post }], Boundary = [new EntityRef { Name = post }],
        }));
        Assert.Equal(features, (await new InspectService(S).FeatureListAsync(doc, false, 500, 0)).Total);

        string block = await NewPartAsync("wf_block");
        await Sk.StartAsync(block, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(block, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 40, HeightMm = 20 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = block, DepthMm = 10 });
        var notWeldment = await Assert.ThrowsAsync<SwException>(() => Wm.FinishAsync(new WeldmentFinishRequest
        {
            Doc = block, Kind = "end_cap", Faces = [new EntityRef { At = [0, 10, 0] }], ThicknessMm = 5,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, notWeldment.Code);
    }

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
