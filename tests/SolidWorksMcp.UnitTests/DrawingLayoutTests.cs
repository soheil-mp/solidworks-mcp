using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>The pure drawing logic: scales, papers and sheet formats, view orientations and projection, and layout.</summary>
public class DrawingLayoutTests
{
    private static SwException Invalid(Action a)
    {
        var ex = Assert.Throws<SwException>(a);
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        return ex;
    }

    // ------------------------------------------------------------------ scales

    [Theory]
    [InlineData("1:2", 1, 2)]
    [InlineData("2:1", 2, 1)]
    [InlineData(" 1 : 5 ", 1, 5)]
    [InlineData("1/10", 1, 10)]
    [InlineData("0.5", 1, 2)]
    [InlineData("2.5", 2.5, 1)]
    [InlineData("1:2.5", 1, 2.5)]
    public void Scales_parse_as_ratios(string text, double num, double den)
    {
        var (n, d) = DrawingLayout.ParseScale(text, "scale");
        Assert.Equal(num, n, 9);
        Assert.Equal(den, d, 9);
    }

    [Theory]
    [InlineData("big")]
    [InlineData("")]
    [InlineData("0:1")]
    [InlineData("1:0")]
    [InlineData("1:100000")]
    [InlineData("-1:2")]
    public void Bad_scales_are_invalid_arguments(string text) => Invalid(() => DrawingLayout.ParseScale(text, "scale"));

    [Fact]
    public void Scales_format_as_ratios_and_standard_scales_round_down()
    {
        Assert.Equal("1:2", DrawingLayout.FormatScale(0.5));
        Assert.Equal("2:1", DrawingLayout.FormatScale(2));
        Assert.Equal("1:1", DrawingLayout.FormatScale(1));
        Assert.Equal("1:2.5", DrawingLayout.FormatScale(1, 2.5));
        Assert.Equal(0.5, DrawingLayout.StandardScaleAtMost(0.7));
        Assert.Equal(1, DrawingLayout.StandardScaleAtMost(1));
        Assert.Equal(0.001, DrawingLayout.StandardScaleAtMost(1e-6)); // the smallest one
    }

    // ------------------------------------------------------------------ papers and formats

    [Fact]
    public void Papers_map_to_the_solidworks_sizes()
    {
        var a3 = DrawingLayout.Paper("a3", null, null, null);
        Assert.Equal((8, 420.0, 297.0, "landscape"), (a3.Paper, a3.WidthMm, a3.HeightMm, a3.Orientation));
        var a4p = DrawingLayout.Paper("A4", "portrait", null, null);
        Assert.Equal((7, 210.0, 297.0, "portrait"), (a4p.Paper, a4p.WidthMm, a4p.HeightMm, a4p.Orientation));
        Assert.Equal(6, DrawingLayout.Paper("a4", "landscape", null, null).Paper);
        Assert.Equal(11, DrawingLayout.Paper("a0", null, null, null).Paper);
        Assert.Equal(1, DrawingLayout.Paper("a", "portrait", null, null).Paper);
        var user = DrawingLayout.Paper("user", "portrait", 500, 300);
        Assert.Equal((12, 300.0, 500.0), (user.Paper, user.WidthMm, user.HeightMm));
        Assert.Equal((500.0, 300.0), (DrawingLayout.Paper("user", null, 500, 300).WidthMm, DrawingLayout.Paper("user", null, 500, 300).HeightMm));
    }

    [Fact]
    public void Impossible_papers_are_invalid_arguments()
    {
        Invalid(() => DrawingLayout.Paper("a3", "portrait", null, null));
        Invalid(() => DrawingLayout.Paper("a7", null, null, null));
        Invalid(() => DrawingLayout.Paper("a3", "sideways", null, null));
        Invalid(() => DrawingLayout.Paper("a3", null, 100, 100));
        Invalid(() => DrawingLayout.Paper("user", null, 500, null));
        Invalid(() => DrawingLayout.Paper("user", null, 10, 300));
    }

