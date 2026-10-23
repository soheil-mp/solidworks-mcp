using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

/// <summary>What an import (STEP, IGES, …) produced.</summary>
public sealed class ImportInfo
{
    /// <summary>step | iges | parasolid | acis | stl.</summary>
    public required string Format { get; init; }
    public int SolidBodies { get; init; }
    public int SurfaceBodies { get; init; }
    /// <summary>Mesh or graphics bodies (typical for STL): no B-rep faces or edges to build on.</summary>
    public int? MeshBodies { get; init; }
    /// <summary>Total volume of the solid bodies.</summary>
    public double? VolumeMm3 { get; init; }
    public BoxMm? Box { get; init; }
    /// <summary>Assembly imports: number of components.</summary>
    public int? Components { get; init; }
    /// <summary>The features the import created (e.g. Imported1).</summary>
    public IReadOnlyList<string>? Features { get; init; }
}

public sealed class OpenResult : ToolResult
{
    public required DocInfo Document { get; init; }
    public required string Message { get; init; }
    /// <summary>True when the file was already open: it was not reopened and kept its access class.</summary>
    public bool? AlreadyOpen { get; init; }
    /// <summary>True when SOLIDWORKS has the document open read-only (save it with mode copy/as).</summary>
    public bool? ReadOnly { get; init; }
    public ImportInfo? Imported { get; init; }
    /// <summary>Load messages decoded from SOLIDWORKS' error and warning flags.</summary>
    public IReadOnlyList<string>? LoadWarnings { get; init; }
    /// <summary>Referenced files that don't exist on disk (components, base parts, drawing models).</summary>
    public IReadOnlyList<string>? MissingReferences { get; init; }
    public IReadOnlyList<string>? Configurations { get; init; }
}

/// <summary>The file kinds doc_open accepts: native documents (OpenDoc6) and neutral formats (LoadFile4 import).</summary>
internal sealed record OpenKind(string Label, swDocumentTypes_e? Native, string? Neutral)
{
    public const string Extensions = ".sldprt .sldasm .slddrw (open); .step .stp .iges .igs .x_t .x_b .sat .sab .stl (import)";

    public static OpenKind? FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".sldprt" => new("part", swDocumentTypes_e.swDocPART, null),
        ".sldasm" => new("assembly", swDocumentTypes_e.swDocASSEMBLY, null),
        ".slddrw" => new("drawing", swDocumentTypes_e.swDocDRAWING, null),
        ".step" or ".stp" => new("STEP", null, "step"),
        ".iges" or ".igs" => new("IGES", null, "iges"),
        ".x_t" or ".x_b" => new("Parasolid", null, "parasolid"),
        ".sat" or ".sab" => new("ACIS", null, "acis"),
        ".stl" => new("STL", null, "stl"),
        _ => null,
    };

    /// <summary>Why an extension is refused (more specific than "unsupported" where it helps).</summary>
    public static string Refusal(string extension) => extension.ToLowerInvariant() switch
    {
        "" => "The file has no extension, so its type is unknown.",
        ".prtdot" or ".asmdot" or ".drwdot" => $"'{extension}' is a template: create a document from it with doc_new {{template}}.",
        ".dxf" or ".dwg" => "DXF/DWG import runs an interactive wizard in SOLIDWORKS, so doc_open doesn't import it.",
        _ => $"doc_open can't open '{extension}' files.",
    };
}

