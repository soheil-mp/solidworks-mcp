using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Multibody operations (move/copy, delete/keep, combine, split), direct face edits, and surfaces (extrude, revolve, planar, offset, knit, thicken).</summary>
[McpServerToolType, Toolset("bodies")]
public sealed class BodyTools(SwSession session)
{
    private const string DocDesc = "Document (handle, title or path); default: the target document.";
    private const string KeepDesc = "Keep a failed result (for diagnosis).";
    private const string SketchDesc = "Sketch name (default: the open sketch, else the last finished one).";

    private readonly BodyService _bodies = new(session);
    private readonly SurfaceService _surfaces = new(session);
    private readonly FaceEditService _faces = new(session);

    // ------------------------------------------------------------------ bodies

    [McpServerTool(Name = "body_move", Title = "Move/copy bodies", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Move / copy / rotate bodies (Move/Copy Body): rotate about an axis through a point, then translate; copies>0 keeps " +
                 "the bodies and adds copies, copy k moved k times. Verified: each body and copy is where the motion puts it " +
                 "(vertices, center of mass) and the body count; else rolled back.")]
    public Task<string> BodyMove(
        IProgress<ProgressNotificationValue> progress,
        [Description("Bodies: {id:'B1'} (body_list), {name:'Boss-Extrude1'} or {at:[x,y,z]} on a face.")] EntityRef[] bodies,
        [Description("Translation [x,y,z] in mm.")] double[]? translateMm = null,
        [Description("{axis:[x,y,z], point:[x,y,z] mm (default origin), angleDeg} (right-hand rule).")] BodyRotation? rotate = null,
        [Description("0 = move; n = make n copies (the originals stay).")] int copies = 0,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_bodies.MoveAsync(new BodyMoveRequest
        {
            Doc = doc, Bodies = bodies, TranslateMm = translateMm, Rotate = rotate, Copies = copies, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "body_move");

    [McpServerTool(Name = "body_delete", Title = "Delete/keep bodies", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Delete bodies, or keep only the listed ones (Delete/Keep Body feature; undo by deleting it). Verified: exactly " +
                 "those bodies are gone, the others unchanged; reports the removed body names.")]
    public Task<string> BodyDelete(
        IProgress<ProgressNotificationValue> progress,
        [Description("Bodies to delete (or to keep), e.g. [{id:'B2'}].")] EntityRef[] bodies,
        [Description("delete | keep_only")] string mode = "delete",
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_bodies.DeleteAsync(doc, bodies, mode, keepOnFailure, cancellationToken), progress, "body_delete");

    [McpServerTool(Name = "body_combine", Title = "Combine bodies", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Combine solid bodies: add (union), subtract (main minus tools), common (intersection). Verified by the bodies' " +
                 "volumes; else rolled back. subtract is live-verified; add and common are experimental ('method' names the API " +
                 "variant that worked).")]
    public Task<string> BodyCombine(
        IProgress<ProgressNotificationValue> progress,
        [Description("The main body (subtract: the one that is cut).")] EntityRef main,
        [Description("The other bodies, e.g. [{id:'B2'}].")] EntityRef[] tools,
        [Description("add | subtract | common")] string operation,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_bodies.CombineAsync(doc, main, tools, operation, keepOnFailure, cancellationToken), progress, "body_combine");

    [McpServerTool(Name = "body_split", Title = "Split body", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Split solid bodies with a plane, planar face or sketch (the pieces stay in the part; experimental). Verified: more " +
                 "bodies whose volumes add up to the original, other bodies unchanged; else rolled back.")]
    public Task<string> BodySplit(
        IProgress<ProgressNotificationValue> progress,
        [Description("Trim tool: {name:'right'}, a planar face {at:[x,y,z]} or a sketch {name:'Sketch2'}.")] EntityRef tool,
        [Description("Only this solid body (default: all the tool crosses).")] EntityRef? body = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_bodies.SplitAsync(doc, body, tool, keepOnFailure, cancellationToken), progress, "body_split");

    // ------------------------------------------------------------------ surfaces

    [McpServerTool(Name = "surface_extrude", Title = "Extruded surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Extruded surface from a sketch (open or closed profile; no caps). Verified: a new surface body spanning the depth " +
                 "on the requested side of the sketch plane, solids unchanged; else rolled back.")]
    public Task<string> SurfaceExtrude(
        IProgress<ProgressNotificationValue> progress,
        [Description("Depth in mm (total with bothSides).")] double depthMm,
        [Description("normal (along the sketch normal) | reverse | a model axis like \"+y\".")] string? direction = null,
        [Description("Mid-plane: half the depth on each side.")] bool bothSides = false,
        [Description(SketchDesc)] string? sketch = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.ExtrudeAsync(new SurfaceExtrudeRequest
        {
            Doc = doc, Sketch = sketch, DepthMm = depthMm, Direction = direction, BothSides = bothSides, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "surface_extrude");

    [McpServerTool(Name = "surface_revolve", Title = "Revolved surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Revolved surface: spins a sketch profile around an axis, by default the sketch's only centerline (experimental). " +
                 "Verified: a new surface body whose faces are all coaxial with the axis; else rolled back.")]
    public Task<string> SurfaceRevolve(
        IProgress<ProgressNotificationValue> progress,
        [Description("Angle in degrees (0 to 360).")] double angleDeg = 360,
        [Description("Axis in the sketch plane: a centerline {name:'Line2'}, a reference axis or a straight edge.")] EntityRef? axis = null,
        [Description(SketchDesc)] string? sketch = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.RevolveAsync(new SurfaceRevolveRequest
        {
            Doc = doc, Sketch = sketch, Axis = axis, AngleDeg = angleDeg, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "surface_revolve");

    [McpServerTool(Name = "surface_planar", Title = "Planar surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Planar surface filling a closed sketch profile, or a closed loop of edges in one plane (experimental). Verified: a " +
                 "new surface body of planar faces in that plane; else rolled back.")]
    public Task<string> SurfacePlanar(
        IProgress<ProgressNotificationValue> progress,
        [Description("Boundary edges instead of a sketch, e.g. [{where:{extreme:'+y'},expect:4}].")] EntityRef[]? edges = null,
        [Description(SketchDesc)] string? sketch = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.PlanarAsync(doc, sketch, edges, keepOnFailure, cancellationToken), progress, "surface_planar");

    [McpServerTool(Name = "surface_offset", Title = "Offset surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Offset surface: copies faces at a distance along their normals (0 = a copy; experimental). Verified: a new surface " +
                 "body with planar faces at the distance (same area), cylinders at r ± distance; else rolled back.")]
    public Task<string> SurfaceOffset(
        IProgress<ProgressNotificationValue> progress,
        [Description("Faces to offset, e.g. [{at:[0,20,0]}].")] EntityRef[] faces,
        [Description("Offset distance in mm (0 or more).")] double distanceMm,
        [Description("Offset against the face normals (into a solid).")] bool flip = false,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.OffsetAsync(doc, faces, distanceMm, flip, keepOnFailure, cancellationToken), progress, "surface_offset");

    [McpServerTool(Name = "surface_knit", Title = "Knit surfaces", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Knit (sew) surface bodies that share edges into one; trySolid closes a watertight set into a solid (experimental). " +
                 "Verified: the inputs merged or a solid with volume formed ('solidFormed').")]
    public Task<string> SurfaceKnit(
        IProgress<ProgressNotificationValue> progress,
        [Description("Surface bodies: {id:'B3'} (body_list) or {at:[x,y,z]} on one of their faces.")] EntityRef[] surfaces,
        [Description("Try to form a solid from a closed set.")] bool trySolid = false,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.KnitAsync(doc, surfaces, trySolid, keepOnFailure, cancellationToken), progress, "surface_knit");

    [McpServerTool(Name = "surface_thicken", Title = "Thicken surface", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Thicken a surface body into a solid. Verified: a flat sheet gains exactly area x thickness (both: the total is " +
                 "reported), a curved one about that; else rolled back.")]
    public Task<string> SurfaceThicken(
        IProgress<ProgressNotificationValue> progress,
        [Description("The surface body: {id:'B2'} (body_list) or {at:[x,y,z]} on its face.")] EntityRef surface,
        [Description("Thickness in mm.")] double thicknessMm,
        [Description("one | other | both (sides of the surface).")] string side = "one",
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_surfaces.ThickenAsync(doc, surface, thicknessMm, side, keepOnFailure, cancellationToken), progress, "surface_thicken");

    // ------------------------------------------------------------------ direct face editing

    [McpServerTool(Name = "face_edit", Title = "Edit faces directly", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Direct face editing, no feature history needed (e.g. an imported STEP): delete faces and heal the gap (patch: the " +
                 "neighbours grow over it, removing a hole, fillet or boss), offset faces along their normals, or move them. Verified: " +
                 "the faces are gone from their spot, or lie at the new distance or position (planes, cylinders), the body stays solid; else rolled back.")]
    public Task<string> FaceEdit(
        IProgress<ProgressNotificationValue> progress,
        [Description("delete | offset | move")] string op,
        [Description("Faces, e.g. [{id:'F3'}] or a hole: [{where:{surface:'cylinder', radiusMm:3}, expect:1}].")] EntityRef[] faces,
        [Description("delete: patch (default) | fill | tangent_fill | none (leaves the body open).")] string? heal = null,
        [Description("offset: mm along each face's outward normal; positive adds material (a hole gets smaller).")] double? distanceMm = null,
        [Description("move: [dx,dy,dz] mm.")] double[]? translateMm = null,
        [Description("Name for the new feature.")] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_faces.EditAsync(new FaceEditRequest
        {
            Doc = doc, Op = op, Faces = faces, Heal = heal, DistanceMm = distanceMm, TranslateMm = translateMm, Name = name,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "face_edit");
}
