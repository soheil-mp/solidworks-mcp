using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>asm_part_new options.</summary>
public sealed record AsmPartNewRequest
{
    public string? Doc { get; init; }
    public string? Name { get; init; }
    /// <summary>Folder for the new file (default: the assembly's).</summary>
    public string? Folder { get; init; }
    /// <summary>In context: a flat face (of a component) whose outline becomes the new part's first extrusion.</summary>
    public EntityRef? Face { get; init; }
    public double? DepthMm { get; init; }
    /// <summary>In context: extrude into the face instead of away from it.</summary>
    public bool Reverse { get; init; }
}

public sealed class AsmPartNewResult : ToolResult
{
    public required string Component { get; init; }
    public required string Path { get; init; }
    /// <summary>The new part's handle, open and the target document (empty part).</summary>
    public string? PartDoc { get; init; }
    public bool InContext { get; init; }
    public double? VolumeMm3 { get; init; }
    /// <summary>The new part's references to the assembly (in-context features).</summary>
    public IReadOnlyList<string>? ExternalReferences { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>body_save options.</summary>
public sealed record BodySaveRequest
{
    public string? Doc { get; init; }
    /// <summary>Default: every solid body.</summary>
    public IReadOnlyList<EntityRef>? Bodies { get; init; }
    /// <summary>Default: the part's folder.</summary>
    public string? Folder { get; init; }
    /// <summary>File names are prefix + body name (default prefix: the part's name and "_").</summary>
    public string? Prefix { get; init; }
    /// <summary>File names for the bodies, in their order (then the prefix defaults to none).</summary>
    public IReadOnlyList<string>? Names { get; init; }
    /// <summary>Also an assembly of the new parts, in place (a file name or path).</summary>
    public string? Assembly { get; init; }
}

public sealed class SavedBody
{
    public required string Body { get; init; }
    public required string Path { get; init; }
    public double VolumeMm3 { get; init; }
}

public sealed class BodySaveResult : ToolResult
{
    public required string Feature { get; init; }
    public required IReadOnlyList<SavedBody> Files { get; init; }
    public string? Assembly { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>part_derive options.</summary>
public sealed record PartDeriveRequest
{
    public string? Doc { get; init; }
    /// <summary>insert | mirror.</summary>
    public string? Kind { get; init; }
    /// <summary>insert: the part to insert into this one.</summary>
    public string? Path { get; init; }
    /// <summary>mirror: the plane or flat face to mirror about.</summary>
    public EntityRef? Plane { get; init; }
    /// <summary>mirror: where to save the new part (default: next to this one, "_mirror" added).</summary>
    public string? NewPath { get; init; }
    public bool BreakLink { get; init; }
}

public sealed class PartDeriveResult : ToolResult
{
    public required string Kind { get; init; }
    public required string Feature { get; init; }
    /// <summary>mirror: the new part's handle (open).</summary>
    public string? NewDoc { get; init; }
    public string? Path { get; init; }
    public double VolumeMm3 { get; init; }
    public int Bodies { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// Top-down design. asm_part_new: a new part in an assembly, either empty with the assembly's origin and axes (fixed at
/// the origin, open for the part tools) or in context (IAssemblyDoc.InsertNewPart2 on a face, its outline converted and
/// extruded while the part is edited in place, so it follows the face). body_save: a multibody part's bodies as parts
/// (IFeatureManager.CreateSaveBodyFeature), optionally with an assembly of them. part_derive: a part inserted into this
/// one (IPartDoc.InsertPart3) or this part mirrored into a new one (IPartDoc.MirrorPart2).
/// </summary>
public sealed class TopDownService(SwSession session)
{
    private const double Rel = 1e-6;

    // ================================================================== asm_part_new

    public async Task<AsmPartNewResult> PartNewAsync(AsmPartNewRequest q, CancellationToken ct = default)
    {
        string name = FileName(q.Name, "name");
        bool inContext = q.Face is not null;
        if (inContext && q.DepthMm is not (> 0 and < 100_000))
            throw SwException.InvalidArgument("An in-context part needs depthMm > 0 (how far its first extrusion goes from the face).");
        if (!inContext && (q.DepthMm is not null || q.Reverse))
            throw SwException.InvalidArgument("depthMm and reverse need a face (the in-context extrusion).");
        var (asm, path) = await session.RunAsync("asm_part_new", ctx =>
        {
            var d = (IModelDoc2)ctx.Assembly();
            string file = Path.Combine(Folder(ctx, q.Folder, DocumentRegistry.SafePath(d), "the assembly"), name + ".sldprt");
            if (File.Exists(file)) throw new SwException(ErrorCodes.IoError, $"'{file}' already exists.", "Choose another name.");
            return (ctx.DocEntry.Handle, ctx.Paths.ForWrite(file, false, out _));
        }, RunOptions.Write(q.Doc), ct);

        if (inContext)
            return await session.RunAsync("asm_part_new", ctx => InContext(ctx, q, path),
                RunOptions.Write(asm) with { Timeout = TimeSpan.FromSeconds(Math.Max(180, session.Options.DefaultTimeout.TotalSeconds)) }, ct);

        var docs = new DocumentService(session);
        string part = (await docs.NewAsync("part", "mmgs", null, name, ct)).Document.Handle;
        await docs.SaveAsync(part, "as", path, false, ct);
        var ins = await new AssemblyService(session).InsertAsync(new AsmInsertRequest { Doc = asm, Path = path, OriginMm = [0, 0, 0], Fixed = true }, ct);
        return new AsmPartNewResult
        {
            Doc = ins.Doc, Component = ins.Component.Name, Path = path, PartDoc = part,
            Checks = [.. ins.Verified, "the part's origin and axes are the assembly's: model it in assembly coordinates, then doc_rebuild the assembly"],
        };
    }

    private static AsmPartNewResult InContext(SwContext ctx, AsmPartNewRequest q, string path)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        BodyKit.NoOpenSketch(d);
        var face = (IFace2)EntityResolver.Resolve(ctx, d, q.Face, "face", EntityKind.Face).Obj;
        var g = Topology.Describe(face);
        if (g.Surface != "plane") throw SwException.InvalidArgument($"The face is a {g.Surface}; an in-context part starts on a flat face.");
        double area = face.GetArea(), depth = Units.M(q.DepthMm!.Value);
        int before = asm.GetComponentCount(true);
        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        d.ClearSelection2(true);
        int status = asm.InsertNewPart2(path, face);
        if (status != (int)swInsertNewPartErrorCode_e.swInsertNewPartError_NoError)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no new part ({NewPartError(status)}).");
        IComponent2? comp = null;
        var checks = new List<string>();
        string? problem = null;
        try
        {
            comp = asm.GetEditTargetComponent()
                   ?? throw new SwException(ErrorCodes.FeatureFailed, "The new part isn't being edited in the assembly.");
            var sm = d.SketchManager;
            if (sm.ActiveSketch is null)
            {
                d.ClearSelection2(true);
                ((IEntity)face).Select4(false, null);
                sm.InsertSketch(true);
            }
            var sketch = sm.ActiveSketch as ISketch ?? throw new SwException(ErrorCodes.SketchState, "No sketch opened on the face.");
            // Every edge of every loop, selected: with the face selected, SketchUseEdge3(chain, innerLoops) converts the
            // outer loop only (innerLoops false) or the inner loops only (true) (VERIFIED).
            d.ClearSelection2(true);
            var edges = face.Edges();
            foreach (var edge in edges) ((IEntity)edge).Select4(true, null);
            bool converted = sm.SketchUseEdge3(false, false);
            d.ClearSelection2(true);
            int segments = ComArgs.Objects<ISketchSegment>((object?)sketch.GetSketchSegments()).Count;
            if (!converted || segments == 0) throw new SwException(ErrorCodes.FeatureFailed, "Converting the face's outline made no sketch segments.");
            checks.Add($"the face's outline converted: {segments} segment(s)");
            var feat = (object?)d.FeatureManager.FeatureExtrusion3(true, false, q.Reverse, (int)swEndConditions_e.swEndCondBlind, 0, depth, 0,
                false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            if (feat is null)
            {
                if (sm.ActiveSketch is not null) sm.InsertSketch(true);
                d.ClearSelection2(true);
                ((IFeature)sketch).Select2(false, 0);
                feat = (object?)d.FeatureManager.FeatureExtrusion3(true, false, q.Reverse, (int)swEndConditions_e.swEndCondBlind, 0, depth, 0,
                    false, false, false, false, 0, 0, false, false, false, false, true, true, true, (int)swStartConditions_e.swStartSketchPlane, 0, false);
            }
            if (feat is null) problem = "SOLIDWORKS made no extrusion of the outline.";
        }
        catch (SwException ex) { problem = ex.Message; }
        finally
        {
            if (d.SketchManager.ActiveSketch is not null) d.SketchManager.InsertSketch(true);
            asm.EditAssembly();
            d.ClearSelection2(true);
        }
        d.EditRebuild3();
        var part = comp is null ? null : (object?)comp.GetModelDoc2() as IModelDoc2;
        double volume = 0;
        if (part is IPartDoc pd)
            foreach (var b in pd.Bodies(swBodyType_e.swSolidBody, visibleOnly: false))
                volume += ComArgs.Doubles((object?)b.GetMassProperties(0)) is { Length: > 3 } mp ? mp[3] : 0;
        double want = area * depth;
        if (problem is null && Math.Abs(volume - want) > Rel * want)
            problem = $"the new part is {Math.Round(Units.Mm3(volume), 3)} mm³, not the face's area × depth = {Math.Round(Units.Mm3(want), 3)} mm³";
        if (problem is not null)
        {
            string removed = comp is null ? "" : AssemblyService.RemoveComponent(d, asm, comp, before);
            throw new SwException(ErrorCodes.FeatureFailed, $"asm_part_new: {problem} {removed}",
                "The face must be flat; a face of a lightweight component needs resolving first.");
        }
        checks.Add($"volume {Math.Round(Units.Mm3(volume), 3)} mm³ = the face's {Math.Round(Units.Mm2(area), 3)} mm² × {q.DepthMm} mm");
        var refs = ExternalReferences(part!);
        if (refs.Count == 0) ctx.Warn("The new part lists no external references; it may not follow the face.");
        else checks.Add($"{refs.Count} in-context reference(s) to the assembly: it follows the face");
        if (UnitSystem(part!) != UnitSystem(d)) SetUnitSystem(part!, UnitSystem(d));
        int e = 0, w = 0;
        if (!part!.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w) || !File.Exists(path))
            ctx.Warn($"The new part could not be saved to '{path}' (error {e}); it is saved with the assembly.");
        else
        {
            ctx.Paths.RecordWritten(path);
            checks.Add($"saved to '{path}'");
        }
        return new AsmPartNewResult
        {
            Doc = ctx.Title(d), Component = comp!.Name2, Path = path, InContext = true, VolumeMm3 = Math.Round(Units.Mm3(volume), 3),
            ExternalReferences = refs, Checks = checks,
        };
    }

    private static string NewPartError(int status) => status switch
    {
        (int)swInsertNewPartErrorCode_e.swInsertNewPartError_FileAlreadyExists => "the file exists",
        (int)swInsertNewPartErrorCode_e.swInsertNewPartError_FolderDoesNotExist => "the folder doesn't exist",
        (int)swInsertNewPartErrorCode_e.swInsertNewPartError_NotAFaceOrPlane => "not a face or plane",
        (int)swInsertNewPartErrorCode_e.swInsertNewPartError_CannotSelectFaceOrPlane => "the face could not be selected",
        _ => $"error {status}",
    };

    /// <summary>The document's external references as "feature → file: entity (status)".</summary>
    private static List<string> ExternalReferences(IModelDoc2 d)
    {
        try
        {
            d.Extension.ListExternalFileReferences(out object models, out _, out object features, out _, out object status, out object entities, out _, out _, out _);
            var m = ComArgs.Strings(models);
            var f = ComArgs.Strings(features);
            var en = ComArgs.Strings(entities);
            var st = ComArgs.Ints(status);
            return m.Select((p, i) => $"{(i < f.Length ? f[i] : "?")} → {Path.GetFileName(p)}{(i < en.Length && en[i].Length > 0 ? ": " + en[i] : "")}" +
                                      $"{(i < st.Length ? $" ({RefStatus(st[i])})" : "")}").ToList();
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return []; }
    }

    private static string RefStatus(int s) => s switch { 1 => "in context", 2 => "out of context", 3 => "broken", 4 => "locked", _ => $"status {s}" };

    // ================================================================== body_save

    public Task<BodySaveResult> BodySaveAsync(BodySaveRequest q, CancellationToken ct = default) =>
        session.RunAsync("body_save", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            string own = DocumentRegistry.SafePath(d);
            if (own.Length == 0) throw SwException.InvalidArgument("Save the part first (doc_save): the new parts refer to its file.");
            // All bodies in name order (SOLIDWORKS' own order changes between rebuilds; numbers compare as numbers), or the
            // given ones in their order: 'names' follows it.
            var bodies = q.Bodies is null
                ? part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false)
                    .OrderBy(b => System.Text.RegularExpressions.Regex.Replace(b.Name ?? "", @"\d+", m => m.Value.PadLeft(10, '0')), StringComparer.OrdinalIgnoreCase).ToList()
                : BodyKit.ResolveBodies(ctx, d, q.Bodies, "bodies", BodyKind.Solid).Select(r => (IBody2)r.Obj).ToList();
            if (bodies.Count == 0) throw SwException.InvalidArgument("The part has no solid body to save.");
            string folder = Folder(ctx, q.Folder, own, "the part");
            if (q.Names is not null && q.Names.Count != bodies.Count)
                throw SwException.InvalidArgument($"'names' has {q.Names.Count} name(s) for {bodies.Count} bod(ies).");
            string prefix = q.Prefix ?? (q.Names is null ? Path.GetFileNameWithoutExtension(own) + "_" : "");
            // Body names after a Save Bodies feature read "Save Bodies1[1]": brackets don't go into file names.
            var names = bodies.Select(b => b.Name ?? "Body").ToList();
            var files = (q.Names ?? names).Select(n => Path.Combine(folder, Safe(prefix + n.Trim()).Replace('[', '_').Replace("]", "") + ".sldprt")).ToList();
            if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
                throw SwException.InvalidArgument("Two bodies have the same name, so their files would too.", "Rename a body or save them one by one with a prefix.");
            foreach (var f in files)
                if (File.Exists(f)) throw new SwException(ErrorCodes.IoError, $"'{f}' already exists.", "Use another prefix or folder.");
            string? asmPath = null;
            if (!string.IsNullOrWhiteSpace(q.Assembly))
            {
                string a = q.Assembly.Trim().Trim('"');
                if (!a.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) a += ".sldasm";
                asmPath = Path.IsPathRooted(a) ? Path.GetFullPath(a) : Path.Combine(folder, a);
                if (File.Exists(asmPath)) throw new SwException(ErrorCodes.IoError, $"'{asmPath}' already exists.");
                ctx.Paths.ForWrite(asmPath, false, out _);
            }
            foreach (var f in files) ctx.Paths.ForWrite(f, false, out _);
            var volumes = bodies.Select(b => ComArgs.Doubles((object?)b.GetMassProperties(0)) is { Length: > 3 } mp ? mp[3] : 0).ToList();
            var openBefore = ctx.App.Documents().ToList();

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            // The new parts come from the default part template; when its units differ from the part's, SOLIDWORKS asks
            // whether to change them (a task dialog, VERIFIED with a CGS template). The part takes the template's units
            // for the call instead, and the new parts get the part's units after.
            int units = UnitSystem(d), tplUnits = TemplateUnits(ctx);
            if (tplUnits > 0 && tplUnits != units) SetUnitSystem(d, tplUnits);
            IFeature? feat;
            try { feat = (object?)d.FeatureManager.CreateSaveBodyFeature(ComArgs.DispatchArray(bodies), files.ToArray(), asmPath ?? "", false, true) as IFeature; }
            finally { if (UnitSystem(d) != units) SetUnitSystem(d, units); }
            var checks = new List<string>();
            var saved = new List<SavedBody>();
            try
            {
                if (feat is null) throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS made no Save Bodies feature.");
                foreach (var f in files.Append(asmPath).OfType<string>().Where(File.Exists)) ToUnits(ctx, f, units);
                for (int i = 0; i < files.Count; i++)
                {
                    if (!File.Exists(files[i])) throw new SwException(ErrorCodes.VerifyFailed, $"'{files[i]}' was not written.");
                    double v = FileVolume(ctx, files[i], swDocumentTypes_e.swDocPART, out _);
                    if (Math.Abs(v - volumes[i]) > Rel * volumes[i] + 1e-12)
                        throw new SwException(ErrorCodes.VerifyFailed, $"'{Path.GetFileName(files[i])}' is {Math.Round(Units.Mm3(v), 3)} mm³, its body {Math.Round(Units.Mm3(volumes[i]), 3)} mm³.");
                    ctx.Paths.RecordWritten(files[i]);
                    saved.Add(new SavedBody { Body = names[i], Path = files[i], VolumeMm3 = Math.Round(Units.Mm3(v), 3) });
                }
                checks.Add($"{files.Count} part file(s), each with its body's volume");
                if (asmPath is not null)
                {
                    if (!File.Exists(asmPath)) throw new SwException(ErrorCodes.VerifyFailed, $"'{asmPath}' was not written.");
                    double v = FileVolume(ctx, asmPath, swDocumentTypes_e.swDocASSEMBLY, out int comps);
                    if (comps != files.Count || Math.Abs(v - volumes.Sum()) > Rel * volumes.Sum())
                        throw new SwException(ErrorCodes.VerifyFailed, $"The assembly has {comps} component(s) and {Math.Round(Units.Mm3(v), 3)} mm³, not {files.Count} and {Math.Round(Units.Mm3(volumes.Sum()), 3)}.");
                    ctx.Paths.RecordWritten(asmPath);
                    checks.Add($"assembly of the {comps} parts, {Math.Round(Units.Mm3(v), 3)} mm³ in all");
                }
            }
            finally
            {
                // Documents the feature opened (new parts, the assembly) are closed again; the files stay.
                foreach (var x in ctx.App.Documents().Where(x => !openBefore.Any(o => DocLocator.SameDoc(o, x))).ToList())
                    try { ctx.App.CloseDoc(x.GetTitle()); }
                    catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* left open */ }
            }
            return new BodySaveResult { Doc = ctx.Title(d), Feature = feat!.Name, Files = saved, Assembly = asmPath, Checks = checks };
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(180, session.Options.DefaultTimeout.TotalSeconds)) }, ct);

    /// <summary>The volume (m³) of a part or assembly file: open already, or opened silently read-only and closed again.</summary>
    private static double FileVolume(SwContext ctx, string path, swDocumentTypes_e type, out int components)
    {
        var open = ctx.App.Documents().FirstOrDefault(x => string.Equals(DocumentRegistry.SafePath(x), path, StringComparison.OrdinalIgnoreCase));
        var d = open;
        if (d is null)
        {
            int e = 0, w = 0;
            d = ctx.App.OpenDoc6(path, (int)type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref e, ref w)
                ?? throw new SwException(ErrorCodes.IoError, $"'{path}' could not be opened (error {e}).");
        }
        try
        {
            components = d is IAssemblyDoc a ? a.GetComponentCount(true) : 0;
            return (object?)d.Extension.CreateMassProperty2() is IMassProperty2 mp ? mp.Volume : 0;
        }
        finally
        {
            if (open is null)
                try { ctx.App.CloseDoc(d.GetTitle()); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* left open */ }
        }
    }

    // ================================================================== part_derive

    public Task<PartDeriveResult> DeriveAsync(PartDeriveRequest q, CancellationToken ct = default)
    {
        string kind = (q.Kind ?? "").Trim().ToLowerInvariant();
        if (kind is not ("insert" or "mirror")) throw SwException.InvalidArgument($"kind '{q.Kind}' is not supported.", "Use insert or mirror.");
        if (kind == "insert" && (string.IsNullOrWhiteSpace(q.Path) || q.Plane is not null || q.NewPath is not null))
            throw SwException.InvalidArgument("kind insert takes 'path' (the part to insert), not plane or newPath.");
        if (kind == "mirror" && (q.Plane is null || q.Path is not null))
            throw SwException.InvalidArgument("kind mirror takes 'plane' (and newPath), not path.");
        return session.RunAsync("part_derive", ctx => kind == "insert" ? Insert(ctx, q) : Mirror(ctx, q),
            RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(180, session.Options.DefaultTimeout.TotalSeconds)) }, ct);
    }

    private static PartDeriveResult Insert(SwContext ctx, PartDeriveRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        string src = ctx.Paths.ForRead(q.Path!, "path");
        if (!src.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) throw SwException.InvalidArgument("'path' must be a part (.sldprt).");
        if (string.Equals(src, DocumentRegistry.SafePath(d), StringComparison.OrdinalIgnoreCase)) throw SwException.InvalidArgument("A part can't be inserted into itself.");
        double vSrc = FileVolume(ctx, src, swDocumentTypes_e.swDocPART, out _);
        var (v0, n0) = Volume(part);
        int options = (int)(swInsertPartOptions_e.swInsertPartImportSolids | swInsertPartOptions_e.swInsertPartImportSurfaces | swInsertPartOptions_e.swInsertPartImportMaterial)
                      | (q.BreakLink ? (int)swInsertPartOptions_e.swInsertPartBreakLink : 0);
        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var feat = (object?)part.InsertPart3(src, options, "") as IFeature
                   ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not insert '{src}'.");
        d.EditRebuild3();
        var (v1, n1) = Volume(part);
        if (Math.Abs(v1 - v0 - vSrc) > Rel * Math.Max(vSrc, v1) + 1e-12)
        {
            UndoFeature(d, feat);
            throw new SwException(ErrorCodes.VerifyFailed, $"The volume went from {Math.Round(Units.Mm3(v0), 3)} to {Math.Round(Units.Mm3(v1), 3)} mm³, not by the inserted part's {Math.Round(Units.Mm3(vSrc), 3)} mm³; removed again.");
        }
        return new PartDeriveResult
        {
            Doc = ctx.Title(d), Kind = "insert", Feature = feat.Name, Path = src, VolumeMm3 = Math.Round(Units.Mm3(v1), 3), Bodies = n1,
            Checks =
            [
                $"the volume grew by the inserted part's {Math.Round(Units.Mm3(vSrc), 3)} mm³ ({n1 - n0} bod(ies) added)",
                q.BreakLink ? "link broken: it won't follow the source" : "linked: it follows the source part when that changes",
                "inserted at this part's origin (body_move places it elsewhere)",
            ],
        };
    }

    private static PartDeriveResult Mirror(SwContext ctx, PartDeriveRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        BodyKit.NoOpenSketch(d);
        string own = DocumentRegistry.SafePath(d);
        if (own.Length == 0 && !q.BreakLink) throw SwException.InvalidArgument("Save the part first (doc_save): the mirrored part refers to its file.", "Or pass breakLink:true.");
        string target = q.NewPath is { Length: > 0 } np
            ? ctx.Paths.ForWrite(np.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ? np : np + ".sldprt", false, out _)
            : own.Length > 0
                ? ctx.Paths.ForWrite(Path.Combine(Path.GetDirectoryName(own)!, Path.GetFileNameWithoutExtension(own) + "_mirror.sldprt"), false, out _)
                : throw SwException.InvalidArgument("'newPath' is required for an unsaved part.");
        var plane = EntityResolver.Resolve(ctx, d, q.Plane, "plane", EntityKind.Plane, EntityKind.Face);
        var (point, normal) = PlaneOf(plane.Obj);
        var (v0, n0) = Volume(part);
        double[] box0 = ComArgs.Doubles((object?)part.GetPartBox(true));
        d.ClearSelection2(true);
        if (!plane.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {plane.Label}.");
        var feat = (object?)part.MirrorPart2(q.BreakLink, (int)(swMirrorPartOptions_e.swMirrorPartOptions_ImportSolids | swMirrorPartOptions_e.swMirrorPartOptions_ImportSurfaces), out ModelDoc2 result) as IFeature;
        d.ClearSelection2(true);
        var mirrored = (object?)result as IModelDoc2 ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS made no mirrored part.");
        ctx.Documents.SetClass(mirrored, DocClass.Owned, "part_derive");
        var checks = new List<string>();
        try
        {
            var mp = (IPartDoc)mirrored;
            var (v1, n1) = Volume(mp);
            if (Math.Abs(v1 - v0) > Rel * v0) throw new SwException(ErrorCodes.VerifyFailed, $"The mirror is {Math.Round(Units.Mm3(v1), 3)} mm³, the part {Math.Round(Units.Mm3(v0), 3)} mm³.");
            checks.Add($"same volume, {Math.Round(Units.Mm3(v1), 3)} mm³, in {n1} bod(ies)");
            double[] box1 = ComArgs.Doubles((object?)mp.GetPartBox(true));
            if (box0.Length == 6 && box1.Length == 6 && Axis(normal) is int ax)
            {
                // About an axis-aligned plane the box is reflected: [lo, hi] → [2p − hi, 2p − lo] along that axis.
                double p = point[ax];
                double lo = 2 * p - box0[ax + 3], hi = 2 * p - box0[ax];
                if (Math.Abs(box1[ax] - lo) > 1e-7 || Math.Abs(box1[ax + 3] - hi) > 1e-7)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The mirror's box along {"xyz"[ax]} is [{Units.Mm(box1[ax])}, {Units.Mm(box1[ax + 3])}] mm, not [{Units.Mm(lo)}, {Units.Mm(hi)}].");
                checks.Add($"the box is reflected about the plane along {"xyz"[ax]}");
            }
            if (UnitSystem(mirrored) != UnitSystem(d)) SetUnitSystem(mirrored, UnitSystem(d));
            int e = 0, w = 0;
            if (!mirrored.Extension.SaveAs3(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref e, ref w) || !File.Exists(target))
                throw new SwException(ErrorCodes.IoError, $"The mirrored part could not be saved to '{target}' (error {e}).");
            ctx.Paths.RecordWritten(target);
            checks.Add($"saved to '{target}'");
            var entry = ctx.Documents.Track(mirrored);
            return new PartDeriveResult
            {
                Doc = ctx.Title(d), Kind = "mirror", Feature = feat?.Name ?? "", NewDoc = entry.Handle, Path = target,
                VolumeMm3 = Math.Round(Units.Mm3(v1), 3), Bodies = n1, Checks = checks,
            };
        }
        catch
        {
            try { ctx.App.CloseDoc(mirrored.GetTitle()); }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* left open */ }
            throw;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static int UnitSystem(IModelDoc2 d) => d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0);

    private static void SetUnitSystem(IModelDoc2 d, int units) =>
        d.Extension.SetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0, units);

    /// <summary>The unit system of the default part template (0 when it can't be read): a scratch part made from it.</summary>
    private static int TemplateUnits(SwContext ctx)
    {
        string tpl = ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
        if (string.IsNullOrWhiteSpace(tpl) || !File.Exists(tpl)) return 0;
        var active = ctx.ActiveDocOrNull;
        var scratch = (object?)ctx.App.NewDocument(tpl, 0, 0, 0) as IModelDoc2;
        if (scratch is null) return 0;
        try { return UnitSystem(scratch); }
        finally
        {
            try { ctx.App.CloseDoc(scratch.GetTitle()); }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* left open */ }
            if (active is not null)
            {
                int err = 0;
                ctx.App.ActivateDoc3(active.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
            }
        }
    }

    /// <summary>Gives a file this unit system (saved), open already or opened for it.</summary>
    private static void ToUnits(SwContext ctx, string path, int units)
    {
        var open = ctx.App.Documents().FirstOrDefault(x => string.Equals(DocumentRegistry.SafePath(x), path, StringComparison.OrdinalIgnoreCase));
        var d = open;
        if (d is null)
        {
            int e0 = 0, w0 = 0;
            int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
            d = ctx.App.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref e0, ref w0);
            if (d is null) { ctx.Warn($"'{Path.GetFileName(path)}' could not be opened to set its units."); return; }
        }
        try
        {
            if (UnitSystem(d) == units) return;
            SetUnitSystem(d, units);
            int e = 0, w = 0;
            if (!d.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref e, ref w)) ctx.Warn($"'{Path.GetFileName(path)}' kept its template's units (save error {e}).");
        }
        finally
        {
            if (open is null)
                try { ctx.App.CloseDoc(d.GetTitle()); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* left open */ }
        }
    }

    private static (double Volume, int Bodies) Volume(IPartDoc p)
    {
        var bodies = p.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
        return (bodies.Sum(b => ComArgs.Doubles((object?)b.GetMassProperties(0)) is { Length: > 3 } mp ? mp[3] : 0), bodies.Count);
    }

    private static (double[] Point, Vec3 Normal) PlaneOf(object o)
    {
        if (o is IFace2 f && Topology.Describe(f) is { Surface: "plane", Normal: Vec3 n })
            return (Topology.InteriorPoint(f).ToArray(), n);
        if (o is IFeature feat && feat.Specific() is IRefPlane rp && (object?)rp.Transform is IMathTransform t)
        {
            var x = new Xform(ComArgs.Doubles((object?)t.ArrayData));
            var origin = x.ApplyPoint(Vec3.Zero);
            return (origin.ToArray(), x.ApplyVector(new Vec3(0, 0, 1)).Normalized());
        }
        throw SwException.InvalidArgument("'plane' must be a reference plane or a flat face.");
    }

    /// <summary>The axis (0 x, 1 y, 2 z) a unit normal lies along, if any.</summary>
    private static int? Axis(Vec3 n) =>
        Math.Abs(Math.Abs(n.X) - 1) < 1e-9 ? 0 : Math.Abs(Math.Abs(n.Y) - 1) < 1e-9 ? 1 : Math.Abs(Math.Abs(n.Z) - 1) < 1e-9 ? 2 : null;

    private static void UndoFeature(IModelDoc2 d, IFeature f)
    {
        d.ClearSelection2(true);
        try { if (f.Select2(false, 0)) d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* reported by the caller */ }
        d.ClearSelection2(true);
        d.EditRebuild3();
    }

    private static string Folder(SwContext ctx, string? folder, string ownPath, string what)
    {
        string dir;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string f = folder.Trim().Trim('"');
            dir = Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(ctx.Paths.OutputDir, f));
        }
        else if (ownPath.Length > 0) dir = Path.GetDirectoryName(ownPath)!;
        else throw SwException.InvalidArgument($"Save {what} first (doc_save), or pass 'folder'.");
        return dir;
    }

    private static string FileName(string? name, string arg)
    {
        if (string.IsNullOrWhiteSpace(name)) throw SwException.InvalidArgument($"'{arg}' is required: the new part's file name.");
        string n = name.Trim();
        if (n.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) n = n[..^7];
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw SwException.InvalidArgument($"'{name}' isn't a valid file name.");
        return n;
    }

    private static string Safe(string s) => new(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
}
