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
    [Description("Rounds edges (and/or all edges of faces) with a constant radius, e.g. edges:[{where:{curve:'line', parallelTo:'y'}, " +
                 "expect:4}]. Kinds: variable (one edge, radiiMm at its ends, the first at startAt), face (blends faces with faces2), " +
                 "full_round (faces and faces2 are the sides, centerFaces rounded away). Verified: no errors, the volume changed, a round " +
                 "face of the radius (variable: the bigger radius at its end); otherwise rolled back.")]
    public Task<string> FeatureFillet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Fillet radius in mm (not for variable and full_round).")] double? radiusMm = null,
        [Description("Edges to round.")] EntityRef[]? edges = null,
        [Description("Faces whose edges are all rounded; face: the first set; full_round: one side.")] EntityRef[]? faces = null,
        [Description("constant | variable | face | full_round")] string kind = "constant",
        [Description("face: the second set; full_round: the other side.")] EntityRef[]? faces2 = null,
        [Description("full_round: the faces between the sides.")] EntityRef[]? centerFaces = null,
        [Description("variable: [radius at one end, at the other] in mm.")] double[]? radiiMm = null,
        [Description("variable: a point at the end of the first radius (default: the edge's start).")] double[]? startAt = null,
        [Description("Also round tangent-connected edges.")] bool propagate = true,
        [Description("Rename the new feature.")] string? name = null,
        [Description("Keep the feature even if verification fails (for diagnosis).")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FilletAsync(new FilletRequest
        {
            Doc = doc, Kind = kind, Edges = edges, Faces = faces, Faces2 = faces2, CenterFaces = centerFaces, RadiusMm = radiusMm, RadiiMm = radiiMm,
            StartAt = startAt, Propagate = propagate, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_fillet");

    [McpServerTool(Name = "feature_thread", Title = "Modelled thread", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("A modelled (helical) ISO metric thread from a circular edge: a shaft's end gets an external thread (Metric Die), a " +
                 "hole's rim an internal one (Metric Tap; drill it at the tap size). For 3D printing, renders and fit checks; drawings " +
                 "and BOMs only need feature_thread_cosmetic. Verified: the volume moved lies within the thread's annulus.")]
    public Task<string> FeatureThread(
        IProgress<ProgressNotificationValue> progress,
        [Description("Circular edge where the thread starts.")] EntityRef edge,
        [Description("M6 (coarse pitch) or M6x0.75.")] string size,
        [Description("Thread length, mm.")] double depthMm,
        [Description("cut (the groove is cut) | extrude (the thread is added).")] string method = "cut",
        [Description("Left-handed thread.")] bool leftHanded = false,
        [Description("Rename the new feature.")] string? name = null,
        [Description("Keep the feature even if verification fails (for diagnosis).")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ThreadAsync(new ThreadRequest
        {
            Doc = doc, Edge = edge, Size = size, DepthMm = depthMm, Method = method, LeftHanded = leftHanded, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_thread");

    [McpServerTool(Name = "feature_split_line", Title = "Split line", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Splits faces by projecting a sketch onto them (a load or fixture region, a partial face to paint or draft). Uses the " +
                 "open/last sketch unless 'sketch' is given. Verified: more faces, the same volume and area; otherwise rolled back.")]
    public Task<string> FeatureSplitLine(
        IProgress<ProgressNotificationValue> progress,
        [Description("Faces to split, e.g. [{at:[0,20,0]}].")] EntityRef[] faces,
        [Description("Sketch name (default: the open or last sketch).")] string? sketch = null,
        [Description("both (default) | normal | reverse: project both ways, or one way along/against the sketch normal.")] string direction = "both",
        [Description("Rename the new feature.")] string? name = null,
        [Description("Keep the feature even if verification fails (for diagnosis).")] bool keepOnFailure = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.SplitLineAsync(new SplitLineRequest { Doc = doc, Faces = faces, Sketch = sketch, Direction = direction, Name = name, KeepOnFailure = keepOnFailure },
            cancellationToken), progress, "feature_split_line");
}
