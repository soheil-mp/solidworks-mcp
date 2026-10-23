using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Advanced part features: sweep, loft, helix, draft, rib and cosmetic thread.</summary>
[McpServerToolType, Toolset("features")]
public sealed class AdvancedFeatureTools(SwSession session)
{
    private const string NameDesc = "Rename the new feature.";
    private const string KeepDesc = "Keep the feature even if verification fails (for diagnosis).";
    private const string DocDesc = "Document (handle, title or path); default: the target document.";
    private const string GuidesDesc = "Guide curves that shape the section (sketches, edges, curves).";
    private const string CutDesc = "Cut material instead of adding it.";
    private const string MergeDesc = "Merge the result with existing bodies (boss).";

    private readonly AdvancedFeatureService _svc = new(session);

    [McpServerTool(Name = "feature_sweep", Title = "Sweep (boss or cut)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sweep / swept boss or cut / spring / tube: moves a closed profile sketch (or a circle of circularProfileDiameterMm) along " +
                 "a path; the profile must sit at the path start (feature_helix returns startMm). Verified: a Sweep without errors, the volume " +
                 "changed, and an unmerged sweep has profile area x path length; otherwise rolled back.")]
    public Task<string> FeatureSweep(
        IProgress<ProgressNotificationValue> progress,
        [Description("Path: a sketch, an edge, or a curve such as Helix/Spiral1 (by name).")] EntityRef path,
        [Description("Closed profile sketch (by name).")] EntityRef? profile = null,
        [Description("Circle diameter in mm, instead of a profile.")] double? circularProfileDiameterMm = null,
        [Description(GuidesDesc)] EntityRef[]? guides = null,
        [Description(CutDesc)] bool cut = false,
        [Description("Twist along the path in degrees.")] double? twistDeg = null,
        [Description(MergeDesc)] bool merge = true,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.SweepAsync(new SweepRequest
        {
            Doc = doc, Profile = profile, Path = path, Guides = guides, Cut = cut, TwistDeg = twistDeg, Merge = merge,
            CircularProfileDiameterMm = circularProfileDiameterMm, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_sweep");

    [McpServerTool(Name = "feature_loft", Title = "Loft (boss or cut)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Loft / blend / transition through closed profiles in order (sketches on separate planes, or faces), optionally with guide " +
                 "curves; cut=true removes material. Verified: a loft without errors, the volume changed, and its faces pass through every " +
                 "profile; otherwise rolled back.")]
    public Task<string> FeatureLoft(
        IProgress<ProgressNotificationValue> progress,
        [Description("Profiles in loft order: sketches (by name) or faces.")] EntityRef[] profiles,
        [Description(GuidesDesc)] EntityRef[]? guides = null,
        [Description(CutDesc)] bool cut = false,
        [Description("Close the loft from the last profile back to the first (3+ profiles).")] bool closed = false,
        [Description(MergeDesc)] bool merge = true,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.LoftAsync(new LoftRequest
        {
            Doc = doc, Profiles = profiles, Guides = guides, Cut = cut, Closed = closed, Merge = merge, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_loft");

    [McpServerTool(Name = "feature_helix", Title = "Helix / spiral", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Helix / spiral curve (springs, threads, coils) from a sketch with one circle (center, radius); it climbs along the sketch " +
                 "normal. Verified: a Helix starting on the circle with the requested height, pitch and length. Returns startMm and " +
                 "profilePlaneNormal: put a sweep profile on that plane through startMm.")]
    public Task<string> FeatureHelix(
        IProgress<ProgressNotificationValue> progress,
        [Description("pitch_revolutions | height_revolutions | height_pitch | spiral")] string definedBy = "pitch_revolutions",
        [Description("Pitch per turn in mm (spiral: the gap between turns).")] double? pitchMm = null,
        [Description("Number of turns.")] double? revolutions = null,
        [Description("Total height in mm.")] double? heightMm = null,
        [Description("Sketch with the circle (default: the open sketch, else the last finished one).")] string? sketch = null,
        [Description("Start angle in degrees.")] double startAngleDeg = 0,
        [Description("Turn clockwise.")] bool clockwise = false,
        [Description("Climb against the sketch normal.")] bool reverse = false,
        [Description("Taper in degrees: > 0 widens, < 0 narrows.")] double? taperDeg = null,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.HelixAsync(new HelixRequest
        {
            Doc = doc, Sketch = sketch, DefinedBy = definedBy, PitchMm = pitchMm, Revolutions = revolutions, HeightMm = heightMm,
            StartAngleDeg = startAngleDeg, Clockwise = clockwise, Reverse = reverse, TaperDeg = taperDeg, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_helix");

    [McpServerTool(Name = "feature_draft", Title = "Draft faces", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Draft / taper faces (mold release) by an angle about a neutral plane or planar face. Verified: a Draft without errors, the " +
                 "volume changed, and each planar face turned by angleDeg about its line on the neutral plane; otherwise rolled back.")]
    public Task<string> FeatureDraft(
        IProgress<ProgressNotificationValue> progress,
        [Description("Faces to draft, e.g. the side walls.")] EntityRef[] faces,
        [Description("Neutral plane or planar face, e.g. the bottom face.")] EntityRef neutral,
        [Description("Draft angle in degrees.")] double angleDeg,
        [Description("Draft the other way (reverse the pull direction).")] bool flip = false,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DraftAsync(new DraftRequest
        {
            Doc = doc, Faces = faces, Neutral = neutral, AngleDeg = angleDeg, Flip = flip, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_draft");

    [McpServerTool(Name = "feature_rib", Title = "Rib", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Rib / gusset / stiffener from an open sketch (usually a line between walls), thickened and extended to the part. Verified: " +
                 "a Rib without errors, material added, side faces thicknessMm apart about the sketch; otherwise rolled back (a side that " +
                 "meets no wall is retried flipped).")]
    public Task<string> FeatureRib(
        IProgress<ProgressNotificationValue> progress,
        [Description("Rib thickness in mm.")] double thicknessMm,
        [Description("Rib sketch (default: the open sketch, else the last finished one).")] string? sketch = null,
        [Description("Thickness centered on the sketch (else on one side).")] bool bothSides = true,
        [Description("Extend the material to the other side of the sketch.")] bool flipMaterial = false,
        [Description("Extrude normal to the sketch plane instead of parallel to it.")] bool normalToSketch = false,
        [Description("Draft angle in degrees.")] double? draftDeg = null,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.RibAsync(new RibRequest
        {
            Doc = doc, Sketch = sketch, ThicknessMm = thicknessMm, BothSides = bothSides, FlipMaterial = flipMaterial,
            NormalToSketch = normalToSketch, DraftDeg = draftDeg, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_rib");

    [McpServerTool(Name = "feature_thread_cosmetic", Title = "Cosmetic thread", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Cosmetic thread annotation (no geometry) on the circular edge of a hole or shaft, e.g. for tapped holes; size M6 gives a " +
                 "hole the major, a shaft the minor diameter. Verified: a CosmeticThread whose diameter and depth read back; otherwise rolled back.")]
    public Task<string> FeatureThreadCosmetic(
        IProgress<ProgressNotificationValue> progress,
        [Description("Circular edge where the thread starts, e.g. a hole rim.")] EntityRef edge,
        [Description("Metric size M6 or M6x0.75 (sets the diameter).")] string? size = null,
        [Description("Thread diameter in mm (instead of size).")] double? diameterMm = null,
        [Description("Thread length in mm.")] double? depthMm = null,
        [Description("Thread the whole length instead of depthMm.")] bool throughAll = false,
        [Description("none (default: the diameter as given) | ansi_metric | iso | ansi_inch | din | jis; a standard needs size.")] string? standard = null,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.CosmeticThreadAsync(new CosmeticThreadRequest
        {
            Doc = doc, Edge = edge, Standard = standard, Size = size, DiameterMm = diameterMm, DepthMm = depthMm, ThroughAll = throughAll,
            Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_thread_cosmetic");
}
