using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Re-opening, inspecting and editing sketches (the open sketch must be one this server opened).</summary>
[McpServerToolType, Toolset("sketch")]
public sealed class SketchEditTools(SwSession session)
{
    private const string DocDescription = "Document (handle, title or path); default: the target document.";
    private const string CornerAt = "Or the corner point [x,y] sketch mm.";

    private readonly SketchEditService _svc = new(session);

    [McpServerTool(Name = "sketch_edit", Title = "Edit sketch", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Re-opens an existing sketch by name for editing (then sketch_add, sketch_dimension_add, ..., sketch_finish). Returns its " +
                 "frame in model space (origin, x/y axes, normal), segment and relation counts and constrained status. SKETCH_STATE if another sketch is open.")]
    public async Task<string> SketchEdit(
        [Description("Sketch name, e.g. Sketch1 (feature_list shows them).")] string sketch,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.EditAsync(doc, sketch, cancellationToken));

    [McpServerTool(Name = "sketch_info", Title = "Sketch info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Reads a sketch: segments (name, kind line/arc/circle/ellipse/spline/text, construction, geometry in sketch mm: line start/end, " +
                 "arc center/radius/start/end, length), point and relation counts, constrained status (under/fully/over), contour status " +
                 "(closed/open loops, regions), frame and dimensions (D1@Sketch1 with values).")]
    public async Task<string> SketchInfo(
        [Description("Sketch name, e.g. Sketch1; default: the open sketch.")] string? sketch = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.InfoAsync(doc, sketch, cancellationToken));

    [McpServerTool(Name = "sketch_dimension_add", Title = "Add sketch dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a driving dimension in the open sketch and sets its value (no Modify dialog). kind: linear (a line's length, or the distance " +
                 "between two entities), horizontal, vertical, radial or diameter (arc/circle), angular (two lines). Verified: the value reads back " +
                 "and the geometry measures it; an over-defining or driven result is removed and refused. Returns the name (D1@Sketch1).")]
    public async Task<string> SketchDimensionAdd(
        [Description("1-2 entities: segments {name:'Line1'}, sketch points {at:[x,y]} or {name:'origin'}.")] EntityRef[] entities,
        [Description("linear | horizontal | vertical | radial | diameter | angular")] string kind,
        [Description("Value in mm (all kinds but angular); omit to keep the current size.")] double? valueMm = null,
        [Description("angular: value in degrees.")] double? valueDeg = null,
        [Description("Text position [x,y] sketch mm; for angular it picks the angle (put it inside the one you mean).")] double[]? textAt = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DimensionAddAsync(doc, entities, kind, valueMm, valueDeg, textAt, cancellationToken));

