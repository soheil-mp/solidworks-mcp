using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Top-down design in assemblies: a new part in the assembly's frame or in context of a component's face.</summary>
[McpServerToolType, Toolset("assembly")]
public sealed class TopDownTools(SwSession session)
{
    private readonly TopDownService _svc = new(session);

    [McpServerTool(Name = "asm_part_new", Title = "New part in assembly", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("A new part in the assembly. Without face: an empty part file fixed at the assembly origin, open as the target " +
                 "document, so the part tools model it in assembly coordinates (doc_rebuild the assembly after). With face and depthMm: " +
                 "in context, the face's outline (holes too) extruded away from it (reverse: into it); it follows the face when that " +
                 "part changes. Verified: volume = face area × depth; lists the references.")]
    public Task<string> AsmPartNew(
        IProgress<ProgressNotificationValue> progress,
        [Description("File name of the new part.")] string name,
        [Description("In context: a flat face of a component, e.g. {at:[x,y,z]} (assembly mm).")] EntityRef? face = null,
        [Description("In context: extrusion depth, mm.")] double? depthMm = null,
        [Description("In context: extrude into the face.")] bool reverse = false,
        [Description("Folder for the file (default: the assembly's).")] string? folder = null,
        [Description("Assembly (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.PartNewAsync(new AsmPartNewRequest
        {
            Doc = doc, Name = name, Face = face, DepthMm = depthMm, Reverse = reverse, Folder = folder,
        }, cancellationToken), progress, "asm_part_new");
}

/// <summary>Multibody parts into parts, and derived parts.</summary>
[McpServerToolType, Toolset("bodies")]
public sealed class DeriveTools(SwSession session)
{
    private const string DocDesc = "Part (handle, title or path); default: the target document.";

    private readonly TopDownService _svc = new(session);

    [McpServerTool(Name = "body_save", Title = "Save bodies as parts", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Saves bodies of a saved multibody part as part files (Save Bodies; they follow this master part), optionally with an " +
                 "assembly of them in place. Files: prefix + body name. Verified: each file has its body's volume; the assembly has " +
                 "every part and their total volume.")]
    public Task<string> BodySave(
        IProgress<ProgressNotificationValue> progress,
        [Description("Bodies (default: every solid body), e.g. [{name:'Boss-Extrude1'}].")] EntityRef[]? bodies = null,
        [Description("Also an assembly of them: a file name or path.")] string? assembly = null,
        [Description("File names for the bodies, in order (default: prefix + body name).")] string[]? names = null,
        [Description("File name prefix (default: the part's name and '_', none with names).")] string? prefix = null,
        [Description("Folder (default: the part's).")] string? folder = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.BodySaveAsync(new BodySaveRequest { Doc = doc, Bodies = bodies, Assembly = assembly, Names = names, Prefix = prefix, Folder = folder }, cancellationToken),
            progress, "body_save");

    [McpServerTool(Name = "part_derive", Title = "Derived part", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("insert: another part's bodies into this one at its origin (linked unless breakLink). mirror: a new part mirrored " +
                 "about a plane or flat face, saved (default: '_mirror' added, next to this part) and opened. Verified: insert grows " +
                 "the volume by the inserted part's; mirror keeps it and reflects the box.")]
    public Task<string> PartDerive(
        IProgress<ProgressNotificationValue> progress,
        [Description("insert | mirror")] string kind,
        [Description("insert: the part file to insert.")] string? path = null,
        [Description("mirror: the plane or flat face, e.g. {name:'right'}.")] EntityRef? plane = null,
        [Description("mirror: where to save the new part.")] string? newPath = null,
        [Description("Don't follow later changes of the source.")] bool breakLink = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DeriveAsync(new PartDeriveRequest { Doc = doc, Kind = kind, Path = path, Plane = plane, NewPath = newPath, BreakLink = breakLink }, cancellationToken),
            progress, "part_derive");
}
