using System.Globalization;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// The drawing tools (doc_new type=drawing, drw_*) against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1). Oracle:
/// a 100 (x) × 60 (y) × 20 (z) block on the front plane, centred on the origin in x and y (z from 0 to 20), whose width
/// and height are driving sketch dimensions, optionally with a Ø20 hole through its centre, saved under a test folder
/// (views need a saved model). Its front view is 100:60 at the view scale, the top view 100:20, the right view 20:60.
/// Every tool has a positive test and negative ones (typed errors, nothing left behind). Drawings are closed before the
/// parts they show.
/// </summary>
public class DrawingLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double L = 1e-3; // mm: sheet positions and model lengths read back
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_drawing_{Guid.NewGuid():N}");
    private readonly List<string> _drawings = [];

    private DrawingService Drw => new(S);
    private DocumentService Docs => new(S);
    private SketchService Sk => new(S);
    private SketchEditService Ed => new(S);
    private FeatureService Fs => new(S);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(_dir);
    }

    public override async ValueTask DisposeAsync()
    {
        if (LiveFixture.Enabled)
        {
            // Drawings first: a part a drawing still shows stays loaded behind it.
            foreach (var h in _drawings)
            {
                try { await Docs.CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed by the test */ }
            }
        }
        try { await base.DisposeAsync(); }
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>The oracle block (see the class summary), saved as &lt;name&gt;.sldprt in the test folder: (handle, file).</summary>
    private async Task<(string Doc, string Path)> BlockAsync(string name, bool hole = false, bool dimensions = true)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        var add = await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        if (dimensions)
        {
            var sides = add.Entities[0].Sides!;
            await Ed.DimensionAddAsync(doc, [new EntityRef { Name = sides["bottom"] }], "linear", valueMm: 100);
            await Ed.DimensionAddAsync(doc, [new EntityRef { Name = sides["left"] }], "vertical", valueMm: 60);
        }
        var r = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, 1e-3);
        if (hole)
        {
            await Sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
            await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 20 }]);
            var cut = await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
            Assert.Equal(-Math.PI * 100 * 20, cut.Verified.VolumeDeltaMm3, 2e-2);
        }
        var saved = await Docs.SaveAsync(doc, "as", Path.Combine(_dir, name + ".sldprt"), false);
        Assert.True(File.Exists(saved.SavedTo), $"{saved.SavedTo} was not written");
        return (doc, saved.SavedTo!);
    }

    /// <summary>A new owned drawing (closed with discard after the test, before the parts).</summary>
    private async Task<string> DrawingAsync(string name)
    {
        var r = await Docs.NewAsync("drawing", "mmgs", null, $"{name}_{Guid.NewGuid().ToString("N")[..6]}");
        _drawings.Add(r.Document.Handle);
        Assert.Equal("drawing", r.Document.Type);
        return r.Document.Handle;
    }

    /// <summary>A drawing of the block with the given standard views (default scale).</summary>
    private async Task<(string Drawing, DrwViewsResult Views)> ViewsAsync(string name, string[] views, bool hole = false, bool dimensions = true)
    {
        var (_, path) = await BlockAsync(name, hole, dimensions);
        string drw = await DrawingAsync(name);
        var r = await Drw.ViewsStandardAsync(path, views, null, drw);
        Assert.Equal(views.Length, r.Views.Count);
        return (drw, r);
    }

    private static double ScaleOf(string scale)
    {
        var p = scale.Split(':');
        return double.Parse(p[0], CultureInfo.InvariantCulture) / double.Parse(p[1], CultureInfo.InvariantCulture);
    }

    /// <summary>The model box of a view on the sheet is exactly the block's projection at the view scale.</summary>
    private static void AssertBoxSize(DrwView v, double w, double h)
    {
        double s = ScaleOf(v.Scale);
        var b = v.ModelBoxMm!;
        Assert.Equal(w * s, b[2] - b[0], L);
        Assert.Equal(h * s, b[3] - b[1], L);
    }

    private static bool Overlap(double[] a, double[] b) => a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

    private async Task<int> ViewCountAsync(string drw) => (await Drw.InfoAsync(drw)).ViewCount;

    private static async Task<SwException> Fails(string code, Func<Task> call)
    {
        var ex = await Assert.ThrowsAsync<SwException>(call);
        Assert.True(ex.Code == code, $"expected {code}, got {ex.ToAgentText()}");
        return ex;
    }

    // ------------------------------------------------------------------ doc_new type=drawing, drw_sheet_setup

    [Fact]
    public async Task New_drawing_is_A3_landscape_iso_first_angle_in_mm()
    {
        var r = await Docs.NewAsync("drawing", "mmgs", null, $"drw_new_{Guid.NewGuid().ToString("N")[..6]}");
        _drawings.Add(r.Document.Handle);
        Assert.Equal("drawing", r.Document.Type);
        Assert.Equal("MMGS", r.Document.Units);
        Assert.Contains("A3", r.Message);
        var info = await Drw.InfoAsync(r.Document.Handle);
        var s = Assert.Single(info.Sheets);
        Assert.Equal("a3", s.Size);
        Assert.Equal(420, s.WidthMm, 0.5);
        Assert.Equal(297, s.HeightMm, 0.5);
        Assert.Equal("landscape", s.Orientation);
        Assert.Equal("first_angle", s.Projection);
        Assert.Equal("1:1", s.Scale);
        Assert.Equal("a3 - iso.slddrt", s.Format, ignoreCase: true);
        Assert.Empty(s.Views!);
    }

    [Fact]
    public async Task Sheet_setup_changes_what_is_given_and_keeps_the_rest()
    {
        string drw = await DrawingAsync("drw_sheet");
        var a4 = await Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Size = "a4", Orientation = "portrait", Scale = "1:2" });
        Assert.Equal("a4", a4.Sheet.Size);
        Assert.Equal(210, a4.Sheet.WidthMm, 0.5);
        Assert.Equal(297, a4.Sheet.HeightMm, 0.5);
        Assert.Equal("portrait", a4.Sheet.Orientation);
        Assert.Equal("1:2", a4.Sheet.Scale);
        Assert.Equal("first_angle", a4.Sheet.Projection);               // kept
        Assert.Equal("a4 - iso.slddrt", a4.Sheet.Format, ignoreCase: true); // the ISO family, now for A4

        var ansi = await Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Format = "ansi" });
        Assert.Equal("third_angle", ansi.Sheet.Projection);             // the ANSI family's projection
        Assert.Equal("a4 - portrait.slddrt", ansi.Sheet.Format, ignoreCase: true);
        Assert.Equal("1:2", ansi.Sheet.Scale);                          // kept

        var user = await Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Size = "user", WidthMm = 500, HeightMm = 300, Format = "none" });
        Assert.Equal("user", user.Sheet.Size);
        Assert.Equal(500, user.Sheet.WidthMm, 0.5);
        Assert.Equal(300, user.Sheet.HeightMm, 0.5);
        Assert.Null(user.Sheet.Format);
        var info = await Drw.InfoAsync(drw);
        Assert.Equal(500, info.Sheets[0].WidthMm, 0.5);
        Assert.Equal("third_angle", info.Sheets[0].Projection);
    }

    [Fact]
    public async Task Sheet_setup_rejects_bad_settings_and_parts()
    {
        string drw = await DrawingAsync("drw_sheet_bad");
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Size = "a3", Orientation = "portrait" }));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Size = "a7" }));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Scale = "big" }));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Format = "din" }));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Projection = "second_angle" }));
        await Fails(ErrorCodes.IoError, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = drw, Format = Path.Combine(_dir, "missing.slddrt") }));
        var s = (await Drw.InfoAsync(drw)).Sheets[0]; // untouched
        Assert.Equal("a3", s.Size);
        Assert.Equal("1:1", s.Scale);

        string part = await NewPartAsync("drw_sheet_part");
        await Fails(ErrorCodes.WrongDocType, () => Drw.SheetSetupAsync(new DrwSheetRequest { Doc = part, Size = "a4" }));
        await Fails(ErrorCodes.WrongDocType, () => Drw.InfoAsync(part));
        await Fails(ErrorCodes.WrongDocType, () => Drw.NoteAddAsync("x", [10, 10], null, null, part));
    }

    // ------------------------------------------------------------------ drw_views_standard (+ PDF export)

    [Fact]
    public async Task Standard_views_have_the_block_proportions_without_overlaps_and_export_to_pdf()
    {
        var (_, path) = await BlockAsync("drw_views");
        string drw = await DrawingAsync("drw_views");
        var r = await Drw.ViewsStandardAsync(path, null, null, drw); // front, top, right, iso
        Assert.Equal(4, r.Views.Count);
        Assert.Equal(path, r.Model, ignoreCase: true);
        var front = Assert.Single(r.Views, v => v.Orientation == "front");
        var top = Assert.Single(r.Views, v => v.Orientation == "top");
        var right = Assert.Single(r.Views, v => v.Orientation == "right");
        Assert.Single(r.Views, v => v.Orientation == "iso");
        AssertBoxSize(front, 100, 60);
        AssertBoxSize(top, 100, 20);
        AssertBoxSize(right, 20, 60);

        // The front view's outline holds its model box; SOLIDWORKS may add a margin, so its proportions are 100:60 up to that.
        var fb = front.BoundsMm!;
        var mb = front.ModelBoxMm!;
        Assert.True(fb[0] <= mb[0] + L && fb[1] <= mb[1] + L && fb[2] >= mb[2] - L && fb[3] >= mb[3] - L,
            $"outline [{string.Join(", ", fb)}] does not hold the model box [{string.Join(", ", mb)}]");
        double ratio = (fb[2] - fb[0]) / (fb[3] - fb[1]);
        Assert.InRange(ratio, 100.0 / 60 * 0.9, 100.0 / 60 * 1.1);

        // First angle (the new sheet's projection): the top view below the front view, the right view on its left.
        Assert.True(top.CenterMm![1] < front.CenterMm![1], "the top view is not below the front view");
        Assert.True(right.CenterMm![0] < front.CenterMm![0], "the right view is not left of the front view");
        Assert.Equal(front.CenterMm[0], top.CenterMm[0], 0.5);
        Assert.Equal(front.CenterMm[1], right.CenterMm[1], 0.5);

        // No two views overlap, and all are on the sheet.
        for (int i = 0; i < r.Views.Count; i++)
        {
            var b = r.Views[i].BoundsMm!;
            Assert.True(b[0] >= 0 && b[1] >= 0 && b[2] <= 420 && b[3] <= 297, $"{r.Views[i].Name} leaves the sheet");
            for (int j = i + 1; j < r.Views.Count; j++)
                Assert.False(Overlap(b, r.Views[j].BoundsMm!), $"{r.Views[i].Name} overlaps {r.Views[j].Name}");
        }

        // drw_info lists every view created, with its model.
        var info = await Drw.InfoAsync(drw);
        Assert.Equal(r.Views.Select(v => v.Name).OrderBy(n => n), info.Sheets[0].Views!.Select(v => v.Name).OrderBy(n => n));
        Assert.All(info.Sheets[0].Views!, v => Assert.Equal(path, v.Model, ignoreCase: true));

        // The drawing exports to PDF through model_export.
        var pdf = await new ExportService(S).ExportAsync(new ExportRequest { Doc = drw, Path = Path.Combine(_dir, "views.pdf") });
        Assert.Equal("pdf", pdf.Format);
        Assert.Equal("PDF", pdf.Details.Header);
        Assert.True(new FileInfo(pdf.Path).Length > 1000);
    }

    [Fact]
    public async Task Standard_views_at_a_given_scale_set_the_empty_sheets_scale()
    {
        var (_, path) = await BlockAsync("drw_scale");
        string drw = await DrawingAsync("drw_scale");
        var r = await Drw.ViewsStandardAsync(path, ["front", "top", "right"], "1:2", drw);
        Assert.Equal("1:2", r.Scale);
        Assert.All(r.Views, v => Assert.Equal("1:2", v.Scale));
        AssertBoxSize(r.Views.Single(v => v.Orientation == "front"), 100, 60);
        Assert.Equal("1:2", (await Drw.InfoAsync(drw)).Sheets[0].Scale);
    }

    [Fact]
    public async Task Standard_views_refuse_unsaved_models_bad_names_and_drawings()
    {
        string unsaved = await NewPartAsync("drw_unsaved");
        string drw = await DrawingAsync("drw_views_bad");
        var ex = await Fails(ErrorCodes.InvalidArgument, () => Drw.ViewsStandardAsync(unsaved, null, null, drw));
        Assert.Contains("saved", ex.Message);
        await Fails(ErrorCodes.IoError, () => Drw.ViewsStandardAsync(Path.Combine(_dir, "missing.sldprt"), null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.ViewsStandardAsync(Path.Combine(_dir, "x.sldprt"), ["front", "diagonal"], null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.ViewsStandardAsync(Path.Combine(_dir, "x.sldprt"), null, "huge", drw));
        await Fails(ErrorCodes.WrongDocType, () => Drw.ViewsStandardAsync(drw, null, null, drw));
        Assert.Equal(0, await ViewCountAsync(drw));
    }

    // ------------------------------------------------------------------ drw_view_projected

    [Fact]
    public async Task Projected_views_follow_first_angle_projection()
    {
        var (drw, r) = await ViewsAsync("drw_proj", ["front"]);
        var front = r.Views[0];
        var left = await Drw.ProjectedAsync(front.Name, "left", drw);
        Assert.Equal("projected", left.View.Type);
        Assert.Equal(front.Name, left.View.Parent);
        Assert.Equal("right", left.View.Orientation); // first angle: the view on the left shows the right side
        Assert.True(left.View.CenterMm![0] < front.BoundsMm![0], "the projected view is not left of the front view");
        AssertBoxSize(left.View, 20, 60);
        var up = await Drw.ProjectedAsync(front.Name, "up", drw);
        Assert.Equal("bottom", up.View.Orientation);  // first angle: the view above shows the bottom
        Assert.True(up.View.CenterMm![1] > front.BoundsMm![3], "the projected view is not above the front view");
        AssertBoxSize(up.View, 100, 20);
        Assert.Equal(3, await ViewCountAsync(drw));
    }

    [Fact]
    public async Task Projected_view_rejects_bad_directions_and_parents()
    {
        var (drw, r) = await ViewsAsync("drw_proj_bad", ["front"]);
        await Fails(ErrorCodes.InvalidArgument, () => Drw.ProjectedAsync(r.Views[0].Name, "diagonal", drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.ProjectedAsync("No Such View", "left", drw));
        Assert.Equal(1, await ViewCountAsync(drw));
    }

    // ------------------------------------------------------------------ drw_view_section

    [Fact]
    public async Task Section_through_the_middle_is_a_hatched_section_view()
    {
        var (drw, r) = await ViewsAsync("drw_section", ["front"]);
        var front = r.Views[0];
        var b = front.ModelBoxMm!;
        double cx = (b[0] + b[2]) / 2;
        var sec = await Drw.SectionAsync(front.Name, [cx, b[1] - 10], [cx, b[3] + 10], null, null, drw);
        Assert.Equal("section", sec.View.Type);
        Assert.Equal("A", sec.Label);
        Assert.Contains("A", sec.View.Name);
        Assert.Equal(front.Name, sec.View.Parent);
        // A vertical cut in the front view lies in the model's YZ plane: the section looks along ±X and shows 20 (z) x 60 (y).
        Assert.True(sec.View.Orientation is "right" or "left", $"the section looks {sec.View.Orientation ?? "along another direction"}");
        AssertBoxSize(sec.View, 20, 60);
        Assert.Contains(sec.Checks!, c => c.Contains("hatched"));
        var info = await Drw.InfoAsync(drw);
        Assert.Contains(info.Sheets[0].Views!, v => v.Name == sec.View.Name && v.Type == "section");
    }

    [Fact]
    public async Task Section_lines_that_do_not_cross_the_part_are_refused()
    {
        var (drw, r) = await ViewsAsync("drw_section_bad", ["front"]);
        var front = r.Views[0];
        var b = front.ModelBoxMm!;
        double cx = (b[0] + b[2]) / 2, cy = (b[1] + b[3]) / 2;
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SectionAsync(front.Name, [cx, b[1] - 10], [cx, cy], null, null, drw)); // ends inside
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SectionAsync(front.Name, [b[2] + 5, b[1] - 10], [b[2] + 5, b[3] + 10], null, null, drw)); // misses
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SectionAsync(front.Name, [cx, cy], [cx, cy + 0.5], null, null, drw)); // too short
        await Fails(ErrorCodes.InvalidArgument, () => Drw.SectionAsync(front.Name, [cx, b[1] - 10], [cx, b[3] + 10], "A1", null, drw)); // bad label
        Assert.Equal(1, await ViewCountAsync(drw));
    }

    // ------------------------------------------------------------------ drw_view_detail

    [Fact]
    public async Task Detail_of_a_corner_has_the_requested_scale()
    {
        var (drw, r) = await ViewsAsync("drw_detail", ["front"]);
        var front = r.Views[0];
        var b = front.ModelBoxMm!;
        var det = await Drw.DetailAsync(front.Name, [b[2] - 5, b[3] - 5], 10, "2:1", null, null, drw);
        Assert.Equal("detail", det.View.Type);
        Assert.Equal("2:1", det.View.Scale);
        Assert.Equal("A", det.Label);
        Assert.Equal(front.Name, det.View.Parent);
        // Detail views looked empty in earlier testing: the result says whether SOLIDWORKS reports visible edges in it.
        Assert.True(det.Checks!.Any(c => c.Contains("visible edge")) || (det.Warnings ?? []).Any(w => w.Contains("no visible edges")),
            "the detail view result says nothing about its content");
        Assert.Contains((await Drw.InfoAsync(drw)).Sheets[0].Views!, v => v.Name == det.View.Name && v.Type == "detail");
    }

    [Fact]
    public async Task Detail_circles_off_the_part_or_out_of_range_are_refused()
    {
        var (drw, r) = await ViewsAsync("drw_detail_bad", ["front"]);
        var front = r.Views[0];
        var b = front.ModelBoxMm!;
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DetailAsync(front.Name, [b[2] + 100, b[3] + 100], 5, null, null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DetailAsync(front.Name, [b[0], b[1]], 0.1, null, null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DetailAsync(front.Name, [b[0], b[1]], 10, "zoom", null, null, drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.DetailAsync("No Such View", [b[0], b[1]], 10, null, null, null, drw));
        Assert.Equal(1, await ViewCountAsync(drw));
    }

    // ------------------------------------------------------------------ drw_dimensions_auto

    [Fact]
    public async Task Auto_dimensions_bring_in_the_sketch_dimensions_once()
    {
        var (drw, r) = await ViewsAsync("drw_auto", ["front", "top", "right"]);
        var first = await Drw.DimensionsAutoAsync(null, drw);
        Assert.True(first.Added >= 2, $"only {first.Added} model dimension(s) inserted (the sketch has 100 and 60)");
        Assert.Equal(first.Added, first.Views.Sum(v => v.Added));
        Assert.All(first.Views, v => Assert.Equal(v.After - v.Before, v.Added));
        var front = first.Views.Single(v => v.View == r.Views.Single(x => x.Orientation == "front").Name);
        Assert.True(front.Added >= 2, $"the front view (where 100 and 60 lie) got {front.Added}");
        var info = await Drw.InfoAsync(drw);
        Assert.Equal(first.Added, info.Sheets[0].Views!.Sum(v => v.Annotations!.Dimensions));

        var again = await Drw.DimensionsAutoAsync(null, drw); // each model dimension appears once
        Assert.Equal(0, again.Added);
        Assert.NotNull(again.Hint);
    }

    [Fact]
    public async Task Auto_dimensions_report_why_nothing_came_and_refuse_unknown_views()
    {
        var (drw, r) = await ViewsAsync("drw_auto_none", ["front"], dimensions: false);
        var none = await Drw.DimensionsAutoAsync([r.Views[0].Name], drw);
        if (none.Added == 0) Assert.NotNull(none.Hint);
        Assert.Equal(none.Added, Assert.Single(none.Views).Added);
        await Fails(ErrorCodes.EntityNotFound, () => Drw.DimensionsAutoAsync(["No Such View"], drw));
    }

    // ------------------------------------------------------------------ drw_dimension_add

    [Fact]
    public async Task Dimensions_on_model_edges_read_the_model_lengths()
    {
        var (drw, r) = await ViewsAsync("drw_dim", ["front", "top"], hole: true);
        var front = r.Views.Single(v => v.Orientation == "front");
        var top = r.Views.Single(v => v.Orientation == "top");

        var width = await Drw.DimensionAddAsync(front.Name, new EntityRef { Type = "edge", At = [0, 30, 20] }, null, "linear", null, drw);
        Assert.Equal(100, width.Value, 1e-6);
        Assert.Equal("mm", width.Unit);
        Assert.Equal(100, width.Expected!.Value, 1e-6);
        Assert.Equal(front.Name, width.View);
        Assert.Contains(width.Checks!, c => c.Contains("matches the geometry"));

        var height = await Drw.DimensionAddAsync(front.Name, new EntityRef { Type = "edge", At = [50, 0, 20] }, null, "vertical", null, drw);
        Assert.Equal(60, height.Value, 1e-6);

        var dia = await Drw.DimensionAddAsync(front.Name, new EntityRef { Type = "edge", At = [10, 0, 20] }, null, "diameter", null, drw);
        Assert.Equal(20, dia.Value, 1e-6);
        var rad = await Drw.DimensionAddAsync(front.Name, new EntityRef { Type = "edge", At = [0, 10, 20] }, null, "radial", null, drw);
        Assert.Equal(10, rad.Value, 1e-6);

        // Two parallel edges in the top view: the front and back top edges are the block's depth apart.
        var depth = await Drw.DimensionAddAsync(top.Name, new EntityRef { Type = "edge", At = [0, 30, 20] },
            new EntityRef { Type = "edge", At = [0, 30, 0] }, "linear", null, drw);
        Assert.Equal(20, depth.Value, 1e-6);
        Assert.Equal(2, depth.Resolved!.Count);

        var info = await Drw.InfoAsync(drw);
        Assert.Equal(4, info.Sheets[0].Views!.Single(v => v.Name == front.Name).Annotations!.Dimensions);
        Assert.Equal(1, info.Sheets[0].Views!.Single(v => v.Name == top.Name).Annotations!.Dimensions);
    }

    [Fact]
    public async Task Dimensions_that_cannot_be_drawn_are_refused()
    {
        var (drw, r) = await ViewsAsync("drw_dim_bad", ["front"]);
        string front = r.Views[0].Name;
        var topEdge = new EntityRef { Type = "edge", At = [0, 30, 20] };
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DimensionAddAsync(front, topEdge, null, "oblique", null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DimensionAddAsync(front, topEdge, null, "radial", null, drw)); // a line
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DimensionAddAsync(front, // an edge along z points at the viewer
            new EntityRef { Type = "edge", At = [50, 30, 10] }, null, "linear", null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.DimensionAddAsync(front, topEdge, // parallel: no angle
            new EntityRef { Type = "edge", At = [0, -30, 20] }, "angular", null, drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.DimensionAddAsync("No Such View", topEdge, null, "linear", null, drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.DimensionAddAsync(front, new EntityRef { Type = "edge", At = [500, 500, 500] }, null, "linear", null, drw));
        Assert.Equal(0, (await Drw.InfoAsync(drw)).Sheets[0].Views![0].Annotations!.Dimensions);
    }

    // ------------------------------------------------------------------ drw_note_add

    [Fact]
    public async Task Note_reads_back_its_text_position_and_height()
    {
        string drw = await DrawingAsync("drw_note");
        int before = (await Drw.InfoAsync(drw)).Sheets[0].Annotations?.Notes ?? 0;
        var n = await Drw.NoteAddAsync("SWMCP NOTE\nsecond line", [100, 50], 5, null, drw);
        Assert.Equal("SWMCP NOTE\nsecond line", n.Text);
        Assert.Equal(100, n.AtMm![0], 0.01);
        Assert.Equal(50, n.AtMm[1], 0.01);
        Assert.Equal(5, n.HeightMm!.Value, 1e-3);
        Assert.Null(n.View);
        Assert.Contains("text read back", n.Checks!);
        Assert.Contains("position read back", n.Checks!);
        int after = (await Drw.InfoAsync(drw)).Sheets[0].Annotations?.Notes ?? 0;
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public async Task Note_in_a_view_belongs_to_it_and_bad_notes_are_refused()
    {
        var (drw, r) = await ViewsAsync("drw_note_view", ["front"]);
        var front = r.Views[0];
        var n = await Drw.NoteAddAsync("IN VIEW", [front.CenterMm![0], front.BoundsMm![1] - 8], null, front.Name, drw);
        Assert.Equal(front.Name, n.View);
        Assert.DoesNotContain(n.Warnings ?? [], w => w.Contains("belongs to"));
        Assert.Equal(1, (await Drw.InfoAsync(drw)).Sheets[0].Views!.Single(v => v.Name == front.Name).Annotations!.Notes);

        await Fails(ErrorCodes.InvalidArgument, () => Drw.NoteAddAsync(" ", [10, 10], null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.NoteAddAsync("x", [10], null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.NoteAddAsync("x", [10, 10], 0.1, null, drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.NoteAddAsync("x", [10, 10], null, "No Such View", drw));
    }

    // ------------------------------------------------------------------ drw_bom_add

    [Fact]
    public async Task Bom_of_a_part_lists_one_row()
    {
        var (drw, r) = await ViewsAsync("drw_bom", ["front"]);
        var bom = await Drw.BomAddAsync(r.Views[0].Name, null, null, drw);
        Assert.Equal(1, bom.Rows);
        var row = Assert.Single(bom.Items);
        Assert.Contains(row, cell => cell.Contains("drw_bom", StringComparison.OrdinalIgnoreCase)); // the part number is the file name
        Assert.Contains(row, cell => cell.Trim() == "1");                                         // quantity
        Assert.EndsWith(".sldbomtbt", bom.Template, StringComparison.OrdinalIgnoreCase);
        Assert.True(bom.Columns.Count >= 3);
        var info = await Drw.InfoAsync(drw);
        Assert.True(info.Sheets[0].Annotations!.Tables + info.Sheets[0].Views!.Sum(v => v.Annotations!.Tables) >= 1);
    }

    [Fact]
    public async Task Bom_rejects_bad_templates_and_views()
    {
        var (drw, r) = await ViewsAsync("drw_bom_bad", ["front"]);
        await Fails(ErrorCodes.InvalidArgument, () => Drw.BomAddAsync(r.Views[0].Name, null, "bom.txt", drw));
        await Fails(ErrorCodes.IoError, () => Drw.BomAddAsync(r.Views[0].Name, null, Path.Combine(_dir, "missing.sldbomtbt"), drw));
        await Fails(ErrorCodes.EntityNotFound, () => Drw.BomAddAsync("No Such View", null, null, drw));
        await Fails(ErrorCodes.InvalidArgument, () => Drw.BomAddAsync(r.Views[0].Name, [1, 2, 3], null, drw));
    }

    // ------------------------------------------------------------------ drw_center_marks

    [Fact]
    public async Task Center_marks_mark_the_hole_in_the_front_view()
    {
        var (drw, r) = await ViewsAsync("drw_marks", ["front"], hole: true);
        var cm = await Drw.CenterMarksAsync(null, drw);
        var front = Assert.Single(cm.Views);
        Assert.Equal(r.Views[0].Name, front.View);
        Assert.True(front.Added >= 1, $"no center mark on the Ø20 hole (before {front.Before}, after {front.After})");
        Assert.Null(cm.Hint);
        Assert.Equal(front.After, (await Drw.InfoAsync(drw)).Sheets[0].Views![0].Annotations!.CenterMarks);
    }

    [Fact]
    public async Task Center_marks_without_holes_add_none_and_unknown_views_are_refused()
    {
        var (drw, r) = await ViewsAsync("drw_marks_none", ["front"]);
        var cm = await Drw.CenterMarksAsync([r.Views[0].Name], drw);
        Assert.Equal(0, cm.Added);
        Assert.NotNull(cm.Hint);
        await Fails(ErrorCodes.EntityNotFound, () => Drw.CenterMarksAsync(["No Such View"], drw));
    }
}
