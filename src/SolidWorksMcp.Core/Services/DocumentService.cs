using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

public sealed class DocInfo
{
    /// <summary>Server handle for this document ("D2"); usable wherever a tool takes 'doc'.</summary>
    public required string Handle { get; init; }
    public required string Title { get; init; }
    public string? Path { get; init; }
    public required string Type { get; init; }
    public bool Dirty { get; init; }
    /// <summary>owned | adopted | foreign (foreign = the user's, read-only for the server).</summary>
    public required string Access { get; init; }
    public bool Active { get; init; }
    public bool Target { get; init; }
    public string? Units { get; init; }
    public string? Configuration { get; init; }
}

public sealed class DocResult : ToolResult
{
    public required DocInfo Document { get; init; }
    public string? Message { get; init; }
    public string? NowActive { get; init; }
    public bool? DiscardedChanges { get; init; }
    public string? SavedTo { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? BackupPath { get; init; }
}

public sealed class DocListResult : ToolResult
{
    public required IReadOnlyList<DocInfo> Documents { get; init; }
    public string? Active { get; init; }
    public string? Target { get; init; }
}

/// <summary>Creating, listing, activating, saving and closing documents (with the ownership rules of design §3.6).</summary>
public sealed class DocumentService(SwSession session)
{
    // ------------------------------------------------------------------ new

    public Task<DocResult> NewAsync(string type, string? units = "mmgs", string? template = null, string? name = null,
        CancellationToken ct = default) =>
        session.RunAsync("doc_new", ctx =>
        {
            var docType = ParseType(type);
            if (docType == swDocumentTypes_e.swDocDRAWING)
                throw new SwException(ErrorCodes.NotSupported, "Drawings are not available yet in this version (planned for M4).");
            int unitSystem = ParseUnits(units);
            string tpl = template is null
                ? ctx.App.GetUserPreferenceStringValue(docType switch
                {
                    swDocumentTypes_e.swDocPART => (int)swUserPreferenceStringValue_e.swDefaultTemplatePart,
                    swDocumentTypes_e.swDocASSEMBLY => (int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly,
                    _ => (int)swUserPreferenceStringValue_e.swDefaultTemplateDrawing,
                })
                : ctx.Paths.ForRead(template, "template");
            if (string.IsNullOrWhiteSpace(tpl) || !File.Exists(tpl))
                throw new SwException(ErrorCodes.IoError, $"Template not found: '{tpl}'.",
                    "Pass 'template' with the full path of a .prtdot/.asmdot/.drwdot file (Tools > Options > Default Templates).");

            var doc = (object?)ctx.App.NewDocument(tpl, 0, 0, 0) as IModelDoc2
                      ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS could not create a {type} from '{tpl}'.");
            ctx.Documents.SetClass(doc, DocClass.Owned, "created");
            ctx.Documents.Target = doc;
            if (doc.GetType() != (int)docType)
                throw new SwException(ErrorCodes.VerifyFailed, $"The template '{tpl}' produced a {SwContext.TypeName(doc.GetType())}, not a {type}.");

            if (unitSystem > 0)
            {
                doc.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0, unitSystem);
                int got = doc.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0);
                if (got != unitSystem) ctx.Warn($"Unit system could not be set (reads {UnitName(got)}).");
            }
            if (!string.IsNullOrWhiteSpace(name) && !doc.SetTitle2(name)) ctx.Warn($"Could not set the title to '{name}'.");
            ctx.Documents.Track(doc).StampAfterLastOp = doc.GetUpdateStamp();
            return new DocResult { Doc = ctx.Title(doc), Document = Info(ctx, doc), Message = $"Created {type} from {Path.GetFileName(tpl)}; it is now the target document." };
        }, null, ct);

    // ------------------------------------------------------------------ list

    public Task<DocListResult> ListAsync(CancellationToken ct = default) =>
        session.RunAsync("doc_list", ctx =>
        {
            var docs = ctx.App.Documents();
            ctx.Documents.Prune(docs);
            var active = ctx.ActiveDocOrNull;
            var target = ctx.Documents.Target;
            return new DocListResult
            {
                Documents = docs.Select(d => Info(ctx, d, active)).ToList(),
                Active = active is null ? null : ctx.Title(active),
                Target = target is null ? null : ctx.Title(target),
            };
        }, null, ct);

    // ------------------------------------------------------------------ activate

