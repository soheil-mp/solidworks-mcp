using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Assembly structure against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): components formed into a
/// subassembly and dissolved again, and a SpeedPak configuration. Oracles: where each component ends up, part quantities.
/// </summary>
public class AssemblyStructureLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_struct_{Guid.NewGuid():N}");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _asms = [];

    private DocumentService Docs => new(S);
    private AssemblyService Asm => new(S);

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

    private async Task<string> BoxAsync(string name, double size)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = size, HeightMm = size }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        Directory.CreateDirectory(_dir);
        return (await Docs.SaveAsync(doc, "as", Path.Combine(_dir, $"{name}_{_tag}.sldprt"), false)).SavedTo!;
    }

    private async Task<(string Asm, string[] Names)> AssemblyAsync(params (string Path, double X)[] parts)
    {
        var a = await Docs.NewAsync("assembly", "mmgs", null, $"struct_asm_{_tag}");
        _asms.Add(a.Document.Handle);
        var names = new List<string>();
        foreach (var (path, x) in parts)
            names.Add((await Asm.InsertAsync(new AsmInsertRequest { Doc = a.Document.Handle, Path = path, OriginMm = [x, 0, 0], Fixed = true })).Component.Name);
        Directory.CreateDirectory(_dir);
        await Docs.SaveAsync(a.Document.Handle, "as", Path.Combine(_dir, $"struct_asm_{_tag}.sldasm"), false);
        return (a.Document.Handle, names.ToArray());
    }

    [Fact]
    public async Task Two_components_form_a_subassembly_and_dissolve_back_with_the_same_parts()
    {
        string small = await BoxAsync("st_small", 20);
        string big = await BoxAsync("st_big", 40);
        var (asm, n) = await AssemblyAsync((small, 0), (small, 50), (big, 120));

        var f = await Asm.StructureAsync(new AsmStructureRequest { Doc = asm, Kind = "form", Components = [n[0], n[1]] });
        Assert.Equal([n[0], n[1]], f.Moved);
        Assert.Contains(f.Checks, c => c.Contains("moved into", StringComparison.Ordinal));
        TestContext.Current.SendDiagnosticMessage(string.Join(" | ", f.Checks));
        Assert.Contains(f.Subassembly, f.TopLevel);
        Assert.Contains(n[2], f.TopLevel);
        Assert.DoesNotContain(n[0], f.TopLevel);
        Assert.Equal(2, f.PartQuantities[$"st_small_{_tag}.sldprt"]);
        Assert.Equal(1, f.PartQuantities[$"st_big_{_tag}.sldprt"]);

        var d = await Asm.StructureAsync(new AsmStructureRequest { Doc = asm, Kind = "dissolve", Components = [f.Subassembly] });
        Assert.DoesNotContain(f.Subassembly, d.TopLevel);
        Assert.Equal(3, d.TopLevel.Count);
        Assert.Equal(2, d.PartQuantities[$"st_small_{_tag}.sldprt"]);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() =>
            Asm.StructureAsync(new AsmStructureRequest { Doc = asm, Kind = "dissolve", Components = [n[2]] }))).Code); // a part, not a subassembly
    }

    [Fact]
    public async Task A_speedpak_configuration_is_added_to_a_saved_assembly()
    {
        string small = await BoxAsync("sp_small", 20);
        var (asm, _) = await AssemblyAsync((small, 0), (small, 50));
        var p = new PropertiesService(S);
        int before = (await p.ConfigListAsync(asm)).Configurations.Count;
        var r = await p.SpeedPakAsync(asm);
        Assert.Contains("SpeedPak", r.Configuration, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before + 1, r.Configurations.Count);
    }
}
