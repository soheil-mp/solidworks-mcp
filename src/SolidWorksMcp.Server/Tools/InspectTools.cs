using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("inspect")]
public sealed class InspectTools(SwSession session)
{
    private readonly InspectService _svc = new(session);

    [McpServerTool(Name = "model_info", Title = "Model summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Summary of the target document: type, units, configurations, material, bodies (id, volume), total volume, area, mass, " +
                 "center of mass, bounding box (mm), feature count and open sketch.")]
    public async Task<string> ModelInfo(
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ModelInfoAsync(doc, cancellationToken));

    [McpServerTool(Name = "feature_list", Title = "Feature tree", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/maxResultSizeChars", 200000)]
    [Description("The feature tree (FeatureManager) in order: name, type, rebuild error/warning, suppressed, rolled back, children " +
                 "(absorbed sketches) and parents.")]
    public async Task<string> FeatureList(
        [Description("Also list system folders (Annotations, Lights, …).")] bool includeSystem = false,
        [Description("Maximum rows (1-500).")] int limit = 100,
        [Description("Rows to skip (paging).")] int offset = 0,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.FeatureListAsync(doc, includeSystem, limit, offset, cancellationToken));

    [McpServerTool(Name = "face_list", Title = "List faces", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/maxResultSizeChars", 200000)]
    [Description("Faces matching a filter, each with an id (usable as {id}), surface type, area, a pick point inside the face, the normal " +
                 "(planar) or axis and radius (cylinder/cone), and the feature that made it. Filter examples: {surface:'plane', normal:'+y'} " +
                 "(top faces), {surface:'cylinder', radiusMm:3}, {feature:'Cut-Extrude1'}, {extreme:'+y'}.")]
    public async Task<string> FaceList(
        [Description("Filter: surface, normal, parallelTo, radiusMm, near+withinMm, extreme, feature, body, component.")] EntityFilter? where = null,
        [Description("Maximum faces (1-1000).")] int limit = 50,
        [Description("Faces to skip (paging).")] int offset = 0,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.FaceListAsync(doc, where, limit, offset, cancellationToken));

    [McpServerTool(Name = "edge_list", Title = "List edges", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/maxResultSizeChars", 200000)]
    [Description("Edges matching a filter, each with an id, curve type, length, start/end/mid points (mid resolves back to the edge), and " +
                 "radius/center/axis for circles. Filter examples: {curve:'line', parallelTo:'y'} (vertical edges), {ofFace:{id:'F2'}, loop:'outer'}, " +
                 "{curve:'circle', radiusMm:5}, {feature:'Boss-Extrude1'}.")]
    public async Task<string> EdgeList(
        [Description("Filter: curve, parallelTo, radiusMm, lengthMm, ofFace (+loop), near+withinMm, extreme, feature, body, component.")] EntityFilter? where = null,
        [Description("Maximum edges (1-1000).")] int limit = 50,
        [Description("Edges to skip (paging).")] int offset = 0,
        [Description("Also report the ids of the two faces each edge joins.")] bool withFaces = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.EdgeListAsync(doc, where, limit, offset, withFaces, cancellationToken));

    [McpServerTool(Name = "vertex_list", Title = "List vertices", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/maxResultSizeChars", 200000)]
    [Description("Vertices matching a filter (near, extreme, ofFace, body, component), each with an id and point (mm).")]
    public async Task<string> VertexList(
        [Description("Filter: near+withinMm, extreme, ofFace, body, component.")] EntityFilter? where = null,
        [Description("Maximum vertices (1-1000).")] int limit = 50,
        [Description("Vertices to skip (paging).")] int offset = 0,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.VertexListAsync(doc, where, limit, offset, cancellationToken));
}
