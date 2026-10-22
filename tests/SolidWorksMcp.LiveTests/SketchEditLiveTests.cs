using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// The sketch-edit tools (sketch_edit, sketch_info, dimensions, relations, trim, extend, offset, fillet, chamfer, pattern,
/// mirror, convert, construction, delete) against a live SOLIDWORKS, with analytic oracles read back through sketch_info.
/// Most tests sketch on the Front plane, where sketch [x, y] is model [x, y, 0].
/// </summary>
public class SketchEditLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double L = 1e-4; // mm tolerance for sketch geometry read back (the tools check to 1e-4 mm)

    private SketchService Sk => new(S);
    private SketchEditService Ed => new(S);

    private static SketchEntity Rect(double w, double h) => new() { Kind = "rectangle", Center = [0, 0], WidthMm = w, HeightMm = h };
    private static SketchEntity Line(double[] from, double[] to) => new() { Kind = "line", From = from, To = to };
    private static SketchEntity Centerline(double[] from, double[] to) => new() { Kind = "centerline", From = from, To = to };
    private static SketchEntity Circle(double[] center, double r) => new() { Kind = "circle", Center = center, RadiusMm = r };
    private static EntityRef Seg(string name) => new() { Name = name };

    /// <summary>A new part with an open sketch on <paramref name="plane"/> holding <paramref name="entities"/>.</summary>
    private async Task<(string Doc, string Sketch, SketchAddResult Added)> SketchAsync(string name, string plane, params SketchEntity[] entities)
    {
        string doc = await NewPartAsync(name);
        var st = await Sk.StartAsync(doc, new EntityRef { Name = plane }, false);
        var added = await Sk.AddAsync(doc, entities);
        return (doc, st.Sketch, added);
    }

    private static string Name(SketchAddResult added, int entity) => added.Entities[entity].Segments[0];

    private static string[] Sides(SketchAddResult added, int entity) =>
        new[] { "bottom", "right", "top", "left" }.Select(k => added.Entities[entity].Sides![k]).ToArray();

    private async Task<SketchSegmentInfo> SegmentAsync(string doc, string name) =>
        Assert.Single((await Ed.InfoAsync(doc, null)).Segments, s => s.Name == name);

    private static IEnumerable<SketchSegmentInfo> Solid(SketchInfoResult info, string kind) =>
        info.Segments.Where(s => s.Kind == kind && s.Construction != true);

    private static void Near(double[] expected, double[]? actual, double tol = L)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Length, actual!.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], tol);
    }

    private static async Task<SwException> Fails(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SwException>(call);
        Assert.True(ex.Code == code, $"expected {code}, got {ex.ToAgentText()}");
        return ex;
    }

    // ------------------------------------------------------------------ sketch_edit

    [Fact]
    public async Task Edit_reopens_a_finished_sketch_with_its_frame()
    {
        var (doc, sketch, _) = await SketchAsync("edit", "top", Rect(100, 60));
        await Sk.FinishAsync(doc);

        var r = await Ed.EditAsync(doc, sketch);
        Assert.Equal(sketch, r.Sketch);
        Assert.Null(r.AlreadyOpen);
        Assert.Equal([1, 0, 0], r.Frame!.XAxis);
        Assert.Equal([0, 0, -1], r.Frame.YAxis);
        Assert.Equal([0, 1, 0], r.Frame.Normal);
        Assert.True(r.Segments >= 4, $"{r.Segments} segments");

        var again = await Ed.EditAsync(doc, sketch); // idempotent
        Assert.True(again.AlreadyOpen);
        var fin = await Sk.FinishAsync(doc); // the server owns the re-opened sketch
        Assert.Equal(sketch, fin.Sketch);
    }

    [Fact]
    public async Task Edit_refuses_non_sketches_unknown_names_and_a_second_open_sketch()
    {
        var (doc, sketch, _) = await SketchAsync("edit_neg", "top", Rect(100, 60));
        var boss = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await Fails(ErrorCodes.InvalidArgument, () => Ed.EditAsync(doc, boss.Feature.Name));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.EditAsync(doc, "NoSuchSketch"));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.EditAsync(doc, " "));

        await Sk.StartAsync(doc, new EntityRef { At = [0, 20, 0] }, false); // a second sketch, on the top face
        await Fails(ErrorCodes.SketchState, () => Ed.EditAsync(doc, sketch));
        await Sk.FinishAsync(doc);
    }

    // ------------------------------------------------------------------ sketch_info

    [Fact]
    public async Task Info_lists_segments_contours_frame_and_dimensions()
    {
        var (doc, sketch, added) = await SketchAsync("info", "front", Rect(100, 60), Circle([0, 0], 10));
        var info = await Ed.InfoAsync(doc, null);
        Assert.True(info.Open);
        Assert.Equal(sketch, info.Sketch);
        Assert.Equal([0, 0, 1], info.Frame!.Normal);
        var lines = Solid(info, "line").Select(s => s.LengthMm!.Value).OrderBy(x => x).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(60, lines[0], L);
        Assert.Equal(60, lines[1], L);
        Assert.Equal(100, lines[2], L);
        Assert.Equal(100, lines[3], L);
        var bottom = Assert.Single(info.Segments, s => s.Name == added.Entities[0].Sides!["bottom"]);
        var ends = new[] { bottom.StartMm!, bottom.EndMm! }.OrderBy(p => p[0]).ToArray();
        Near([-50, -30], ends[0]);
        Near([50, -30], ends[1]);
        var circle = Assert.Single(Solid(info, "circle"));
        Assert.Equal(Name(added, 1), circle.Name);
        Assert.Equal(10, circle.RadiusMm!.Value, L);
        Near([0, 0], circle.CenterMm);
        Assert.Equal(2 * Math.PI * 10, circle.LengthMm!.Value, 1e-3);
        Assert.Equal("closed", info.Contours.Status);
        Assert.Equal(2, info.Contours.ClosedContours);
        Assert.True(info.Points > 0);

        await Ed.DimensionAddAsync(doc, [Seg(circle.Name)], "diameter", valueMm: 20);
        await Sk.FinishAsync(doc);
        var closed = await Ed.InfoAsync(doc, sketch); // a closed sketch, by name
        Assert.False(closed.Open);
        Assert.Equal(info.SegmentCount, closed.SegmentCount);
        var dim = Assert.Single(closed.Dimensions!);
        Assert.StartsWith("D1@", dim.Name);
        Assert.Equal(20, dim.Value, L);
    }

    [Fact]
    public async Task Info_needs_an_open_sketch_or_a_sketch_name()
    {
        string doc = await NewPartAsync("info_neg");
        await Fails(ErrorCodes.SketchState, () => Ed.InfoAsync(doc, null));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.InfoAsync(doc, "NoSuchSketch"));
    }

    // ------------------------------------------------------------------ sketch_dimension_add

    [Fact]
    public async Task Dimension_80_on_a_100_wide_rectangle_makes_the_side_80()
    {
        // Top Plane: the text point goes through the sketch → model mapping.
        var (doc, sketch, added) = await SketchAsync("dim", "top", Rect(100, 60));
        string bottom = added.Entities[0].Sides!["bottom"];
        var r = await Ed.DimensionAddAsync(doc, [Seg(bottom)], "linear", valueMm: 80);
        Assert.Equal(sketch, r.Sketch);
        Assert.StartsWith("D1@" + sketch, r.Dimension.Name);
        Assert.Equal(80, r.Dimension.Value, L);
        Assert.Equal("mm", r.Dimension.Unit);
        Assert.Null(r.Dimension.Driven);
        Assert.Equal(80, r.Measured!.Value, L);
        Assert.Equal(80, (await SegmentAsync(doc, bottom)).LengthMm!.Value, L);

        // The height is independent of the width: a vertical dimension of 50 on the left side.
        string left = added.Entities[0].Sides!["left"];
        var v = await Ed.DimensionAddAsync(doc, [Seg(left)], "vertical", valueMm: 50);
        Assert.Equal(50, v.Dimension.Value, L);
        var ls = await SegmentAsync(doc, left);
        Assert.Equal(50, Math.Abs(ls.EndMm![1] - ls.StartMm![1]), L);
        Assert.Equal(80, (await SegmentAsync(doc, bottom)).LengthMm!.Value, L); // the first dimension still holds
        var info = await Ed.InfoAsync(doc, null);
        Assert.Equal(2, info.Dimensions!.Count);
    }

    [Fact]
    public async Task Diameter_and_angular_dimensions_drive_the_geometry()
    {
        var (doc, _, added) = await SketchAsync("dim_kinds", "front",
            Circle([-40, 0], 10), Line([20, 0], [70, 0]), Line([20, 0], [55, 35]));
        string circle = Name(added, 0), l1 = Name(added, 1), l2 = Name(added, 2);

        var dia = await Ed.DimensionAddAsync(doc, [Seg(circle)], "diameter", valueMm: 30);
        Assert.Equal(30, dia.Dimension.Value, L);
        Assert.Equal(15, (await SegmentAsync(doc, circle)).RadiusMm!.Value, L);

        var ang = await Ed.DimensionAddAsync(doc, [Seg(l1), Seg(l2)], "angular", valueDeg: 60);
        Assert.Equal("deg", ang.Dimension.Unit);
        Assert.Equal(60, ang.Dimension.Value, 1e-6);
        Assert.Equal(60, ang.Measured!.Value, 1e-4);
        var a = await SegmentAsync(doc, l1);
        var b = await SegmentAsync(doc, l2);
        double Dir(SketchSegmentInfo s) => Math.Atan2(s.EndMm![1] - s.StartMm![1], s.EndMm[0] - s.StartMm[0]);
        double between = Math.Abs(Dir(a) - Dir(b)) * 180 / Math.PI % 180;
        Assert.True(Math.Abs(between - 60) < 1e-3 || Math.Abs(between - 120) < 1e-3, $"angle between the lines is {between}°");
    }

    [Fact]
    public async Task An_over_defining_dimension_is_refused_and_removed()
    {
        var (doc, _, added) = await SketchAsync("dim_over", "front", Line([0, 0], [50, 0]));
        string line = Name(added, 0);
        await Ed.DimensionAddAsync(doc, [Seg(line)], "linear", valueMm: 40);
        try
        {
            var ex = await Assert.ThrowsAsync<SwException>(() => Ed.DimensionAddAsync(doc, [Seg(line)], "linear", valueMm: 45));
            if (ex.Code == ErrorCodes.DialogBlocking)
            {
                // The over-defining prompt was not matched by DialogRules.OverdefiningDimension: answer it so the run can go on.
                foreach (var d in S.ListDialogs()) await new SessionService(S).RespondAsync(d.HandleHex, "Cancel", false);
                Assert.Fail("The over-defining dimension prompt was not answered automatically: " + ex.Message);
            }
            Assert.Equal(ErrorCodes.VerifyFailed, ex.Code);
        }
        finally
        {
            for (int i = 0; i < 50 && S.Dispatcher.Current is not null; i++) await Task.Delay(100);
        }
        var info = await Ed.InfoAsync(doc, null);
        var dim = Assert.Single(info.Dimensions!);
        Assert.Equal(40, dim.Value, L);
        Assert.Equal(40, (await SegmentAsync(doc, line)).LengthMm!.Value, L);
    }

    [Fact]
    public async Task Dimension_arguments_are_checked_before_anything_changes()
    {
        var (doc, _, added) = await SketchAsync("dim_args", "front", Line([0, 0], [50, 0]), Circle([0, 30], 5));
        string line = Name(added, 0), circle = Name(added, 1);
        await Fails(ErrorCodes.InvalidArgument, () => Ed.DimensionAddAsync(doc, [Seg(line)], "radial", valueMm: 5));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.DimensionAddAsync(doc, [Seg(circle)], "linear", valueMm: 5));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.DimensionAddAsync(doc, [Seg(line)], "linear", valueDeg: 30));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.DimensionAddAsync(doc, [Seg(line)], "linear", valueMm: -3));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.DimensionAddAsync(doc, [Seg(line)], "oblique", valueMm: 3));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.DimensionAddAsync(doc, [Seg("Line99")], "linear", valueMm: 3));
        Assert.Null((await Ed.InfoAsync(doc, null)).Dimensions);
    }

    // ------------------------------------------------------------------ sketch_relation_add

    [Fact]
    public async Task Horizontal_relation_levels_a_slanted_line()
    {
        var (doc, _, added) = await SketchAsync("rel_h", "front", Line([0, 0], [50, 20]));
        string line = Name(added, 0);
        var r = await Ed.RelationAddAsync(doc, "horizontal", [Seg(line)]);
        Assert.Equal(r.RelationsBefore + 1, r.RelationsAfter);
        var s = await SegmentAsync(doc, line);
        Assert.Equal(s.StartMm![1], s.EndMm![1], 1e-6);
        Assert.Contains(r.Segments!, x => x.Name == line);
    }

    [Fact]
    public async Task Equal_and_perpendicular_relations_hold_on_the_solved_geometry()
    {
        var (doc, _, added) = await SketchAsync("rel_eq", "front", Line([0, 0], [40, 0]), Line([60, 0], [90, 40]));
        string a = Name(added, 0), b = Name(added, 1);
        await Ed.RelationAddAsync(doc, "equal", [Seg(a), Seg(b)]);
        var sa = await SegmentAsync(doc, a);
        var sb = await SegmentAsync(doc, b);
        Assert.Equal(sa.LengthMm!.Value, sb.LengthMm!.Value, L);

        var r = await Ed.RelationAddAsync(doc, "perpendicular", [Seg(a), Seg(b)]);
        sa = await SegmentAsync(doc, a);
        sb = await SegmentAsync(doc, b);
        double dot = (sa.EndMm![0] - sa.StartMm![0]) * (sb.EndMm![0] - sb.StartMm![0]) + (sa.EndMm[1] - sa.StartMm[1]) * (sb.EndMm[1] - sb.StartMm[1]);
        Assert.Equal(0, dot / (sa.LengthMm!.Value * sb.LengthMm!.Value), 1e-6);
        Assert.Equal(r.RelationsBefore + 1, r.RelationsAfter);
        Assert.Equal(sa.LengthMm!.Value, sb.LengthMm!.Value, L); // the equal relation still holds
    }

    [Fact]
    public async Task Relation_misuse_and_conflicts_are_refused()
    {
        var (doc, _, added) = await SketchAsync("rel_neg", "front", Line([0, 0], [10, 40]), Line([20, 0], [60, 0]));
        string a = Name(added, 0), b = Name(added, 1);
        await Fails(ErrorCodes.InvalidArgument, () => Ed.RelationAddAsync(doc, "sideways", [Seg(a)]));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.RelationAddAsync(doc, "parallel", [Seg(a)]));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.RelationAddAsync(doc, "tangent", [Seg(a), Seg(b)]));

        await Ed.RelationAddAsync(doc, "vertical", [Seg(a)]);
        int before = (await Ed.InfoAsync(doc, null)).Relations;
        await Fails(ErrorCodes.VerifyFailed, () => Ed.RelationAddAsync(doc, "horizontal", [Seg(a)])); // conflicts with vertical
        var info = await Ed.InfoAsync(doc, null);
        Assert.Equal(before, info.Relations);
        Assert.NotEqual("over", info.Constrained);
    }

    // ------------------------------------------------------------------ sketch_trim

    [Fact]
    public async Task Trim_removes_the_picked_piece_of_a_crossing_line()
    {
        var (doc, _, added) = await SketchAsync("trim", "front", Line([-50, 0], [50, 0]), Line([0, -50], [0, 50]));
        string vertical = Name(added, 1);
        var r = await Ed.TrimAsync(doc, [25, 0]);
        Assert.Equal("trim", r.Operation);
        var info = await Ed.InfoAsync(doc, null);
        var level = Solid(info, "line").Where(s => Math.Abs(s.StartMm![1]) < L && Math.Abs(s.EndMm![1]) < L).ToList();
        Assert.Equal(50, level.Sum(s => s.LengthMm!.Value), L);
        Assert.True(level.All(s => Math.Max(s.StartMm![0], s.EndMm![0]) < L), "the piece right of the crossing is gone");
        Assert.Equal(100, Assert.Single(info.Segments, s => s.Name == vertical).LengthMm!.Value, L);
    }

    [Fact]
    public async Task Trim_needs_a_point_on_a_segment()
    {
        var (doc, _, _) = await SketchAsync("trim_neg", "front", Line([-50, 0], [50, 0]));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.TrimAsync(doc, [200, 200]));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.TrimAsync(doc, null));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.TrimAsync(doc, [1, 2, 3, 4]));
        Assert.Equal(100, Assert.Single((await Ed.InfoAsync(doc, null)).Segments).LengthMm!.Value, L);
    }

    // ------------------------------------------------------------------ sketch_extend

    [Fact]
    public async Task Extend_reaches_the_next_segment()
    {
        var (doc, _, added) = await SketchAsync("extend", "front", Line([50, -20], [50, 20]), Line([0, 0], [10, 0]));
        string stub = Name(added, 1);
        var r = await Ed.ExtendAsync(doc, Seg(stub), "auto");
        Assert.Contains(r.Changed!, c => c.Name == stub && c.LengthBeforeMm is > 9.99 and < 10.01);
        var s = await SegmentAsync(doc, stub);
        Assert.Equal(50, s.LengthMm!.Value, L);
        Assert.Equal(50, Math.Max(s.StartMm![0], s.EndMm![0]), L);
    }

    [Fact]
    public async Task Extend_refuses_circles_and_lines_with_nothing_ahead()
    {
        var (doc, _, added) = await SketchAsync("extend_neg", "front", Line([50, -20], [50, 20]), Circle([0, 0], 5));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ExtendAsync(doc, Seg(Name(added, 1))));
        await Fails(ErrorCodes.VerifyFailed, () => Ed.ExtendAsync(doc, Seg(Name(added, 0))));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ExtendAsync(doc, Seg(Name(added, 0)), "middle"));
        Assert.Equal(40, (await SegmentAsync(doc, Name(added, 0))).LengthMm!.Value, L);
    }

    // ------------------------------------------------------------------ sketch_offset

    [Fact]
    public async Task Offset_rectangle_5_outward_is_110_by_70_and_inward_90_by_50()
    {
        var (doc, _, added) = await SketchAsync("offset", "front", Rect(100, 60));
        var sides = Sides(added, 0).Select(Seg).ToArray();
        var outward = await Ed.OffsetAsync(doc, sides, 5, "outward");
        Assert.Equal("outward", outward.Side);
        Near([-55, -35, 55, 35], outward.BoxMm);
        Assert.Equal(4, outward.Created!.Count(c => c.Kind == "line" && c.Construction != true));

        var inward = await Ed.OffsetAsync(doc, sides, 5, "inward"); // the original rectangle again
        Assert.Equal("inward", inward.Side);
        Near([-45, -25, 45, 25], inward.BoxMm);
    }

    [Fact]
    public async Task Offset_circle_outward_and_by_chain()
    {
        var (doc, _, added) = await SketchAsync("offset_circle", "front", Circle([0, 0], 5), Rect(100, 60));
        var r = await Ed.OffsetAsync(doc, [Seg(Name(added, 0))], 2, "outward");
        Assert.Equal(7, Assert.Single(r.Created!, c => c.Kind == "circle").RadiusMm!.Value, L);

        // One side of the rectangle with chain=true offsets the whole loop.
        var chained = await Ed.OffsetAsync(doc, [Seg(added.Entities[1].Sides!["top"])], 3, "outward", chain: true);
        Near([-53, -33, 53, 33], chained.BoxMm);
    }

    [Fact]
    public async Task Offset_arguments_are_checked()
    {
        var (doc, _, added) = await SketchAsync("offset_neg", "front", Rect(100, 60));
        var sides = Sides(added, 0).Select(Seg).ToArray();
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, sides, 0, "outward"));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, sides, 5, "up"));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, [], 5, "outward"));
        Assert.Equal(4, Solid(await Ed.InfoAsync(doc, null), "line").Count());
    }

    // ------------------------------------------------------------------ sketch_fillet

    [Fact]
    public async Task Fillet_r5_rounds_a_rectangle_corner()
    {
        var (doc, _, added) = await SketchAsync("fillet", "front", Rect(100, 60));
        var byPoint = await Ed.FilletAsync(doc, null, [50, 30], 5);
        var arc = Assert.Single(byPoint.Created!, c => c.Kind == "arc");
        Assert.Equal(5, arc.RadiusMm!.Value, L);
        Near([45, 25], arc.CenterMm);
        Assert.Equal(Math.PI * 5 / 2, arc.LengthMm!.Value, 1e-3);

        var sides = added.Entities[0].Sides!;
        var bySegments = await Ed.FilletAsync(doc, [Seg(sides["left"]), Seg(sides["bottom"])], null, 5);
        Near([-45, -25], Assert.Single(bySegments.Created!, c => c.Kind == "arc").CenterMm);
    }

    [Fact]
    public async Task Fillet_needs_one_corner_and_a_positive_radius()
    {
        var (doc, _, added) = await SketchAsync("fillet_neg", "front", Rect(100, 60));
        var sides = added.Entities[0].Sides!;
        await Fails(ErrorCodes.InvalidArgument, () => Ed.FilletAsync(doc, null, [50, 30], 0));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.FilletAsync(doc, null, null, 5));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.FilletAsync(doc, [Seg(sides["top"]), Seg(sides["right"])], [50, 30], 5));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.FilletAsync(doc, null, [10, 10], 5));
        Assert.Empty(Solid(await Ed.InfoAsync(doc, null), "arc"));
    }

    // ------------------------------------------------------------------ sketch_chamfer

    [Fact]
    public async Task Chamfer_equal_and_two_distances_cut_the_corner()
    {
        var (doc, _, added) = await SketchAsync("chamfer", "front", Rect(100, 60));
        // The chamfer line is the new regular line that is neither horizontal nor vertical (robust to renamed sides).
        static bool Slanted(SketchSegmentInfo c) => c.Kind == "line" && c.Construction != true &&
            Math.Abs(c.StartMm![0] - c.EndMm![0]) > L && Math.Abs(c.StartMm[1] - c.EndMm[1]) > L;
        var eq = await Ed.ChamferAsync(doc, null, [50, 30], "equal", 5);
        var cut = Assert.Single(eq.Created!, Slanted);
        Assert.Equal(5 * Math.Sqrt(2), cut.LengthMm!.Value, L);
        var ends = new[] { cut.StartMm!, cut.EndMm! }.OrderBy(p => p[0]).ToArray();
        Near([45, 30], ends[0]);
        Near([50, 25], ends[1]);

        var sides = added.Entities[0].Sides!;
        var dd = await Ed.ChamferAsync(doc, [Seg(sides["bottom"]), Seg(sides["left"])], null, "distance_distance", 5, distance2Mm: 10);
        Assert.Equal(Math.Sqrt(125), Assert.Single(dd.Created!, Slanted).LengthMm!.Value, L);
    }

    [Fact]
    public async Task Chamfer_arguments_are_checked()
    {
        var (doc, _, _) = await SketchAsync("chamfer_neg", "front", Rect(100, 60));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ChamferAsync(doc, null, [50, 30], "distance_distance", 5));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ChamferAsync(doc, null, [50, 30], "distance_angle", 5, angleDeg: 0));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ChamferAsync(doc, null, [50, 30], "equal", 5, distance2Mm: 3));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ChamferAsync(doc, null, [50, 30], "round", 5));
        Assert.Equal(4, Solid(await Ed.InfoAsync(doc, null), "line").Count());
    }

    // ------------------------------------------------------------------ sketch_pattern

    [Fact]
    public async Task Linear_pattern_of_a_circle_places_3_circles_20_apart()
    {
        var (doc, _, added) = await SketchAsync("pattern_lin", "front", Circle([0, 0], 5));
        var r = await Ed.PatternAsync(new SketchPatternRequest
        {
            Doc = doc, Kind = "linear", Entities = [Seg(Name(added, 0))], CountX = 3, SpacingXMm = 20,
        });
        Assert.Equal("pattern", r.Operation);
        var circles = Solid(await Ed.InfoAsync(doc, null), "circle").OrderBy(c => c.CenterMm![0]).ToList();
        Assert.Equal(3, circles.Count);
        for (int i = 0; i < 3; i++)
        {
            Near([20 * i, 0], circles[i].CenterMm);
            Assert.Equal(5, circles[i].RadiusMm!.Value, L);
        }
    }

    [Fact]
    public async Task Circular_pattern_places_4_circles_around_the_center()
    {
        var (doc, _, added) = await SketchAsync("pattern_circ", "front", Circle([30, 0], 3));
        await Ed.PatternAsync(new SketchPatternRequest
        {
            Doc = doc, Kind = "circular", Entities = [Seg(Name(added, 0))], Center = [0, 0], Count = 4,
        });
        var centers = Solid(await Ed.InfoAsync(doc, null), "circle").Select(c => c.CenterMm!).ToList();
        Assert.Equal(4, centers.Count);
        double[][] wanted = [[30, 0], [0, 30], [-30, 0], [0, -30]];
        foreach (var want in wanted)
            Assert.Contains(centers, c => Math.Abs(c[0] - want[0]) < L && Math.Abs(c[1] - want[1]) < L);
    }

    [Fact]
    public async Task Pattern_arguments_are_checked()
    {
        var (doc, _, added) = await SketchAsync("pattern_neg", "front", Circle([0, 0], 5));
        EntityRef[] seed = [Seg(Name(added, 0))];
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "linear", Entities = seed, CountX = 1 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "linear", Entities = seed, CountX = 3 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 4 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 1, Center = [0, 0] }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "spiral", Entities = seed, Count = 3 }));
        Assert.Single(Solid(await Ed.InfoAsync(doc, null), "circle"));
    }

    // ------------------------------------------------------------------ sketch_mirror

    [Fact]
    public async Task Mirror_copies_a_circle_across_a_centerline()
    {
        var (doc, _, added) = await SketchAsync("mirror", "front", Centerline([0, -50], [0, 50]), Circle([20, 10], 5));
        string axis = Name(added, 0), circle = Name(added, 1);
        var r = await Ed.MirrorAsync(doc, [Seg(circle)], Seg(axis));
        Assert.Equal("mirror", r.Operation);
        var copy = Assert.Single(Solid(await Ed.InfoAsync(doc, null), "circle"), c => c.Name != circle);
        Near([-20, 10], copy.CenterMm);
        Assert.Equal(5, copy.RadiusMm!.Value, L);
    }

    [Fact]
    public async Task Mirror_needs_a_line_that_is_not_mirrored_itself()
    {
        var (doc, _, added) = await SketchAsync("mirror_neg", "front", Centerline([0, -50], [0, 50]), Circle([20, 10], 5));
        string axis = Name(added, 0), circle = Name(added, 1);
        await Fails(ErrorCodes.InvalidArgument, () => Ed.MirrorAsync(doc, [Seg(axis)], Seg(circle)));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.MirrorAsync(doc, [Seg(axis), Seg(circle)], Seg(axis)));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.MirrorAsync(doc, [Seg(circle)], null));
        Assert.Single(Solid(await Ed.InfoAsync(doc, null), "circle"));
    }

    // ------------------------------------------------------------------ sketch_convert

    [Fact]
    public async Task Convert_turns_a_100_mm_block_edge_into_a_100_mm_line()
    {
        // Block 100 (x) × 20 (y) × 60 (z) on the top plane; the top face is at y = 20.
        string doc = await NewPartAsync("convert");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [Rect(100, 60)]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await Sk.StartAsync(doc, new EntityRef { At = [0, 20, 0] }, false);

        var r = await Ed.ConvertAsync(doc, [new EntityRef { Type = "edge", At = [0, 20, 30] }]);
        var line = Assert.Single(r.Created!);
        Assert.Equal("line", line.Kind);
        Assert.Equal(100, line.LengthMm!.Value, L);
        Assert.Contains(r.Checks!, c => c.Contains("equals the edges' length"));
    }

    [Fact]
    public async Task Convert_needs_model_edges()
    {
        var (doc, _, added) = await SketchAsync("convert_neg", "front", Line([0, 0], [50, 0]));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.ConvertAsync(doc, [new EntityRef { Type = "edge", At = [0, 0, 0] }])); // no bodies
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ConvertAsync(doc, [Seg(Name(added, 0))])); // already in this sketch
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ConvertAsync(doc, []));
    }

    // ------------------------------------------------------------------ sketch_construction_set

    [Fact]
    public async Task Construction_flag_toggles_and_reads_back()
    {
        var (doc, _, added) = await SketchAsync("construction", "front", Line([0, 0], [50, 0]), Circle([20, 20], 5));
        string line = Name(added, 0);
        var on = await Ed.ConstructionSetAsync(doc, [Seg(line)], true);
        Assert.Contains(on.Changed!, c => c.Name == line && c.Construction == true);
        Assert.True((await SegmentAsync(doc, line)).Construction);
        Assert.Equal(1, on.Contours.Segments); // only the circle still counts for features

        var again = await Ed.ConstructionSetAsync(doc, [Seg(line)], true); // idempotent
        Assert.Null(again.Changed);

        await Ed.ConstructionSetAsync(doc, [Seg(line)], false);
        Assert.Null((await SegmentAsync(doc, line)).Construction);
    }

    [Fact]
    public async Task Construction_set_needs_existing_segments()
    {
        var (doc, _, _) = await SketchAsync("construction_neg", "front", Line([0, 0], [50, 0]));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.ConstructionSetAsync(doc, [Seg("Line99")], true));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.ConstructionSetAsync(doc, [], true));
    }

    // ------------------------------------------------------------------ sketch_delete

    [Fact]
    public async Task Delete_removes_the_segment()
    {
        var (doc, _, added) = await SketchAsync("delete", "front", Line([0, 0], [50, 0]), Line([0, 10], [50, 10]));
        string first = Name(added, 0), second = Name(added, 1);
        var r = await Ed.DeleteAsync(doc, [Seg(first)]);
        Assert.Equal([first], r.Removed!);
        var info = await Ed.InfoAsync(doc, null);
        Assert.Equal(second, Assert.Single(info.Segments).Name);
    }

    [Fact]
    public async Task Delete_needs_existing_segments_and_an_open_sketch()
    {
        var (doc, _, added) = await SketchAsync("delete_neg", "front", Line([0, 0], [50, 0]));
        await Fails(ErrorCodes.EntityNotFound, () => Ed.DeleteAsync(doc, [Seg("Line99")]));
        await Sk.FinishAsync(doc);
        await Fails(ErrorCodes.SketchState, () => Ed.DeleteAsync(doc, [Seg(Name(added, 0))]));
    }

    // ------------------------------------------------------------------ workflow and sketch state

    [Fact]
    public async Task Edit_dimension_finish_rebuilds_the_extrude()
    {
        // A Ø20 × 20 cylinder; re-open its sketch, dimension the circle Ø30 and finish: V = π·15²·20.
        var (doc, sketch, added) = await SketchAsync("edit_flow", "top", Circle([0, 0], 10));
        var boss = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(Math.PI * 100 * 20, boss.Verified.VolumeDeltaMm3, 1e-2);
        await Ed.EditAsync(doc, sketch);
        var dim = await Ed.DimensionAddAsync(doc, [Seg(Name(added, 0))], "diameter", valueMm: 30);
        await Sk.FinishAsync(doc);
        var info = await new InspectService(S).ModelInfoAsync(doc);
        Assert.Equal(Math.PI * 225 * 20, info.VolumeMm3!.Value, 1e-2);
        var kept = Assert.Single((await Ed.InfoAsync(doc, sketch)).Dimensions!);
        Assert.Equal(dim.Dimension.Name, kept.Name);
        Assert.Equal(30, kept.Value, L);
    }

    [Fact]
    public async Task Editing_tools_need_the_servers_open_sketch()
    {
        string doc = await NewPartAsync("no_sketch");
        await Fails(ErrorCodes.SketchState, () => Ed.TrimAsync(doc, [0, 0]));
        await Fails(ErrorCodes.SketchState, () => Ed.DimensionAddAsync(doc, [Seg("Line1")], "linear", valueMm: 5));
        await Fails(ErrorCodes.SketchState, () => Ed.RelationAddAsync(doc, "horizontal", [Seg("Line1")]));
        await Fails(ErrorCodes.SketchState, () => Ed.OffsetAsync(doc, [Seg("Line1")], 5, "outward"));
        await Fails(ErrorCodes.SketchState, () => Ed.ConvertAsync(doc, [new EntityRef { Type = "edge", At = [0, 0, 0] }]));
    }
}
