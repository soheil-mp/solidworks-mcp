using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Templates chosen per call against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): new documents from the
/// installed templates, a template of the wrong kind refused without a stray document, a missing one refused.
/// </summary>
public class TemplateLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const string Templates = @"C:\ProgramData\SOLIDWORKS\SOLIDWORKS 2025\templates";
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task Parts_and_assemblies_come_from_the_given_templates()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Templates, "Part.PRTDOT")), "The installed SOLIDWORKS 2025 templates are not there.");
        var docs = new DocumentService(S);
        var p = await docs.NewAsync("part", "mmgs", Path.Combine(Templates, "Part.PRTDOT"), $"tpl_part_{_tag}");
        Track(p.Document.Handle);
        Assert.Equal("part", p.Document.Type);
        Assert.Equal("MMGS", p.Document.Units);
        var a = await docs.NewAsync("assembly", "ips", Path.Combine(Templates, "Assembly.ASMDOT"), $"tpl_asm_{_tag}");
        Track(a.Document.Handle);
        Assert.Equal("assembly", a.Document.Type);
        Assert.Equal("IPS", a.Document.Units);

        var titles = (await docs.ListAsync()).Documents.Select(d => d.Title).ToList();
        var e = await Assert.ThrowsAsync<SwException>(() => docs.NewAsync("part", "mmgs", Path.Combine(Templates, "Assembly.ASMDOT"), $"tpl_bad_{_tag}"));
        Assert.Equal(ErrorCodes.InvalidArgument, e.Code);
        Assert.Equal(titles, (await docs.ListAsync()).Documents.Select(d => d.Title).ToList()); // no stray assembly left open
        Assert.Equal(ErrorCodes.IoError, (await Assert.ThrowsAsync<SwException>(() =>
            docs.NewAsync("part", "mmgs", Path.Combine(Templates, "NoSuch.prtdot"), $"tpl_missing_{_tag}"))).Code);
    }
}