    /// <summary>Makes a document active and the session's target; <paramref name="allowEdit"/> adopts a user document.</summary>
    public Task<DocResult> ActivateAsync(string doc, bool allowEdit, CancellationToken ct = default) =>
        session.RunAsync("doc_activate", ctx =>
        {
            var open = ctx.App.Documents();
            var target = DocLocator.Find(ctx.Documents, open, doc);
            var entry = ctx.Documents.Track(target);
            string? backup = null;
            if (allowEdit && entry.Class == DocClass.Foreign)
            {
                backup = Backup(ctx, target);
                ctx.Documents.SetClass(target, DocClass.Adopted, "adopted (allowEdit)");
                entry.BackupPath = backup;
            }
            int errors = 0;
            ctx.App.ActivateDoc3(entry.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
            var now = ctx.ActiveDocOrNull;
            if (now is null || !DocLocator.SameDoc(now, target))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{entry.Title}' did not become active (error {errors}).");
            ctx.Documents.Target = target;
            entry.StampAfterLastOp = target.GetUpdateStamp();
            var info = Info(ctx, now, now);
            return new DocResult
            {
                Doc = ctx.Title(now),
                Document = info,
                BackupPath = backup,
                Message = info.Access == "foreign"
                    ? "Active and targeted for reading. It is the user's document: pass allowEdit=true (only if the user asked) to modify it."
                    : "Active and targeted.",
            };
        }, null, ct);

    /// <summary>Saves a copy of a user document before the server is allowed to edit it.</summary>
    private static string? Backup(SwContext ctx, IModelDoc2 doc)
    {
        string ext = doc.GetType() switch
        {
            (int)swDocumentTypes_e.swDocPART => ".sldprt",
            (int)swDocumentTypes_e.swDocASSEMBLY => ".sldasm",
            _ => ".slddrw",
        };
        string dir = Path.Combine(ctx.Paths.BackupDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(DocumentRegistry.SafePath(doc) is { Length: > 0 } p ? p : ctx.Title(doc));
        string file = Path.Combine(dir, name + ext);
        int e = 0, w = 0;
        bool ok = doc.Extension.SaveAs3(file, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy), null, null, ref e, ref w);
        if (!ok || !File.Exists(file))
        {
            ctx.Warn($"Could not save a backup copy before adopting '{ctx.Title(doc)}' (error {e}).");
            return null;
        }
        return file;
    }

    // ------------------------------------------------------------------ save

    /// <summary>
    /// mode "inPlace" (default; file must exist), "as" (Save As: the document is renamed — owned documents only), or
    /// "copy" (writes a copy; the document is unchanged).
    /// </summary>
    public Task<DocResult> SaveAsync(string? doc, string mode, string? path, bool overwrite, CancellationToken ct = default) =>
        session.RunAsync("doc_save", ctx =>
        {
            var target = ctx.Doc;
            var entry = ctx.DocEntry;
            int errors = 0, warnings = 0;
            string saved;
            string? backup = null;
            switch (mode.Trim().ToLowerInvariant())
            {
                case "inplace" or "in_place" or "":
                    if (!string.IsNullOrWhiteSpace(path))
                        throw SwException.InvalidArgument("mode 'inPlace' takes no path.", "Use mode 'as' or 'copy' to write another file.");
                    saved = DocumentRegistry.SafePath(target);
                    if (saved.Length == 0)
                        throw SwException.InvalidArgument($"'{ctx.Title(target)}' has never been saved, so it has no file yet.",
                            "Use mode 'as' with a path (e.g. bracket.sldprt, saved under the output folder).");
                    CheckVersionUpgrade(ctx, saved);
                    if (!target.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref errors, ref warnings) || errors != 0)
                        throw new SwException(ErrorCodes.IoError, $"Save failed ({SaveErrors(errors)}).", "Check that the file isn't read-only or open elsewhere.");
                    break;
                case "as" or "copy":
                    bool copy = mode.Trim().Equals("copy", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(path)) throw SwException.InvalidArgument($"mode '{mode}' needs a path.");
                    if (!copy && entry.Class != DocClass.Owned)
                        throw new SwException(ErrorCodes.NotOwned, $"Save As would rename the user's document '{entry.Title}'.",
                            "Use mode 'copy' instead (the user's document stays as it is).");
                    saved = ctx.Paths.ForWrite(WithNativeExtension(path!, target.GetType()), overwrite, out backup);
                    int options = (int)swSaveAsOptions_e.swSaveAsOptions_Silent | (copy ? (int)swSaveAsOptions_e.swSaveAsOptions_Copy : 0);
                    var before = File.Exists(saved) ? File.GetLastWriteTimeUtc(saved) : DateTime.MinValue;
                    bool ok = target.Extension.SaveAs3(saved, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, options, null, null, ref errors, ref warnings);
                    if (!ok || errors != 0)
                        throw new SwException(ErrorCodes.IoError, $"Save As '{saved}' failed ({SaveErrors(errors)}).");
                    if (File.GetLastWriteTimeUtc(saved) <= before)
                        throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS reported success but did not rewrite '{saved}'.");
                    ctx.Paths.RecordWritten(saved);
                    if (!copy) entry.Path = saved;
                    break;
                default:
                    throw SwException.InvalidArgument($"Unknown save mode '{mode}'.", "Use inPlace, as or copy.");
            }
            var fi = new FileInfo(saved);
            if (!fi.Exists || fi.Length == 0)
                throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS reported success but '{saved}' is missing or empty.");
            if (warnings != 0) ctx.Warn($"Save warnings: {SaveWarnings(warnings)}.");
            return new DocResult
            {
                Doc = ctx.Title(target),
                Document = Info(ctx, target),
                SavedTo = saved,
                FileSizeBytes = fi.Length,
                BackupPath = backup,
            };
        }, RunOptions.Write(doc), ct);

