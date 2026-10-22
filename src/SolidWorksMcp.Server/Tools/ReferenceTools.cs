using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("reference")]
public sealed class ReferenceTools(SwSession session)
{
    private readonly ReferenceService _svc = new(session);

    private const string DocDescription = "Document (handle, title or path); default: the target document.";

    [McpServerTool(Name = "ref_plane", Title = "Reference plane", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reference (datum) plane. kind: offset {base, offsetMm}, angle {base, axis, angleDeg}, three_points {points}, " +
                 "parallel_point {base, point}, mid_plane {base, second}, normal_to_curve {base: curve, point}. Points (model mm) go " +
                 "into a 3D sketch. Verified against the plane SOLIDWORKS reports, else rolled back; returns its name and frame.")]
    public Task<string> RefPlane(
        IProgress<ProgressNotificationValue> progress,
        [Description("offset | angle | three_points | parallel_point | mid_plane | normal_to_curve")] string kind,
        [Description("Plane or planar face, e.g. {name:'top'}, {id:'F3'}; normal_to_curve: the edge.")] EntityRef? @base = null,
        [Description("offset: along the base normal (faces: outward); negative = other side.")] double? offsetMm = null,
        [Description("angle: to the base, 0-180.")] double? angleDeg = null,
        [Description("angle: straight edge or axis in the base to turn about.")] EntityRef? axis = null,
        [Description("three_points: [[x,y,z]×3] mm.")] double[][]? points = null,
        [Description("[x,y,z] mm the plane passes through (normal_to_curve: on the curve; default its start).")] double[]? point = null,
        [Description("mid_plane: the other plane or planar face.")] EntityRef? second = null,
        [Description("The other side (offset) or rotation (angle).")] bool flip = false,
        [Description("Rename the new plane.")] string? name = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.PlaneAsync(new RefPlaneRequest
        {
            Doc = doc, Kind = kind, Base = @base, OffsetMm = offsetMm, AngleDeg = angleDeg, Axis = axis, Points = points,
            Point = point, Second = second, Flip = flip, Name = name,
        }, cancellationToken), progress, "ref_plane");

    [McpServerTool(Name = "ref_axis", Title = "Reference axis", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reference axis. kind: two_planes {entities: 2 crossing planes/planar faces}; two_points {points, or entities: 2 vertices/points}; " +
                 "cylinder {entities: 1 cylindrical/conical face}; edge {entities: 1 straight edge}. Verified: its direction and position " +
                 "match the references, else rolled back. Returns its name and frame (origin, direction).")]
    public Task<string> RefAxis(
        IProgress<ProgressNotificationValue> progress,
        [Description("two_planes | two_points | cylinder | edge")] string kind,
        [Description("The references, e.g. [{name:'front'},{name:'right'}] or [{type:'face', at:[x,y,z]}].")] EntityRef[]? entities = null,
        [Description("two_points: [[x,y,z],[x,y,z]] model mm (instead of entities).")] double[][]? points = null,
        [Description("Rename the new axis.")] string? name = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.AxisAsync(doc, kind, entities, points, name, cancellationToken), progress, "ref_axis");

    [McpServerTool(Name = "ref_point", Title = "Reference point", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Reference point. kind: xyz {at}; arc_center {entities: 1 circular edge}; face_center {entities: 1 face}; " +
                 "intersection {entities: 2 crossing edges/axes}. Verified: the point SOLIDWORKS reports is where requested, else rolled back. " +
                 "Returns its name and position (frame.originMm).")]
    public Task<string> RefPoint(
        IProgress<ProgressNotificationValue> progress,
        [Description("xyz | arc_center | face_center | intersection")] string kind,
        [Description("xyz: [x,y,z] model mm.")] double[]? at = null,
        [Description("The references, e.g. [{type:'edge', at:[x,y,z]}].")] EntityRef[]? entities = null,
        [Description("Rename the new point.")] string? name = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.PointAsync(doc, kind, at, entities, name, cancellationToken), progress, "ref_point");

    [McpServerTool(Name = "ref_coordsys", Title = "Coordinate system", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Coordinate system at an origin ([x,y,z] mm, or a vertex/point), with optional x/y directions from straight edges or axes " +
                 "(default: the model axes). Verified: origin and axes as SOLIDWORKS reports them, else rolled back. Returns its name and " +
                 "frame (origin, x, y and z axes).")]
    public Task<string> RefCoordsys(
        IProgress<ProgressNotificationValue> progress,
        [Description("[x,y,z] model mm, or a vertex/point reference such as {id:'V3'} or {name:'Point1'}.")] JsonElement origin,
        [Description("Straight edge or axis giving the x direction.")] EntityRef? xAxis = null,
        [Description("Straight edge or axis giving the y direction.")] EntityRef? yAxis = null,
        [Description("Rename the new coordinate system.")] string? name = null,
        [Description(DocDescription)] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var (xyz, reference) = Origin(origin);
        return Output.Json(_svc.CoordSysAsync(doc, xyz, reference, xAxis, yAxis, name, cancellationToken), progress, "ref_coordsys");
    }

    /// <summary>'origin' is either [x, y, z] (mm) or an entity reference object.</summary>
    internal static (double[]? Xyz, EntityRef? Reference) Origin(JsonElement origin)
    {
        const string Usage = "'origin' must be [x, y, z] (model mm) or a vertex/point reference such as {\"id\":\"V3\"} or {\"name\":\"Point1\"}.";
        switch (origin.ValueKind)
        {
            case JsonValueKind.Array:
                if (origin.GetArrayLength() == 3 && origin.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number))
                    return (origin.EnumerateArray().Select(v => v.GetDouble()).ToArray(), null);
                break;
            case JsonValueKind.Object:
                try
                {
                    return (null, origin.Deserialize<EntityRef>(McpJson.Options) ?? throw SwException.InvalidArgument(Usage));
                }
                catch (JsonException ex)
                {
                    throw SwException.InvalidArgument($"'origin' is not a valid reference: {ex.Message}", Usage);
                }
        }
        throw SwException.InvalidArgument(Usage);
    }
}
