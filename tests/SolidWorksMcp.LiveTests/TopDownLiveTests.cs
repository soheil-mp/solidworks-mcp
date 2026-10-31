using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Top-down design against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1): parts made in an assembly (empty in its
/// frame, or in context of a face, following it when it moves), bodies saved as parts with an assembly, and derived parts
/// (inserted, mirrored). Oracles: volumes, assembly-space boxes. Files live in a fresh temp folder.
/// </summary>
public class TopDownLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double B = 0.01; // mm: boxes (padded on curved bodies)
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_td_{Guid.NewGuid():N}");
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<string> _asms = [];

    private TopDownService Td => new(S);
    private AssemblyService Asm => new(S);
    private DocumentService Docs => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);

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

    private async Task<string> SaveAsync(string doc, string name, string ext = ".sldprt")
    {
        Directory.CreateDirectory(_dir);
        return (await Docs.SaveAsync(doc, "as", Path.Combine(_dir, $"{name}_{_tag}{ext}"), false)).SavedTo!;
    }

    /// <summary>A rectangle on the Top plane extruded <paramref name="h"/> up (y 0..h).</summary>
    private async Task Block(string doc, double[][] corners, double h, bool merge = true)
    {
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = corners }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = h, Merge = merge });
    }

    /// <summary>A saved 40 × 20 box, 10 high (x ±20, y 0..10, z ±10) with a Ø6 hole through its centre (7 717.3 mm³).</summary>
    private async Task<(string Doc, string Path)> BoxAsync(string name)
    {
        string doc = await NewPartAsync(name);
        await Block(doc, [[-20, -10], [20, 10]], 10);
        await Sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], DiameterMm = 6 }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, Cut = true, End = "through_all" });
        return (doc, await SaveAsync(doc, name));
    }

    private async Task<string> AssemblyAsync(string name, params string[] parts)
    {
        var r = await Docs.NewAsync("assembly", "mmgs", null, $"{name}_{_tag}");
        _asms.Add(r.Document.Handle);
        foreach (var p in parts) await Asm.InsertAsync(new AsmInsertRequest { Doc = r.Document.Handle, Path = p, OriginMm = [0, 0, 0], Fixed = true });
        await SaveAsync(r.Document.Handle, name, ".sldasm");
        return r.Document.Handle;
    }

    private async Task<BoxMm> BoxOf(string asm, string component) =>
        (await Asm.ComponentListAsync(asm, false, 100, 0)).Components.Single(c => c.Name == component).Box!;

    [Fact]
    public async Task An_in_context_cover_is_the_faces_outline_extruded_and_follows_the_face_when_the_box_grows()
    {
        var box = await BoxAsync("td_box");
        string asm = await AssemblyAsync("td_asm", box.Path);
        var r = await Td.PartNewAsync(new AsmPartNewRequest { Doc = asm, Name = $"td_cover_{_tag}", Face = new EntityRef { Type = "face", At = [10, 10, 5] }, DepthMm = 3 });
        Assert.True(r.InContext);
        Assert.Equal((800 - Math.PI * 9) * 3, r.VolumeMm3!.Value, 1e-2); // the hole comes along
        Assert.NotEmpty(r.ExternalReferences!);
        Assert.True(File.Exists(r.Path), r.Path);
        var cover = await BoxOf(asm, r.Component);
        Assert.Equal(10, cover.Min[1], B);
        Assert.Equal(13, cover.Max[1], B);
        Assert.Equal(-20, cover.Min[0], B);
        Assert.Equal(20, cover.Max[0], B);

        // The box grows to 15 mm: the cover moves up with its face and keeps its shape.
        await new FeatureManagementService(S).DimensionSetAsync(box.Doc, [new DimensionValue { Name = "D1@Boss-Extrude1", Value = 15 }]);
        await new FeatureManagementService(S).RebuildAsync(asm, force: true);
        var moved = await BoxOf(asm, r.Component);
        Assert.Equal(15, moved.Min[1], B);
        Assert.Equal(18, moved.Max[1], B);

        // A curved face is refused, and nothing is added.
        int count = (await Asm.ComponentListAsync(asm, false, 100, 0)).Components.Count;
        var curved = await Assert.ThrowsAsync<SwException>(() => Td.PartNewAsync(new AsmPartNewRequest
        {
            Doc = asm, Name = $"td_bad_{_tag}", Face = new EntityRef { Type = "face", At = [3, 5, 0] }, DepthMm = 3,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, curved.Code);
        Assert.Equal(count, (await Asm.ComponentListAsync(asm, false, 100, 0)).Components.Count);
    }

    [Fact]
    public async Task An_empty_new_part_has_the_assemblys_frame_so_what_is_modelled_in_it_lands_in_assembly_coordinates()
    {
        var box = await BoxAsync("td_frame_box");
        string asm = await AssemblyAsync("td_frame_asm", box.Path);
        var r = await Td.PartNewAsync(new AsmPartNewRequest { Doc = asm, Name = $"td_new_{_tag}" });
        Assert.False(r.InContext);
        Track(r.PartDoc!);
        await Block(r.PartDoc!, [[30, -5], [40, 5]], 4);
        await new FeatureManagementService(S).RebuildAsync(asm, force: true);
        var b = await BoxOf(asm, r.Component);
        Assert.Equal([30, 0, -5], b.Min.Select(v => Math.Round(v, 3) + 0.0).ToArray());
        Assert.Equal([40, 4, 5], b.Max.Select(v => Math.Round(v, 3) + 0.0).ToArray());
        Assert.Equal(ErrorCodes.IoError, (await Assert.ThrowsAsync<SwException>(() => Td.PartNewAsync(new AsmPartNewRequest { Doc = asm, Name = $"td_new_{_tag}" }))).Code);
    }

    [Fact]
    public async Task Saved_bodies_become_parts_with_their_volumes_and_an_assembly_of_them()
    {
        string doc = await NewPartAsync("td_multi");
        await Block(doc, [[-20, -10], [20, 10]], 10);                // 8 000 mm³
        await Block(doc, [[50, -10], [70, 10]], 10, merge: false);    // 4 000 mm³, a second body
        string path = await SaveAsync(doc, "td_multi");
        var r = await Td.BodySaveAsync(new BodySaveRequest { Doc = doc, Assembly = $"td_bodies_{_tag}" });
        Assert.Equal(2, r.Files.Count);
        Assert.Equal([8000.0, 4000.0], r.Files.Select(f => Math.Round(f.VolumeMm3, 3)).ToArray());
        Assert.All(r.Files, f => Assert.True(File.Exists(f.Path), f.Path));
        Assert.StartsWith(Path.GetFileNameWithoutExtension(path) + "_", Path.GetFileName(r.Files[0].Path), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(r.Assembly), r.Assembly);
        Assert.Contains(r.Checks, c => c.StartsWith("assembly of the 2 parts", StringComparison.Ordinal));
        // The new parts have the master's units, not the default template's (no units prompt either).
        var part = await new OpenService(S).OpenAsync(r.Files[1].Path, true, null);
        Track(part.Document.Handle);
        Assert.Equal("MMGS", part.Document.Units);
        // A name whose file exists is refused before anything is written; so is a list of the wrong length.
        string taken = Path.GetFileNameWithoutExtension(r.Files[0].Path);
        Assert.Equal(ErrorCodes.IoError, (await Assert.ThrowsAsync<SwException>(() => Td.BodySaveAsync(new BodySaveRequest { Doc = doc, Names = [taken, "td_other"] }))).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Td.BodySaveAsync(new BodySaveRequest { Doc = doc, Names = ["one"] }))).Code);
        Assert.False(File.Exists(Path.Combine(_dir, "td_other.sldprt")));
    }

    [Fact]
    public async Task A_mirrored_part_keeps_the_volume_and_reflects_the_box_and_an_inserted_part_adds_its_volume()
    {
        string doc = await NewPartAsync("td_hand");
        await Block(doc, [[0, -10], [40, 10]], 10); // x 0..40: not symmetric about the Right plane
        string path = await SaveAsync(doc, "td_hand");
        var m = await Td.DeriveAsync(new PartDeriveRequest { Doc = doc, Kind = "mirror", Plane = new EntityRef { Name = "right" } });
        Track(m.NewDoc!);
        Assert.Equal(8000, m.VolumeMm3, 1e-3);
        Assert.Equal(Path.Combine(_dir, $"td_hand_{_tag}_mirror.sldprt"), m.Path, ignoreCase: true);
        Assert.True(File.Exists(m.Path));
        Assert.Contains(m.Checks, c => c.StartsWith("the box is reflected about the plane along x", StringComparison.Ordinal));
        var mirrored = await new MeasureService(S).MassAsync(m.NewDoc, null);
        Assert.Equal(-20, mirrored.CenterOfMassMm[0], 1e-6); // the original's is at +20

        string host = await NewPartAsync("td_host");
        await Block(host, [[-30, -5], [-20, 5]], 5); // 500 mm³, clear of the inserted block
        var ins = await Td.DeriveAsync(new PartDeriveRequest { Doc = host, Kind = "insert", Path = path });
        Assert.Equal(8500, ins.VolumeMm3, 1e-3);
        Assert.Equal(2, ins.Bodies);
        Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Td.DeriveAsync(new PartDeriveRequest { Doc = host, Kind = "insert" }))).Code);
    }
}