    [Fact]
    public void Current_paper_and_size_names_round_trip()
    {
        Assert.Equal("a3", DrawingLayout.SizeName(8));
        Assert.Equal("a4", DrawingLayout.SizeName(7));
        Assert.Equal("user", DrawingLayout.SizeName(12));
        Assert.Equal("user", DrawingLayout.SizeName(99));
        var p = DrawingLayout.CurrentPaper(7, 210, 297);
        Assert.Equal(("a4", 7, true), (p.Size, p.Paper, p.Portrait));
        var u = DrawingLayout.CurrentPaper(12, 500, 300);
        Assert.Equal(("user", 500.0, 300.0), (u.Size, u.WidthMm, u.HeightMm));
        Assert.Equal(("a3", 8), (DrawingLayout.CurrentPaper(8, 420, 297).Size, DrawingLayout.CurrentPaper(8, 420, 297).Paper));
    }

    [Fact]
    public void Sheet_format_files_follow_the_installed_names()
    {
        var (iso, n1) = DrawingLayout.FormatFiles(DrawingLayout.Paper("a3", null, null, null), "iso");
        Assert.Equal(["a3 - iso.slddrt"], iso);
        Assert.Null(n1);
        Assert.Equal(["a4 - iso.slddrt"], DrawingLayout.FormatFiles(DrawingLayout.Paper("a4", "portrait", null, null), "iso").Files);
        var (a4l, note) = DrawingLayout.FormatFiles(DrawingLayout.Paper("a4", "landscape", null, null), "iso");
        Assert.Equal(["a4 - landscape.slddrt"], a4l); // there is no landscape A4 ISO format
        Assert.NotNull(note);
        Assert.Equal(["a - landscape.slddrt"], DrawingLayout.FormatFiles(DrawingLayout.Paper("a", null, null, null), "ansi").Files);
        Assert.Equal(["a3 - landscape.slddrt"], DrawingLayout.FormatFiles(DrawingLayout.Paper("a3", null, null, null), "ansi").Files);
        Assert.Empty(DrawingLayout.FormatFiles(DrawingLayout.Paper("a3", null, null, null), "none").Files);
        var (user, un) = DrawingLayout.FormatFiles(DrawingLayout.Paper("user", null, 500, 300), "iso");
        Assert.Empty(user);
        Assert.NotNull(un);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\lang\english\sheetformat\a3 - iso.slddrt", "iso")]
    [InlineData("a4 - iso.slddrt", "iso")]
    [InlineData("a - landscape.slddrt", "ansi")]
    [InlineData("A4 - Portrait.SLDDRT", "ansi")]
    [InlineData("a3 - din.slddrt", "file")]
    [InlineData(@"D:\formats\company.slddrt", "file")]
    [InlineData("", "none")]
    [InlineData(null, "none")]
    public void Format_families_come_from_the_file_names(string? path, string family) =>
        Assert.Equal(family, DrawingLayout.FormatFamily(path));

    // ------------------------------------------------------------------ drw_sheet_setup planning

    private static readonly SheetState A3Iso = new(8, 420, 297, 1, 1, true, @"C:\f\a3 - iso.slddrt");

    [Fact]
    public void Sheet_plans_keep_what_is_left_out()
    {
        var keep = DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, null, null, null);
        Assert.Equal(("a3", 8, 1.0, 1.0, true, "iso"), (keep.Paper.Size, keep.Paper.Paper, keep.Num, keep.Den, keep.FirstAngle, keep.Family));

        var a4 = DrawingLayout.PlanSheet(A3Iso, "a4", "portrait", null, null, "1:2", null, null, null);
        Assert.Equal((7, 1.0, 2.0, true, "iso"), (a4.Paper.Paper, a4.Num, a4.Den, a4.FirstAngle, a4.Family));

        var scaleOnly = DrawingLayout.PlanSheet(A3Iso, null, null, null, null, "2:1", null, null, null);
        Assert.Equal((8, 2.0, 1.0, "iso"), (scaleOnly.Paper.Paper, scaleOnly.Num, scaleOnly.Den, scaleOnly.Family));
    }

