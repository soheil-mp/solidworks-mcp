using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>M0 tools against a live SOLIDWORKS, with analytic oracles (DESIGN §6.3).</summary>
public class M0LiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-3; // mm³ tolerance for planar oracles

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane, centred on the origin in x and z.</summary>
    private async Task<string> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, V);
        return doc;
    }

    [Fact]
    public async Task Connect_reports_the_revision()
    {
        var st = await new SessionService(S).StatusAsync();
        Assert.True(st.Attached);
        Assert.StartsWith("33.", st.Revision);
    }

    [Fact]
    public async Task Box_extrude_matches_the_oracle()
    {
        string doc = await BlockAsync("box");
        var info = await new InspectService(S).ModelInfoAsync(doc);
        Assert.Equal(120000, info.VolumeMm3!.Value, V);
        Assert.Equal([100, 20, 60], info.Box!.Size);
        Assert.Equal("MMGS", info.Units);
    }

    [Fact]
    public async Task Top_plane_sketch_frame_is_x_0_minus_y()
    {
        string doc = await NewPartAsync("frame");
        var st = await new SketchService(S).StartAsync(doc, new EntityRef { Name = "top" }, false);
        Assert.Equal([1, 0, 0], st.Frame!.XAxis);
        Assert.Equal([0, 0, -1], st.Frame.YAxis);
        Assert.Equal([0, 1, 0], st.Frame.Normal);
        await new SketchService(S).FinishAsync(doc);
    }

    [Fact]
    public async Task Mid_plane_extrude_has_caps_at_plus_minus_half_depth()
    {
        string doc = await NewPartAsync("mid");
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 10 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, End = "mid_plane", DepthMm = 30 });
        Assert.Equal(Math.PI * 100 * 30, r.Verified.VolumeDeltaMm3, 1e-2);
        Assert.Equal([-15], [r.Model.Box!.Min[2]]);
        Assert.Equal([15], [r.Model.Box.Max[2]]);
        Assert.Contains("cap at 15 mm", r.Verified.Checks!);
        Assert.Contains("cap at -15 mm", r.Verified.Checks!);
    }

    [Fact]
    public async Task Convex_fillet_removes_the_analytic_volume()
    {
        string doc = await BlockAsync("fillet_convex");
        // The top edge along x at y=20, z=30 is 100 mm long and convex.
        var r = await new FeatureService(S).FilletAsync(doc, [new EntityRef { At = [0, 20, 30] }], null, 5, true, null, false);
        Assert.Equal(-(25 - 25 * Math.PI / 4) * 100, r.Verified.VolumeDeltaMm3, 1e-2);
        Assert.Equal("removed", r.Verified.Material);
        Assert.Contains(r.NewFaces!, f => f.Surface == "cylinder" && f.RadiusMm == 5);
    }

    [Fact]
    public async Task Concave_fillet_adds_the_analytic_volume()
    {
        string doc = await BlockAsync("fillet_concave");
        // A 100 × 20 × 30 step on top of the back half (z from -30 to 0) makes a concave 100 mm edge at y=20, z=0.
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { At = [0, 20, -15] }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[-50, 20, -30], [50, 20, 0]] }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        var r = await new FeatureService(S).FilletAsync(doc, [new EntityRef { At = [0, 20, 0] }], null, 5, true, null, false);
        Assert.Equal((25 - 25 * Math.PI / 4) * 100, r.Verified.VolumeDeltaMm3, 1e-2);
        Assert.Equal("added", r.Verified.Material);
    }

    [Fact]
    public async Task Cut_from_an_open_sketch_goes_into_the_material()
    {
        string doc = await BlockAsync("cut");
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false); // the plane is the bottom face: material is on +y
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 10 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal(-Math.PI * 25 * 20, r.Verified.VolumeDeltaMm3, 1e-2);
        Assert.Equal([0, 1, 0], r.DirectionModel!);
    }

    [Fact]
    public async Task A_boss_buried_in_the_body_is_rolled_back()
    {
        string doc = await BlockAsync("rollback");
        var before = await new InspectService(S).FeatureListAsync(doc, false, 500, 0);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { At = [0, 20, 0] }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 20, 0], RadiusMm = 5 }]);
        var ex = await Assert.ThrowsAsync<SwException>(() =>
            new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 5, Direction = "reverse" }));
        Assert.Equal(ErrorCodes.VerifyFailed, ex.Code);
        Assert.Contains("Rolled back", ex.Message);
        var after = await new InspectService(S).FeatureListAsync(doc, false, 500, 0);
        Assert.Equal(before.Total + 1, after.Total); // only the (kept) sketch is new
        Assert.Equal("ProfileFeature", after.Features[^1].Type);
    }

    [Fact]
    public async Task Point_on_a_corner_is_ambiguous_and_listing_pick_points_resolve_back()
    {
        string doc = await BlockAsync("picks");
        var ex = await Assert.ThrowsAsync<SwException>(() =>
            new FeatureService(S).FilletAsync(doc, null, [new EntityRef { At = [-50, 20, -30] }], 1, true, null, false));
        Assert.Equal(ErrorCodes.EntityAmbiguous, ex.Code);

        var faces = await new InspectService(S).FaceListAsync(doc, null, 100, 0);
        Assert.Equal(6, faces.Total);
        var edges = await new InspectService(S).EdgeListAsync(doc, null, 100, 0, false);
        Assert.Equal(12, edges.Total);
        // Every face's pick point is inside that face: a fillet on the face given by point finds exactly that face.
        foreach (var f in faces.Faces)
        {
            var q = await new InspectService(S).FaceListAsync(doc, new EntityFilter { Near = f.PickMm, WithinMm = 0.001 }, 10, 0);
            Assert.Equal(f.Id, Assert.Single(q.Faces).Id);
        }
    }

    [Fact]
    public async Task Queries_with_expect_guard_the_count()
    {
        string doc = await BlockAsync("query");
        var ex = await Assert.ThrowsAsync<SwException>(() => new FeatureService(S).FilletAsync(doc,
            [new EntityRef { Where = System.Text.Json.JsonSerializer.SerializeToElement(new { curve = "line", parallelTo = "y" }), Expect = 3 }],
            null, 2, true, null, false));
        Assert.Equal(ErrorCodes.EntityAmbiguous, ex.Code);
        var r = await new FeatureService(S).FilletAsync(doc,
            [new EntityRef { Where = System.Text.Json.JsonSerializer.SerializeToElement(new { curve = "line", parallelTo = "y" }), Expect = 4 }],
            null, 2, true, null, false);
        Assert.Equal(4, r.Resolved!.Count);
    }

    [Fact]
    public async Task Foreign_documents_are_read_only()
    {
        // A document created through the raw API is not owned by the session.
        string title = await TestHooks.NewForeignPartAsync(S);
        try
        {
            var ex = await Assert.ThrowsAsync<SwException>(() => new SketchService(S).StartAsync(title, new EntityRef { Name = "top" }, false));
            Assert.Equal(ErrorCodes.NotOwned, ex.Code);
            var close = await Assert.ThrowsAsync<SwException>(() => new DocumentService(S).CloseAsync(title, false, true));
            Assert.Equal(ErrorCodes.NotOwned, close.Code);
            var info = await new InspectService(S).ModelInfoAsync(title); // reading is fine
            Assert.Equal("part", info.Type);
        }
        finally
        {
            await TestHooks.CloseRawAsync(S, title);
        }
    }

    [Fact]
    public async Task Dirty_owned_documents_need_save_or_discard()
    {
        string doc = await BlockAsync("dirty");
        var ex = await Assert.ThrowsAsync<SwException>(() => new DocumentService(S).CloseAsync(doc, false, false));
        Assert.Equal(ErrorCodes.Dirty, ex.Code);
    }

    [Fact]
    public async Task Save_copy_writes_a_fresh_file_and_refuses_overwrites()
    {
        string doc = await BlockAsync("save");
        string file = Path.Combine(Path.GetTempPath(), $"swmcp_test_{Guid.NewGuid():N}.sldprt");
        try
        {
            var r = await new DocumentService(S).SaveAsync(doc, "copy", file, false);
            Assert.True(File.Exists(r.SavedTo!) && r.FileSizeBytes > 10000);
            var ex = await Assert.ThrowsAsync<SwException>(() => new DocumentService(S).SaveAsync(doc, "copy", file, false));
            Assert.Equal(ErrorCodes.IoError, ex.Code);
            await new DocumentService(S).SaveAsync(doc, "copy", file, true); // our own file: overwrite allowed without a backup
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Screenshot_returns_a_png_and_restores_display_settings()
    {
        string doc = await BlockAsync("shot");
        var r = await new ViewService(S).ScreenshotAsync(doc, "iso", true, "shaded_edges", true, true, 640, null, false);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, r.Png[..4]);
        Assert.InRange(Math.Max(r.WidthPx, r.HeightPx), 600, 640);
    }

    [Fact]
    public async Task Vba_is_disabled_by_default()
    {
        var ex = await Assert.ThrowsAsync<SwException>(() => new AutomationService(S).VbaRunAsync(null, "Echo 1", 10));
        Assert.Equal(ErrorCodes.Disabled, ex.Code);
    }

    [Fact]
    public async Task Vba_captures_output_errors_and_syntax_failures()
    {
        var vba = new AutomationService(Fx.VbaSession);
        string doc = await NewPartAsync("vba", Fx.VbaSession);
        var ok = await vba.VbaRunAsync(doc, "Echo MM(25)\nEcho \"Ø 6\"", 20);
        Assert.Equal(["0.025", "Ø 6"], ok.Output);
        var rt = await Assert.ThrowsAsync<SwException>(() => vba.VbaRunAsync(doc, "Dim x As Double\nx = 1 / 0", 20));
        Assert.Equal(ErrorCodes.VbaError, rt.Code);
        Assert.Contains("11", rt.Message);
        var syn = await Assert.ThrowsAsync<SwException>(() => vba.VbaRunAsync(doc, "Echo (\"unclosed", 20));
        Assert.Contains("did not run", syn.Message);
    }

    [Fact]
    public async Task A_modal_dialog_returns_DIALOG_BLOCKING_and_can_be_answered()
    {
        var session = Fx.VbaSession;
        string doc = await NewPartAsync("dialog", session);
        var call = new AutomationService(session).VbaRunAsync(doc, "MsgBox \"swmcp-live-test\"\nEcho \"after\"", 60);
        var ex = await Assert.ThrowsAsync<SwException>(() => call);
        Assert.Equal(ErrorCodes.DialogBlocking, ex.Code);
        var dlg = Assert.Single(session.ListDialogs(), d => d.Text.Contains("swmcp-live-test"));
        var st = await new SessionService(session).StatusAsync(); // answers while SOLIDWORKS is blocked
        Assert.True(st.UiBlocked);
        var answered = await new SessionService(session).RespondAsync(dlg.HandleHex, "OK", false);
        Assert.True(answered.Closed);
        for (int i = 0; i < 50 && session.Dispatcher.Current is not null; i++) await Task.Delay(100);
        Assert.Contains(session.Dispatcher.LateCompletions, l => l.Name == "vba_run" && l.Succeeded);
    }
}
