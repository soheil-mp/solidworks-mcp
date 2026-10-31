using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of model_export_all: a release package of a part or an assembly in one folder.</summary>
public sealed record ExportAllRequest
{
    public string? Doc { get; init; }
    /// <summary>step | iges | parasolid | stl | 3mf (each model file), pdf (each model's drawing), dxf (each sheet-metal blank), bom.</summary>
    public IReadOnlyList<string>? Formats { get; init; }
    /// <summary>The folder (relative paths go under the output folder); default "&lt;name&gt;_release".</summary>
    public string? Folder { get; init; }
    public bool Overwrite { get; init; }
    public int StepAp { get; init; } = 214;
}

public sealed class ReleaseFile
{
    public required string Path { get; init; }
    public required string Format { get; init; }
    /// <summary>The model or drawing it came from (file name).</summary>
    public required string Source { get; init; }
    public long SizeBytes { get; init; }
    public string? Header { get; init; }
}

public sealed class ReleaseSkip
{
    public required string Source { get; init; }
    public required string Format { get; init; }
    public required string Why { get; init; }
}

public sealed class ExportAllResult : ToolResult
{
    public required string Folder { get; init; }
    public required IReadOnlyList<ReleaseFile> Files { get; init; }
    public IReadOnlyList<ReleaseSkip>? Skipped { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// model_export_all: every file a release needs from one call. Each export goes through the same code and file checks as
/// model_export / sheetmetal_export_flat / asm_bom; what can't be exported (no drawing next to a model, not sheet metal,
/// unsaved) is listed with the reason instead of failing the rest.
/// </summary>
public sealed class ReleaseService(SwSession session)
{
    private static readonly string[] ModelFormats = ["step", "iges", "parasolid", "stl", "3mf"];
    private static readonly string[] DefaultFormats = ["step", "pdf", "dxf", "bom"];

    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["step"] = ".step", ["iges"] = ".igs", ["parasolid"] = ".x_t", ["stl"] = ".stl", ["3mf"] = ".3mf", ["pdf"] = ".pdf",
    };

    public Task<ExportAllResult> ExportAllAsync(ExportAllRequest q, CancellationToken ct = default)
    {
        var formats = new List<string>();
        foreach (var f in q.Formats is { Count: > 0 } ? q.Formats : DefaultFormats)
        {
            string n = (f ?? "").Trim().ToLowerInvariant() switch
            {
                "stp" => "step",
                "igs" => "iges",
                "x_t" => "parasolid",
                "flat" or "flat_pattern" => "dxf",
                "drawing" or "drawings" => "pdf",
                var x => x,
            };
            if (!ModelFormats.Contains(n) && n is not ("pdf" or "dxf" or "bom"))
                throw SwException.InvalidArgument($"Unknown format '{f}'.", "Use step, iges, parasolid, stl, 3mf (each model), pdf (each drawing), dxf (each sheet-metal blank) or bom.");
            if (!formats.Contains(n)) formats.Add(n);
        }
        if (q.StepAp is not (203 or 214 or 242)) throw SwException.InvalidArgument($"stepAp must be 203, 214 or 242 (got {q.StepAp}).");
        var timeout = TimeSpan.FromSeconds(Math.Max(900, session.Options.DefaultTimeout.TotalSeconds));
        return session.RunAsync("model_export_all", ctx => Run(ctx, q, formats), RunOptions.Read(q.Doc) with { Timeout = timeout }, ct);
    }

    private sealed record Model(IModelDoc2 Doc, string Path, bool IsPart)
    {
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    private static ExportAllResult Run(SwContext ctx, ExportAllRequest q, List<string> formats)
    {
        var d = ctx.Doc;
        int type = d.GetType();
        if (type is not ((int)swDocumentTypes_e.swDocPART or (int)swDocumentTypes_e.swDocASSEMBLY))
            throw new SwException(ErrorCodes.WrongDocType, $"'{ctx.Title(d)}' is a {SwContext.TypeName(type)}; release packages are made of parts and assemblies.",
                "Activate the assembly (or part) the drawings belong to; their PDFs come with 'pdf'.");
        string topPath = DocumentRegistry.SafePath(d);
        if (topPath.Length == 0)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' has not been saved.", "Save it first (doc_save {mode:'as'}), so its files can be found and named.");
        // The folder: created through the path policy (allowed roots, no device names).
        string folderArg = q.Folder ?? $"{System.IO.Path.GetFileNameWithoutExtension(topPath)}_release";
        string dir = System.IO.Path.GetDirectoryName(ctx.Paths.ForWrite(System.IO.Path.Combine(folderArg, "release.probe"), overwrite: true, out _, "folder"))!;

        var skipped = new List<ReleaseSkip>();
        var models = new List<Model> { new(d, topPath, type == (int)swDocumentTypes_e.swDocPART) };
        if (type == (int)swDocumentTypes_e.swDocASSEMBLY) Collect((IAssemblyDoc)d, models, skipped);

        var files = new List<ReleaseFile>();
        void Add(string path, string format, string source, ExportDetails? details)
        {
            var fi = new FileInfo(path);
            files.Add(new ReleaseFile { Path = path, Format = format, Source = source, SizeBytes = fi.Exists ? fi.Length : 0, Header = details?.Header });
        }
        void Skip(string source, string format, string why) => skipped.Add(new ReleaseSkip { Source = source, Format = format, Why = why });

        foreach (var format in formats)
        {
            if (ModelFormats.Contains(format))
            {
                foreach (var m in models)
                {
                    var req = new ExportRequest { Path = System.IO.Path.Combine(dir, m.Name + Extensions[format]), Format = format, Overwrite = q.Overwrite, StepAp = q.StepAp };
                    try
                    {
                        var r = ExportService.ExportDoc(ctx, m.Doc, req, ExportPlan.Parse(req));
                        Add(r.Path, format, System.IO.Path.GetFileName(m.Path), r.Details);
                    }
                    catch (SwException e)
                    {
                        Skip(System.IO.Path.GetFileName(m.Path), format, e.Message);
                    }
                }
            }
            else if (format == "pdf")
            {
                foreach (var m in models)
                {
                    string drawing = System.IO.Path.ChangeExtension(m.Path, ".slddrw");
                    if (!File.Exists(drawing))
                    {
                        Skip(System.IO.Path.GetFileName(m.Path), "pdf", $"no drawing {System.IO.Path.GetFileName(drawing)} next to it");
                        continue;
                    }
                    ExportDrawing(ctx, drawing, dir, q.Overwrite, files, skipped);
                }
            }
            else if (format == "dxf")
            {
                foreach (var m in models.Where(x => x.IsPart))
                {
                    if (!Tree.Walk(m.Doc).Any(f => f.GetTypeName2() == "SheetMetal")) continue; // only sheet metal has a blank
                    try
                    {
                        var r = SheetMetalService.ExportFlatOf(ctx, (IPartDoc)m.Doc, System.IO.Path.Combine(dir, m.Name + "_flat.dxf"), "dxf", true, q.Overwrite);
                        Add(r.Path, "dxf", System.IO.Path.GetFileName(m.Path), r.Details);
                    }
                    catch (SwException e)
                    {
                        Skip(System.IO.Path.GetFileName(m.Path), "dxf", e.Message);
                    }
                }
            }
            else if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                try
                {
                    var bom = AssemblyService.BomOf(ctx, "parts", new AsmBomRequest { Path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(topPath) + "_bom.csv"), Overwrite = q.Overwrite });
                    Add(bom.File!, "bom", System.IO.Path.GetFileName(topPath), null);
                }
                catch (SwException e)
                {
                    Skip(System.IO.Path.GetFileName(topPath), "bom", e.Message);
                }
            }
            else Skip(System.IO.Path.GetFileName(topPath), "bom", "a part has no bill of materials");
        }

        var checks = new List<string>
        {
            $"{files.Count} file(s) written to {dir}: {string.Join(", ", files.GroupBy(f => f.Format).Select(g => $"{g.Count()} {g.Key}"))}",
            $"{models.Count} model file(s): {string.Join(", ", models.Take(12).Select(m => m.Name))}{(models.Count > 12 ? ", …" : "")}",
        };
        if (files.Count == 0)
            throw new SwException(ErrorCodes.FeatureFailed, $"model_export_all wrote nothing: {string.Join("; ", skipped.Take(6).Select(s => $"{s.Source} {s.Format}: {s.Why}"))}.",
                "Check the formats against the models (pdf needs a drawing with the model's name next to it; dxf needs sheet metal).");
        return new ExportAllResult { Doc = ctx.Title(d), Folder = dir, Files = files, Skipped = skipped.Count == 0 ? null : skipped, Checks = checks };
    }

