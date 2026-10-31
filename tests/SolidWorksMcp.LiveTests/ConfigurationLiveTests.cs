using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Configuration families against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): config_table rows (dimensions,
/// suppression, properties, material; components in assemblies), the table read back, a failing table undone, and display
/// states. Oracles: volumes per configuration, values read in each configuration.
/// </summary>
public class ConfigurationLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-3; // mm³
    private static readonly double Hole = Math.PI * 25; // Ø10 hole area, mm²
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_cfg_{Guid.NewGuid():N}");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _asms = [];

    private PropertiesService P => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);
    private DocumentService Docs => new(S);

    public override async ValueTask DisposeAsync()
    {
        if (LiveFixture.Enabled)
            foreach (var h in Enumerable.Reverse(_asms))
            {
                try { await Docs.CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed by the test */ }
            }
        try { await base.DisposeAsync(); }
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best effort */ }
        }
    }

    /// <summary>A 100 × 60 block, 20 high (D1@Boss-Extrude1), with a Ø10 hole through its centre (Cut-Extrude1).</summary>
    private async Task<string> HoledBlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 10 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        return doc;
    }

    private static ConfigTableRow Row(string name, params (string Column, object Value)[] values) => new()
    {
        Name = name,
        Values = values.ToDictionary(v => v.Column, v => v.Value is string s ? new ConfigCell(null, s) : new ConfigCell(Convert.ToDouble(v.Value), null)),
    };

    private static double Block(double h, bool hole) => 100 * 60 * h - (hole ? Hole * h : 0);

    [Fact]
    public async Task A_table_makes_three_sizes_each_with_its_own_volume_and_reads_back()
    {
        string doc = await HoledBlockAsync("cfg_table");
        string start = (await P.ConfigListAsync(doc)).Active;
        var r = await P.ConfigTableAsync(doc,
        [
            Row(start, ("D1@Boss-Extrude1", 20), ("$PRP@PartNo", "B-20")),
            Row("Short", ("D1@Boss-Extrude1", 10), ("$STATE@Cut-Extrude1", "suppressed"), ("$PRP@PartNo", "B-10"), ("$MATERIAL", "AISI 1020")),
            Row("Long", ("D1@Boss-Extrude1", 40), ("$PRP@PartNo", "B-40")),
        ]);
        Assert.Equal(start, r.Active);
        Assert.Equal(["Short", "Long"], r.Created);
        Assert.Equal(Block(20, true), r.Rows[0].VolumeMm3!.Value, V);
        Assert.Equal(Block(10, false), r.Rows[1].VolumeMm3!.Value, V);
        Assert.Equal(Block(40, true), r.Rows[2].VolumeMm3!.Value, V);
        Assert.Equal(Block(10, false) * 1e-9 * 7900, r.Rows[1].MassKg!.Value, 1e-5); // AISI 1020: 7900 kg/m³
        Assert.Equal("suppressed", r.Rows[1].Values["$STATE@Cut-Extrude1"]);
        Assert.Equal(40.0, r.Rows[2].Values["D1@Boss-Extrude1"]);

        // Read mode: the columns that differ, per configuration.
        var t = await P.ConfigTableAsync(doc, null);
        Assert.Contains("D1@Boss-Extrude1", t.Columns);
        Assert.Contains("$STATE@Cut-Extrude1", t.Columns);
        Assert.Contains("$PRP@PartNo", t.Columns);
        Assert.Contains("$MATERIAL", t.Columns);
        var byName = t.Rows.ToDictionary(x => x.Configuration);
        Assert.Equal(10.0, byName["Short"].Values["D1@Boss-Extrude1"]);
        Assert.Equal("unsuppressed", byName["Long"].Values["$STATE@Cut-Extrude1"]);
        Assert.Equal("B-40", byName["Long"].Values["$PRP@PartNo"]);
        Assert.Equal("AISI 1020", byName["Short"].Values["$MATERIAL"]);

        // An update of one existing configuration leaves the others alone.
        var u = await P.ConfigTableAsync(doc, [Row("Long", ("D1@Boss-Extrude1", 30))]);
        Assert.Null(u.Created);
        Assert.Equal(Block(30, true), u.Rows[0].VolumeMm3!.Value, V);
        var again = (await P.ConfigTableAsync(doc, null, ["D1@Boss-Extrude1"])).Rows.ToDictionary(x => x.Configuration);
        Assert.Equal(10.0, again["Short"].Values["D1@Boss-Extrude1"]);
        Assert.Equal(20.0, again[start].Values["D1@Boss-Extrude1"]);
        Assert.Equal(30.0, again["Long"].Values["D1@Boss-Extrude1"]);
    }

    [Fact]
    public async Task A_table_that_breaks_a_feature_is_undone_completely()
    {
        string doc = await HoledBlockAsync("cfg_undo");
        await new PartFeatureService(S).ShellAsync(new ShellRequest { Doc = doc, RemoveFaces = [new EntityRef { Type = "face", At = [30, 20, 10] }], ThicknessMm = 2 });
        string start = (await P.ConfigListAsync(doc)).Active;
        var before = (await P.ConfigListAsync(doc)).Configurations.Count;

        var e = await Assert.ThrowsAsync<SwException>(() => P.ConfigTableAsync(doc,
        [
            Row(start, ("D1@Boss-Extrude1", 25)),
            Row("TooThick", ("D1@Shell1", 40)), // a 40 mm wall in a 20 mm block fails (a too-big fillet would just overflow)
        ]));
        Assert.Equal(ErrorCodes.RebuildError, e.Code);
        Assert.Contains("Nothing was kept", e.Message);
        var list = await P.ConfigListAsync(doc);
        Assert.Equal(before, list.Configurations.Count);
        Assert.Equal(start, list.Active);
        var t = await P.ConfigTableAsync(doc, null, ["D1@Boss-Extrude1", "D1@Shell1"]);
        Assert.Equal(20.0, t.Rows.Single().Values["D1@Boss-Extrude1"]);
        Assert.Equal(2.0, t.Rows.Single().Values["D1@Shell1"]);

        // Bad input is refused before anything changes.
        Assert.Equal(ErrorCodes.EntityNotFound, (await Assert.ThrowsAsync<SwException>(() => P.ConfigTableAsync(doc, [Row("X", ("D9@Nothing", 5))]))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => P.ConfigTableAsync(doc, [Row("X", ("$STATE@Shell1", "maybe"))]))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => P.ConfigTableAsync(doc, [Row("X", ("$COLOR", "red"))]))).Code);
        Assert.Equal(before, (await P.ConfigListAsync(doc)).Configurations.Count);
    }

    [Fact]
    public async Task Display_states_are_added_activated_and_deleted()
    {
        string doc = await HoledBlockAsync("cfg_ds");
        var list = await P.ConfigListAsync(doc);
        string first = Assert.Single(list.DisplayStates!);

        var a = await P.DisplayStateAsync(doc, "add", "SwmcpShaded");
        Assert.Equal("SwmcpShaded", a.ActiveDisplayState);
        Assert.Equal(first, a.PreviousDisplayState);
        var b = await P.DisplayStateAsync(doc, "add", "SwmcpWire", activate: false);
        Assert.Equal("SwmcpShaded", b.ActiveDisplayState);
        Assert.Equal(3, b.DisplayStates!.Count);

        var c = await P.DisplayStateAsync(doc, "activate", "swmcpwire");
        Assert.Equal("SwmcpWire", c.ActiveDisplayState);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => P.DisplayStateAsync(doc, "delete", "SwmcpWire"))).Code);
        var d = await P.DisplayStateAsync(doc, "delete", "SwmcpShaded");
        Assert.DoesNotContain("SwmcpShaded", d.DisplayStates!);
        Assert.Equal(ErrorCodes.EntityNotFound, (await Assert.ThrowsAsync<SwException>(() => P.DisplayStateAsync(doc, "activate", "NoSuchState"))).Code);
        Assert.Equal("SwmcpWire", (await P.ConfigListAsync(doc)).DisplayStates![0]);
    }

    [Fact]
    public async Task An_assembly_table_picks_component_configurations_and_suppresses_components()
    {
        string part = await HoledBlockAsync("cfg_comp");
        await P.ConfigTableAsync(part, [Row("Thin", ("D1@Boss-Extrude1", 5)), Row("Thick", ("D1@Boss-Extrude1", 30))]);
        Directory.CreateDirectory(_dir);
        string path = (await Docs.SaveAsync(part, "as", Path.Combine(_dir, $"cfg_comp_{_tag}.sldprt"), false)).SavedTo!;

        var a = await Docs.NewAsync("assembly", "mmgs", null, $"cfg_asm_{_tag}");
        string asm = a.Document.Handle;
        _asms.Add(asm);
        var asmSvc = new AssemblyService(S);
        await asmSvc.InsertAsync(new AsmInsertRequest { Doc = asm, Path = path, OriginMm = [0, 0, 0], Fixed = true });
        await asmSvc.InsertAsync(new AsmInsertRequest { Doc = asm, Path = path, OriginMm = [0, 100, 0], Fixed = true });
        string start = (await P.ConfigListAsync(asm)).Active;
        string c1 = $"cfg_comp_{_tag}-1", c2 = $"cfg_comp_{_tag}-2";

        var r = await P.ConfigTableAsync(asm,
        [
            Row("Light", ($"$CONFIGURATION@{c1}", "Thin"), ($"$STATE@{c2}", "suppressed")),
            Row("Heavy", ($"$CONFIGURATION@cfg_comp_{_tag}<1>", "Thick"), ($"$CONFIGURATION@{c2}", "Thick")),
        ]);
        Assert.Equal(start, r.Active);
        Assert.Equal(Block(5, true), r.Rows[0].VolumeMm3!.Value, V);
        Assert.Equal(2 * Block(30, true), r.Rows[1].VolumeMm3!.Value, V);
        Assert.Equal("Thin", r.Rows[0].Values[$"$CONFIGURATION@{c1}"]);
        Assert.Equal("suppressed", r.Rows[0].Values[$"$STATE@{c2}"]);

        var t = (await P.ConfigTableAsync(asm, null, [$"$CONFIGURATION@{c1}", $"$STATE@{c2}"])).Rows.ToDictionary(x => x.Configuration);
        Assert.Equal("Thick", t["Heavy"].Values[$"$CONFIGURATION@{c1}"]);
        Assert.Equal("unsuppressed", t["Heavy"].Values[$"$STATE@{c2}"]);
        Assert.Equal(ErrorCodes.EntityNotFound, (await Assert.ThrowsAsync<SwException>(() =>
            P.ConfigTableAsync(asm, [Row("Odd", ($"$CONFIGURATION@{c1}", "NoSuchConfiguration"))]))).Code);
    }
}