    [Fact]
    public void Sheet_plans_keep_the_orientation_where_the_new_size_has_it()
    {
        var a4Portrait = A3Iso with { Paper = 7, WidthMm = 210, HeightMm = 297 };
        Assert.Equal(8, DrawingLayout.PlanSheet(a4Portrait, "a3", null, null, null, null, null, null, null).Paper.Paper);   // no A3 portrait
        Assert.Equal(1, DrawingLayout.PlanSheet(a4Portrait, "a", null, null, null, null, null, null, null).Paper.Paper);    // A portrait
        Assert.Equal(6, DrawingLayout.PlanSheet(a4Portrait, null, "landscape", null, null, null, null, null, null).Paper.Paper);
        var user = A3Iso with { Paper = 12, WidthMm = 500, HeightMm = 300, FormatPath = null };
        var turned = DrawingLayout.PlanSheet(user, null, "portrait", null, null, null, null, null, null);
        Assert.Equal((12, 300.0, 500.0, "none"), (turned.Paper.Paper, turned.Paper.WidthMm, turned.Paper.HeightMm, turned.Family));
        var sized = DrawingLayout.PlanSheet(A3Iso, null, null, 600, 400, null, null, null, null);
        Assert.Equal((12, 600.0, 400.0), (sized.Paper.Paper, sized.Paper.WidthMm, sized.Paper.HeightMm));
    }