    /// <summary>Refuses an in-place save that would silently upgrade a file written by an older SOLIDWORKS.</summary>
    private static void CheckVersionUpgrade(SwContext ctx, string path)
    {
        try
        {
            var history = ComArgs.Strings((object?)ctx.App.VersionHistory(path));
            if (history.Length == 0) return;
            string last = history[^1]; // e.g. "sldprt 4000" — the number after the type is the file version
            var digits = new string(last.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int fileVersion) && fileVersion < ctx.App.GetLatestSupportedFileVersion())
                ctx.Warn($"'{Path.GetFileName(path)}' was last saved by an older SOLIDWORKS (file version {fileVersion}); saving upgraded it to this release's format.");
        }
        catch { /* informational only */ }
    }

    // ------------------------------------------------------------------ close

    /// <summary>
    /// Closes the target (or given) document. Dirty documents need save=true or discard=true; the user's documents
    /// (foreign or adopted) are never discarded.
    /// </summary>
    public Task<DocResult> CloseAsync(string? doc, bool save, bool discard, CancellationToken ct = default) =>
        session.RunAsync("doc_close", ctx =>
        {
            var target = ctx.Doc;
            var entry = ctx.DocEntry;
            var info = Info(ctx, target);
            bool dirty = target.GetSaveFlag();
            if (entry.Class == DocClass.Foreign)
                throw new SwException(ErrorCodes.NotOwned, $"'{info.Title}' is the user's document; the server doesn't close it.",
                    "Leave it open, or ask the user to close it.");
            if (dirty && !save && !discard)
                throw new SwException(ErrorCodes.Dirty, $"'{info.Title}' has unsaved changes.",
                    "Pass save=true to save first, or discard=true to throw the changes away.");
            if (dirty && discard && entry.Class != DocClass.Owned)
                throw new SwException(ErrorCodes.NotOwned, $"'{info.Title}' belongs to the user; the server never discards its changes.",
                    "Pass save=true, or ask the user.");
            if (save && dirty)
            {
                if (string.IsNullOrEmpty(info.Path))
                    throw SwException.InvalidArgument($"'{info.Title}' has no file yet.", "Save it first with doc_save mode 'as'.");
                int e = 0, w = 0;
                if (!target.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w) || e != 0)
                    throw new SwException(ErrorCodes.IoError, $"Saving before close failed ({SaveErrors(e)}).");
            }

            // Close by identity: CloseDoc takes a title, so make sure that title names exactly this document.
            var open = ctx.App.Documents();
            string title = ctx.Title(target);
            if (open.Count(d => string.Equals(ctx.Title(d), title, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new SwException(ErrorCodes.EntityAmbiguous, $"Several open documents are titled '{title}', so closing by title is unsafe.",
                    "Ask the user to close it.");
            var others = open.Where(d => !ReferenceEquals(d, target)).ToList();
            ctx.App.CloseDoc(title);
            var after = ctx.App.Documents();
            if (after.Any(d => ReferenceEquals(d, target)))
                throw new SwException(ErrorCodes.VerifyFailed, $"'{title}' is still open after CloseDoc.",
                    "A dialog may have interrupted the close; check dialog_list.");
            var lost = others.Where(o => !after.Any(a => ReferenceEquals(a, o))).Select(ctx.Title).ToList();
            if (lost.Count > 0) ctx.Warn($"SOLIDWORKS also closed: {string.Join(", ", lost)}.");
            ctx.Documents.Remove(target);
            ctx.Entities.Forget(target);
            var now = ctx.ActiveDocOrNull;
            return new DocResult
            {
                Doc = title,
                Document = info,
                Message = $"Closed '{title}'.",
                DiscardedChanges = dirty && !save ? true : null,
                NowActive = now is null ? null : ctx.Title(now),
            };
        }, RunOptions.Read(doc) with { DialogRules = [save ? DialogRules.SimulationSaveYes : DialogRules.SimulationSaveNo] }, ct);

    // ------------------------------------------------------------------ helpers

    internal static DocInfo Info(SwContext ctx, IModelDoc2 d, IModelDoc2? active = null)
    {
        active ??= ctx.ActiveDocOrNull;
        var entry = ctx.Documents.Track(d);
        string? cfg = null;
        try { cfg = d.ConfigurationManager?.ActiveConfiguration?.Name; } catch { /* drawings */ }
        string path = DocumentRegistry.SafePath(d);
        return new DocInfo
        {
            Handle = entry.Handle,
            Title = ctx.Title(d),
            Path = path.Length == 0 ? null : path,
            Type = SwContext.TypeName(d.GetType()),
            Dirty = d.GetSaveFlag(),
            Access = entry.Class.ToString().ToLowerInvariant(),
            Active = active is not null && DocLocator.SameDoc(active, d),
            Target = ctx.Documents.Target is not null && DocLocator.SameDoc(ctx.Documents.Target, d),
            Units = UnitName(d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0)),
            Configuration = cfg,
        };
    }

    internal static swDocumentTypes_e ParseType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "part" or "sldprt" => swDocumentTypes_e.swDocPART,
        "assembly" or "asm" or "sldasm" => swDocumentTypes_e.swDocASSEMBLY,
        "drawing" or "drw" or "slddrw" => swDocumentTypes_e.swDocDRAWING,
        _ => throw SwException.InvalidArgument($"Unknown document type '{type}'.", "Use part, assembly or drawing."),
    };

    /// <summary>"mmgs" (default), "cgs", "mks", "ips", or "template" (keep the template's units). Returns 0 for "keep".</summary>
    internal static int ParseUnits(string? units) => (units ?? "mmgs").Trim().ToLowerInvariant() switch
    {
        "mmgs" or "mm" => (int)swUnitSystem_e.swUnitSystem_MMGS,
        "cgs" or "cm" => (int)swUnitSystem_e.swUnitSystem_CGS,
        "mks" or "m" => (int)swUnitSystem_e.swUnitSystem_MKS,
        "ips" or "in" or "inch" => (int)swUnitSystem_e.swUnitSystem_IPS,
        "template" or "keep" => 0,
        _ => throw SwException.InvalidArgument($"Unknown unit system '{units}'.", "Use mmgs, cgs, mks, ips or template."),
    };

    internal static string UnitName(int unitSystem) => unitSystem switch
    {
        1 => "CGS", 2 => "MKS", 3 => "IPS", 4 => "custom", 5 => "MMGS", _ => unitSystem.ToString(),
    };

    private static string WithNativeExtension(string path, int docType)
    {
        string want = docType switch
        {
            (int)swDocumentTypes_e.swDocPART => ".sldprt",
            (int)swDocumentTypes_e.swDocASSEMBLY => ".sldasm",
            _ => ".slddrw",
        };
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext.Length == 0) return path + want;
        if (ext != want)
            throw SwException.InvalidArgument($"doc_save writes native {want} files; '{ext}' is not one.",
                "Use the export tool for STEP/STL/PDF/DXF and other formats.");
        return path;
    }

    internal static string SaveErrors(int e) => e == 0 ? "no error code" : string.Join(", ",
        Enum.GetValues<swFileSaveError_e>().Where(v => (int)v != 0 && (e & (int)v) == (int)v).Select(v => v.ToString())
            .DefaultIfEmpty($"code {e}"));

    internal static string SaveWarnings(int w) => string.Join(", ",
        Enum.GetValues<swFileSaveWarning_e>().Where(v => (int)v != 0 && (w & (int)v) == (int)v).Select(v => v.ToString())
            .DefaultIfEmpty($"code {w}"));
}
