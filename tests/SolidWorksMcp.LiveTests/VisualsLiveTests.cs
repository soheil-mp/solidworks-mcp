using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Section views in screenshots against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1). Oracles: the sectioned
/// image differs from the plain one, and the plain one is the same before and after.
/// </summary>
public class VisualsLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private ViewService V => new(S);

    private async Task<string> HoledBlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        var fs = new FeatureService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 20 }]);
        await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        return doc;
    }

    [Fact]
    public async Task A_section_cuts_the_image_and_is_removed_afterwards()
    {
        string doc = await HoledBlockAsync("vis_section");
        var plain = await V.ScreenshotAsync(doc, "iso", true, "shaded_edges", true, false, 512, null, false);
        var cut = await V.ScreenshotAsync(doc, "iso", true, "shaded_edges", true, false, 512, null, false,
            new EntityRef { Name = "front" }, 0, false);
        Assert.NotNull(cut.Section);
        Assert.NotEqual(plain.Png, cut.Png);
        var again = await V.ScreenshotAsync(doc, "iso", true, "shaded_edges", true, false, 512, null, false);
        Assert.Equal(plain.Png, again.Png); // the section is gone
        await Assert.ThrowsAsync<SwException>(() => V.ScreenshotAsync(doc, "iso", true, "shaded_edges", true, false, 512, null, false,
            new EntityRef { Type = "face", At = [0, 10, 10] }, 0, false)); // the hole's face isn't planar
    }
}
