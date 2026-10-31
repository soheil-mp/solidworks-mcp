using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Feature recognition against a live SOLIDWORKS with FeatureWorks (skipped without SWMCP_LIVE_TESTS=1): a block with a
/// hole exported to STEP, imported (one dumb body) and turned back into features. Oracles: feature types, the volume.
/// </summary>
public class RecognitionLiveTests(LiveFixture fx) : LiveTest(fx)
{
    [Fact]
    public async Task An_imported_step_block_with_a_hole_becomes_features_again()
    {
        string doc = await NewPartAsync("fw_src");
        var sk = new SketchService(S);
        var fs = new FeatureService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 80, HeightMm = 50 }]);
        await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 15 });
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [20, 0], DiameterMm = 12 }]);
        await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        double volume = 80 * 50 * 15 - Math.PI * 36 * 15;

        string dir = Path.Combine(Path.GetTempPath(), $"swmcp_fw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "block.step");
            await new ExportService(S).ExportAsync(new ExportRequest { Doc = doc, Path = file });
            var opened = await new OpenService(S).OpenAsync(file, false, null);
            string imp = opened.Document.Handle;
            Track(imp);
            var r = await new RecognitionService(S).RecognizeAsync(imp, ["extrude", "holes"], false);
            Assert.True(r.Recognized >= 1);
            Assert.Equal(volume, r.VolumeMm3, 0.1);
            Assert.Contains(r.Features, f => f.Type is "Extrusion" or "Boss" or "BaseExtrude" || f.Type.Contains("Extru", StringComparison.Ordinal));
            var list = await new FeatureManagementService(S).DimensionListAsync(imp);
            Assert.NotEmpty(list.Dimensions); // editable again
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }
}