/// <summary>
/// doc_open: opens SOLIDWORKS documents silently (no rebuild prompt) and imports neutral CAD files as new parts. A
/// file that is already open is never reopened: it becomes the target and keeps its access class. Documents opened or
/// imported here are owned by the server; load messages and missing references are reported.
/// </summary>
public sealed class OpenService(SwSession session)
{
    public Task<OpenResult> OpenAsync(string path, bool readOnly, string? configuration, CancellationToken ct = default)
    {
        // Argument errors before anything is queued.
        if (string.IsNullOrWhiteSpace(path))
            throw SwException.InvalidArgument("'path' is empty.", $"Pass a file: {OpenKind.Extensions}.");
        string ext = Path.GetExtension(path.Trim().Trim('"'));
        var kind = OpenKind.FromExtension(ext)
                   ?? throw SwException.InvalidArgument(OpenKind.Refusal(ext), $"Supported: {OpenKind.Extensions}.");
        string? config = string.IsNullOrWhiteSpace(configuration) ? null : configuration.Trim();
        if (config is not null && kind.Native is not (swDocumentTypes_e.swDocPART or swDocumentTypes_e.swDocASSEMBLY))
            throw SwException.InvalidArgument($"'configuration' applies to parts and assemblies, not to {kind.Label} files.", "Omit it.");
        string full = session.Paths.ForRead(path); // IO_ERROR when the file doesn't exist

        var timeout = TimeSpan.FromSeconds(Math.Max(300, session.Options.DefaultTimeout.TotalSeconds));
        var rules = kind.Neutral is null ? null : new[] { DialogRules.ImportDiagnosticsNo, DialogRules.ImportFeatureRecognitionNo };
        return session.RunAsync("doc_open", ctx => Open(ctx, full, kind, readOnly, config),
            new RunOptions { Timeout = timeout, DialogRules = rules }, ct);
    }

    private static OpenResult Open(SwContext ctx, string full, OpenKind kind, bool readOnly, string? config)
    {
        var open = ctx.App.Documents();
        ctx.Documents.Prune(open);
        // Imports never match: an imported part has no file of its own until it is saved.
        var existing = open.FirstOrDefault(d => string.Equals(DocumentRegistry.SafePath(d), full, StringComparison.OrdinalIgnoreCase));
        if (existing is null && kind.Native is not null && ctx.App.OpenDocNamed(full) is IModelDoc2 named &&
            string.Equals(DocumentRegistry.SafePath(named), full, StringComparison.OrdinalIgnoreCase))
            existing = named;
        if (existing is not null) return AlreadyOpen(ctx, existing, readOnly, config);

        ctx.CheckUserCommand();
        return kind.Native is swDocumentTypes_e type
            ? OpenNative(ctx, full, type, readOnly, config, open)
            : Import(ctx, full, kind, readOnly, open);
    }

    // ------------------------------------------------------------------ already open

