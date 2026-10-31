using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("documents")]
public sealed class DocumentTools(SwSession session)
{
    private readonly DocumentService _svc = new(session);

    [McpServerTool(Name = "doc_new", Title = "New document", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a new part, assembly or drawing (owned by this server) and makes it the target document. Units default to MMGS " +
                 "(mm, g, s) whatever the template says. A drawing starts as A3 landscape, ISO sheet format, first angle, 1:1 " +
                 "(drw_sheet_setup changes it).")]
    public async Task<string> DocNew(
        [Description("part | assembly | drawing")] string type = "part",
        [Description("mmgs (default) | cgs | mks | ips | template (keep the template's units).")] string units = "mmgs",
        [Description("Optional template file (.prtdot/.asmdot/.drwdot); default: the user's default template.")] string? template = null,
        [Description("Optional document name (used as the file name when saved).")] string? name = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.NewAsync(type, units, template, name, cancellationToken));

    [McpServerTool(Name = "doc_list", Title = "List documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists open documents: handle (D1…), title, path, type, dirty, access (owned = created by this server; adopted = the user " +
                 "allowed edits; foreign = the user's, read-only), active and target flags, units.")]
    public async Task<string> DocList(CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ListAsync(cancellationToken));

    [McpServerTool(Name = "doc_activate", Title = "Activate document", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Makes a document active and the session's target. With allowEdit=true, a user document becomes editable for this server " +
                 "(a backup copy is saved first) — only do this when the user asked you to change that document.")]
    public async Task<string> DocActivate(
        [Description("Handle (D2), title or path of an open document.")] string doc,
        [Description("Allow this server to modify the user's document (never discard or rename it).")] bool allowEdit = false,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ActivateAsync(doc, allowEdit, cancellationToken));

    [McpServerTool(Name = "doc_save", Title = "Save document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Saves the target document. mode: inPlace (existing file), as (Save As: the document becomes the new file; owned documents " +
                 "only) or copy (writes a copy, the document is unchanged). Relative paths go under Documents\\solidworks-mcp. Existing files " +
                 "need overwrite=true (a file this server didn't write is backed up first). Verifies the file was written.")]
    public async Task<string> DocSave(
        [Description("inPlace | as | copy")] string mode = "inPlace",
        [Description("Target file for as/copy (.sldprt/.sldasm; the extension is added if missing).")] string? path = null,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.SaveAsync(doc, mode, path, overwrite, cancellationToken));

    [McpServerTool(Name = "doc_pack", Title = "Pack and Go", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Pack and Go: copies a saved part, assembly or drawing with every file it references (and the models' drawings) into one " +
                 "folder, optionally renamed with a prefix or suffix, the copies referencing each other. The open document stays as it is. " +
                 "Verified: every copy written; the copied assemblies and drawings reference the copies only.")]
    public Task<string> DocPack(
        IProgress<ProgressNotificationValue> progress,
        [Description("Target folder (relative paths go under Documents\\solidworks-mcp); default <name>_pack.")] string? folder = null,
        [Description("Put this before every copied file's name.")] string? prefix = null,
        [Description("Put this after every copied file's name.")] string? suffix = null,
        [Description("Also copy the drawings of the models.")] bool includeDrawings = true,
        [Description("All copies in one folder (else the original folder structure).")] bool flatten = true,
        [Description("Replace files that exist (backed up unless this server wrote them).")] bool overwrite = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(new PackService(session).PackAsync(new DocPackRequest
        {
            Doc = doc, Folder = folder, Prefix = prefix, Suffix = suffix, IncludeDrawings = includeDrawings, Flatten = flatten, Overwrite = overwrite,
        }, cancellationToken), progress, "doc_pack");

    [McpServerTool(Name = "doc_close", Title = "Close document", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Closes a document this server owns (or the user allowed). A document with unsaved changes needs save=true or discard=true " +
                 "(discard only for documents this server created). The user's own documents are never closed.")]
    public async Task<string> DocClose(
        [Description("Save before closing (the document must already have a file).")] bool save = false,
        [Description("Throw away unsaved changes (owned documents only).")] bool discard = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.CloseAsync(doc, save, discard, cancellationToken));
}