    [McpServerTool(Name = "sketch_relation_add", Title = "Add sketch relation", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a geometric relation in the open sketch. Takes: horizontal/vertical (lines, or points), coincident (point + point/line/arc; " +
                 "two segment end points merge), collinear/parallel (lines), perpendicular (2 lines), tangent, concentric, equal, symmetric " +
                 "(2 entities then the symmetry line), midpoint (point, line), intersection (point, 2 lines), fix, pierce (point + a model edge or " +
                 "a segment of another sketch crossing the plane), merge (2 points). Verified: the relation was added (or the points merged) and the " +
                 "solved geometry satisfies it; an over-defining relation is undone; VERIFY_FAILED if SOLIDWORKS ignored it.")]
    public async Task<string> SketchRelationAdd(
        [Description("horizontal | vertical | coincident | collinear | parallel | perpendicular | tangent | concentric | equal | symmetric | " +
                     "midpoint | intersection | fix | pierce | merge")] string relation,
        [Description("Entities: segments {name:'Line1'}, sketch points {at:[x,y]}; for pierce a model edge or {name:'Line1', sketch:'Sketch1'}.")] EntityRef[] entities,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.RelationAddAsync(doc, relation, entities, cancellationToken));

    [McpServerTool(Name = "sketch_trim", Title = "Trim sketch segment", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Trims away the piece of a segment of the open sketch at a point, up to the nearest intersections (trim to closest). " +
                 "Verified: the segment got shorter, was split or removed. Returns the created, changed and removed segments.")]
    public async Task<string> SketchTrim(
        [Description("Point on the piece to remove: [x,y] sketch mm or [x,y,z] model mm.")] double[] at,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.TrimAsync(doc, at, cancellationToken));

    [McpServerTool(Name = "sketch_extend", Title = "Extend sketch segment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Extends a line or arc of the open sketch up to the next segment it meets. Verified: the segment got longer.")]
    public async Task<string> SketchExtend(
        [Description("Line or arc to extend, e.g. {name:'Line2'}.")] EntityRef segment,
        [Description("auto (an end with a segment ahead) | start | end")] string end = "auto",
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ExtendAsync(doc, segment, end, cancellationToken));

    [McpServerTool(Name = "sketch_offset", Title = "Offset sketch segments", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Offsets segments of the open sketch by a distance. A closed loop goes outward, inward or both (a 100x60 rectangle offset 5 " +
                 "outward gives a 110x70 loop); an open chain goes left, right or both, seen along its first segment from start to end. Verified " +
                 "on the loop size or side and the new segments' distance; otherwise undone. Returns boxMm.")]
    public async Task<string> SketchOffset(
        [Description("Segments to offset: e.g. the 4 lines of a rectangle, or one of them with chain=true.")] EntityRef[] segments,
        [Description("Offset distance in mm.")] double distanceMm,
        [Description("Closed loop: outward | inward | both. Open chain: left | right | both.")] string side = "outward",
        [Description("Also offset the segments connected end to end with them.")] bool chain = false,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.OffsetAsync(doc, segments, distanceMm, side, chain, cancellationToken));

    [McpServerTool(Name = "sketch_fillet", Title = "Sketch fillet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Rounds a corner of the open sketch with a tangent arc (its two segments are trimmed). The corner is given by its two segments " +
                 "or by the corner point. Verified: a new arc of that radius.")]
    public async Task<string> SketchFillet(
        [Description("Fillet radius in mm.")] double radiusMm,
        [Description("The corner's two segments, e.g. [{name:'Line1'},{name:'Line2'}].")] EntityRef[]? segments = null,
        [Description(CornerAt)] double[]? at = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.FilletAsync(doc, segments, at, radiusMm, cancellationToken));

    [McpServerTool(Name = "sketch_chamfer", Title = "Sketch chamfer", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Cuts a corner of the open sketch with a chamfer line: type equal (distanceMm on both sides), distance_distance (distanceMm " +
                 "along the first segment, distance2Mm along the second) or distance_angle (distanceMm along the first segment, angleDeg from it). " +
                 "Corner by its two segments or its point. Verified: the chamfer's ends lie at those distances (and that angle) on each segment.")]
    public async Task<string> SketchChamfer(
        [Description("Chamfer distance in mm, along the first segment for the unequal types (with 'at' the result says which segment).")] double distanceMm,
        [Description("equal | distance_distance | distance_angle")] string type = "equal",
        [Description("distance_distance: second distance in mm.")] double? distance2Mm = null,
        [Description("distance_angle: angle in degrees.")] double? angleDeg = null,
        [Description("The corner's two segments.")] EntityRef[]? segments = null,
        [Description(CornerAt)] double[]? at = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ChamferAsync(doc, segments, at, type, distanceMm, distance2Mm, angleDeg, cancellationToken));

    [McpServerTool(Name = "sketch_pattern", Title = "Sketch pattern", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Copies entities of the open sketch in a pattern. linear: countX instances spacingXMm apart along angleDeg (0 = sketch +x), " +
                 "optionally countY rows spacingYMm apart at +90 deg. circular: count instances about center, evenly around a full circle " +
                 "(totalAngleDeg 360) or spanning a smaller angle, counter-clockwise (negative = clockwise). Counts include the seed. Verified: " +
                 "the copies lie at the predicted positions; otherwise undone.")]
    public async Task<string> SketchPattern(
        [Description("linear | circular")] string kind,
        [Description("Segments (or points) to copy, e.g. [{name:'Arc1'}].")] EntityRef[] entities,
        [Description("linear: instances along the first direction.")] int? countX = null,
        [Description("linear: spacing along the first direction, mm.")] double? spacingXMm = null,
        [Description("linear: instances along the second direction (default 1).")] int? countY = null,
        [Description("linear: spacing along the second direction, mm.")] double? spacingYMm = null,
        [Description("linear: first direction in degrees from sketch +x (default 0).")] double? angleDeg = null,
        [Description("circular: center [x,y] sketch mm.")] double[]? center = null,
        [Description("circular: number of instances.")] int? count = null,
        [Description("circular: 360 = full circle; less = the angle from the seed to the last instance (at most 360·(count-1)/count); negative = clockwise.")] double totalAngleDeg = 360,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.PatternAsync(new SketchPatternRequest
        {
            Doc = doc, Kind = kind, Entities = entities, CountX = countX, SpacingXMm = spacingXMm, CountY = countY,
            SpacingYMm = spacingYMm, AngleDeg = angleDeg, Center = center, Count = count, TotalAngleDeg = totalAngleDeg,
        }, cancellationToken));

    [McpServerTool(Name = "sketch_mirror", Title = "Sketch mirror", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Mirrors entities of the open sketch about a line or centerline of it (the copies get symmetric relations). " +
                 "Verified: each copy lies at the reflected position.")]
    public async Task<string> SketchMirror(
        [Description("Segments (or points) to mirror.")] EntityRef[] entities,
        [Description("The mirror line or centerline, e.g. {name:'Line5'}.")] EntityRef about,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.MirrorAsync(doc, entities, about, cancellationToken));

    [McpServerTool(Name = "sketch_convert", Title = "Convert entities", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Convert Entities: projects model edges (or all edges of a face) into the open sketch as segments linked to the model. " +
                 "Verified: new segments, with their total length compared to the edges'.")]
    public async Task<string> SketchConvert(
        [Description("Model edges or faces, e.g. [{type:'edge',at:[x,y,z]}] or [{id:'E3'}]; a bare {at:[x,y,z]} is an edge.")] EntityRef[] entities,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ConvertAsync(doc, entities, cancellationToken));

    [McpServerTool(Name = "sketch_construction_set", Title = "Set construction geometry", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns segments of the open sketch into construction geometry (ignored by features) or back into regular geometry. " +
                 "Verified by reading the flag back.")]
    public async Task<string> SketchConstructionSet(
        [Description("Segments, e.g. [{name:'Line1'}].")] EntityRef[] segments,
        [Description("true = construction, false = regular geometry.")] bool construction,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ConstructionSetAsync(doc, segments, construction, cancellationToken));

    [McpServerTool(Name = "sketch_delete", Title = "Delete sketch segments", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes segments (or points) of the open sketch together with their relations. Verified: they are gone.")]
    public async Task<string> SketchDelete(
        [Description("Segments or points, e.g. [{name:'Line3'}] or [{at:[x,y]}].")] EntityRef[] segments,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DeleteAsync(doc, segments, cancellationToken));
}
