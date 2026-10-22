using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("sketch")]
public sealed class SketchTools(SwSession session)
{
    private readonly SketchService _svc = new(session);

    [McpServerTool(Name = "sketch_start", Title = "Start sketch", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Opens a new sketch on a plane ({name:'top'|'front'|'right'} or a plane's name) or on a planar face ({id:'F3'} or " +
                 "{at:[x,y,z]}). Returns the sketch name and its frame in model space (origin, x axis, y axis, normal), and for a face " +
                 "its extents in sketch coordinates. Inference snapping is off while this server draws.")]
    public async Task<string> SketchStart(
        [Description("Plane or planar face to sketch on.")] EntityRef? on = null,
        [Description("Open a 3D sketch instead (points are then model [x,y,z]).")] bool threeD = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.StartAsync(doc, on, threeD, cancellationToken));

    [McpServerTool(Name = "sketch_add", Title = "Add sketch entities", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Draws entities in the open sketch, all or nothing. Points: [x,y] sketch mm or [x,y,z] model mm (projected). Kinds and fields: " +
                 "line/centerline {from,to}; polyline {points,closed}; rectangle {corners:[p1,p2]} or {center,widthMm,heightMm}; " +
                 "circle {center,radiusMm|diameterMm}; arc {start,mid,end} or {center,radiusMm,startDeg,endDeg} (CCW); slot {from,to,widthMm}; " +
                 "polygon {center,sides,circumradiusMm|inradiusMm,rotationDeg}; ellipse {center,majorRadiusMm,minorRadiusMm,rotationDeg}; " +
                 "spline {points}; point {at}. Any kind: construction, label. Returns segment names per entity (rectangle sides named) and " +
                 "the contour status (closed loops are needed for extrudes).")]
    public async Task<string> SketchAdd(
        [Description("Entities to draw, e.g. [{\"kind\":\"rectangle\",\"center\":[0,0],\"widthMm\":100,\"heightMm\":60}].")] SketchEntity[] entities,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.AddAsync(doc, entities, cancellationToken));

    [McpServerTool(Name = "sketch_finish", Title = "Finish sketch", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Closes the open sketch. Returns its segment and relation counts, constrained status and contour status (closed/open " +
                 "loops, regions). The next feature tool uses this sketch by default.")]
    public async Task<string> SketchFinish(
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.FinishAsync(doc, cancellationToken));
}
