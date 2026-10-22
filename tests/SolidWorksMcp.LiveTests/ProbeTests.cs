using SolidWorksMcp.Core;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>Maintenance helpers for developers, run explicitly with -class (they skip in normal runs).</summary>
public class ProbeTests(LiveFixture fx) : LiveTest(fx)
{
    /// <summary>Closes leftover documents named in SWMCP_PROBE_CLOSE (comma list) — only for debris created by probes.</summary>
    [Fact]
    public async Task Close_probe_leftovers()
    {
        var names = Environment.GetEnvironmentVariable("SWMCP_PROBE_CLOSE");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(names), "Set SWMCP_PROBE_CLOSE to the titles of probe leftovers to close.");
        var open = await TestHooks.OpenTitlesAsync(S);
        foreach (var n in names!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (open.Contains(n)) await TestHooks.CloseRawAsync(S, n);
        var after = await TestHooks.OpenTitlesAsync(S);
        Assert.DoesNotContain(after, t => names.Contains(t));
    }
}