    private static OpenResult AlreadyOpen(SwContext ctx, IModelDoc2 doc, bool readOnly, string? config, List<string>? notes = null)
    {
        var entry = ctx.Documents.Track(doc);
        var active = ctx.ActiveDocOrNull;
        if (active is null || !DocLocator.SameDoc(active, doc))
        {
            int err = 0;
            ctx.App.ActivateDoc3(entry.Title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
            var now = ctx.ActiveDocOrNull;
            if (now is null || !DocLocator.SameDoc(now, doc))
                ctx.Warn($"'{entry.Title}' is open but could not be brought to the front (error {err}); it is the target anyway.");
        }
        ctx.Documents.Target = doc;
        entry.StampAfterLastOp = doc.GetUpdateStamp();

        string? cfg = ActiveConfiguration(doc);
        if (config is not null && !string.Equals(cfg, config, StringComparison.OrdinalIgnoreCase))
            ctx.Warn($"'{entry.Title}' was already open in configuration '{cfg}'; it was not switched to '{config}'.");
        bool ro = SafeReadOnly(doc);
        if (readOnly && !ro && entry.Class != DocClass.Foreign)
            ctx.Warn($"'{entry.Title}' was already open for editing; readOnly was not applied.");
        var info = DocumentService.Info(ctx, doc);
        return new OpenResult
        {
            Doc = info.Title,
            Document = info,
            AlreadyOpen = true,
            ReadOnly = ro ? true : null,
            LoadWarnings = notes is { Count: > 0 } ? notes : null,
            Message = entry.Class == DocClass.Foreign
                ? $"'{info.Title}' was already open in SOLIDWORKS (the user's document, {info.Handle}). It is now the target for reading; " +
                  $"this server won't change it. Only if the user asked for changes: doc_activate {{doc:'{info.Handle}', allowEdit:true}}."
                : $"'{info.Title}' was already open ({info.Access}); it is now the target document.",
        };
    }

    // ------------------------------------------------------------------ native documents

    private static OpenResult OpenNative(SwContext ctx, string full, swDocumentTypes_e type, bool readOnly, string? config,
        IReadOnlyList<IModelDoc2> before)
    {
        string name = Path.GetFileName(full);
        if (config is not null)
        {
            // Read from the file without opening it: a wrong name never opens (and then has to close) the document.
            var configs = ctx.App.ConfigNamesOf(full);
            if (configs.Length > 0)
            {
                config = configs.FirstOrDefault(c => string.Equals(c, config, StringComparison.OrdinalIgnoreCase))
                         ?? throw SwException.InvalidArgument($"'{name}' has no configuration '{config}'.",
                             $"Its configurations: {string.Join(", ", configs.Take(20))}. Omit 'configuration' to open the last saved one.");
            }
        }

        int errors = 0, warnings = 0;
        int options = (int)swOpenDocOptions_e.swOpenDocOptions_Silent | (readOnly ? (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly : 0);
        IModelDoc2? doc;
        var prefsBefore = PrefAudit.Take(ctx.App);
        try
        {
            using var prefs = ctx.SystemPrefs();
            // 0 would ask the user whether to rebuild when the document activates (a prompt).
            prefs.Integer(SystemPrefIds.RebuildOnActivation, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc);
            doc = (object?)ctx.App.OpenDoc6(full, (int)type, options, config ?? "", ref errors, ref warnings) as IModelDoc2;
        }
        finally
        {
            PrefAudit.Check(ctx, prefsBefore);
        }
        if (doc is null)
            throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not open '{full}': {LoadFlags.Describe(errors, warnings)}.",
                LoadFlags.Hint(errors, ctx.Session.Revision));

        var notes = LoadFlags.Notes(errors, warnings);
        bool wasOpen = before.Any(d => ReferenceEquals(d, doc) || DocLocator.SameDoc(d, doc)) ||
                       (warnings & (int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen) != 0 ||
                       (errors & (int)swFileLoadError_e.swFileAlreadyOpenWarn) != 0;
        if (wasOpen) return AlreadyOpen(ctx, doc, readOnly, config, notes); // never claim a document someone else opened

        ctx.Documents.SetClass(doc, DocClass.Owned, readOnly ? "opened (read-only)" : "opened");
        ctx.Documents.Target = doc;
        ctx.Documents.Track(doc).StampAfterLastOp = doc.GetUpdateStamp();

        // Read back what was asked for.
        string got = DocumentRegistry.SafePath(doc);
        if (got.Length == 0 || !string.Equals(Path.GetFullPath(got), full, StringComparison.OrdinalIgnoreCase))
            ctx.Warn($"SOLIDWORKS reports the path '{got}' for the opened document (requested '{full}').");
        if (doc.GetType() != (int)type)
            ctx.Warn($"'{name}' opened as a {SwContext.TypeName(doc.GetType())}, not a {SwContext.TypeName(type)}.");
        string? cfg = ActiveConfiguration(doc);
        if (config is not null && !string.Equals(cfg, config, StringComparison.OrdinalIgnoreCase))
            ctx.Warn($"'{name}' opened in configuration '{cfg}', not '{config}'.");
        bool ro = SafeReadOnly(doc);
        if (readOnly && !ro) ctx.Warn("readOnly was requested, but SOLIDWORKS opened the file for editing.");

        var missing = MissingReferences(doc);
        var configsNow = type == swDocumentTypes_e.swDocDRAWING ? [] : doc.ConfigNames();
        var info = DocumentService.Info(ctx, doc);
        string what = SwContext.TypeName(type);
        return new OpenResult
        {
            Doc = info.Title,
            Document = info,
            ReadOnly = ro ? true : null,
            LoadWarnings = notes.Count > 0 ? notes : null,
            MissingReferences = missing.Count > 0 ? missing : null,
            Configurations = configsNow.Length > 1 ? configsNow : null,
            Message = $"Opened {what} '{info.Title}'{(ro ? " read-only" : "")} ({info.Handle}); it is now the target document and this server may edit it. " +
                      (ro ? "Keep changes with doc_save mode 'copy' or 'as'." : "doc_save mode 'inPlace' overwrites the original file; mode 'copy' or 'as' keeps it.") +
                      (missing.Count > 0 ? $" {missing.Count} referenced file(s) are missing (see missingReferences)." : ""),
        };
    }

    /// <summary>Referenced files (components, base parts, drawing models) that don't exist on disk. Informational.</summary>
    private static List<string> MissingReferences(IModelDoc2 doc)
    {
        var missing = new List<string>();
        try
        {
            foreach (var p in doc.DependencyPairs(traverse: true))
            {
                // The array holds names and paths; only rooted paths can be checked. Virtual components ("x^asm") live inside the assembly.
                if (string.IsNullOrWhiteSpace(p) || p.Contains('^') || !Path.IsPathFullyQualified(p)) continue;
                bool exists;
                try { exists = File.Exists(p); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { exists = true; }
                if (!exists && !missing.Contains(p, StringComparer.OrdinalIgnoreCase)) missing.Add(p);
                if (missing.Count >= 20) break;
            }
        }
        catch (Exception ex) when (ex is COMException or SwException)
        {
            /* informational only */
        }
        return missing;
    }

    // ------------------------------------------------------------------ neutral formats (import)

    private static OpenResult Import(SwContext ctx, string full, OpenKind kind, bool readOnly, IReadOnlyList<IModelDoc2> before)
    {
        string name = Path.GetFileName(full);
        string format = kind.Neutral!;
        if (readOnly) ctx.Warn("readOnly applies to SOLIDWORKS files only; an import is a new, unsaved part.");

        int errors = 0;
        IModelDoc2? doc;
        var options = PrefAudit.Take(ctx.App);
        try
        {
            using var prefs = ctx.SystemPrefs();
            // Import Diagnostics would prompt; 3D Interconnect would link the file instead of translating it.
            prefs.Toggle(SystemPrefIds.ImportAutoRunDiagnostics, false)
                 .Toggle(SystemPrefIds.ImportNeutralRunDiagnostics, false)
                 .Toggle(SystemPrefIds.Enable3DInterconnect, false);
            if (kind.Neutral == "stl")
                // STL comes in as a graphics body (nothing to measure or build on) unless solid bodies are asked for;
                // a mesh has no units, so read it as mm like everything else here.
                prefs.Integer(SystemPrefIds.ImportStlModelType, (int)swImportStlVrmlModelType_e.swImportStlVrmlModelType_Solid)
                     .Integer(SystemPrefIds.ImportStlUnits, (int)swLengthUnit_e.swMM);
            object? data = ctx.App.ImportDataFor(full);
            doc = (object?)ctx.App.LoadFile4(full, "r", data, ref errors) as IModelDoc2;
        }
        finally
        {
            PrefAudit.Check(ctx, options);
        }
        if (doc is null)
            throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not import '{full}': {LoadFlags.Describe(errors, 0)}.",
                errors == 0 ? $"Check that it is a valid {kind.Label} file (open it in SOLIDWORKS to see the translator's messages)." : LoadFlags.Hint(errors, ctx.Session.Revision));

        var notes = LoadFlags.Notes(errors, 0);
        var created = ctx.App.Documents().Where(d => !before.Any(b => ReferenceEquals(b, d))).ToList();
        if (!created.Any(d => ReferenceEquals(d, doc)))
            return AlreadyOpen(ctx, doc, false, null, notes); // SOLIDWORKS handed back a document that was open before

        // Everything the import created belongs to this server (an assembly import also creates its parts).
        foreach (var c in created)
            ctx.Documents.SetClass(c, DocClass.Owned, ReferenceEquals(c, doc) ? $"imported ({format})" : $"created by importing {name}");
        ctx.Documents.Target = doc;

        int t = doc.GetType();
        if (t is (int)swDocumentTypes_e.swDocPART or (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            const int mmgs = (int)swUnitSystem_e.swUnitSystem_MMGS;
            doc.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0, mmgs);
            int units = doc.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0);
            if (units != mmgs) ctx.Warn($"The imported document's units could not be set to MMGS (they read {DocumentService.UnitName(units)}).");
        }

        var imported = Summarize(doc, format);
        bool hasGeometry = imported.SolidBodies + imported.SurfaceBodies + (imported.MeshBodies ?? 0) > 0 || imported.Components > 0;
        if (!hasGeometry)
        {
            string closed = CloseCreated(ctx, created);
            throw new SwException(ErrorCodes.VerifyFailed,
                $"Importing '{name}' produced a {SwContext.TypeName(t)} without any bodies, so the import failed; {closed}.",
                $"Check the {kind.Label} file (it may contain only curves or points, or be empty).");
        }
        if (imported is { SolidBodies: 0, SurfaceBodies: 0, MeshBodies: > 0 })
            ctx.Warn("The file was imported as a mesh (graphics) body: it can be viewed, measured and exported, but has no faces or edges to sketch on, cut or fillet.");
        ctx.Documents.Track(doc).StampAfterLastOp = doc.GetUpdateStamp();

        var info = DocumentService.Info(ctx, doc);
        string bodies = imported.Components is int n ? $"{n} component(s)"
            : string.Join(", ", new[]
            {
                imported.SolidBodies > 0 ? $"{imported.SolidBodies} solid bod{(imported.SolidBodies == 1 ? "y" : "ies")}" : null,
                imported.SurfaceBodies > 0 ? $"{imported.SurfaceBodies} surface bod{(imported.SurfaceBodies == 1 ? "y" : "ies")}" : null,
                imported.MeshBodies > 0 ? $"{imported.MeshBodies} mesh bod{(imported.MeshBodies == 1 ? "y" : "ies")}" : null,
            }.Where(s => s is not null));
        return new OpenResult
        {
            Doc = info.Title,
            Document = info,
            Imported = imported,
            LoadWarnings = notes.Count > 0 ? notes : null,
            Message = $"Imported {kind.Label} '{name}' as a new {SwContext.TypeName(t)} '{info.Title}' ({info.Handle}) with {bodies}, units {info.Units}. " +
                      "It is the target document, owned by this server and not saved yet: doc_save mode 'as' keeps it as a SOLIDWORKS file.",
        };
    }

    /// <summary>Bodies, volume and extents of an imported document (no COM types leave this method).</summary>
    private static ImportInfo Summarize(IModelDoc2 doc, string format)
    {
        int t = doc.GetType();
        if (t == (int)swDocumentTypes_e.swDocPART)
        {
            var part = (IPartDoc)doc;
            var snap = Snapshot.Take(doc); // solid + sheet bodies, solid volume, box over both
            int mesh = CountBodies(part, swBodyType_e.swMeshBody) + CountBodies(part, swBodyType_e.swGraphicsBody);
            var features = Tree.Walk(doc)
                .Where(f => f.GetTypeName2() is var ty && !Tree.SystemTypes.Contains(ty) && ty is not ("RefPlane" or "OriginProfileFeature" or "MaterialFolder"))
                .Select(f => f.Name).Take(10).ToList();
            return new ImportInfo
            {
                Format = format,
                SolidBodies = snap.SolidBodies,
                SurfaceBodies = snap.Bodies - snap.SolidBodies,
                MeshBodies = mesh > 0 ? mesh : null,
                VolumeMm3 = snap.SolidBodies > 0 ? Units.Mm3(snap.Volume) : null,
                Box = BoxMm.FromMeters(snap.Box),
                Features = features.Count > 0 ? features : null,
            };
        }
        if (t == (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            var asm = (IAssemblyDoc)doc;
            var box = asm.AsmBox();
            return new ImportInfo { Format = format, Components = asm.GetComponentCount(false), Box = BoxMm.FromMeters(box.Length == 6 ? box : null) };
        }
        return new ImportInfo { Format = format };
    }

    private static int CountBodies(IPartDoc part, swBodyType_e type)
    {
        try { return part.Bodies(type, visibleOnly: false).Count; }
        catch (Exception ex) when (ex is COMException or SwException) { return 0; }
    }

    /// <summary>Rollback of a failed import: closes the documents it created (they are new, unsaved and the server's).</summary>
    private static string CloseCreated(SwContext ctx, List<IModelDoc2> created)
    {
        var left = new List<string>();
        foreach (var d in created)
        {
            string title = ctx.Title(d);
            var open = ctx.App.Documents();
            if (title.Length == 0 || open.Count(o => string.Equals(ctx.Title(o), title, StringComparison.OrdinalIgnoreCase)) != 1)
            {
                left.Add(title);
                continue; // closing by title would be ambiguous
            }
            ctx.App.CloseDoc(title);
            if (ctx.App.Documents().Any(o => ReferenceEquals(o, d))) { left.Add(title); continue; }
            ctx.Documents.Remove(d);
            ctx.Entities.Forget(d);
        }
        return left.Count == 0 ? "the empty document was closed again" : $"could not close {string.Join(", ", left)} (close it with doc_close discard=true)";
    }

    // ------------------------------------------------------------------ helpers

    private static string? ActiveConfiguration(IModelDoc2 doc)
    {
        try { return doc.ConfigurationManager?.ActiveConfiguration?.Name; }
        catch (COMException) { return null; } // drawings
    }

    private static bool SafeReadOnly(IModelDoc2 doc)
    {
        try { return doc.IsOpenedReadOnly(); }
        catch (COMException) { return false; }
    }
}

/// <summary>Plain-language versions of SOLIDWORKS' <c>swFileLoadError_e</c> / <c>swFileLoadWarning_e</c> flags.</summary>
internal static class LoadFlags
{
    /// <summary>Error flags that prevent opening.</summary>
    private static readonly (int Flag, string Text)[] Errors =
    [
        ((int)swFileLoadError_e.swGenericError, "a generic load error"),
        ((int)swFileLoadError_e.swFileNotFoundError, "file not found"),
        ((int)swFileLoadError_e.swIdMatchError, "the file's internal id doesn't match"),
        ((int)swFileLoadError_e.swInvalidFileTypeError, "the file is not of the expected type"),
        ((int)swFileLoadError_e.swFutureVersion, "it was saved by a newer SOLIDWORKS version"),
        ((int)swFileLoadError_e.swFileWithSameTitleAlreadyOpen, "a different document with the same file name is already open"),
        ((int)swFileLoadError_e.swLiquidMachineDoc, "it is a rights-managed (Liquid Machines) document"),
        ((int)swFileLoadError_e.swLowResourcesError, "SOLIDWORKS is low on resources"),
        ((int)swFileLoadError_e.swNoDisplayData, "it has no display data"),
        ((int)swFileLoadError_e.swAddinInteruptError, "an add-in interrupted the load"),
        ((int)swFileLoadError_e.swFileRequiresRepairError, "the file needs repair (or its content doesn't match its extension)"),
        ((int)swFileLoadError_e.swFileCriticalDataRepairError, "critical data in the file needs repair"),
        ((int)swFileLoadError_e.swApplicationBusy, "SOLIDWORKS is busy"),
        ((int)swFileLoadError_e.swConnectedIsOffline, "the 3DEXPERIENCE connection is offline"),
    ];

    /// <summary>Flags of the errors argument that are only warnings when the document still opened.</summary>
    private static readonly (int Flag, string Text)[] ErrorWarnings =
    [
        ((int)swFileLoadError_e.swReadOnlyWarn, "opened read-only (the file is read-only or in use)"),
        ((int)swFileLoadError_e.swSharingViolationWarn, "the file is open elsewhere (sharing violation)"),
        ((int)swFileLoadError_e.swDrawingANSIUpdateWarn, "drawing updated to the ANSI standard"),
        ((int)swFileLoadError_e.swSheetScaleUpdateWarn, "sheet scale updated"),
        ((int)swFileLoadError_e.swNeedsRegenWarn, "needs a rebuild"),
        ((int)swFileLoadError_e.swBasePartNotLoadedWarn, "a base (derived/mirrored) part was not loaded"),
        ((int)swFileLoadError_e.swFileAlreadyOpenWarn, "already open"),
        ((int)swFileLoadError_e.swDrawingsOnlyRapidDraftWarn, "opened as a detached (RapidDraft) drawing"),
        ((int)swFileLoadError_e.swViewOnlyRestrictions, "view-only restrictions"),
        ((int)swFileLoadError_e.swViewMissingReferencedConfig, "a drawing view references a missing configuration"),
        ((int)swFileLoadError_e.swDrawingSFSymbolConvertWarn, "surface-finish symbols were converted"),
    ];

    private static readonly (int Flag, string Text)[] Warnings =
    [
        ((int)swFileLoadWarning_e.swFileLoadWarning_IdMismatch, "internal id mismatch (a referenced file may have been replaced)"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_ReadOnly, "opened read-only (the file is read-only or in use)"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_SharingViolation, "the file is open elsewhere (sharing violation)"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_DrawingANSIUpdate, "drawing updated to the ANSI standard"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_SheetScaleUpdate, "sheet scale updated"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_NeedsRegen, "needs a rebuild"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_BasePartNotLoaded, "a base (derived/mirrored) part was not loaded"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_AlreadyOpen, "already open"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_DrawingsOnlyRapidDraft, "opened as a detached (RapidDraft) drawing"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_ViewOnlyRestrictions, "view-only restrictions"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_ViewMissingReferencedConfig, "a drawing view references a missing configuration"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_DrawingSFSymbolConvert, "surface-finish symbols were converted"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_RevolveDimTolerance, "revolve dimension tolerances were changed"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_ModelOutOfDate, "the model is out of date (rebuild needed)"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_DimensionsReferencedIncorrectlyToModels, "some dimensions reference the model incorrectly"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_ComponentMissingReferencedConfig, "a component references a missing configuration"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_InvisibleDoc_LinkedDesignTableUpdateFail, "a linked design table could not be updated"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_MissingDesignTable, "the design table is missing"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_AutomaticRepair, "SOLIDWORKS repaired the file automatically"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_CriticalDataRepair, "critical data was repaired"),
        ((int)swFileLoadWarning_e.swFileLoadWarning_MissingExternalReferences, "external references are missing"),
    ];

    /// <summary>All set flags of a failed load, in words.</summary>
    public static string Describe(int errors, int warnings)
    {
        var parts = Set(Errors, errors).Concat(Set(ErrorWarnings, errors)).Concat(Set(Warnings, warnings)).Distinct().ToList();
        int known = Errors.Concat(ErrorWarnings).Aggregate(0, (acc, x) => acc | x.Flag);
        if ((errors & ~known) != 0) parts.Add($"error code {errors}");
        return parts.Count == 0 ? $"no error code (error {errors})" : string.Join("; ", parts);
    }

    /// <summary>The warnings of a load that succeeded (empty when there are none).</summary>
    public static List<string> Notes(int errors, int warnings) =>
        Set(ErrorWarnings, errors).Concat(Set(Warnings, warnings)).Concat(Set(Errors, errors)).Distinct().ToList();

    public static string Hint(int errors, string? revision)
    {
        bool Has(swFileLoadError_e e) => (errors & (int)e) != 0;
        if (Has(swFileLoadError_e.swFileNotFoundError)) return "Check the path: absolute, or relative to the output folder (Documents\\solidworks-mcp).";
        if (Has(swFileLoadError_e.swFutureVersion))
            return $"This SOLIDWORKS ({revision ?? "this release"}) is older than the one that saved the file; open it there, or ask for a STEP or Parasolid copy.";
        if (Has(swFileLoadError_e.swFileWithSameTitleAlreadyOpen))
            return "SOLIDWORKS can't open two documents with the same file name: close the other one (doc_list shows it) or copy this file under another name.";
        if (Has(swFileLoadError_e.swInvalidFileTypeError) || Has(swFileLoadError_e.swFileRequiresRepairError) || Has(swFileLoadError_e.swFileCriticalDataRepairError))
            return "The file is damaged, or its content doesn't match its extension.";
        if (Has(swFileLoadError_e.swApplicationBusy)) return "Wait until SOLIDWORKS is idle, then retry.";
        if (Has(swFileLoadError_e.swLowResourcesError)) return "Close documents that aren't needed, then retry.";
        return "Check dialog_list for a message, or try opening the file in SOLIDWORKS itself.";
    }

    private static IEnumerable<string> Set((int Flag, string Text)[] table, int value) =>
        table.Where(x => x.Flag != 0 && (value & x.Flag) == x.Flag).Select(x => x.Text);
}
