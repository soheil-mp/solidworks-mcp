using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Standard hardware against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): library parts from the ISO tables
/// (their volumes are the oracle) and screws fastened into plates (placements, no interference, BOM quantities). The
/// library and the plates live in a fresh temp folder.
/// </summary>
public class HardwareLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_hw_{Guid.NewGuid():N}");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _asms = [];

    private HardwareService Hw => new(S);
    private AssemblyService Asm => new(S);
    private DocumentService Docs => new(S);
    private string Library => Path.Combine(_dir, "lib");

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

    /// <summary>
    /// A 60 × 40 plate, <paramref name="t"/> thick (y 0..t), with Ø<paramref name="d"/> holes at x = ±20, z = 0: through, or
    /// blind <paramref name="depth"/> deep from the top. Saved; returns its path.
    /// </summary>
    private async Task<string> PlateAsync(string name, double t, double d, double? depth = null)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        var fs = new FeatureService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 60, HeightMm = 40 }]);
        await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = t });
        await sk.StartAsync(doc, new EntityRef { Type = "face", At = [0, t, 10] }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [20, 0], DiameterMm = d }, new SketchEntity { Kind = "circle", Center = [-20, 0], DiameterMm = d }]);
        var cut = await fs.ExtrudeAsync(depth is double dd
            ? new ExtrudeRequest { Doc = doc, Cut = true, DepthMm = dd }
            : new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        Assert.Equal(-2 * Math.PI / 4 * d * d * (depth ?? t), cut.Verified.VolumeDeltaMm3, 0.01);
        Directory.CreateDirectory(_dir);
        return (await Docs.SaveAsync(doc, "as", Path.Combine(_dir, $"{name}_{_tag}.sldprt"), false)).SavedTo!;
    }

    private async Task<string> AssemblyWithAsync(string plate)
    {
        var r = await Docs.NewAsync("assembly", "mmgs", null, $"hw_asm_{_tag}");
        _asms.Add(r.Document.Handle);
        await Asm.InsertAsync(new AsmInsertRequest { Doc = r.Document.Handle, Path = plate, OriginMm = [0, 0, 0], Fixed = true });
        return r.Document.Handle;
    }

    [Fact]
    public async Task Standard_parts_have_the_volumes_of_their_iso_tables_and_are_reused_from_the_library()
    {
        var titles = await TestHooks.OpenTitlesAsync(S);
        var screw = await Hw.PartAsync(new HardwarePartRequest { Standard = "ISO 4762", Size = "M5", LengthMm = 20, Folder = Library });
        Assert.True(screw.Created);
        Assert.Equal(Path.Combine(Library, "ISO 4762 M5x20.sldprt"), screw.Path, ignoreCase: true);
        Assert.True(File.Exists(screw.Path));
        Assert.Equal(HardwareCatalog.Resolve("ISO 4762", "M5", 20).VolumeMm3, screw.VolumeMm3, 1e-3);
        Assert.Equal(screw.VolumeMm3 * 7.7e-6, screw.MassKg!.Value, 1e-4); // Alloy Steel, 7700 kg/m³
        Assert.Equal("ISO 4762 - M5 x 20", screw.PartNo);
        Assert.Equal(titles, await TestHooks.OpenTitlesAsync(S)); // made, saved and closed again

        var again = await Hw.PartAsync(new HardwarePartRequest { Standard = "iso4762", Size = "m5", LengthMm = 20, Folder = Library });
        Assert.False(again.Created);
        Assert.Equal(screw.VolumeMm3, again.VolumeMm3, 1e-3);

        foreach (var (std, size, len) in new (string, string, double?)[] { ("ISO 4017", "M8", 30), ("ISO 4032", "M5", null), ("ISO 7089", "M5", null), ("ISO 8734", "6", 20), ("ISO 15", "6204", null) })
        {
            var r = await Hw.PartAsync(new HardwarePartRequest { Standard = std, Size = size, LengthMm = len, Folder = Library });
            Assert.True(r.Created, std);
            Assert.Equal(HardwareCatalog.Resolve(std, size, len).VolumeMm3, r.VolumeMm3, 1e-3);
        }
        // A length that doesn't exist is refused before anything is made.
        var odd = await Assert.ThrowsAsync<SwException>(() => Hw.PartAsync(new HardwarePartRequest { Standard = "ISO 4762", Size = "M5", LengthMm = 21, Folder = Library }));
        Assert.Equal(ErrorCodes.InvalidArgument, odd.Code);
        Assert.False(File.Exists(Path.Combine(Library, "ISO 4762 M5x21.sldprt")));
    }

    [Fact]
    public async Task Screws_with_washers_and_nuts_go_through_the_plate_seated_with_no_interference_and_the_bom_counts_them()
    {
        string plate = await PlateAsync("hw_plate", 10, 5.5);
        string asm = await AssemblyWithAsync(plate);
        var r = await Hw.FastenAsync(new AsmFastenerRequest
        {
            Doc = asm, Size = "M5", Holes = [[20, 10, 0], [-20, 10, 0]], Washer = true, Nut = true, Folder = Library,
        });
        // 10 grip + 2 × 1 washers + 4.7 nut + 2 × 0.8 = 18.3: the next length is 20.
        Assert.Equal("ISO 4762 - M5 x 20", r.Screw);
        Assert.Equal(9, r.ComponentCount);
        Assert.Equal(16, r.Mates);
        Assert.All(r.Holes, h =>
        {
            Assert.True(h.Through);
            Assert.Equal(10, h.GripMm, 1e-6);
            Assert.Equal(20 - 1 - 10 - 1 - 4.7, h.ProtrusionMm!.Value, 1e-6);
            Assert.Equal(3, h.Others!.Count);
        });
        var comps = (await Asm.ComponentListAsync(asm, false, 100, 0)).Components;
        var screwAt = comps.Where(c => c.Name.StartsWith("ISO 4762 M5x20", StringComparison.OrdinalIgnoreCase)).Select(c => c.OriginMm!).OrderBy(o => o[0]).ToList();
        Assert.Equal(2, screwAt.Count);
        Assert.Equal([-20, 11, 0], screwAt[0].Select(v => Math.Round(v, 6) + 0.0).ToArray()); // on the 1 mm washer on the 10 mm plate
        var nutAt = comps.Where(c => c.Name.StartsWith("ISO 4032 M5", StringComparison.OrdinalIgnoreCase)).Select(c => c.OriginMm!).OrderBy(o => o[0]).ToList();
        Assert.Equal([20, -1, 0], nutAt[1].Select(v => Math.Round(v, 6) + 0.0).ToArray()); // under the lower washer

        var clash = await Asm.InterferenceAsync(asm);
        Assert.Equal(0, clash.Count);
        var bom = await Asm.BomAsync(new AsmBomRequest { Doc = asm });
        Assert.Equal(2, bom.Rows.Single(x => x.PartNumber == "ISO 4762 - M5 x 20").Quantity);
        Assert.Equal(4, bom.Rows.Single(x => x.PartNumber == "ISO 7089 - M5").Quantity);
        Assert.Equal(2, bom.Rows.Single(x => x.PartNumber == "ISO 4032 - M5").Quantity);
        Assert.Equal("Hexagon socket head cap screw ISO 4762 - M5 x 20", bom.Rows.Single(x => x.PartNumber == "ISO 4762 - M5 x 20").Description);
    }

    [Fact]
    public async Task A_screw_in_a_blind_hole_reports_its_tip_clearance_and_bad_requests_change_nothing()
    {
        string plate = await PlateAsync("hw_blind", 20, 4.2, depth: 15);
        string asm = await AssemblyWithAsync(plate);
        async Task<string> Refused(AsmFastenerRequest q) => (await Assert.ThrowsAsync<SwException>(() => Hw.FastenAsync(q))).Code;
        Assert.Equal(ErrorCodes.EntityNotFound, await Refused(new AsmFastenerRequest { Doc = asm, Size = "M5", LengthMm = 12, Holes = [[0, 20, 0]], Folder = Library }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new AsmFastenerRequest { Doc = asm, Size = "M5", LengthMm = 12, Holes = [[20, 20, 0]], Nut = true, Folder = Library }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new AsmFastenerRequest { Doc = asm, Size = "M5", Holes = [[20, 20, 0]], Folder = Library }));
        Assert.Equal(ErrorCodes.InvalidArgument, await Refused(new AsmFastenerRequest { Doc = asm, Standard = "ISO 4032", Size = "M5", Holes = [[20, 20, 0]], Folder = Library }));
        Assert.Single((await Asm.ComponentListAsync(asm, false, 100, 0)).Components);

        var r = await Hw.FastenAsync(new AsmFastenerRequest { Doc = asm, Size = "M5", LengthMm = 12, Holes = [[20, 20, 0]], Folder = Library });
        var h = Assert.Single(r.Holes);
        Assert.False(h.Through);
        Assert.Equal(15, h.GripMm, 1e-6);
        Assert.Equal(3, h.TipClearanceMm!.Value, 1e-6);
        Assert.Contains(r.Checks, c => c.Contains("smaller than the screw", StringComparison.Ordinal));
        Assert.Equal(2, r.ComponentCount);
    }
}
