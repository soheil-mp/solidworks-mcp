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
    private static SketchEntity Point(double[] at) => new() { Kind = "point", At = at };
    private static EntityRef Seg(string name) => new() { Name = name };
    private static EntityRef At(double x, double y) => new() { At = [x, y] };

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

    /// <summary>True when the line runs between a and b (in either direction).</summary>
    private static bool Joins(SketchSegmentInfo l, double[] a, double[] b, double tol = L)
    {
        bool At(double[] p, double[]? q) => q is not null && Math.Abs(p[0] - q[0]) < tol && Math.Abs(p[1] - q[1]) < tol;
        return (At(a, l.StartMm) && At(b, l.EndMm)) || (At(a, l.EndMm) && At(b, l.StartMm));
    }

    /// <summary>Distance from p to the infinite line through the segment.</summary>
    private static double DistToLine(double[] p, SketchSegmentInfo l)
    {
        double dx = l.EndMm![0] - l.StartMm![0], dy = l.EndMm[1] - l.StartMm[1];
        return Math.Abs(dx * (p[1] - l.StartMm[1]) - dy * (p[0] - l.StartMm[0])) / Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>p mirrored about the infinite line through the segment.</summary>
    private static double[] Mirror(double[] p, SketchSegmentInfo l)
    {
        double dx = l.EndMm![0] - l.StartMm![0], dy = l.EndMm[1] - l.StartMm[1];
        double t = ((p[0] - l.StartMm[0]) * dx + (p[1] - l.StartMm[1]) * dy) / (dx * dx + dy * dy);
        double fx = l.StartMm[0] + t * dx, fy = l.StartMm[1] + t * dy;
        return [2 * fx - p[0], 2 * fy - p[1]];
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
    public async Task Radial_horizontal_and_point_to_point_vertical_dimensions_drive_the_geometry()
    {
        var (doc, _, added) = await SketchAsync("dim_more", "front",
            Circle([0, 0], 10), Line([20, 0], [50, 40]), Circle([60, 0], 3), Circle([80, 10], 3));
        string circle = Name(added, 0), line = Name(added, 1), c1 = Name(added, 2), c2 = Name(added, 3);

        var rad = await Ed.DimensionAddAsync(doc, [Seg(circle)], "radial", valueMm: 12);
        Assert.Equal(12, rad.Dimension.Value, L);
        Assert.Equal(12, (await SegmentAsync(doc, circle)).RadiusMm!.Value, L);

        // The slanted line's horizontal extent (30 → 20), not its length (50).
        var hor = await Ed.DimensionAddAsync(doc, [Seg(line)], "horizontal", valueMm: 20);
        Assert.Equal(20, hor.Measured!.Value, L);
        var ls = await SegmentAsync(doc, line);
        Assert.Equal(20, Math.Abs(ls.EndMm![0] - ls.StartMm![0]), L);

        // Between the two circle centers (Δy 10 → 25).
        var ver = await Ed.DimensionAddAsync(doc, [At(60, 0), At(80, 10)], "vertical", valueMm: 25);
        Assert.Equal(25, ver.Dimension.Value, L);
        Assert.Equal(25, Math.Abs((await SegmentAsync(doc, c2)).CenterMm![1] - (await SegmentAsync(doc, c1)).CenterMm![1]), L);
        Assert.Equal(3, (await Ed.InfoAsync(doc, null)).Dimensions!.Count);
    }

    [Fact]
    public async Task Angular_dimension_on_the_top_plane_takes_the_drawn_angle()
    {
        // Top Plane (sketch y = model -z): the text point goes to AddDimension2 in model coordinates. Read in sketch
        // coordinates it would land in the other angle (126.87°), and the drawn 53.13° could not be set to 60°.
        var (doc, _, added) = await SketchAsync("dim_angle_top", "top", Line([0, 100], [50, 100]), Line([0, 100], [30, 140]));
        string l1 = Name(added, 0), l2 = Name(added, 1);
        var ang = await Ed.DimensionAddAsync(doc, [Seg(l1), Seg(l2)], "angular", valueDeg: 60);
        Assert.Equal(60, ang.Dimension.Value, 1e-6);
        Assert.Equal(60, ang.Measured!.Value, 1e-4);
        var a = await SegmentAsync(doc, l1);
        var b = await SegmentAsync(doc, l2);
        double Dir(SketchSegmentInfo s) => Math.Atan2(s.EndMm![1] - s.StartMm![1], s.EndMm[0] - s.StartMm[0]);
        double between = Math.Abs(Dir(a) - Dir(b)) * 180 / Math.PI % 180;
        Assert.True(Math.Abs(between - 60) < 1e-3 || Math.Abs(between - 120) < 1e-3, $"angle between the lines is {between}°");
    }

    [Fact]
    public async Task The_origin_takes_dimensions_and_relations()
    {
        // Top Plane: the circle center (30, 40) is 50 mm from the origin; dimension it to 40, then put the other circle's
        // center on the origin.
        var (doc, _, added) = await SketchAsync("origin_refs", "top", Circle([30, 40], 5), Circle([-20, 10], 3));
        EntityRef origin = new() { Name = "origin" };
        var dim = await Ed.DimensionAddAsync(doc, [origin, At(30, 40)], "linear", valueMm: 40);
        Assert.Equal(40, dim.Dimension.Value, L);
        var c0 = (await SegmentAsync(doc, Name(added, 0))).CenterMm!;
        Assert.Equal(40, Math.Sqrt(c0[0] * c0[0] + c0[1] * c0[1]), L);

        await Ed.RelationAddAsync(doc, "coincident", [At(-20, 10), origin]);
        Near([0, 0], (await SegmentAsync(doc, Name(added, 1))).CenterMm);
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
    public async Task Horizontal_relation_of_two_points_levels_the_circle_centers()
    {
        var (doc, _, added) = await SketchAsync("rel_hpts", "front", Circle([60, 0], 3), Circle([80, 10], 3));
        string c1 = Name(added, 0), c2 = Name(added, 1);
        var r = await Ed.RelationAddAsync(doc, "horizontal", [At(60, 0), At(80, 10)]);
        Assert.Equal(r.RelationsBefore + 1, r.RelationsAfter);
        Assert.Equal((await SegmentAsync(doc, c1)).CenterMm![1], (await SegmentAsync(doc, c2)).CenterMm![1], 1e-6);
    }

    [Fact]
    public async Task Merge_and_coincident_join_two_line_ends_into_one_point()
    {
        var (doc, _, added) = await SketchAsync("rel_merge", "front",
            Line([0, 0], [20, 0]), Line([25, 5], [40, 20]), Line([0, 50], [20, 50]), Line([25, 55], [40, 70]), Line([60, 0], [80, 0]), Point([100, 30]));
        string a1 = Name(added, 0), a2 = Name(added, 1), b1 = Name(added, 2), b2 = Name(added, 3);
        int points = (await Ed.InfoAsync(doc, null)).Points;

        var m = await Ed.RelationAddAsync(doc, "merge", [At(20, 0), At(25, 5)]);
        Assert.Equal(m.RelationsBefore, m.RelationsAfter); // a merge adds no relation: the two points become one
        Assert.Contains(m.Checks!, c => c.StartsWith("points merged"));
        Near((await SegmentAsync(doc, a1)).EndMm!, (await SegmentAsync(doc, a2)).StartMm, 1e-6);

        // SOLIDWORKS merges two end points for 'coincident' as well.
        var c = await Ed.RelationAddAsync(doc, "coincident", [At(20, 50), At(25, 55)]);
        Assert.Contains(c.Checks!, x => x.StartsWith("points merged"));
        Near((await SegmentAsync(doc, b1)).EndMm!, (await SegmentAsync(doc, b2)).StartMm, 1e-6);
        Assert.Equal(points - 2, (await Ed.InfoAsync(doc, null)).Points);

        // A free point can't merge: 'merge' makes it coincident with the line end (a relation) instead.
        var f = await Ed.RelationAddAsync(doc, "merge", [At(100, 30), At(80, 0)]);
        Assert.Equal(f.RelationsBefore + 1, f.RelationsAfter);
        Assert.Contains(f.Checks!, x => x.StartsWith("coincident relation instead"));
        Assert.Contains(f.Checks!, x => x == "point on the entity");
    }

    [Fact]
    public async Task Pierce_puts_a_point_where_a_line_of_another_sketch_crosses_the_plane()
    {
        // Sketch on Top: (10,-20) → (30,20) is model (10,0,20) → (30,0,-20), which crosses the Front plane at (20,0,0).
        string doc = await NewPartAsync("rel_pierce");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        var line = await Sk.AddAsync(doc, [Line([10, -20], [30, 20])]);
        var first = await Sk.FinishAsync(doc);
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await Sk.AddAsync(doc, [Point([5, 7])]);

        var r = await Ed.RelationAddAsync(doc, "pierce", [At(5, 7), new EntityRef { Name = Name(line, 0), Sketch = first.Sketch }]);
        Assert.Equal(r.RelationsBefore + 1, r.RelationsAfter);
        Assert.Contains(r.Checks!, c => c.StartsWith("the point lies on the curve"));
        Assert.Equal("fully", r.Constrained);
        await Fails(ErrorCodes.EntityNotFound, () => Ed.DeleteAsync(doc, [At(5, 7)])); // the point left (5, 7) ...
        await Ed.DeleteAsync(doc, [At(20, 0)]);                                          // ... for the pierce point
    }

    [Fact]
    public async Task Pierce_puts_a_point_where_a_model_edge_crosses_the_plane()
    {
        // Block 100 (x) × 20 (y) × 60 (z) from the Top plane: its edge at x = 50, y = 20 runs along z through the Front plane.
        string doc = await NewPartAsync("rel_pierce_edge");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [Rect(100, 60)]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        var added = await Sk.AddAsync(doc, [Point([5, 7]), Line([0, -10], [40, -10])]);
        // A segment of the open sketch lies in its plane, so it can't be pierced.
        await Fails(ErrorCodes.InvalidArgument, () => Ed.RelationAddAsync(doc, "pierce", [At(5, 7), Seg(Name(added, 1))]));

        var r = await Ed.RelationAddAsync(doc, "pierce", [At(5, 7), new EntityRef { Type = "edge", At = [50, 20, 10] }]);
        Assert.Equal(r.RelationsBefore + 1, r.RelationsAfter);
        Assert.Contains(r.Checks!, c => c.StartsWith("the point lies on the curve"));
        await Ed.DeleteAsync(doc, [At(50, 20)]); // the point is at the pierce point
        await Sk.FinishAsync(doc);
    }

    [Fact]
    public async Task Tangent_concentric_midpoint_symmetric_collinear_parallel_intersection_and_fix_hold()
    {
        // Circle centers stand in for points, so their solved positions can be read back.
        var (doc, _, added) = await SketchAsync("rel_more", "front",
            Circle([0, 0], 10), Line([-20, 15], [20, 15]),                                            // 0, 1 tangent
            Circle([60, 0], 3), Circle([63, 2], 6),                                                    // 2, 3 concentric
            Line([90, 0], [130, 0]), Circle([100, 7], 2),                                              // 4, 5 midpoint
            Centerline([160, -20], [160, 40]), Circle([150, 10], 2), Circle([175, 12], 2),             // 6, 7, 8 symmetric
            Line([0, -40], [20, -40]), Line([30, -45], [50, -44]),                                     // 9, 10 collinear
            Line([0, -80], [20, -70]), Line([30, -80], [60, -75]),                                     // 11, 12 parallel
            Line([100, -80], [140, -40]), Line([100, -40], [140, -80]), Circle([125, -55], 2));        // 13, 14, 15 intersection
        string N(int i) => Name(added, i);
        async Task<SketchSegmentInfo> G(int i) => await SegmentAsync(doc, N(i));

        await Ed.RelationAddAsync(doc, "tangent", [Seg(N(1)), Seg(N(0))]);
        var big = await G(0);
        Assert.Equal(big.RadiusMm!.Value, DistToLine(big.CenterMm!, await G(1)), L);

        await Ed.RelationAddAsync(doc, "concentric", [Seg(N(2)), Seg(N(3))]);
        Near((await G(2)).CenterMm!, (await G(3)).CenterMm);

        await Ed.RelationAddAsync(doc, "midpoint", [At(100, 7), Seg(N(4))]);
        var ml = await G(4);
        Near([(ml.StartMm![0] + ml.EndMm![0]) / 2, (ml.StartMm[1] + ml.EndMm[1]) / 2], (await G(5)).CenterMm);

        await Ed.RelationAddAsync(doc, "symmetric", [At(150, 10), At(175, 12), Seg(N(6))]);
        Near(Mirror((await G(7)).CenterMm!, await G(6)), (await G(8)).CenterMm);

        await Ed.RelationAddAsync(doc, "collinear", [Seg(N(9)), Seg(N(10))]);
        var c9 = await G(9);
        var c10 = await G(10);
        Assert.Equal(0, DistToLine(c10.StartMm!, c9), L);
        Assert.Equal(0, DistToLine(c10.EndMm!, c9), L);

        await Ed.RelationAddAsync(doc, "parallel", [Seg(N(11)), Seg(N(12))]);
        var p11 = await G(11);
        var p12 = await G(12);
        double cross = (p11.EndMm![0] - p11.StartMm![0]) * (p12.EndMm![1] - p12.StartMm![1]) - (p11.EndMm[1] - p11.StartMm[1]) * (p12.EndMm[0] - p12.StartMm[0]);
        Assert.Equal(0, cross / (p11.LengthMm!.Value * p12.LengthMm!.Value), 1e-6);

        await Ed.RelationAddAsync(doc, "intersection", [At(125, -55), Seg(N(13)), Seg(N(14))]);
        var a = await G(13);
        var b = await G(14);
        var h = (await G(15)).CenterMm!;
        Assert.Equal(0, DistToLine(h, a), L);
        Assert.Equal(0, DistToLine(h, b), L);

        var fix = await Ed.RelationAddAsync(doc, "fix", [Seg(N(9))]);
        Assert.Equal(fix.RelationsBefore + 1, fix.RelationsAfter);
        Assert.NotEqual("over", fix.Constrained);
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
    public async Task Trim_cuts_the_picked_half_of_a_circle()
    {
        // A line through the center of an r10 circle: trimming at the top leaves the lower half-arc (length 10π).
        var (doc, _, added) = await SketchAsync("trim_circle", "front", Circle([0, 0], 10), Line([-20, 0], [20, 0]));
        await Ed.TrimAsync(doc, [0, 10]);
        var info = await Ed.InfoAsync(doc, null);
        Assert.Empty(Solid(info, "circle"));
        var arc = Assert.Single(Solid(info, "arc"));
        Assert.Equal(10, arc.RadiusMm!.Value, L);
        Assert.Equal(10 * Math.PI, arc.LengthMm!.Value, 1e-3);
        Assert.True(Joins(arc, [10, 0], [-10, 0]), "the arc ends where the line crosses the circle");
        Assert.Equal(40, Assert.Single(info.Segments, s => s.Name == Name(added, 1)).LengthMm!.Value, L);
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
    public async Task Extend_on_the_top_plane_moves_only_the_requested_end()
    {
        // Top Plane (sketch y = model -z): a stub from (10,0) to (10,10) between lines at y = 40 and y = -20. The pick point
        // is in sketch coordinates; the same point in model coordinates would extend the start instead.
        var (doc, _, added) = await SketchAsync("extend_top", "top", Line([10, 0], [10, 10]), Line([-20, 40], [40, 40]), Line([-20, -20], [40, -20]));
        string stub = Name(added, 0);
        await Ed.ExtendAsync(doc, Seg(stub), "end");
        var s = await SegmentAsync(doc, stub);
        Near([10, 0], s.StartMm);
        Near([10, 40], s.EndMm);
        await Ed.ExtendAsync(doc, Seg(stub), "start");
        s = await SegmentAsync(doc, stub);
        Near([10, -20], s.StartMm);
        Near([10, 40], s.EndMm);
        await Fails(ErrorCodes.VerifyFailed, () => Ed.ExtendAsync(doc, Seg(stub), "end")); // it already ends on the top line
        Assert.Equal(60, (await SegmentAsync(doc, stub)).LengthMm!.Value, L);
    }

    [Fact]
    public async Task Extend_carries_an_arc_along_its_circle_to_the_next_line()
    {
        // Arc r10 about the origin from 0° to 90°; the line x = -5 is 30° ahead of its end (at 120°) and 120° behind its
        // start, so 'auto' extends the end to (-5, 8.66): the arc then spans 120°.
        var (doc, _, added) = await SketchAsync("extend_arc", "front",
            new SketchEntity { Kind = "arc", Center = [0, 0], RadiusMm = 10, StartDeg = 0, EndDeg = 90 }, Line([-5, -20], [-5, 20]));
        string arc = Name(added, 0);
        await Ed.ExtendAsync(doc, Seg(arc), "auto");
        var s = await SegmentAsync(doc, arc);
        Near([0, 0], s.CenterMm);
        Assert.Equal(10, s.RadiusMm!.Value, L);
        Assert.True(Joins(s, [10, 0], [-5, 10 * Math.Sin(Math.PI * 2 / 3)]), $"arc from [{string.Join(", ", s.StartMm!)}] to [{string.Join(", ", s.EndMm!)}]");
        Assert.Equal(10 * 2 * Math.PI / 3, s.LengthMm!.Value, 1e-3);
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
    public async Task Offset_both_sides_makes_the_outer_and_inner_loops()
    {
        var (doc, _, added) = await SketchAsync("offset_both", "front", Rect(100, 60));
        var r = await Ed.OffsetAsync(doc, Sides(added, 0).Select(Seg).ToArray(), 5, "both");
        Assert.Equal("both", r.Side);
        var lines = r.Created!.Where(c => c.Kind == "line" && c.Construction != true).Select(c => c.LengthMm!.Value).OrderBy(x => x).ToList();
        Assert.Equal(new double[] { 50, 50, 70, 70, 90, 90, 110, 110 }, lines.Select(x => Math.Round(x, 6)).ToArray());
        Near([-55, -35, 55, 35], r.BoxMm);
    }

    [Fact]
    public async Task Offset_open_chain_goes_left_or_right_of_its_first_segment()
    {
        // Line (0,0) → (50,0): right of it is -y. An L from (100,0) → (140,0) and (140,30) → (140,0) (drawn backwards):
        // seen along the first segment given, right is -y then +x; seen along the second, right is -x then +y.
        var (doc, _, added) = await SketchAsync("offset_open", "front",
            Line([0, 0], [50, 0]), Line([100, 0], [140, 0]), Line([140, 30], [140, 0]));
        string line = Name(added, 0), a = Name(added, 1), b = Name(added, 2);
        var right = await Ed.OffsetAsync(doc, [Seg(line)], 5, "right");
        Assert.Equal("right", right.Side);
        Near([0, -5, 50, -5], right.BoxMm);
        var left = await Ed.OffsetAsync(doc, [Seg(line)], 5, "left");
        Near([0, 5, 50, 5], left.BoxMm);

        var ab = await Ed.OffsetAsync(doc, [Seg(a), Seg(b)], 5, "right");
        Near([100, -5, 145, 30], ab.BoxMm);
        var ba = await Ed.OffsetAsync(doc, [Seg(b), Seg(a)], 5, "right");
        Near([100, 5, 135, 30], ba.BoxMm);

        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, [Seg(line)], 5, "outward")); // an open chain has no inside
    }

    [Fact]
    public async Task Offset_arguments_are_checked()
    {
        var (doc, _, added) = await SketchAsync("offset_neg", "front", Rect(100, 60));
        var sides = Sides(added, 0).Select(Seg).ToArray();
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, sides, 0, "outward"));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, sides, 5, "up"));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.OffsetAsync(doc, sides, 5, "left")); // a closed loop has no left
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

        // distanceMm goes along the first segment (the bottom loses 5 of its 100 mm), distance2Mm along the second (the left
        // side loses 10 of its 60 mm).
        var sides = added.Entities[0].Sides!;
        var dd = await Ed.ChamferAsync(doc, [Seg(sides["bottom"]), Seg(sides["left"])], null, "distance_distance", 5, distance2Mm: 10);
        Assert.Equal(Math.Sqrt(125), Assert.Single(dd.Created!, Slanted).LengthMm!.Value, L);
        Assert.Equal(95, (await SegmentAsync(doc, sides["bottom"])).LengthMm!.Value, L);
        Assert.Equal(50, (await SegmentAsync(doc, sides["left"])).LengthMm!.Value, L);
    }

    [Fact]
    public async Task Chamfer_distance_angle_measures_both_from_the_first_segment()
    {
        // Corner (50,30) of a 100x60 rectangle: 5 mm along the top, 30° from the top, so the right side loses 5·tan 30°.
        var (doc, _, added) = await SketchAsync("chamfer_da", "front", Rect(100, 60));
        var sides = added.Entities[0].Sides!;
        var r = await Ed.ChamferAsync(doc, [Seg(sides["top"]), Seg(sides["right"])], null, "distance_angle", 5, angleDeg: 30);
        double cut = 5 * Math.Tan(Math.PI / 6);
        var line = Assert.Single(r.Created!, c => c.Kind == "line");
        Assert.Equal(5 / Math.Cos(Math.PI / 6), line.LengthMm!.Value, L);
        var ends = new[] { line.StartMm!, line.EndMm! }.OrderBy(p => p[0]).ToArray();
        Near([45, 30], ends[0]);
        Near([50, 30 - cut], ends[1]);
        Assert.Equal(95, (await SegmentAsync(doc, sides["top"])).LengthMm!.Value, L);
        Assert.Equal(60 - cut, (await SegmentAsync(doc, sides["right"])).LengthMm!.Value, L);
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
    public async Task Linear_pattern_3_by_2_at_30_degrees_moves_circles_and_lines()
    {
        // Rows along 30° (20 mm apart), the second direction along 120° (10 mm apart); counts include the seed.
        var (doc, _, added) = await SketchAsync("pattern_lin2d", "front", Circle([0, 0], 2), Line([100, 0], [104, 3]));
        await Ed.PatternAsync(new SketchPatternRequest
        {
            Doc = doc, Kind = "linear", Entities = [Seg(Name(added, 0)), Seg(Name(added, 1))],
            CountX = 3, SpacingXMm = 20, CountY = 2, SpacingYMm = 10, AngleDeg = 30,
        });
        var info = await Ed.InfoAsync(doc, null);
        var circles = Solid(info, "circle").ToList();
        var lines = Solid(info, "line").ToList();
        Assert.Equal(6, circles.Count);
        Assert.Equal(6, lines.Count);
        double c30 = Math.Cos(Math.PI / 6), s30 = Math.Sin(Math.PI / 6);
        for (int ix = 0; ix < 3; ix++)
            for (int iy = 0; iy < 2; iy++)
            {
                double dx = ix * 20 * c30 - iy * 10 * s30, dy = ix * 20 * s30 + iy * 10 * c30;
                Assert.Contains(circles, c => Math.Abs(c.CenterMm![0] - dx) < L && Math.Abs(c.CenterMm[1] - dy) < L && Math.Abs(c.RadiusMm!.Value - 2) < L);
                Assert.Contains(lines, l => Joins(l, [100 + dx, dy], [104 + dx, 3 + dy]));
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
    public async Task Circular_pattern_over_90_degrees_spans_the_angle_either_way()
    {
        // 3 instances over 90°: the seed at 0°, copies at 45° and 90° (counter-clockwise), or -45° and -90° (clockwise).
        var (doc, _, added) = await SketchAsync("pattern_arc90", "front", Circle([30, 0], 3));
        EntityRef[] seed = [Seg(Name(added, 0))];
        double h = 30 / Math.Sqrt(2);
        await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Center = [0, 0], Count = 3, TotalAngleDeg = 90 });
        await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Center = [0, 0], Count = 3, TotalAngleDeg = -90 });
        var centers = Solid(await Ed.InfoAsync(doc, null), "circle").Select(c => c.CenterMm!).ToList();
        Assert.Equal(5, centers.Count);
        double[][] wanted = [[30, 0], [h, h], [0, 30], [h, -h], [0, -30]];
        foreach (var want in wanted)
            Assert.Contains(centers, c => Math.Abs(c[0] - want[0]) < L && Math.Abs(c[1] - want[1]) < L);
    }

    [Fact]
    public async Task Circular_pattern_rotates_lines_groups_and_points_about_the_center()
    {
        // SOLIDWORKS rotates about the selection's bounding-box center plus an offset, so every kind of seed is checked
        // against the requested center: a slanted line, two circles together, and a sketch point.
        var (doc, _, added) = await SketchAsync("pattern_kinds", "front",
            Line([20, -5], [40, 5]), Circle([130, 0], 3), Circle([140, 0], 2), Point([-60, 10]));
        string line = Name(added, 0), c1 = Name(added, 1), c2 = Name(added, 2);
        await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = [Seg(line)], Center = [0, 0], Count = 4 });
        var lines = Solid(await Ed.InfoAsync(doc, null), "line").ToList();
        double[][][] wantLines = [[[5, 20], [-5, 40]], [[-20, 5], [-40, -5]], [[-5, -20], [5, -40]]]; // +90°, 180°, 270°
        foreach (var w in wantLines)
            Assert.Contains(lines, l => new[] { l.StartMm!, l.EndMm! }.All(p => w.Any(q => Math.Abs(p[0] - q[0]) < L && Math.Abs(p[1] - q[1]) < L)));

        // Both circles turned 180° about (100, 0) as a group.
        await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = [Seg(c1), Seg(c2)], Center = [100, 0], Count = 2 });
        var circles = Solid(await Ed.InfoAsync(doc, null), "circle").ToList();
        Assert.Contains(circles, c => Math.Abs(c.CenterMm![0] - 70) < L && Math.Abs(c.CenterMm[1]) < L && Math.Abs(c.RadiusMm!.Value - 3) < L);
        Assert.Contains(circles, c => Math.Abs(c.CenterMm![0] - 60) < L && Math.Abs(c.CenterMm[1]) < L && Math.Abs(c.RadiusMm!.Value - 2) < L);

        // The point (-60, 10) about (-60, 0), below it (the API angle is 270°, not -90°): copies at (-70, 0), (-60, -10)
        // and (-50, 0), plus the point SOLIDWORKS adds at every circular pattern's center.
        int points = (await Ed.InfoAsync(doc, null)).Points;
        var p = await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = [At(-60, 10)], Center = [-60, 0], Count = 4 });
        Assert.Contains(p.Checks!, c => c.StartsWith("SOLIDWORKS added a sketch point at the center [-60, 0]"));
        Assert.Equal(points + 4, (await Ed.InfoAsync(doc, null)).Points);
        await Ed.DeleteAsync(doc, [At(-70, 0), At(-60, -10), At(-50, 0), At(-60, 0)]); // the points are where they belong
    }

    [Fact]
    public async Task Circular_pattern_of_an_arc_turns_it_about_the_center()
    {
        // The arc r10 about (0, 0) from 45° to 135°: its box (x ±7.07, y 7.07…10) differs from the box of its center and
        // end points (y 0…10), so the rotation reference is tested. Turned 180° about (0, -50): center (0, -100).
        var (doc, _, added) = await SketchAsync("pattern_arc", "front",
            new SketchEntity { Kind = "arc", Center = [0, 0], RadiusMm = 10, StartDeg = 45, EndDeg = 135 });
        string arc = Name(added, 0);
        await Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = [Seg(arc)], Center = [0, -50], Count = 2 });
        var copy = Assert.Single(Solid(await Ed.InfoAsync(doc, null), "arc"), a => a.Name != arc);
        Near([0, -100], copy.CenterMm);
        double h = 10 / Math.Sqrt(2);
        Assert.True(Joins(copy, [h, -100 - h], [-h, -100 - h]), $"arc copy from [{string.Join(", ", copy.StartMm!)}] to [{string.Join(", ", copy.EndMm!)}]");
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
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 3, Center = [20, 0], TotalAngleDeg = 0 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 3, Center = [20, 0], TotalAngleDeg = 400 }));
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "spiral", Entities = seed, Count = 3 }));
        // 3 instances over 300° would be 150° apart; SOLIDWORKS would space them 120° apart instead (verified), so it is refused.
        await Fails(ErrorCodes.InvalidArgument, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 3, Center = [20, 0], TotalAngleDeg = 300 }));
        // The circle's own center is the middle of its box: SOLIDWORKS can't pattern about it (radius 0).
        var ex = await Fails(ErrorCodes.NotSupported, () => Ed.PatternAsync(new SketchPatternRequest { Doc = doc, Kind = "circular", Entities = seed, Count = 4, Center = [0, 0] }));
        Assert.Contains("bounding box", ex.Message);
        var info = await Ed.InfoAsync(doc, null);
        Assert.Single(Solid(info, "circle"));
        Assert.Equal(0, info.Relations);
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
    public async Task Mirror_about_a_solid_line_also_copies_a_centerline()
    {
        // The axis is a regular line at x = -60. A centerline among the entities must be mirrored, not used as the axis.
        var (doc, _, added) = await SketchAsync("mirror_solid", "front",
            Line([-60, -50], [-60, 50]), Centerline([10, -20], [10, 20]), Circle([20, 10], 5), Line([30, 0], [40, 10]));
        string axis = Name(added, 0), cl = Name(added, 1), circle = Name(added, 2), line = Name(added, 3);
        var r = await Ed.MirrorAsync(doc, [Seg(cl), Seg(circle), Seg(line)], Seg(axis));
        Assert.Equal(3, r.Created!.Count);
        var info = await Ed.InfoAsync(doc, null);
        var copy = Assert.Single(Solid(info, "circle"), c => c.Name != circle);
        Near([-140, 10], copy.CenterMm);
        var clCopy = Assert.Single(info.Segments, s => s.Kind == "line" && s.Construction == true && s.Name != cl);
        Assert.True(Math.Abs(clCopy.StartMm![0] + 130) < L && Math.Abs(clCopy.EndMm![0] + 130) < L, "the centerline's copy is at x = -130");
        Assert.Contains(Solid(info, "line"), l => new[] { l.StartMm!, l.EndMm! }.All(p =>
            (Math.Abs(p[0] + 150) < L && Math.Abs(p[1]) < L) || (Math.Abs(p[0] + 160) < L && Math.Abs(p[1] - 10) < L)));
    }

    [Fact]
    public async Task Mirror_about_a_slanted_centerline_reverses_an_arc_and_copies_a_point()
    {
        // About y = x, (x, y) → (y, x): the arc centered (30, 0) from 0° to 90° (r 5) becomes the arc centered (0, 30)
        // from (0, 35) to (5, 30); the point (20, 5) lands at (5, 20).
        var (doc, _, added) = await SketchAsync("mirror_arc", "front",
            Centerline([0, 0], [40, 40]), new SketchEntity { Kind = "arc", Center = [30, 0], RadiusMm = 5, StartDeg = 0, EndDeg = 90 }, Point([20, 5]));
        string axis = Name(added, 0), arc = Name(added, 1);
        var r = await Ed.MirrorAsync(doc, [Seg(arc), At(20, 5)], Seg(axis));
        var copy = Assert.Single(r.Created!, c => c.Kind == "arc");
        Near([0, 30], copy.CenterMm);
        Assert.Equal(5, copy.RadiusMm!.Value, L);
        Assert.True(Joins(copy, [0, 35], [5, 30]), $"arc copy from [{string.Join(", ", copy.StartMm!)}] to [{string.Join(", ", copy.EndMm!)}]");
        Assert.Equal(Math.PI * 5 / 2, copy.LengthMm!.Value, 1e-3);
        await Ed.DeleteAsync(doc, [At(5, 20)]); // the point's copy is where it belongs
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
    public async Task Convert_of_a_face_gives_its_closed_outline()
    {
        // The top face of a 100 × 60 block: four lines, 320 mm in all, one closed loop.
        string doc = await NewPartAsync("convert_face");
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [Rect(100, 60)]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await Sk.StartAsync(doc, new EntityRef { At = [0, 20, 0] }, false);

        var r = await Ed.ConvertAsync(doc, [new EntityRef { Type = "face", At = [0, 20, 0] }]);
        Assert.Equal(4, r.Created!.Count(c => c.Kind == "line"));
        Assert.Equal(320, r.Created!.Sum(c => c.LengthMm!.Value), L);
        Assert.Equal("closed", r.Contours.Status);
        Assert.Equal(1, r.Contours.ClosedContours);
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
    public async Task Construction_relation_and_delete_work_in_a_3D_sketch()
    {
        // 3D sketch: points are model [x, y, z] mm, and so is the geometry read back.
        string doc = await NewPartAsync("sketch3d");
        await Sk.StartAsync(doc, null, true);
        var added = await Sk.AddAsync(doc, [Line([0, 0, 0], [30, 40, 50]), Line([10, 0, 0], [10, 25, 0])]);
        string slanted = Name(added, 0), upright = Name(added, 1);
        var info = await Ed.InfoAsync(doc, null);
        Assert.True(info.ThreeD);
        var s = Assert.Single(info.Segments, x => x.Name == slanted);
        Near([30, 40, 50], new[] { s.StartMm!, s.EndMm! }.Single(p => p[2] > 1));
        Assert.Equal(Math.Sqrt(30 * 30 + 40 * 40 + 50 * 50), s.LengthMm!.Value, L);

        var on = await Ed.ConstructionSetAsync(doc, [Seg(slanted)], true);
        Assert.Contains(on.Changed!, c => c.Name == slanted && c.Construction == true);
        var fix = await Ed.RelationAddAsync(doc, "fix", [Seg(upright)]);
        Assert.Equal(fix.RelationsBefore + 1, fix.RelationsAfter);

        await Ed.DeleteAsync(doc, [Seg(slanted)]);
        Assert.Equal(upright, Assert.Single((await Ed.InfoAsync(doc, null)).Segments).Name);
        await Fails(ErrorCodes.NotSupported, () => Ed.OffsetAsync(doc, [Seg(upright)], 5, "left")); // a 2D-only tool

        var fin = await Sk.FinishAsync(doc);
        var again = await Ed.EditAsync(doc, fin.Sketch); // a 3D sketch re-opens too
        Assert.True(again.ThreeD);
        Assert.Null(again.Frame);
        Assert.Equal(1, again.Segments);
        Assert.Equal(fin.Sketch, (await Sk.FinishAsync(doc)).Sketch);
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
