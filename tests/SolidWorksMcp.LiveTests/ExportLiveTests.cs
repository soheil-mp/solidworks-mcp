using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// model_export and doc_open against a live SOLIDWORKS (skipped without SWMCP_LIVE_TESTS=1). Oracles: the
/// 100 x 20 x 60 block (V = 120 000 mm³, 12 triangles), STEP schemas per AP, the STL size formula, and STEP round trips.
/// The LiveTest base also proves that every allow-listed system preference (STEP AP, STL options, import options,
/// rebuild-on-activation) is unchanged after each test.
/// </summary>
public class ExportLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double V = 1e-3; // mm³ tolerance for planar oracles
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_export_{Guid.NewGuid():N}");
    private readonly List<string> _opened = [];
    private readonly List<string> _backups = [];

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Directory.CreateDirectory(_dir);
    }

    public override async ValueTask DisposeAsync()
    {
        if (LiveFixture.Enabled)
        {
            foreach (var h in _opened)
            {
                try { await new DocumentService(S).CloseAsync(h, save: false, discard: true); }
                catch (SwException) { /* closed by the test */ }
            }
        }
        try { await base.DisposeAsync(); }
        finally
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
            foreach (var b in _backups)
            {
                try
                {
                    File.Delete(b);
                    if (Path.GetDirectoryName(b) is { } d && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch (IOException) { /* best effort */ }
            }
        }
    }

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

    /// <summary>An r 10 × 10 cylinder standing on the top plane (V = 1000π mm³).</summary>
    private async Task<string> CylinderAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "circle", Center = [0, 0], RadiusMm = 10 }]);
        await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        return doc;
    }

    private Task<ExportResult> Export(string doc, string file, int stepAp = 214, bool stlBinary = true, string stlQuality = "fine",
        double? stlDeviationMm = null, double? stlAngleDeg = null, bool overwrite = false, string? format = null) =>
        new ExportService(S).ExportAsync(new ExportRequest
        {
            Doc = doc, Path = Path.IsPathRooted(file) ? file : Path.Combine(_dir, file), Format = format, StepAp = stepAp,
            StlBinary = stlBinary, StlQuality = stlQuality, StlDeviationMm = stlDeviationMm, StlAngleDeg = stlAngleDeg, Overwrite = overwrite,
        });

    private async Task<OpenResult> Open(string file, bool readOnly = false, string? configuration = null)
    {
        var r = await new OpenService(S).OpenAsync(file, readOnly, configuration);
        if (r.AlreadyOpen != true) _opened.Add(r.Document.Handle);
        return r;
    }

    private static void FreshFile(ExportResult r)
    {
        var fi = new FileInfo(r.Path);
        Assert.True(fi.Exists, $"{r.Path} does not exist");
        Assert.Equal(fi.Length, r.SizeBytes);
        Assert.True(fi.Length > 0);
        Assert.Contains(r.Details.Checks!, c => c.StartsWith("new file written by this call", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ STEP

    [Fact]
    public async Task Step_ap214_writes_automotive_design_and_restores_the_users_ap()
    {
        string doc = await BlockAsync("step214");
        var before = await Fx.PreferencesAsync();
        var r = await Export(doc, "block.step");
        FreshFile(r);
        Assert.Equal("step", r.Format);
        Assert.StartsWith("AUTOMOTIVE_DESIGN", r.Details.Schema);
        Assert.Equal(214, r.Details.StepAp);
        Assert.Equal(1, r.Details.Solids);
        Assert.StartsWith("ISO-10303-21;", File.ReadAllText(r.Path).TrimStart());
        var after = await Fx.PreferencesAsync();
        Assert.Equal(before["Integer75"], after["Integer75"]); // the user's STEP AP is back
    }

    [Theory]
    [InlineData(203, "CONFIG_CONTROL_DESIGN")]
    [InlineData(242, "AP242")]
    public async Task Step_schema_matches_the_requested_ap(int ap, string schemaPrefix)
    {
        string doc = await BlockAsync($"step{ap}");
        var r = await Export(doc, $"block{ap}.stp", stepAp: ap);
        FreshFile(r);
        Assert.StartsWith(schemaPrefix, r.Details.Schema);
        Assert.Equal(ap, r.Details.StepAp);
        Assert.Contains($"STEP AP{ap}", r.Settings!);
    }

    // ------------------------------------------------------------------ STL

    [Fact]
    public async Task Stl_binary_size_is_84_plus_50_per_triangle_and_the_real_name_is_reported()
    {
        string doc = await BlockAsync("stl");
        var r = await Export(doc, "block.stl");
        FreshFile(r);
        Assert.True(r.Details.Binary);
        Assert.Equal(12, r.Details.Triangles);
        Assert.Equal(84 + 50 * 12, r.SizeBytes);
        Assert.Equal(".stl", Path.GetExtension(r.Path), ignoreCase: true);
        // SOLIDWORKS writes ".STL": the result names the file exactly as it is on disk.
        Assert.Equal(Path.GetFileName(Directory.GetFiles(_dir, "block.stl").Single()), Path.GetFileName(r.Path));
        Assert.NotNull(r.Details.Units);
        Assert.Equal(120000, r.Details.VolumeMm3!.Value, 0.5);
        Assert.Equal(100, r.Details.SizeMm![0], 1e-3);
        Assert.Equal(20, r.Details.SizeMm[1], 1e-3);
        Assert.Equal(60, r.Details.SizeMm[2], 1e-3);
    }

    [Fact]
    public async Task Stl_ascii_coarse_starts_with_solid()
    {
        string doc = await BlockAsync("stl_ascii");
        var r = await Export(doc, "block_ascii.stl", stlBinary: false, stlQuality: "coarse");
        FreshFile(r);
        Assert.False(r.Details.Binary);
        Assert.Equal(12, r.Details.Triangles);
        Assert.StartsWith("solid", File.ReadAllText(r.Path).TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STL ASCII", r.Settings!);
    }

    [Fact]
    public async Task Stl_custom_resolution_is_finer_than_coarse_and_close_to_the_true_volume()
    {
        string doc = await CylinderAsync("stl_custom");
        var coarse = await Export(doc, "cyl_coarse.stl", stlQuality: "coarse");
        var custom = await Export(doc, "cyl_custom.stl", stlQuality: "custom", stlDeviationMm: 0.005, stlAngleDeg: 2);
        Assert.True(custom.Details.Triangles > coarse.Details.Triangles,
            $"custom {custom.Details.Triangles} triangles, coarse {coarse.Details.Triangles}");
        double v = Math.PI * 100 * 10;
        // An inscribed facetted cylinder is slightly smaller than the true one.
        Assert.InRange(custom.Details.VolumeMm3!.Value, v * 0.995, v * 1.0001);
        Assert.Contains(custom.Settings!, s => s.StartsWith("STL quality custom", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ other formats

    [Fact]
    public async Task Iges_parasolid_and_3mf_have_their_signatures()
    {
        string doc = await BlockAsync("formats");
        var igs = await Export(doc, "block.igs");
        FreshFile(igs);
        Assert.Equal("IGES", igs.Details.Header);
        Assert.True(igs.Details.Entities > 0);

        var xt = await Export(doc, "block.x_t");
        FreshFile(xt);
        Assert.Equal("parasolid", xt.Format);
        Assert.StartsWith("Parasolid", xt.Details.Header);

        var mf = await Export(doc, "block.3mf");
        FreshFile(mf);
        Assert.Equal("3MF (ZIP)", mf.Details.Header);
        Assert.Equal(12, mf.Details.Triangles);
    }

    [Fact]
    public async Task Images_pdf_and_edrawings_are_written()
    {
        string doc = await BlockAsync("images");
        var png = await Export(doc, "block.png");
        FreshFile(png);
        Assert.Equal("PNG", png.Details.Header);
        Assert.True(png.Details.WidthPx > 100 && png.Details.HeightPx > 100);

        var jpg = await Export(doc, "block.jpg");
        FreshFile(jpg);
        Assert.Equal("JPEG", jpg.Details.Header);
        Assert.True(jpg.Details.WidthPx > 100 && jpg.Details.HeightPx > 100);

        var pdf = await Export(doc, "block.pdf");
        FreshFile(pdf);
        Assert.Equal("PDF", pdf.Details.Header);

        var e = await Export(doc, "block", format: "eprt"); // the extension follows the document type
        FreshFile(e);
        Assert.Equal("edrawings", e.Format);
        Assert.EndsWith(".eprt", e.Path, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ overwrite policy

    [Fact]
    public async Task Existing_files_need_overwrite_and_our_own_files_get_no_backup()
    {
        string doc = await BlockAsync("overwrite");
        var first = await Export(doc, "same.step");
        var ex = await Assert.ThrowsAsync<SwException>(() => Export(doc, "same.step"));
        Assert.Equal(ErrorCodes.IoError, ex.Code);
        Assert.Contains("overwrite", ex.Hint);
        var second = await Export(doc, "same.step", overwrite: true);
        FreshFile(second);
        Assert.True(second.Replaced);
        Assert.Null(second.BackupPath);
        Assert.Equal(first.Path, second.Path, ignoreCase: true);
    }

    [Fact]
    public async Task Overwriting_a_file_the_server_did_not_write_backs_it_up_first()
    {
        string doc = await BlockAsync("backup");
        string file = Path.Combine(_dir, "users.step");
        await File.WriteAllTextAsync(file, "the user's own file");
        var r = await Export(doc, file, overwrite: true);
        FreshFile(r);
        Assert.NotNull(r.BackupPath);
        _backups.Add(r.BackupPath!);
        Assert.Equal("the user's own file", await File.ReadAllTextAsync(r.BackupPath!));
        Assert.StartsWith("ISO-10303-21;", (await File.ReadAllTextAsync(r.Path)).TrimStart());
    }

    // ------------------------------------------------------------------ export errors

    [Fact]
    public async Task Drawing_formats_need_a_drawing()
    {
        string doc = await BlockAsync("dwg");
        var ex = await Assert.ThrowsAsync<SwException>(() => Export(doc, "block.dwg"));
        Assert.Equal(ErrorCodes.WrongDocType, ex.Code);
        Assert.False(File.Exists(Path.Combine(_dir, "block.dwg")));
    }

    [Fact]
    public async Task An_empty_part_has_nothing_to_export()
    {
        string doc = await NewPartAsync("empty");
        var ex = await Assert.ThrowsAsync<SwException>(() => Export(doc, "empty.step"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.False(File.Exists(Path.Combine(_dir, "empty.step")));
    }

    [Fact]
    public async Task An_open_sketch_blocks_model_exports()
    {
        string doc = await BlockAsync("sketch_open");
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        try
        {
            var ex = await Assert.ThrowsAsync<SwException>(() => Export(doc, "open.step"));
            Assert.Equal(ErrorCodes.SketchState, ex.Code);
        }
        finally { await sk.FinishAsync(doc); }
    }

    [Fact]
    public async Task Paths_outside_the_allowed_folders_are_refused()
    {
        string doc = await BlockAsync("forbidden");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ex = await Assert.ThrowsAsync<SwException>(() => Export(doc, Path.Combine(windows, "swmcp_forbidden.step")));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
    }

    // ------------------------------------------------------------------ doc_open

    [Fact]
    public async Task A_saved_part_reopens_as_owned_target_with_its_geometry()
    {
        string doc = await BlockAsync("reopen");
        string file = Path.Combine(_dir, "reopen_copy.sldprt");
        await new DocumentService(S).SaveAsync(doc, "copy", file, false);
        var r = await Open(file);
        Assert.Null(r.AlreadyOpen);
        Assert.Equal("owned", r.Document.Access);
        Assert.True(r.Document.Target);
        Assert.Equal("part", r.Document.Type);
        Assert.Equal(file, r.Document.Path, ignoreCase: true);
        var info = await new InspectService(S).ModelInfoAsync(r.Document.Handle);
        Assert.Equal(120000, info.VolumeMm3!.Value, V);
        Assert.Equal([100, 20, 60], info.Box!.Size);
    }

    [Fact]
    public async Task A_read_only_open_is_reported()
    {
        string doc = await BlockAsync("readonly");
        string file = Path.Combine(_dir, "readonly_copy.sldprt");
        await new DocumentService(S).SaveAsync(doc, "copy", file, false);
        var r = await Open(file, readOnly: true);
        Assert.True(r.ReadOnly);
        Assert.Equal("owned", r.Document.Access);
    }

    [Fact]
    public async Task A_file_that_is_already_open_is_not_reopened_and_keeps_its_access()
    {
        string doc = await BlockAsync("already");
        string file = Path.Combine(_dir, "already.sldprt");
        var saved = await new DocumentService(S).SaveAsync(doc, "as", file, false); // the document is now that file
        var r = await Open(file);
        Assert.True(r.AlreadyOpen);
        Assert.Equal(saved.Document.Handle, r.Document.Handle);
        Assert.Equal("owned", r.Document.Access);
        Assert.True(r.Document.Target);
    }

    [Fact]
    public async Task An_exported_step_imports_back_as_one_solid_of_the_same_volume()
    {
        string doc = await BlockAsync("roundtrip");
        var step = await Export(doc, "roundtrip.step");
        var r = await Open(step.Path);
        Assert.NotNull(r.Imported);
        Assert.Equal("step", r.Imported!.Format);
        Assert.Equal(1, r.Imported.SolidBodies);
        Assert.Equal(120000, r.Imported.VolumeMm3!.Value, 0.01);
        Assert.Equal(100, r.Imported.Box!.Size[0], 1e-3);
        Assert.Equal(20, r.Imported.Box.Size[1], 1e-3);
        Assert.Equal(60, r.Imported.Box.Size[2], 1e-3);
        Assert.Equal("MMGS", r.Document.Units);
        Assert.Equal("owned", r.Document.Access);
        Assert.True(r.Document.Target);
        var info = await new InspectService(S).ModelInfoAsync(r.Document.Handle); // the import is the target and readable
        Assert.Equal(120000, info.VolumeMm3!.Value, 0.01);
    }

    [Fact]
    public async Task An_exported_stl_imports_back_with_a_body()
    {
        string doc = await BlockAsync("stl_import");
        var stl = await Export(doc, "stl_import.stl");
        var r = await Open(stl.Path);
        Assert.Equal("stl", r.Imported!.Format);
        Assert.True(r.Imported.SolidBodies + r.Imported.SurfaceBodies + (r.Imported.MeshBodies ?? 0) >= 1);
        Assert.Equal("owned", r.Document.Access);
    }

    [Fact]
    public async Task Open_refuses_missing_files_unknown_types_and_unknown_configurations()
    {
        var svc = new OpenService(S);
        var missing = await Assert.ThrowsAsync<SwException>(() => svc.OpenAsync(Path.Combine(_dir, "nope.sldprt"), false, null));
        Assert.Equal(ErrorCodes.IoError, missing.Code);

        string txt = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(txt, "x");
        var type = await Assert.ThrowsAsync<SwException>(() => svc.OpenAsync(txt, false, null));
        Assert.Equal(ErrorCodes.InvalidArgument, type.Code);

        string doc = await BlockAsync("configs");
        string file = Path.Combine(_dir, "configs.sldprt");
        await new DocumentService(S).SaveAsync(doc, "copy", file, false);
        var cfg = await Assert.ThrowsAsync<SwException>(() => svc.OpenAsync(file, false, "NoSuchConfiguration"));
        Assert.Equal(ErrorCodes.InvalidArgument, cfg.Code);
        Assert.Contains("NoSuchConfiguration", cfg.Message);

        var step = await Export(doc, "configs.step");
        var neutral = await Assert.ThrowsAsync<SwException>(() => svc.OpenAsync(step.Path, false, "Default"));
        Assert.Equal(ErrorCodes.InvalidArgument, neutral.Code);
    }
}

/// <summary>The export verification logic on synthetic files (runs without SOLIDWORKS).</summary>
public class ExportFileCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"swmcp_check_{Guid.NewGuid():N}");

    public ExportFileCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string TextFile(string name, string text) => Write(name, Encoding.ASCII.GetBytes(text));

    private string Write(string name, byte[] bytes)
    {
        string p = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    private static (ExportDetails Details, List<string> Problems) Check(string format, string file, ExportFileCheck.Expect? expect = null)
    {
        var problems = new List<string>();
        var d = ExportFileCheck.Inspect(format, file, expect ?? new ExportFileCheck.Expect(), problems);
        return (d, problems);
    }

    private static string Step(string schema) =>
        "ISO-10303-21;\r\nHEADER;\r\nFILE_DESCRIPTION (( 'STEP AP' ), '1' );\r\n" +
        "FILE_NAME ('block.STEP', '2026-10-22T12:00:00', ( '' ), ( '' ), 'SwSTEP 2.0', 'SolidWorks 2025', '' );\r\n" +
        $"FILE_SCHEMA (( '{schema}' ));\r\nENDSEC;\r\nDATA;\r\n#10 = MANIFOLD_SOLID_BREP ( 'Boss-Extrude1', #20 ) ;\r\nENDSEC;\r\nEND-ISO-10303-21;\r\n";

    [Theory]
    [InlineData("AUTOMOTIVE_DESIGN", 214)]
    [InlineData("AUTOMOTIVE_DESIGN { 1 0 10303 214 1 1 1 1 }", 214)]
    [InlineData("CONFIG_CONTROL_DESIGN", 203)]
    [InlineData("AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF { 1 0 10303 442 1 1 4 }", 242)]
    public void Step_schema_gives_the_application_protocol(string schema, int ap)
    {
        var (d, problems) = Check("step", TextFile("a.step", Step(schema)), new ExportFileCheck.Expect(StepAp: ap));
        Assert.Empty(problems);
        Assert.Equal(ap, d.StepAp);
        Assert.Equal(schema, d.Schema);
        Assert.Equal(1, d.Solids);
    }

    [Fact]
    public void Step_with_another_schema_than_requested_fails()
    {
        var (_, problems) = Check("step", TextFile("b.step", Step("CONFIG_CONTROL_DESIGN")), new ExportFileCheck.Expect(StepAp: 214));
        Assert.Contains(problems, p => p.Contains("not AP214"));
        var (_, notStep) = Check("step", TextFile("c.step", "hello"));
        Assert.Contains(notStep, p => p.Contains("ISO-10303-21"));
    }

    private static string Iges(string data, char section, int seq) => data.PadRight(72) + section + seq.ToString(CultureInfo.InvariantCulture).PadLeft(7);

    [Fact]
    public void Iges_needs_S_in_column_73_and_a_terminate_line()
    {
        string ok = string.Join("\r\n",
            Iges("SolidWorks IGES file using analytic representation for surfaces", 'S', 1),
            Iges("1H,,1H;,9Hblock.IGS,", 'G', 1),
            Iges("     186       1       0       0       0       0       0       000000001", 'D', 1),
            Iges("     186       0       0       1       0                               0", 'D', 2),
            Iges("186,1,1,0;", 'P', 1),
            Iges("S      1G      1D      2P      1", 'T', 1)) + "\r\n";
        var (d, problems) = Check("iges", TextFile("a.igs", ok));
        Assert.Empty(problems);
        Assert.Equal("IGES", d.Header);
        Assert.Equal(1, d.Entities);
        var (_, bad) = Check("iges", TextFile("b.igs", "not an iges file\r\n"));
        Assert.Contains(bad, p => p.Contains("column 73"));
    }

    [Fact]
    public void Parasolid_text_has_the_transmit_header()
    {
        string xt = "**ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz**************************\n" +
                    "**PARASOLID !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~0123456789**************************\n" +
                    "**PART1;\nMC=x86;\nMC_MODEL=x86;\nSCH=SCH_3300174_33003;\n";
        var (d, problems) = Check("parasolid", TextFile("a.x_t", xt));
        Assert.Empty(problems);
        Assert.Equal("SCH_3300174_33003", d.Version);
        var (_, bad) = Check("parasolid", TextFile("b.x_t", "solid"));
        Assert.NotEmpty(bad);
    }

    /// <summary>The 12 outward triangles of the box [x0,x1] × [y0,y1] × [z0,z1].</summary>
    private static List<double[]> Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        double[][] v =
        [
            [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
            [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
        ];
        int[][] t =
        [
            [0, 3, 2], [0, 2, 1], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4],
            [3, 7, 6], [3, 6, 2], [0, 4, 7], [0, 7, 3], [1, 2, 6], [1, 6, 5],
        ];
        return t.Select(i => v[i[0]].Concat(v[i[1]]).Concat(v[i[2]]).ToArray()).ToList();
    }

    private static byte[] BinaryStl(List<double[]> tris, string header = "solid box (binary, but the header says solid)")
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var h = new byte[80];
        Encoding.ASCII.GetBytes(header).CopyTo(h, 0);
        w.Write(h);
        w.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            w.Write(0f); w.Write(0f); w.Write(0f);
            foreach (var c in t) w.Write((float)c);
            w.Write((ushort)0);
        }
        w.Flush();
        return ms.ToArray();
    }

    private static string AsciiStl(List<double[]> tris)
    {
        var sb = new StringBuilder("solid box\n");
        foreach (var t in tris)
        {
            sb.Append(" facet normal 0 0 0\n  outer loop\n");
            for (int i = 0; i < 9; i += 3)
                sb.Append(string.Create(CultureInfo.InvariantCulture, $"   vertex {t[i]} {t[i + 1]} {t[i + 2]}\n"));
            sb.Append("  endloop\n endfacet\n");
        }
        return sb.Append("endsolid box\n").ToString();
    }

    [Fact]
    public void Binary_stl_is_recognised_by_the_size_formula_and_measured_in_mm()
    {
        var tris = Box(-50, 0, -30, 50, 20, 30);
        var (d, problems) = Check("stl", Write("a.stl", BinaryStl(tris)), new ExportFileCheck.Expect(StlBinary: true, ModelSizeMm: [100, 20, 60]));
        Assert.Empty(problems);
        Assert.True(d.Binary);
        Assert.Equal(12, d.Triangles);
        Assert.Equal("mm", d.Units);
        Assert.Equal(120000, d.VolumeMm3!.Value, 1e-3);
        Assert.Equal([100, 20, 60], d.SizeMm!);
    }

    [Fact]
    public void Stl_coordinates_in_meters_are_detected_from_the_model_size()
    {
        var tris = Box(-0.05, 0, -0.03, 0.05, 0.02, 0.03);
        var (d, problems) = Check("stl", Write("m.stl", BinaryStl(tris)), new ExportFileCheck.Expect(StlBinary: true, ModelSizeMm: [100, 20, 60]));
        Assert.Empty(problems);
        Assert.Equal("m", d.Units);
        Assert.Equal(120000, d.VolumeMm3!.Value, 0.5); // single-precision coordinates
    }

    [Fact]
    public void Ascii_stl_counts_facets_and_a_wrong_flavour_fails()
    {
        var tris = Box(0, 0, 0, 10, 10, 10);
        var (d, problems) = Check("stl", TextFile("a.stl", AsciiStl(tris)), new ExportFileCheck.Expect(StlBinary: false, ModelSizeMm: [10, 10, 10]));
        Assert.Empty(problems);
        Assert.False(d.Binary);
        Assert.Equal(12, d.Triangles);
        Assert.Equal(1000, d.VolumeMm3!.Value, 1e-6);

        var (_, wrong) = Check("stl", Write("b.stl", BinaryStl(tris)), new ExportFileCheck.Expect(StlBinary: false));
        Assert.Contains(wrong, p => p.Contains("ASCII was requested"));
        var truncated = BinaryStl(tris, header: "binary")[..500];
        var (_, bad) = Check("stl", Write("c.stl", truncated));
        Assert.Contains(bad, p => p.Contains("not an STL file"));
    }

    [Fact]
    public void Threemf_is_a_zip_with_a_model_part()
    {
        string file = Path.Combine(_dir, "a.3mf");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("[Content_Types].xml").Open())) w.Write("<Types/>");
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<model unit=\"millimeter\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\">" +
                                       "<resources><object id=\"1\" type=\"model\"><mesh><vertices>");
            for (int i = 0; i < 8; i++) sb.Append("<vertex x=\"0\" y=\"0\" z=\"0\" />");
            sb.Append("</vertices><triangles>");
            for (int i = 0; i < 12; i++) sb.Append("<triangle v1=\"0\" v2=\"1\" v3=\"2\" />");
            sb.Append("</triangles></mesh></object></resources><build><item objectid=\"1\" /></build></model>");
            using var m = new StreamWriter(zip.CreateEntry("3D/3dmodel.model").Open());
            m.Write(sb.ToString());
        }
        var (d, problems) = Check("3mf", file);
        Assert.Empty(problems);
        Assert.Equal(12, d.Triangles);
        Assert.Equal("millimeter", d.Units);
        var (_, bad) = Check("3mf", TextFile("b.3mf", "PK but not really"));
        Assert.NotEmpty(bad);
    }

    [Fact]
    public void Png_and_jpeg_report_their_size()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");
        var (p, pp) = Check("png", Write("a.png", png));
        Assert.Empty(pp);
        Assert.Equal(1, p.WidthPx);
        Assert.Equal(1, p.HeightPx);

        byte[] jpeg =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x40, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
            0xFF, 0xD9,
        ];
        var (j, jp) = Check("jpg", Write("a.jpg", jpeg));
        Assert.Empty(jp);
        Assert.Equal(64, j.WidthPx);
        Assert.Equal(32, j.HeightPx);

        var (_, bad) = Check("png", Write("b.png", jpeg));
        Assert.NotEmpty(bad);
    }

    [Fact]
    public void Pdf_dwg_and_dxf_headers()
    {
        var (pdf, pp) = Check("pdf", TextFile("a.pdf", "%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n"));
        Assert.Empty(pp);
        Assert.Equal("1.4", pdf.Version);
        var (_, truncated) = Check("pdf", TextFile("b.pdf", "%PDF-1.4\n1 0 obj\n"));
        Assert.Contains(truncated, p => p.Contains("%%EOF"));

        var (dwg, dp) = Check("dwg", Write("a.dwg", [.. Encoding.ASCII.GetBytes("AC1015"), .. new byte[64]]));
        Assert.Empty(dp);
        Assert.Equal("AC1015 (2000)", dwg.Version);

        var (dxf, xp) = Check("dxf", TextFile("a.dxf", "  0\r\nSECTION\r\n  2\r\nHEADER\r\n  9\r\n$ACADVER\r\n  1\r\nAC1018\r\n  0\r\nENDSEC\r\n  0\r\nEOF\r\n"));
        Assert.Empty(xp);
        Assert.Equal("AC1018 (2004)", dxf.Version);
    }
}

/// <summary>Format inference and option validation of model_export, and doc_open's file kinds (no SOLIDWORKS needed).</summary>
public class ExportRequestTests
{
    private static ExportPlan Plan(string path, string? format = null, int stepAp = 214, string quality = "fine", double? dev = null, double? ang = null) =>
        ExportPlan.Parse(new ExportRequest { Path = path, Format = format, StepAp = stepAp, StlQuality = quality, StlDeviationMm = dev, StlAngleDeg = ang });

    private static void Invalid(Action a, string? contains = null)
    {
        var ex = Assert.Throws<SwException>(a);
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        if (contains is not null) Assert.Contains(contains, ex.Message + " " + ex.Hint);
    }

    [Theory]
    [InlineData("a.step", "step")]
    [InlineData("a.STP", "step")]
    [InlineData("a.igs", "iges")]
    [InlineData("a.x_t", "parasolid")]
    [InlineData("a.stl", "stl")]
    [InlineData("a.3mf", "3mf")]
    [InlineData("a.jpeg", "jpg")]
    [InlineData("a.eprt", "edrawings")]
    [InlineData("a.pdf", "pdf")]
    [InlineData("a.dxf", "dxf")]
    public void The_extension_picks_the_format(string path, string format)
    {
        var p = Plan(path);
        Assert.Equal(format, p.Format.Name);
        Assert.Equal(path, p.FileFor(path, 1));
    }

    [Fact]
    public void A_missing_extension_comes_from_the_format()
    {
        Assert.Equal("a.stl", Plan("a", "stl").FileFor("a", 1));
        Assert.Equal("a.v2.step", Plan("a.v2", "stp").FileFor("a.v2", 1));
        Assert.Equal("a.easm", Plan("a", "eprt").FileFor("a", 2));
        Invalid(() => Plan("a.eprt").FileFor("a.eprt", 2), ".easm");
    }

    [Fact]
    public void Invalid_requests_fail_before_anything_runs()
    {
        Invalid(() => Plan(""), "empty");
        Invalid(() => Plan("a"), "no extension");
        Invalid(() => Plan("a.xyz"), "not an export format");
        Invalid(() => Plan("a.step", "stl"), "but format");
        Invalid(() => Plan("a.step", "nope"), "Unknown export format");
        Invalid(() => Plan("a.step", stepAp: 215), "203, 214 or 242");
        Invalid(() => Plan("a.stl", quality: "ultra"), "coarse, fine or custom");
        Invalid(() => Plan("a.stl", dev: 0.01), "custom");
        Invalid(() => Plan("a.stl", quality: "custom", ang: 95), "between 0 and 90");
        Invalid(() => Plan("a.stl", quality: "custom", dev: -1), "positive");
        Assert.Equal("custom", Plan("a.stl", quality: "Custom", dev: 0.01, ang: 5).Quality);
    }

    [Theory]
    [InlineData(".SLDPRT", "part")]
    [InlineData(".sldasm", "assembly")]
    [InlineData(".slddrw", "drawing")]
    [InlineData(".stp", "STEP")]
    [InlineData(".IGES", "IGES")]
    [InlineData(".x_b", "Parasolid")]
    [InlineData(".sat", "ACIS")]
    [InlineData(".stl", "STL")]
    public void Doc_open_knows_these_file_kinds(string ext, string label) =>
        Assert.Equal(label, OpenKind.FromExtension(ext)?.Label);

    [Fact]
    public void Doc_open_explains_refused_kinds()
    {
        Assert.Null(OpenKind.FromExtension(".dxf"));
        Assert.Contains("wizard", OpenKind.Refusal(".dxf"));
        Assert.Contains("doc_new", OpenKind.Refusal(".prtdot"));
        Assert.Contains("no extension", OpenKind.Refusal(""));
    }

    [Fact]
    public void Import_prompt_rules_match_only_their_prompts()
    {
        static Core.Win32.DialogInfo D(string title, string text) =>
            new(IntPtr.Zero, title, "#32770", text, [new Core.Win32.DialogButton("Yes", 6, true, IntPtr.Zero), new Core.Win32.DialogButton("No", 7, true, IntPtr.Zero)]);
        var fw = Core.Win32.DialogRules.ImportFeatureRecognitionNo;
        var diag = Core.Win32.DialogRules.ImportDiagnosticsNo;
        Assert.True(fw.Matches(D("SOLIDWORKS", "Do you want to proceed with feature recognition?")));
        Assert.True(fw.Matches(D("FeatureWorks", "Proceed with Feature Recognition?")));
        Assert.True(diag.Matches(D("SOLIDWORKS", "Do you want to run Import Diagnostics on the part?")));
        Assert.False(fw.Matches(D("SOLIDWORKS", "Do you want to save changes to Part1?")));
        Assert.False(diag.Matches(D("SOLIDWORKS", "Do you want to save changes to Part1?")));
        Assert.False(diag.Matches(D("Microsoft Visual Basic", "Import Diagnostics")));
        Assert.Equal("No", fw.Button);
        Assert.Equal("No", diag.Button);
    }

    [Fact]
    public void Load_flags_read_as_words()
    {
        Assert.Contains("file not found", LoadFlags.Describe(2, 0));
        Assert.Contains("newer SOLIDWORKS", LoadFlags.Describe(8192, 0));
        Assert.Contains("no error code", LoadFlags.Describe(0, 0));
        Assert.Contains("error code 1073741824", LoadFlags.Describe(1 << 30, 0));
        Assert.Contains(LoadFlags.Notes(0, 2), n => n.Contains("read-only"));
        Assert.Contains(LoadFlags.Notes(0, 1048576), n => n.Contains("external references are missing"));
        Assert.Empty(LoadFlags.Notes(0, 0));
        Assert.Contains("same file name", LoadFlags.Hint(65536, "33.1.2"));
        Assert.Contains("33.1.2", LoadFlags.Hint(8192, "33.1.2"));
    }
}

/// <summary>sw_help: every topic renders, and every example uses real tool and parameter names (no SOLIDWORKS needed).</summary>
public class HelpGuideTests
{
    private static readonly string[] Constant =
        [Guide.Conventions, Guide.EntityRef, Guide.WorkflowPlate, Guide.WorkflowEnclosure, Guide.Editing, Guide.Recovery];

    [Fact]
    public void Every_topic_renders_and_unknown_topics_are_refused()
    {
        var index = Guide.Get(null, "33.1.2");
        Assert.Equal("index", index.Topic);
        Assert.Equal(7, index.Topics!.Count);
        Assert.Contains("33.1.2", index.Text);
        foreach (var t in Guide.Topics)
        {
            var r = Guide.Get(t.Name);
            Assert.Equal(t.Name, r.Topic);
            Assert.InRange(r.Text.Length, 200, t.Name == "tools" ? 160 * Toolsets.Catalog.Count : 6000); // 'tools' has a line per tool
        }
        Assert.Equal("workflow_plate", Guide.Get("Plate").Topic);
        Assert.Equal("workflow_enclosure", Guide.Get("workflow-enclosure").Topic);
        var ex = Assert.Throws<SwException>(() => Guide.Get("teleport"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("workflow_plate", ex.Hint);
    }

    [Fact]
    public void The_tools_topic_lists_the_real_catalog()
    {
        Assert.Equal(Toolsets.Catalog.Keys.OrderBy(x => x, StringComparer.Ordinal), Guide.Tools().Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal));
        string text = Guide.Get("tools").Text;
        Assert.Contains("model_export {path, format?, stepAp?, stlBinary?, stlQuality?, stlDeviationMm?, stlAngleDeg?, overwrite?, doc?}", text);
        Assert.Contains("doc_open {path, readOnly?, configuration?}", text);
        Assert.Contains("sw_help {topic?}: Help and guides (read-only)", text);
    }

    [Fact]
    public void Reflected_parameters_match_the_published_schemas()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build();
        var schemas = host.Services.GetServices<McpServerTool>().ToDictionary(t => t.ProtocolTool.Name,
            t => t.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        foreach (var tool in Guide.Tools())
            Assert.Equal(schemas[tool.Name], tool.Parameters.Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Every "tool {key: …}" example: the tool exists and each top-level key is one of its parameters.</summary>
    [Fact]
    public void Examples_use_real_tools_and_parameters()
    {
        var tools = Guide.Tools().ToDictionary(t => t.Name, t => t.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
        var problems = new List<string>();
        int calls = 0;
        foreach (var text in Constant)
        {
            foreach (var (tool, keys, snippet) in Calls(text))
            {
                calls++;
                if (!tools.TryGetValue(tool, out var ps)) { problems.Add($"unknown tool in: {snippet}"); continue; }
                foreach (var k in keys.Where(k => !ps.Contains(k))) problems.Add($"{tool} has no parameter '{k}': {snippet}");
            }
            foreach (Match m in Regex.Matches(text, @"\b(?:sw|doc|sketch|feature|model|face|edge|vertex|view|dialog|vba|macro)_[a-z_]+\b"))
                if (!tools.ContainsKey(m.Value)) problems.Add($"'{m.Value}' looks like a tool name but isn't one");
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.True(calls >= 40, $"only {calls} examples were parsed");
    }

    private static IEnumerable<(string Tool, List<string> Keys, string Snippet)> Calls(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\b([a-z][a-z0-9]*(?:_[a-z0-9]+)+) \{"))
        {
            int i = m.Index + m.Length, depth = 1;
            bool inString = false;
            var keys = new List<string>();
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
                else if (depth == 1 && char.IsLetter(c) && !char.IsLetterOrDigit(text[i - 1]))
                {
                    int j = i;
                    while (j < text.Length && char.IsLetterOrDigit(text[j])) j++;
                    int k = j;
                    while (k < text.Length && text[k] == ' ') k++;
                    if (k < text.Length && text[k] == ':') keys.Add(text[i..j]);
                    i = j - 1;
                }
                i++;
            }
            yield return (m.Groups[1].Value, keys, text[m.Index..Math.Min(i, text.Length)].ReplaceLineEndings(" "));
        }
    }
}