    [Fact]
    public void Sheet_plans_take_the_projection_of_a_new_format_family()
    {
        Assert.False(DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, null, "ansi", null).FirstAngle);
        var thirdIso = A3Iso with { FirstAngle = false };
        Assert.True(DrawingLayout.PlanSheet(thirdIso, null, null, null, null, null, null, "iso", null).FirstAngle);
        Assert.False(DrawingLayout.PlanSheet(thirdIso, null, null, null, null, null, null, "none", null).FirstAngle); // kept
        var explicitThird = DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, "third_angle", "iso", null);
        Assert.False(explicitThird.FirstAngle);
        var custom = A3Iso with { FormatPath = @"D:\f\company.slddrt" };
        var keptFile = DrawingLayout.PlanSheet(custom, "a2", null, null, null, null, null, null, null);
        Assert.Equal(("file", @"D:\f\company.slddrt"), (keptFile.Family, keptFile.FormatFile));
        var newFile = DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, null, null, @"D:\f\other.slddrt");
        Assert.Equal(("file", @"D:\f\other.slddrt", true), (newFile.Family, newFile.FormatFile, newFile.FirstAngle));
        Invalid(() => DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, null, "din", null));
        Invalid(() => DrawingLayout.PlanSheet(A3Iso, null, null, null, null, null, "second", null, null));
        Invalid(() => DrawingLayout.PlanSheet(A3Iso, null, "portrait", null, null, null, null, null, null)); // A3 has no portrait
    }

    // ------------------------------------------------------------------ orientations and projection

    /// <summary>A model-to-sheet ArrayData for view axes (row-vector convention: column 0 is the model direction along sheet x).</summary>
    private static double[] Xf(ViewAxes a, double scale = 1) =>
        [a.SheetX.X, a.SheetY.X, a.Toward.X, a.SheetX.Y, a.SheetY.Y, a.Toward.Y, a.SheetX.Z, a.SheetY.Z, a.Toward.Z, 0.1, 0.2, 0, scale, 0, 0, 0];

    [Fact]
    public void Named_views_are_right_handed_and_classify_back()
    {
        foreach (var (name, (api, axes)) in DrawingLayout.NamedViews)
        {
            Assert.StartsWith("*", api);
            Assert.True(axes.SheetX.Cross(axes.SheetY).DistanceTo(axes.Toward) < 1e-12, $"{name}: x × y is not the viewing direction");
            Assert.Equal(1, axes.Toward.Length, 12);
            Assert.Equal(name, DrawingLayout.Classify(DrawingLayout.AxesOf(Xf(axes, 0.5))));
        }
        // A view rotated in its plane still looks the same way.
        var front = DrawingLayout.NamedViews["front"].Axes;
        Assert.Equal("front", DrawingLayout.Classify(new ViewAxes(front.Toward, front.SheetY, -front.SheetX)));
        Assert.Null(DrawingLayout.Classify(new ViewAxes(new Vec3(1, 1, 0).Normalized(), Vec3.UnitZ, Vec3.UnitX)));
    }

    [Theory]
    [InlineData("right", false, "right")]
    [InlineData("right", true, "left")]
    [InlineData("left", false, "left")]
    [InlineData("left", true, "right")]
    [InlineData("up", false, "top")]
    [InlineData("up", true, "bottom")]
    [InlineData("down", false, "bottom")]
    [InlineData("down", true, "top")]
    public void Projected_views_of_the_front_view_follow_the_projection(string side, bool firstAngle, string shows)
    {
        var toward = DrawingLayout.ProjectedToward(DrawingLayout.NamedViews["front"].Axes, side, firstAngle);
        Assert.True(toward.DistanceTo(DrawingLayout.NamedViews[shows].Axes.Toward) < 1e-12, $"{side} ({(firstAngle ? "first" : "third")} angle) shows {toward}");
    }

    [Fact]
    public void Extent_is_the_box_size_along_a_direction()
    {
        Assert.Equal(20, DrawingLayout.Extent(Vec3.UnitZ, 100, 60, 20), 12);
        Assert.Equal((100 + 20) / Math.Sqrt(2), DrawingLayout.Extent(DrawingLayout.NamedViews["iso"].Axes.SheetX, 100, 60, 20), 9);
    }

    // ------------------------------------------------------------------ standard views

    [Fact]
    public void Cells_put_the_views_where_each_projection_wants_them()
    {
        var used = new List<(int, int)>();
        foreach (var v in new[] { "front", "top", "right" }) used.Add(DrawingLayout.Cell(v, false, used));
        Assert.Equal([(0, 0), (0, 1), (1, 0)], used);
        Assert.Equal((1, 1), DrawingLayout.Cell("iso", false, used)); // the empty quadrant: top right

        var first = new List<(int, int)>();
        foreach (var v in new[] { "front", "top", "right" }) first.Add(DrawingLayout.Cell(v, true, first));
        Assert.Equal([(0, 0), (0, -1), (-1, 0)], first);
        Assert.Equal((-1, -1), DrawingLayout.Cell("iso", true, first)); // the empty quadrant: bottom left

        Assert.Equal((1, 1), DrawingLayout.Cell("iso", true, [(0, 0)])); // front only: the first free corner
        Assert.Equal((2, 0), DrawingLayout.Cell("back", false, []));
        Assert.Equal((0, -1), DrawingLayout.Cell("bottom", false, []));
        Assert.Equal((1, 0), DrawingLayout.Cell("left", true, []));
    }

    private static readonly SheetRect A3Usable = DrawingLayout.Usable(420, 297, true);

    private static List<SheetRect> Rects(StandardLayout l) =>
        l.Slots.Select(s => SheetRect.Around(s.Cx, s.Cy, s.W1 * l.Scale, s.H1 * l.Scale)).ToList();

    [Fact]
    public void The_block_fits_an_a3_sheet_at_1_1_without_overlaps()
    {
        var l = DrawingLayout.LayoutStandard(["front", "top", "right", "iso"], true, 100, 60, 20, A3Usable, 1, null);
        Assert.Equal(1, l.Scale);
        Assert.True(l.Fits);
        var r = Rects(l);
        for (int i = 0; i < r.Count; i++)
        {
            Assert.True(r[i].Inside(A3Usable), $"{l.Slots[i].View} leaves the usable area");
            for (int j = i + 1; j < r.Count; j++)
                Assert.False(r[i].Overlaps(r[j], DrawingLayout.Gap - 1e-6), $"{l.Slots[i].View} and {l.Slots[j].View} are closer than the gap");
        }
        var front = l.Slots.Single(s => s.View == "front");
        var top = l.Slots.Single(s => s.View == "top");
        var right = l.Slots.Single(s => s.View == "right");
        var iso = l.Slots.Single(s => s.View == "iso");
        Assert.Equal(["front", "top", "right", "iso"], l.Slots.Select(s => s.View)); // the requested order
        Assert.True(top.Cy < front.Cy && Math.Abs(top.Cx - front.Cx) < 1e-9);       // first angle: top below, aligned
        Assert.True(right.Cx < front.Cx && Math.Abs(right.Cy - front.Cy) < 1e-9);   // right view on the left, aligned
        Assert.True(iso.Cx < front.Cx && iso.Cy < front.Cy);                         // the free quadrant
        Assert.Equal((100.0, 60.0), (front.W1, front.H1));
        Assert.Equal((20.0, 60.0), (right.W1, right.H1));
        Assert.Equal((100.0, 20.0), (top.W1, top.H1));
        // Centred in the usable area.
        var b = l.Bounds;
        Assert.Equal(A3Usable.Cx, b.Cx, 6);
    }

    [Fact]
    public void Big_models_get_the_largest_standard_scale_that_fits()
    {
        var l = DrawingLayout.LayoutStandard(["front", "top", "right", "iso"], false, 1000, 600, 200, A3Usable, 1, null);
        Assert.True(l.Scale < 1);
        Assert.Contains(l.Scale, DrawingLayout.StandardScales);
        Assert.True(l.Scale <= l.FitScale + 1e-12);
        Assert.True(l.Fits);
        Assert.All(Rects(l), r => Assert.True(r.Inside(A3Usable)));

        var forced = DrawingLayout.LayoutStandard(["front"], false, 1000, 600, 200, A3Usable, 1, 1.0);
        Assert.Equal(1, forced.Scale);
        Assert.False(forced.Fits);
        var keepSheet = DrawingLayout.LayoutStandard(["front"], false, 10, 6, 2, A3Usable, 2, null);
        Assert.Equal(2, keepSheet.Scale); // the sheet scale when it fits (no enlarging beyond it)
    }

    [Fact]
    public void Layouts_move_away_from_views_already_on_the_sheet()
    {
        var l = DrawingLayout.LayoutStandard(["front"], true, 100, 60, 20, A3Usable, 1, null);
        var (same, free0) = DrawingLayout.PlaceGroup(l, A3Usable, []);
        Assert.True(free0);
        Assert.Equal(l.Bounds, same.Bounds);

        var blocker = SheetRect.Around(l.Bounds.Cx, l.Bounds.Cy, 120, 80);
        var (moved, free) = DrawingLayout.PlaceGroup(l, A3Usable, [blocker]);
        Assert.True(free);
        Assert.False(moved.Bounds.Overlaps(blocker));
        Assert.True(moved.Bounds.Inside(A3Usable));

        var (stuck, none) = DrawingLayout.PlaceGroup(l, A3Usable, [A3Usable]);
        Assert.False(none);
        Assert.Equal(l.Bounds, stuck.Bounds);
    }

    [Fact]
    public void Usable_area_leaves_margins_and_the_title_block()
    {
        var u = DrawingLayout.Usable(420, 297, true);
        Assert.Equal((20.0, 297 - 10.0, 420 - 10.0), (u.X0, u.Y1, u.X1));
        Assert.True(u.Y0 >= 50);
        var bare = DrawingLayout.Usable(420, 297, false);
        Assert.Equal((10.0, 10.0, 410.0, 287.0), (bare.X0, bare.Y0, bare.X1, bare.Y1));
    }

    // ------------------------------------------------------------------ placing one view

    [Fact]
    public void Side_spots_are_aligned_and_skip_occupied_space()
    {
        var parent = new SheetRect(100, 100, 200, 160);
        var area = new SheetRect(0, 0, 420, 297);
        var (x, y, free) = DrawingLayout.SideSpot(parent, "right", 50, 30, [], area);
        Assert.True(free);
        Assert.Equal((200 + DrawingLayout.Gap + 25, 130.0), (x, y));

        var occupied = new SheetRect(230, 100, 280, 160);
        var (x2, y2, free2) = DrawingLayout.SideSpot(parent, "right", 50, 30, [occupied], area);
        Assert.True(free2);
        Assert.Equal(130, y2);
        Assert.False(SheetRect.Around(x2, y2, 50, 30).Overlaps(occupied, 5));
        Assert.True(x2 - 25 >= 285 - 1e-9);

        var (ux, uy, ufree) = DrawingLayout.SideSpot(parent, "up", 50, 30, [], area);
        Assert.True(ufree);
        Assert.Equal((150.0, 160 + DrawingLayout.Gap + 15), (ux, uy));

        var (_, _, cramped) = DrawingLayout.SideSpot(parent, "left", 150, 30, [], area); // no room left of x = 100
        Assert.False(cramped);
    }

    [Fact]
    public void Free_spots_fall_back_to_the_nearest_free_place()
    {
        var area = new SheetRect(0, 0, 200, 100);
        var (x, y, free) = DrawingLayout.FindSpot(area, [], 20, 20, [(50, 50)]);
        Assert.Equal((50.0, 50.0, true), (x, y, free));
        var block = new SheetRect(30, 30, 70, 70);
        var (x2, y2, free2) = DrawingLayout.FindSpot(area, [block], 20, 20, [(50, 50)]);
        Assert.True(free2);
        Assert.False(SheetRect.Around(x2, y2, 20, 20).Overlaps(block, 5));
        var (_, _, none) = DrawingLayout.FindSpot(area, [area], 20, 20, [(50, 50)]);
        Assert.False(none);
    }

    [Fact]
    public void Sheet_rects_read_outlines_in_meters()
    {
        var o = SheetRect.FromMeters([0.1, 0.3, 0.05, 0.2])!.Value;
        Assert.Equal((50.0, 200.0, 100.0, 300.0), (o.X0, o.Y0, o.X1, o.Y1));
        Assert.Null(SheetRect.FromMeters([0.1, 0.2]));
        Assert.Null(SheetRect.FromMeters([0.1, double.NaN, 0.2, 0.3]));
        Assert.Equal([50.0, 200.0, 100.0, 300.0], o.ToArray());
        Assert.True(o.Inflate(1).Contains(49.5, 199.5));
    }

    // ------------------------------------------------------------------ labels and dimension text

    [Fact]
    public void Labels_skip_used_letters_and_i_o_q()
    {
        Assert.Equal("A", DrawingLayout.NextLabel([]));
        Assert.Equal("A", DrawingLayout.NextLabel(["Drawing View1", "Drawing View2"]));
        Assert.Equal("B", DrawingLayout.NextLabel(["Section View A-A"]));
        Assert.Equal("C", DrawingLayout.NextLabel(["Section View A-A", "Detail View B (2 : 1)"]));
        Assert.Equal("J", DrawingLayout.NextLabel(["A-A", "B-B", "C-C", "D-D", "E-E", "F-F", "G-G", "H-H"]));
    }

    [Fact]
    public void Dimension_text_goes_outside_the_view()
    {
        var view = new SheetRect(100, 100, 200, 160);
        Assert.Equal((150.0, 170.0), DrawingLayout.DimensionText("horizontal", [(150, 160)], view));
        Assert.Equal((150.0, 90.0), DrawingLayout.DimensionText("horizontal", [(150, 100)], view));
        Assert.Equal((210.0, 130.0), DrawingLayout.DimensionText("vertical", [(200, 130)], view));
        var (x, y) = DrawingLayout.DimensionText("linear", [(150, 160)], view);
        Assert.Equal(150, x, 9);
        Assert.Equal(170, y, 9);
    }
}