    /// <summary>The assembly's distinct model files at every level (suppressed, envelope and BOM-excluded components left out).</summary>
    private static void Collect(IAssemblyDoc asm, List<Model> models, List<ReleaseSkip> skipped)
    {
        foreach (var c in asm.Components(false))
        {
            string name = Try(() => c.Name2) ?? "?";
            if (Try(() => c.GetSuppression2()) == (int)swComponentSuppressionState_e.swComponentSuppressed || Try(() => c.ExcludeFromBOM) == true ||
                Try(() => c.IsEnvelope()) == true)
                continue;
            string path = Try(() => c.GetPathName()) ?? "";
            if (path.Length == 0 || models.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            if ((object?)c.GetModelDoc2() is not IModelDoc2 md)
            {
                skipped.Add(new ReleaseSkip { Source = System.IO.Path.GetFileName(path), Format = "all", Why = $"'{name}' is not loaded (lightweight?); resolve it first" });
                continue;
            }
            if (!File.Exists(path))
            {
                skipped.Add(new ReleaseSkip { Source = System.IO.Path.GetFileName(path), Format = "all", Why = "not saved to its file yet" });
                continue;
            }
            models.Add(new Model(md, path, md.GetType() == (int)swDocumentTypes_e.swDocPART));
        }
    }

    /// <summary>PDF of a drawing file: an open document as it is, else opened silently and read-only, exported and closed again.</summary>
    private static void ExportDrawing(SwContext ctx, string drawing, string dir, bool overwrite, List<ReleaseFile> files, List<ReleaseSkip> skipped)
    {
        string source = System.IO.Path.GetFileName(drawing);
        var open = ctx.App.Documents().FirstOrDefault(x => string.Equals(DocumentRegistry.SafePath(x), drawing, StringComparison.OrdinalIgnoreCase));
        bool opened = false;
        try
        {
            if (open is null)
            {
                int errors = 0, warnings = 0;
                open = ctx.App.OpenDoc6(drawing, (int)swDocumentTypes_e.swDocDRAWING,
                    (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings);
                if (open is null)
                {
                    skipped.Add(new ReleaseSkip { Source = source, Format = "pdf", Why = $"SOLIDWORKS could not open it (error {errors})" });
                    return;
                }
                opened = true;
            }
            var req = new ExportRequest { Path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(drawing) + ".pdf"), Format = "pdf", Overwrite = overwrite };
            var r = ExportService.ExportDoc(ctx, open, req, ExportPlan.Parse(req));
            var fi = new FileInfo(r.Path);
            files.Add(new ReleaseFile { Path = r.Path, Format = "pdf", Source = source, SizeBytes = fi.Exists ? fi.Length : 0, Header = r.Details.Header });
        }
        catch (SwException e)
        {
            skipped.Add(new ReleaseSkip { Source = source, Format = "pdf", Why = e.Message });
        }
        finally
        {
            if (opened && open is not null)
            {
                try { ctx.App.CloseDoc(open.GetTitle()); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { ctx.Warn($"'{source}' was opened for its PDF and could not be closed again."); }
            }
        }
    }

    private static T? Try<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return null; }
    }

    private static string? Try(Func<string?> read)
    {
        try { return read(); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return null; }
    }
}
