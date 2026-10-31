using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>Feature management, dimensions, rebuild, undo and units against a live SOLIDWORKS, with analytic oracles.</summary>
public class FeatureManagementLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-2; // mm³: planar oracles, and the quarter-circle of a fillet

    /// <summary>An r5 fillet on a convex 100 mm edge removes 100·(r² − πr²/4) = 536.5 mm³ (DESIGN §6.3).</summary>
    private static readonly double FilletDv = (25 - 25 * Math.PI / 4) * 100;

    private FeatureManagementService M => new(S);

    private sealed record Block(string Doc, string Extrude, string Sketch, string Depth);

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane: V = 120 000 mm³, one depth dimension of 20 mm.</summary>
    private async Task<Block> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        var st = await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(120000, r.Verified.VolumeDeltaMm3, 1e-3);
        var depth = r.Dimensions!.First(x => x.Unit == "mm" && Math.Abs(x.Value - 20) < 1e-6);
        return new Block(doc, r.Feature.Name, st.Sketch, depth.Name);
    }

    /// <summary>The r5 fillet on the block's convex top edge along x (y = 20, z = 30).</summary>
    private async Task<string> FilletAsync(string doc)
    {
        var r = await new FeatureService(S).FilletAsync(doc, [new EntityRef { At = [0, 20, 30] }], null, 5, true, null, false);
        Assert.Equal(-FilletDv, r.Verified.VolumeDeltaMm3, V);
        return r.Feature.Name;
    }

    private async Task<double> VolumeAsync(string doc) => (await new InspectService(S).ModelInfoAsync(doc)).VolumeMm3 ?? 0;

    // ------------------------------------------------------------------ feature_info

    [Fact]
    public async Task Feature_info_reads_the_extrude_and_fillet_definitions()
    {
        var b = await BlockAsync("fm_info");
        string fillet = await FilletAsync(b.Doc);

        var ex = await M.InfoAsync(b.Doc, b.Extrude);
        Assert.Contains(ex.Type, new[] { "Extrusion", "ICE", "Boss" });
        Assert.Null(ex.Error);
        Assert.Null(ex.Suppressed);
        Assert.Contains(b.Sketch, ex.Parents!);
        Assert.Contains(b.Sketch, ex.SubFeatures!);
        Assert.Contains(fillet, ex.Children!);
        Assert.Contains(ex.Dimensions!, x => x.Name == b.Depth && Math.Abs(x.Value - 20) < 1e-9 && x.Unit == "mm");
        var def = ex.Definition!;
        Assert.Equal("extrude", def.Kind);
        Assert.Equal("blind", def.EndCondition);
        Assert.Equal(20, def.DepthMm!.Value, 1e-6);
        Assert.Null(def.Cut);
        Assert.Null(def.DraftDeg);

        var fi = await M.InfoAsync(b.Doc, fillet);
        Assert.Equal("Fillet", fi.Type);
        Assert.Contains(b.Extrude, fi.Parents!);
        Assert.Equal("fillet", fi.Definition!.Kind);
        Assert.Equal(5, fi.Definition.RadiusMm!.Value, 1e-6);
        Assert.Equal(1, fi.Definition.EdgeCount); // read without AccessSelections (unverified that the count needs none)

        var top = await M.InfoAsync(b.Doc, "top"); // standard planes answer to front/top/right
        Assert.Equal("RefPlane", top.Type);
        Assert.Null(top.Definition);
    }

    [Fact]
    public async Task Feature_info_of_an_unknown_name_is_ENTITY_NOT_FOUND()
    {
        var b = await BlockAsync("fm_info_missing");
        var ex = await Assert.ThrowsAsync<SwException>(() => M.InfoAsync(b.Doc, "NoSuchFeature1"));
        Assert.Equal(ErrorCodes.EntityNotFound, ex.Code);
    }

    // ------------------------------------------------------------------ dimension_list

    [Fact]
    public async Task Dimension_list_returns_the_depth_in_mm_as_a_driving_dimension()
    {
        var b = await BlockAsync("fm_dimlist");
        var f = await M.DimensionListAsync(b.Doc, b.Extrude);
        var depth = Assert.Single(f.Dimensions, x => x.Name == b.Depth);
        Assert.Equal(20, depth.Value, 1e-9);
        Assert.Equal("mm", depth.Unit);
        Assert.Null(depth.Driven); // swDimensionDriving: checks the DrivenState reading that dimension_set relies on

        var all = await M.DimensionListAsync(b.Doc);
        Assert.Contains(all.Dimensions, x => x.Name == b.Depth);
        Assert.Equal(all.Dimensions.Count, all.Total);
        var sketch = await M.DimensionListAsync(b.Doc, sketch: b.Sketch);
        Assert.Equal(sketch.Dimensions.Count, sketch.Total);
    }

    [Fact]
    public async Task Dimension_list_rejects_a_feature_passed_as_sketch_and_unknown_names()
    {
        var b = await BlockAsync("fm_dimlist_bad");
        var notSketch = await Assert.ThrowsAsync<SwException>(() => M.DimensionListAsync(b.Doc, sketch: b.Extrude));
        Assert.Equal(ErrorCodes.InvalidArgument, notSketch.Code);
        var missing = await Assert.ThrowsAsync<SwException>(() => M.DimensionListAsync(b.Doc, feature: "NoSuchFeature1"));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
    }

    // ------------------------------------------------------------------ dimension_set

    [Fact]
    public async Task Dimension_set_changes_the_depth_and_the_volume()
    {
        var b = await BlockAsync("fm_dimset");
        var r = await M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = b.Depth, Value = 30 }]);
        var c = Assert.Single(r.Changes);
        Assert.Equal(20, c.Previous, 1e-9);
        Assert.Equal(30, c.Value, 1e-9);
        Assert.Equal("mm", c.Unit);
        Assert.Equal("this", r.Configs);
        Assert.Equal(60000, r.VolumeDeltaMm3!.Value, 1e-3);
        Assert.Equal(180000, r.Model!.VolumeMm3, 1e-3);
        Assert.Equal([100, 30, 60], r.Model.Box!.Size);
        Assert.Equal(180000, await VolumeAsync(b.Doc), 1e-3);
    }

    [Fact]
    public async Task Dimension_set_refuses_bad_values_unknown_names_and_an_open_sketch()
    {
        var b = await BlockAsync("fm_dimset_bad");
        var missing = await Assert.ThrowsAsync<SwException>(() =>
            M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = "D9@NoSuchFeature1", Value = 5 }]));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
        var negative = await Assert.ThrowsAsync<SwException>(() => M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = b.Depth, Value = -5 }]));
        Assert.Equal(ErrorCodes.InvalidArgument, negative.Code);

        var sk = new SketchService(S);
        await sk.StartAsync(b.Doc, new EntityRef { Name = "front" }, false);
        var open = await Assert.ThrowsAsync<SwException>(() => M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = b.Depth, Value = 30 }]));
        Assert.Equal(ErrorCodes.SketchState, open.Code);
        await sk.AddAsync(b.Doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 5 }]);
        await sk.FinishAsync(b.Doc);
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3); // nothing changed
    }

    // ------------------------------------------------------------------ dimension_tolerance_set

    [Fact]
    public async Task Tolerances_read_back_and_fits_follow_the_ISO_tables()
    {
        var b = await BlockAsync("fm_tol");
        Task<DimensionToleranceResult> Set(ToleranceRequest q) => M.DimensionToleranceSetAsync(b.Doc, [q]);

        var bi = await Set(new ToleranceRequest { Name = b.Depth, Type = "bilateral", Upper = 0.1, Lower = -0.05 });
        var c = Assert.Single(bi.Changes);
        Assert.Equal("none", c.Previous.Type);
        Assert.Equal(("bilateral", 0.1, -0.05), (c.Tolerance.Type, c.Tolerance.Upper!.Value, c.Tolerance.Lower!.Value));
        Assert.Equal([19.95, 20.1], c.Limits!);
        var listed = Assert.Single((await M.DimensionListAsync(b.Doc, b.Extrude)).Dimensions, x => x.Name == b.Depth);
        Assert.Equal("bilateral", listed.Tolerance!.Type);

        var sym = Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "symmetric", Upper = 0.2 })).Changes);
        Assert.Equal([19.8, 20.2], sym.Limits!);
        Assert.Equal("bilateral", sym.Previous.Type);
        var lim = Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "limit", Upper = 0.3, Lower = 0.1 })).Changes);
        Assert.Equal([20.1, 20.3], lim.Limits!);
        Assert.Equal("basic", Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "basic" })).Changes).Tolerance.Type);

        // ISO 286 for 18..30 mm: H7 = 0 / +0.021, g6 = -0.020 / -0.007 (SOLIDWORKS computes them from the class).
        var h7 = Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "fit_with_tolerance", Hole = "H7" })).Changes);
        Assert.Equal("H7", h7.Tolerance.Hole);
        Assert.Equal(0.021, h7.Tolerance.Upper!.Value, 1e-9);
        Assert.Equal(0, h7.Tolerance.Lower!.Value, 1e-9);
        var g6 = Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "fit_tolerance_only", Shaft = "g6" })).Changes);
        Assert.Equal(-0.007, g6.Tolerance.Upper!.Value, 1e-9);
        Assert.Equal(-0.020, g6.Tolerance.Lower!.Value, 1e-9);
        var fit = Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "fit", Hole = "H7", Shaft = "g6" })).Changes);
        Assert.Equal(("H7", "g6"), (fit.Tolerance.Hole, fit.Tolerance.Shaft));

        // A rebuild keeps it; none clears it; the geometry never changes.
        await M.RebuildAsync(b.Doc, force: true);
        Assert.Equal("fit", Assert.Single((await M.DimensionListAsync(b.Doc, b.Extrude)).Dimensions, x => x.Name == b.Depth).Tolerance!.Type);
        Assert.Equal("none", Assert.Single((await Set(new ToleranceRequest { Name = b.Depth, Type = "none" })).Changes).Tolerance.Type);
        Assert.Null(Assert.Single((await M.DimensionListAsync(b.Doc, b.Extrude)).Dimensions, x => x.Name == b.Depth).Tolerance);
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3);
    }

    [Fact]
    public async Task Tolerances_refuse_unknown_dimensions_and_bad_entries_before_changing_anything()
    {
        var b = await BlockAsync("fm_tol_bad");
        var missing = await Assert.ThrowsAsync<SwException>(() =>
            M.DimensionToleranceSetAsync(b.Doc, [new ToleranceRequest { Name = b.Depth, Type = "basic" }, new ToleranceRequest { Name = "D9@NoSuchFeature1", Type = "basic" }]));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
        var bad = await Assert.ThrowsAsync<SwException>(() =>
            M.DimensionToleranceSetAsync(b.Doc, [new ToleranceRequest { Name = b.Depth, Type = "bilateral", Upper = -0.1, Lower = 0.1 }]));
        Assert.Equal(ErrorCodes.InvalidArgument, bad.Code);
        Assert.Null(Assert.Single((await M.DimensionListAsync(b.Doc, b.Extrude)).Dimensions, x => x.Name == b.Depth).Tolerance);
    }

    [Fact]
    public async Task Dimension_set_restores_the_value_when_a_feature_breaks()
    {
        // Not with a fillet radius: SOLIDWORKS lets a fillet overflow its faces (r50 and even r1000 next to the 20 mm front
        // face rebuild without an error, VERIFIED live). A shell thicker than the part does fail.
        var b = await BlockAsync("fm_dimset_break");
        var shell = await new PartFeatureService(S).ShellAsync(new ShellRequest { Doc = b.Doc, RemoveFaces = [new EntityRef { At = [0, 20, 0] }], ThicknessMm = 2 });
        double v0 = await VolumeAsync(b.Doc);
        var t = (await M.DimensionListAsync(b.Doc, shell.Feature.Name)).Dimensions.Single(x => x.Unit == "mm");
        Assert.Equal(2, t.Value, 1e-9);
        var ex = await Assert.ThrowsAsync<SwException>(() => M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = t.Name, Value = 40 }]));
        Assert.Contains(ex.Code, new[] { ErrorCodes.RebuildError, ErrorCodes.VerifyFailed, ErrorCodes.InvalidArgument });
        Assert.Equal(2, (await M.DimensionListAsync(b.Doc, shell.Feature.Name)).Dimensions.Single(x => x.Name == t.Name).Value, 1e-9);
        Assert.Equal(v0, await VolumeAsync(b.Doc), V);
        Assert.Empty((await M.RebuildAsync(b.Doc)).Errors);
    }

    // ------------------------------------------------------------------ feature_delete

    [Fact]
    public async Task Feature_delete_of_a_fillet_restores_the_volume()
    {
        var b = await BlockAsync("fm_delete");
        string fillet = await FilletAsync(b.Doc);
        var r = await M.DeleteAsync(b.Doc, [fillet]);
        var row = Assert.Single(r.Removed);
        Assert.Equal(fillet, row.Name);
        Assert.Equal("requested", row.Why);
        Assert.Equal(FilletDv, r.VolumeDeltaMm3!.Value, V);
        Assert.Equal(120000, r.Model!.VolumeMm3, 1e-3);
        Assert.Null((await M.InfoAsync(b.Doc, b.Extrude)).Children);
    }

    [Fact]
    public async Task Feature_delete_refuses_a_parent_with_children_and_the_dry_run_matches_the_delete()
    {
        var b = await BlockAsync("fm_delete_parent");
        string fillet = await FilletAsync(b.Doc);
        var refused = await Assert.ThrowsAsync<SwException>(() => M.DeleteAsync(b.Doc, [b.Extrude]));
        Assert.Equal(ErrorCodes.InvalidArgument, refused.Code);
        Assert.Contains(fillet, refused.Message);

        var dry = await M.DeleteAsync(b.Doc, [b.Extrude], deleteChildren: true, deleteAbsorbed: true, dryRun: true);
        Assert.True(dry.DryRun);
        Assert.Equal(new[] { b.Extrude, fillet, b.Sketch }.Order(), dry.Removed.Select(x => x.Name).Order());
        Assert.Equal(120000 - FilletDv, await VolumeAsync(b.Doc), V); // the dry run changed nothing

        var r = await M.DeleteAsync(b.Doc, [b.Extrude], deleteChildren: true, deleteAbsorbed: true);
        Assert.Equal(dry.Removed.Select(x => (x.Name, x.Why)).Order(), r.Removed.Select(x => (x.Name, x.Why)).Order());
        Assert.Equal(0, r.Model!.Bodies);
        Assert.Equal(-(120000 - FilletDv), r.VolumeDeltaMm3!.Value, V);
    }

    [Fact]
    public async Task Feature_delete_keeps_the_absorbed_sketch_by_default()
    {
        var b = await BlockAsync("fm_delete_keep");
        var r = await M.DeleteAsync(b.Doc, [b.Extrude]);
        Assert.Equal(b.Extrude, Assert.Single(r.Removed).Name);
        Assert.Equal(new[] { b.Sketch }, r.KeptAbsorbed!);
        Assert.Equal("ProfileFeature", (await M.InfoAsync(b.Doc, b.Sketch)).Type);
        Assert.Equal(0, r.Model!.Bodies);
    }

    // ------------------------------------------------------------------ feature_suppress_set

    [Fact]
    public async Task Suppress_and_unsuppress_round_trip_with_previous_states()
    {
        var b = await BlockAsync("fm_suppress");
        string fillet = await FilletAsync(b.Doc);

        var s = await M.SuppressAsync(b.Doc, [fillet], true);
        var row = Assert.Single(s.Features);
        Assert.False(row.Previous);
        Assert.True(row.Suppressed);
        Assert.Equal("this", s.Configs);
        Assert.Equal(FilletDv, s.VolumeDeltaMm3!.Value, V);
        Assert.True((await M.InfoAsync(b.Doc, fillet)).Suppressed);

        var u = await M.SuppressAsync(b.Doc, [fillet], false);
        Assert.True(Assert.Single(u.Features).Previous);
        Assert.False(u.Features[0].Suppressed);
        Assert.Equal(-FilletDv, u.VolumeDeltaMm3!.Value, V);

        // A suppressed parent takes its dependent along; both come back in one call (parents first).
        var p = await M.SuppressAsync(b.Doc, [b.Extrude], true);
        Assert.Contains(fillet, p.AlsoChanged!);
        Assert.Equal(0, p.Model!.VolumeMm3, 1e-3);
        await M.SuppressAsync(b.Doc, [b.Extrude, fillet], false);
        Assert.Equal(120000 - FilletDv, await VolumeAsync(b.Doc), V);
    }

    [Fact]
    public async Task Suppress_refuses_system_features_and_unknown_configs()
    {
        var b = await BlockAsync("fm_suppress_bad");
        var origin = await Assert.ThrowsAsync<SwException>(() => M.SuppressAsync(b.Doc, ["Origin"], true));
        Assert.Equal(ErrorCodes.InvalidArgument, origin.Code);
        var cfg = await Assert.ThrowsAsync<SwException>(() => M.SuppressAsync(b.Doc, [b.Extrude], true, "Default"));
        Assert.Equal(ErrorCodes.InvalidArgument, cfg.Code);
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3);
    }

    // ------------------------------------------------------------------ feature_rename

    [Fact]
    public async Task Rename_reads_back_and_renames_the_dimensions()
    {
        var b = await BlockAsync("fm_rename");
        var r = await M.RenameAsync(b.Doc, b.Extrude, "Plate");
        Assert.Equal(b.Extrude, r.Previous);
        Assert.Equal("Plate", r.Name);
        var depth = Assert.Single(r.Dimensions!, x => x.Name.EndsWith("@Plate", StringComparison.Ordinal));
        Assert.Equal(20, depth.Value, 1e-9);
        var set = await M.DimensionSetAsync(b.Doc, [new DimensionValue { Name = depth.Name, Value = 25 }]);
        Assert.Equal(150000, set.Model!.VolumeMm3, 1e-3);
        Assert.Equal("Plate", (await M.InfoAsync(b.Doc, "plate")).Name); // names resolve ignoring case
    }

    [Fact]
    public async Task Rename_refuses_names_in_use_and_names_with_at()
    {
        var b = await BlockAsync("fm_rename_bad");
        var used = await Assert.ThrowsAsync<SwException>(() => M.RenameAsync(b.Doc, b.Extrude, b.Sketch));
        Assert.Equal(ErrorCodes.InvalidArgument, used.Code);
        var at = await Assert.ThrowsAsync<SwException>(() => M.RenameAsync(b.Doc, b.Extrude, "a@b"));
        Assert.Equal(ErrorCodes.InvalidArgument, at.Code);
        Assert.Equal(b.Extrude, (await M.InfoAsync(b.Doc, b.Extrude)).Name);
    }

    // ------------------------------------------------------------------ feature_rollback

    [Fact]
    public async Task Rollback_to_the_extrude_hides_the_fillet_and_end_restores_it()
    {
        var b = await BlockAsync("fm_rollback");
        string fillet = await FilletAsync(b.Doc);

        var r = await M.RollbackAsync(b.Doc, b.Extrude);
        Assert.Equal($"after {b.Extrude}", r.Bar);
        Assert.Equal(new[] { fillet }, r.RolledBack!);
        Assert.Equal(FilletDv, r.VolumeDeltaMm3!.Value, V);
        Assert.Equal(120000, r.Model!.VolumeMm3, 1e-3);
        Assert.Contains(r.Warnings!, w => w.Contains("rollback bar"));
        Assert.True((await M.InfoAsync(b.Doc, fillet)).RolledBack);

        var named = await M.RollbackAsync(b.Doc, fillet, before: true); // the same position, named from below
        Assert.Equal(new[] { fillet }, named.RolledBack!);

        var end = await M.RollbackAsync(b.Doc, "end");
        Assert.Equal("end", end.Bar);
        Assert.Null(end.RolledBack);
        Assert.Equal(120000 - FilletDv, end.Model!.VolumeMm3, V);
    }

    [Fact]
    public async Task Rollback_refuses_unknown_features_and_before_end()
    {
        var b = await BlockAsync("fm_rollback_bad");
        var missing = await Assert.ThrowsAsync<SwException>(() => M.RollbackAsync(b.Doc, "NoSuchFeature1"));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
        var beforeEnd = await Assert.ThrowsAsync<SwException>(() => M.RollbackAsync(b.Doc, "end", before: true));
        Assert.Equal(ErrorCodes.InvalidArgument, beforeEnd.Code);
    }

    // ------------------------------------------------------------------ doc_rebuild

    [Fact]
    public async Task Rebuild_of_a_clean_part_reports_no_errors()
    {
        var b = await BlockAsync("fm_rebuild");
        var r = await M.RebuildAsync(b.Doc);
        Assert.True(r.Rebuilt);
        Assert.Empty(r.Errors);
        Assert.Null(r.Forced);
        Assert.Equal(120000, r.Model!.VolumeMm3, 1e-3);
        var forced = await M.RebuildAsync(b.Doc, force: true);
        Assert.True(forced.Forced);
        Assert.Empty(forced.Errors);
        Assert.Equal(120000, forced.Model!.VolumeMm3, 1e-3);
    }

    [Fact]
    public async Task Rebuild_refuses_while_a_sketch_is_open()
    {
        var b = await BlockAsync("fm_rebuild_sketch");
        var sk = new SketchService(S);
        await sk.StartAsync(b.Doc, new EntityRef { Name = "front" }, false);
        var ex = await Assert.ThrowsAsync<SwException>(() => M.RebuildAsync(b.Doc));
        Assert.Equal(ErrorCodes.SketchState, ex.Code);
        await sk.AddAsync(b.Doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 5 }]);
        await sk.FinishAsync(b.Doc);
    }

    // ------------------------------------------------------------------ doc_undo

    [Fact]
    public async Task Undo_after_a_fillet_removes_it()
    {
        var b = await BlockAsync("fm_undo");
        string fillet = await FilletAsync(b.Doc);
        var r = await M.UndoAsync(b.Doc, 1);
        Assert.Equal(1, r.Steps);
        Assert.Equal(new[] { fillet }, r.Removed!);
        Assert.Equal(FilletDv, r.VolumeDeltaMm3!.Value, V);
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3);
        Assert.Null(r.OpenSketch);
    }

    [Fact]
    public async Task Undo_never_goes_beyond_the_servers_own_steps()
    {
        var b = await BlockAsync("fm_undo_limit"); // sketch_start, sketch_add, feature_extrude: at most 3 server steps
        var tooMany = await Assert.ThrowsAsync<SwException>(() => M.UndoAsync(b.Doc, 20));
        Assert.Equal(ErrorCodes.InvalidArgument, tooMany.Code);
        Assert.Contains("steps", tooMany.Hint!);
        var range = await Assert.ThrowsAsync<SwException>(() => M.UndoAsync(b.Doc, 0));
        Assert.Equal(ErrorCodes.InvalidArgument, range.Code);
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3);
    }

    [Fact]
    public async Task Undo_units_rebuild_and_delete_refuse_the_users_documents()
    {
        string title = await TestHooks.NewForeignPartAsync(S);
        try
        {
            var undo = await Assert.ThrowsAsync<SwException>(() => M.UndoAsync(title, 1));
            Assert.Equal(ErrorCodes.NotOwned, undo.Code);
            var units = await Assert.ThrowsAsync<SwException>(() => M.UnitsSetAsync(title, "ips"));
            Assert.Equal(ErrorCodes.NotOwned, units.Code);
            var rebuild = await Assert.ThrowsAsync<SwException>(() => M.RebuildAsync(title));
            Assert.Equal(ErrorCodes.NotOwned, rebuild.Code);
            var delete = await Assert.ThrowsAsync<SwException>(() => M.DeleteAsync(title, ["Origin"]));
            Assert.Equal(ErrorCodes.NotOwned, delete.Code);
            var info = await M.DimensionListAsync(title); // reading is fine
            Assert.Equal(info.Dimensions.Count, info.Total);
        }
        finally
        {
            await TestHooks.CloseRawAsync(S, title);
        }
    }

    // ------------------------------------------------------------------ doc_units_set

    [Fact]
    public async Task Units_set_switches_the_unit_system_and_tool_values_stay_in_mm()
    {
        var b = await BlockAsync("fm_units");
        var r = await M.UnitsSetAsync(b.Doc, "ips");
        Assert.Equal("MMGS", r.Previous);
        Assert.Equal("IPS", r.Units);
        Assert.True(r.Changed);
        Assert.Equal("IPS", (await new InspectService(S).ModelInfoAsync(b.Doc)).Units);
        var depth = (await M.DimensionListAsync(b.Doc, b.Extrude)).Dimensions.Single(x => x.Name == b.Depth);
        Assert.Equal(20, depth.Value, 1e-9); // still mm
        Assert.Equal(120000, await VolumeAsync(b.Doc), 1e-3);

        Assert.False((await M.UnitsSetAsync(b.Doc, "ips")).Changed);
        var back = await M.UnitsSetAsync(b.Doc, "mmgs");
        Assert.Equal("IPS", back.Previous);
        Assert.Equal("MMGS", back.Units);
    }

    [Fact]
    public async Task Units_set_rejects_unknown_unit_systems()
    {
        var b = await BlockAsync("fm_units_bad");
        var ex = await Assert.ThrowsAsync<SwException>(() => M.UnitsSetAsync(b.Doc, "furlongs"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        var keep = await Assert.ThrowsAsync<SwException>(() => M.UnitsSetAsync(b.Doc, "template"));
        Assert.Equal(ErrorCodes.InvalidArgument, keep.Code);
        Assert.Equal("MMGS", (await new InspectService(S).ModelInfoAsync(b.Doc)).Units);
    }
}

/// <summary>The COM-free rules of the feature-management tools (FeatureManagementLogic). These run without SOLIDWORKS.</summary>
public class FeatureManagementLogicTests
{
    private const int Linear = 0, Angular = 1, Count = 2; // swDimensionParamType_e

    private static string Code(Action a) => Assert.Throws<SwException>(a).Code;

    [Theory]
    [InlineData("this", 1)]
    [InlineData(null, 1)]
    [InlineData(" ALL ", 2)]
    public void Configs_map_to_the_api_options(string? configs, int expected) =>
        Assert.Equal(expected, FeatureManagementLogic.ParseConfigs(configs));

    [Fact]
    public void Configuration_names_are_not_supported() =>
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ParseConfigs("Default")));

    [Fact]
    public void Values_convert_by_dimension_type()
    {
        Assert.Equal(0.03, FeatureManagementLogic.ToApi(Linear, 30, "D1"), 12);
        Assert.Equal(Math.PI / 2, FeatureManagementLogic.ToApi(Angular, 90, "D2"), 12);
        Assert.Equal(4.0, FeatureManagementLogic.ToApi(Count, 4, "D3"));
        Assert.Equal(30.0, FeatureManagementLogic.FromApi(Linear, 0.03));
        Assert.Equal(90.0, FeatureManagementLogic.FromApi(Angular, Math.PI / 2));
        Assert.Equal(6.0, FeatureManagementLogic.FromApi(Count, 6.0000001));
        Assert.Equal("mm", FeatureManagementLogic.UnitOf(Linear));
        Assert.Equal("deg", FeatureManagementLogic.UnitOf(Angular));
        Assert.Equal("count", FeatureManagementLogic.UnitOf(Count));
        Assert.Equal("mm", FeatureManagementLogic.UnitOf(-1));
    }

    [Theory]
    [InlineData(Linear, -1.0)]
    [InlineData(Linear, 0.0)]
    [InlineData(Linear, double.NaN)]
    [InlineData(Linear, double.PositiveInfinity)]
    [InlineData(Angular, 0.0)]
    [InlineData(Angular, 400.0)]
    [InlineData(Count, 0.0)]
    [InlineData(Count, 2.5)]
    public void Out_of_range_values_are_refused(int type, double value) =>
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ToApi(type, value, "D1@X")));

    [Fact]
    public void Read_back_compares_with_a_tight_tolerance()
    {
        Assert.True(FeatureManagementLogic.SameApi(Linear, 0.0300000000001, 0.03));
        Assert.False(FeatureManagementLogic.SameApi(Linear, 0.0301, 0.03));
        Assert.False(FeatureManagementLogic.SameApi(Linear, double.NaN, 0.03));
        Assert.True(FeatureManagementLogic.SameApi(Count, 4.0000001, 4));
        Assert.False(FeatureManagementLogic.SameApi(Count, 5, 4));
    }

    [Theory]
    [InlineData("\"D1@Boss-Extrude1\" = 20mm", "D1@Boss-Extrude1")]
    [InlineData("\"W\" = 60mm", "W")]
    [InlineData("  \"D2@Sketch1\"=\"W\"/2", "D2@Sketch1")]
    [InlineData("no assignment", null)]
    [InlineData("", null)]
    public void Equation_targets_are_the_left_hand_side(string equation, string? expected) =>
        Assert.Equal(expected, FeatureManagementLogic.EquationTarget(equation));

    [Fact]
    public void New_names_are_trimmed_and_checked()
    {
        Assert.Equal("Plate", FeatureManagementLogic.ValidateNewName("  Plate "));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ValidateNewName(" ")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ValidateNewName("D1@Plate")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ValidateNewName("a\"b")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.ValidateNewName(new string('x', 201))));
    }

    [Fact]
    public void Feature_names_are_trimmed_and_deduplicated_ignoring_case()
    {
        Assert.Equal(["Fillet1", "Cut-Extrude1"], FeatureManagementLogic.RequireNames([" Fillet1", "fillet1", "Cut-Extrude1"], "features"));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireNames([], "features")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireNames(["Fillet1", " "], "features")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireNames(null, "features")));
    }

    [Fact]
    public void Dimension_values_need_unique_names_and_finite_numbers()
    {
        var ok = FeatureManagementLogic.RequireValues([new DimensionValue { Name = " D1@Boss-Extrude1 ", Value = 30 }]);
        Assert.Equal(("D1@Boss-Extrude1", 30.0), Assert.Single(ok));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireValues([])));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireValues(
            [new DimensionValue { Name = "D1@A", Value = 1 }, new DimensionValue { Name = "d1@a", Value = 2 }])));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireValues([new DimensionValue { Name = "D1@A", Value = double.NaN }])));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => FeatureManagementLogic.RequireValues([new DimensionValue { Name = "", Value = 1 }])));
    }

    [Fact]
    public void Enum_names_are_snake_case()
    {
        Assert.Equal("blind", FeatureManagementLogic.EndConditionName(0));
        Assert.Equal("mid_plane", FeatureManagementLogic.EndConditionName(6));
        Assert.Equal("through_all_both", FeatureManagementLogic.EndConditionName(9));
        Assert.Equal("42", FeatureManagementLogic.EndConditionName(42));
        Assert.True(FeatureManagementLogic.HasDepth(0));
        Assert.False(FeatureManagementLogic.HasDepth(1));
        Assert.Equal("hole_blind", FeatureManagementLogic.HoleTypeName(22));
        Assert.Null(FeatureManagementLogic.HoleTypeName(-1));
        Assert.Equal("AnsiMetric", FeatureManagementLogic.HoleStandardName(1));
        Assert.Equal("equal_distance", FeatureManagementLogic.ChamferTypeName(16));
        Assert.Equal("constant_radius", FeatureManagementLogic.FilletTypeName(0));
        Assert.Equal("two_direction", FeatureManagementLogic.RevolveTypeName(2));
        Assert.Equal("counter_bore_blind", FeatureManagementLogic.Snake("CounterBoreBlind"));
    }

    [Fact]
    public void Set_value_failures_map_to_typed_errors()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, FeatureManagementLogic.SetValueError(2, "D1@A", 5, "mm").Code);
        Assert.Equal(ErrorCodes.InvalidArgument, FeatureManagementLogic.SetValueError(3, "D1@A", 5, "mm").Code);
        Assert.Equal(ErrorCodes.NotSupported, FeatureManagementLogic.SetValueError(4, "D1@A", 5, "mm").Code);
        Assert.Equal(ErrorCodes.NotSupported, FeatureManagementLogic.SetValueError(5, "D1@A", 5, "mm").Code);
        Assert.Equal(ErrorCodes.FeatureFailed, FeatureManagementLogic.SetValueError(1, "D1@A", 5, "mm").Code);
    }
}
