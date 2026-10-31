using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of doc_pack: Pack and Go of a saved document and everything it references.</summary>
public sealed record DocPackRequest
{
    public string? Doc { get; init; }
    /// <summary>The folder to copy into (relative paths go under the output folder); default &lt;name&gt;_pack.</summary>
    public string? Folder { get; init; }
    public string? Prefix { get; init; }
    public string? Suffix { get; init; }
    /// <summary>Also copy the drawings of the models (saved next to them under the same name).</summary>
    public bool IncludeDrawings { get; init; } = true;
    /// <summary>All copies in one folder (default) instead of the original folder structure.</summary>
    public bool Flatten { get; init; } = true;
    public bool Overwrite { get; init; }
}

public sealed class PackedFile
{
    public required string From { get; init; }
    public required string To { get; init; }
    public long SizeBytes { get; init; }
    public string? BackupPath { get; init; }
}

public sealed class DocPackResult : ToolResult
{
    public required string Folder { get; init; }
    public required IReadOnlyList<PackedFile> Files { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// doc_pack: Pack and Go (IModelDocExtension.GetPackAndGo, the target folder, prefix or suffix, then SavePackAndGo). Tier B:
/// every copy exists, the copies of assemblies and drawings reference the copies only (ISldWorks.GetDocumentDependencies2,
/// where the files were last saved), and the open document keeps its own path.
/// </summary>
public sealed class PackService(SwSession session)
{
    public Task<DocPackResult> PackAsync(DocPackRequest q, CancellationToken ct = default)
    {
        foreach (var (v, name) in new[] { (q.Prefix, "prefix"), (q.Suffix, "suffix") })
            if (v is { Length: > 0 } && (v.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || v.Length > 40))
                throw SwException.InvalidArgument($"'{name}' must be a short piece of a file name (got '{v}').");
        return session.RunAsync("doc_pack", ctx => OnActive(ctx, () => Pack(ctx, q)), RunOptions.Read(q.Doc) with
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(300, session.Options.DefaultTimeout.TotalSeconds)),
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="run"/> with the document active, then re-activates the previous one: GetPackAndGo works on the
    /// active document (VERIFIED: called through an assembly while its drawing was active, it packed the drawing alone).
    /// </summary>
    private static T OnActive<T>(SwContext ctx, Func<T> run)
    {
        var d = ctx.Doc;
        var previous = ctx.ActiveDocOrNull;
        bool switched = previous is null || !DocLocator.SameDoc(previous, d);
        if (switched)
        {
            int e = 0;
            ctx.App.ActivateDoc3(ctx.Title(d), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e);
            if (ctx.ActiveDocOrNull is not { } now || !DocLocator.SameDoc(now, d))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{ctx.Title(d)}' could not be activated for Pack and Go (error {e}).");
        }
        try
        {
            return run();
        }
        finally
        {
            if (switched && previous is not null)
            {
                int e = 0;
                try { ctx.App.ActivateDoc3(ctx.Title(previous), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e); }
                catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { ctx.Warn($"'{ctx.Title(previous)}' could not be activated again: {ex.Message}"); }
            }
        }
    }

    private static DocPackResult Pack(SwContext ctx, DocPackRequest q)
    {
        var d = ctx.Doc;
        string src = DocumentRegistry.SafePath(d);
        if (src.Length == 0)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' has not been saved.", "Save it first (doc_save {mode:'as'}): Pack and Go copies files.");
        if (d.GetSaveFlag())
            ctx.Warn($"'{ctx.Title(d)}' has unsaved changes; Pack and Go copies what is saved (doc_save first to include them).");
        string folderArg = q.Folder ?? $"{Path.GetFileNameWithoutExtension(src)}_pack";
        string dir = Path.GetDirectoryName(ctx.Paths.ForWrite(Path.Combine(folderArg, "pack.probe"), overwrite: true, out _, "folder"))!;

        // SOLIDWORKS' own order (its "Pack and Go an Assembly" example): the options, the document list, then the folder,
        // flattening and names, and the targets read again before saving.
        var pg = d.Extension.GetPackAndGo() ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS offered no Pack and Go for this document.");
        _ = pg.GetDocumentNamesCount();
        pg.IncludeDrawings = q.IncludeDrawings;
        pg.IncludeSimulationResults = false;
        pg.GetDocumentNames(out object names);
        pg.GetDocumentSaveToNames(out object _, out object _);
        if (!pg.SetSaveToName(true, dir)) throw new SwException(ErrorCodes.FeatureFailed, $"Pack and Go refused the folder '{dir}'.");
        pg.FlattenToSingleFolder = q.Flatten;
        if (q.Prefix is { Length: > 0 } pre) pg.AddPrefix = pre;
        if (q.Suffix is { Length: > 0 } suf) pg.AddSuffix = suf;
        pg.GetDocumentSaveToNames(out object targets, out object _);
        var from = ComArgs.Strings(names);
        var to = ComArgs.Strings(targets);
        if (from.Length == 0 || from.Length != to.Length)
            throw new SwException(ErrorCodes.FeatureFailed, $"Pack and Go listed {from.Length} documents and {to.Length} targets.");
        // Pack and Go leaves out some references without a word (VERIFIED: files under the Windows temp folder): compare its
        // list with the file's own dependencies before anything is written.
        var listed = new HashSet<string>(from.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var needed = ComArgs.Strings((object?)ctx.App.GetDocumentDependencies2(src, true, false, false));
        var left = Enumerable.Range(0, needed.Length / 2).Select(i => needed[2 * i + 1]).Where(f => f.Length > 0 && !listed.Contains(Path.GetFullPath(f))).ToList();
        if (left.Count > 0)
            throw new SwException(ErrorCodes.InvalidArgument,
                $"Pack and Go would leave out {string.Join(", ", left.Take(5).Select(Path.GetFileName))}{(left.Count > 5 ? ", …" : "")}, so the copies would still reference them.",
                left.Any(f => f.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
                    ? "SOLIDWORKS skips files under the Windows temp folder: save the files somewhere else (e.g. Documents\\solidworks-mcp) first."
                    : "Check that they can be found (doc_open the top document), then retry.");
        // Every target goes through the path policy; one that exists needs overwrite (and is backed up unless this server wrote it).
        var backups = new string?[to.Length];
        for (int i = 0; i < to.Length; i++)
        {
            ctx.Paths.ForWrite(to[i], q.Overwrite, out var b, "folder");
            backups[i] = b;
        }
        var status = ComArgs.Ints((object?)d.Extension.SavePackAndGo(pg));
        if (status.Length == 0 || status.Any(s => s != (int)swPackAndGoSaveStatus_e.swPackAndGoSaveStatus_Succeed))
            throw new SwException(ErrorCodes.FeatureFailed,
                $"Pack and Go reported {(status.Length == 0 ? "nothing" : string.Join(", ", status.Distinct().Select(s => Enum.GetName(typeof(swPackAndGoSaveStatus_e), s) ?? s.ToString())))}.");
        foreach (var t in to) ctx.Paths.RecordWritten(t);

        var checks = new List<string>();
        var missing = to.Where(t => !File.Exists(t)).ToList();
        if (missing.Count > 0)
            throw new SwException(ErrorCodes.VerifyFailed, $"Pack and Go did not write {string.Join(", ", missing.Take(5).Select(Path.GetFileName))}.");
        checks.Add($"{to.Length} file(s) written to {dir}");
        // The copies point at the copies: every reference of a copied assembly or drawing is one of the copies.
        var copies = new HashSet<string>(to.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var stray = new List<string>();
        int checkedFiles = 0;
        foreach (var t in to.Where(t => t.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase)))
        {
            var deps = ComArgs.Strings((object?)ctx.App.GetDocumentDependencies2(t, false, false, false));
            for (int i = 1; i < deps.Length; i += 2)
                if (!copies.Contains(Path.GetFullPath(deps[i]))) stray.Add($"{Path.GetFileName(t)} → {deps[i]}");
            checkedFiles++;
        }
        if (stray.Count > 0)
            throw new SwException(ErrorCodes.VerifyFailed,
                $"Some copies still reference other files: {string.Join("; ", stray.Take(5))} (Pack and Go listed {string.Join(", ", from.Select(Path.GetFileName))}).",
                "The copies were written; check the references before using them.");
        if (checkedFiles > 0) checks.Add($"the {checkedFiles} copied assembl(ies)/drawing(s) reference the copies only");
        if (!string.Equals(DocumentRegistry.SafePath(d), src, StringComparison.OrdinalIgnoreCase))
            ctx.Warn($"'{ctx.Title(d)}' is now '{DocumentRegistry.SafePath(d)}' (it was '{src}').");
        else checks.Add("the open document still has its own file");
        return new DocPackResult
        {
            Doc = ctx.Title(d),
            Folder = dir,
            Files = from.Select((f, i) => new PackedFile
            {
                From = f, To = to[i], SizeBytes = new FileInfo(to[i]).Length, BackupPath = backups[i],
            }).ToList(),
            Checks = checks,
        };
    }
}
