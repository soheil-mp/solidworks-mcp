using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("features")]
public sealed class FeatureTools(SwSession session)
{
    private readonly FeatureService _svc = new(session);

    [McpServerTool(Name = "feature_extrude", Title = "Extrude (boss or cut)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Extrudes a closed sketch profile into a boss, or cuts it (cut=true). Uses the open/last sketch unless 'sketch' is given. " +
                 "Verified: the feature exists without errors, the volume grew (boss) or shrank (cut), and the end cap lies at the requested depth; " +
                 "otherwise it is rolled back. Returns the feature, its dimensions (e.g. D1@Boss-Extrude1), the new faces with ids and the model extents.")]
    public Task<string> FeatureExtrude(
        IProgress<ProgressNotificationValue> progress,
        [Description("Depth in mm (blind; total thickness for mid_plane).")] double? depthMm = null,
        [Description("blind | through_all | through_all_both | mid_plane")] string end = "blind",
        [Description("Cut material instead of adding it.")] bool cut = false,
        [Description("normal (along the sketch normal) | reverse | auto (cuts: into the material, the default) | a model axis like \"+y\" | \"x,y,z\".")] string? direction = null,
        [Description("Sketch to extrude (default: the open sketch, else the last finished one).")] string? sketch = null,
        [Description("Merge the result with existing bodies (boss).")] bool merge = true,
        [Description("Draft angle in degrees (0 = none).")] double? draftDeg = null,
        [Description("Draft outward instead of inward.")] bool draftOutward = false,
        [Description("Rename the new feature.")] string? name = null,
        [Description("Keep the feature even if verification fails (for diagnosis).")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ExtrudeAsync(new ExtrudeRequest
        {
            Doc = doc, Sketch = sketch, Cut = cut, End = end, DepthMm = depthMm, Direction = direction, Merge = merge,
            DraftDeg = draftDeg, DraftOutward = draftOutward, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_extrude");

    [McpServerTool(Name = "feature_fillet", Title = "Fillet", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Rounds edges (and/or all edges of faces) with a constant radius. Edges by id, point or query, e.g. " +
                 "edges:[{where:{curve:'line', parallelTo:'y'}, expect:4}]. Verified: the fillet exists without errors, changed the volume " +
                 "(concave edges add material, convex remove it) and has a round face of that radius; otherwise rolled back.")]
    public Task<string> FeatureFillet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Fillet radius in mm.")] double radiusMm,
        [Description("Edges to round.")] EntityRef[]? edges = null,
        [Description("Faces whose edges are all rounded.")] EntityRef[]? faces = null,
        [Description("Also round tangent-connected edges.")] bool propagate = true,
        [Description("Rename the new feature.")] string? name = null,
        [Description("Keep the feature even if verification fails (for diagnosis).")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FilletAsync(doc, edges, faces, radiusMm, propagate, name, keepOnFailure, cancellationToken), progress, "feature_fillet");
}
