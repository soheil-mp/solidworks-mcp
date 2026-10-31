using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Part features beyond extrude and fillet: revolve, chamfer, shell, Hole Wizard holes, patterns and mirror.</summary>
[McpServerToolType, Toolset("features")]
public sealed class PartFeatureTools(SwSession session)
{
    private const string NameDesc = "Rename the new feature.";
    private const string KeepDesc = "Keep the feature even if verification fails (for diagnosis).";
    private const string DocDesc = "Document (handle, title or path); default: the target document.";
    private const string FeaturesDesc = "Features to copy, by name (feature_list), e.g. [\"Cut-Extrude1\"].";

    private readonly PartFeatureService _svc = new(session);

    [McpServerTool(Name = "feature_revolve", Title = "Revolve (boss or cut)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Revolve / revolved boss / revolved cut (cut=true): spins a closed sketch profile around an axis, by default the sketch's only " +
                 "centerline. Uses the open/last sketch unless 'sketch' is given. Verified: the feature exists without errors, the volume grew " +
                 "(boss) or shrank (cut), and every new face is coaxial with the axis (a partial revolve's end caps at the angle); otherwise it " +
                 "is rolled back.")]
    public Task<string> FeatureRevolve(
        IProgress<ProgressNotificationValue> progress,
        [Description("Sketch to revolve (default: the open sketch, else the last finished one).")] string? sketch = null,
        [Description("Axis (default: the sketch's only centerline): a centerline by name {name:'Line5'}, a reference axis {name:'Axis1'} " +
                     "or a straight edge {type:'edge',at:[x,y,z]}; it must lie in the sketch plane.")] EntityRef? axis = null,
        [Description("Revolve angle in degrees (0 to 360).")] double angleDeg = 360,
        [Description("Angle in the opposite direction in degrees (two-direction revolve; the sum at most 360).")] double? angle2Deg = null,
        [Description("Cut material instead of adding it.")] bool cut = false,
        [Description("Merge the result with existing bodies (boss).")] bool merge = true,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.RevolveAsync(new RevolveRequest
        {
            Doc = doc, Sketch = sketch, Axis = axis, AngleDeg = angleDeg, Angle2Deg = angle2Deg, Cut = cut, Merge = merge,
            Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_revolve");

    [McpServerTool(Name = "feature_chamfer", Title = "Chamfer", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Chamfer / bevel edges: equal distance, two distances or distance + angle. Edges by id, point or query, e.g. " +
                 "edges:[{at:[0,20,30]}]. Verified: a Chamfer feature with faces (an empty chamfer is rejected), the volume or face count " +
                 "changed, and on straight edges the chamfer face's two legs measured; otherwise rolled back.")]
    public Task<string> FeatureChamfer(
        IProgress<ProgressNotificationValue> progress,
        [Description("Edges to chamfer.")] EntityRef[] edges,
        [Description("Chamfer distance in mm (the first leg).")] double distanceMm,
        [Description("equal | distance_distance | angle_distance")] string type = "equal",
        [Description("distance_distance: the second leg in mm.")] double? distance2Mm = null,
        [Description("angle_distance: the angle in degrees (between 0 and 90).")] double angleDeg = 45,
        [Description("Swap which face the first distance (or the angle) applies to.")] bool flip = false,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ChamferAsync(new ChamferRequest
        {
            Doc = doc, Edges = edges, Type = type, DistanceMm = distanceMm, Distance2Mm = distance2Mm, AngleDeg = angleDeg, Flip = flip,
            Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_chamfer");

    [McpServerTool(Name = "feature_shell", Title = "Shell", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Shell / hollow out a solid: removes the given faces (open sides) and leaves walls of constant thickness, inward or " +
                 "outward. Verified: a Shell feature without errors, the volume changed, and walls found at the thickness behind (or in front " +
                 "of) the original planar faces; otherwise rolled back.")]
    public Task<string> FeatureShell(
        IProgress<ProgressNotificationValue> progress,
        [Description("Faces to remove (the open sides), e.g. [{at:[0,20,0]}]; [] makes a closed hollow body.")] EntityRef[] removeFaces,
        [Description("Wall thickness in mm.")] double thicknessMm,
        [Description("Grow the walls outward instead of inward.")] bool outward = false,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ShellAsync(new ShellRequest
        {
            Doc = doc, RemoveFaces = removeFaces, ThicknessMm = thicknessMm, Outward = outward, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_shell");

    [McpServerTool(Name = "feature_hole", Title = "Hole Wizard holes", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Hole Wizard holes at points on planar faces, drilled against the face normal: drill, clearance (fit), counterbore, " +
                 "countersink, tapped. One HoleWzd feature per point. Verified per hole: a coaxial cylinder of the planned diameter (blind: " +
                 "the requested depth, without the drill tip) and volume removed; on any failure all holes are rolled back. Drill, " +
                 "clearance, counterbore, countersink and through_all are live-verified; tapped is made as its tap-drill hole (no thread " +
                 "geometry) unless SWMCP_EXPERIMENTAL=1.")]
    public Task<string> FeatureHole(
        IProgress<ProgressNotificationValue> progress,
        [Description("Hole centers in model mm, on a planar face: [[x,y,z], ...].")] double[][] points,
        [Description("drill | clearance | counterbore | countersink | tapped")] string type = "drill",
        [Description("Drill: diameter in mm. Others: overrides the table diameter of the through hole.")] double? diameterMm = null,
        [Description("Screw size \"M5\" (tapped also \"M6x0.75\"), or a drill size \"Ø6\".")] string? size = null,
        [Description("blind | through_all")] string end = "blind",
        [Description("Blind depth in mm (full diameter, without the drill tip).")] double? depthMm = null,
        [Description("close | normal | loose (clearance, counterbore, countersink)")] string fit = "normal",
        [Description("ansi_metric | iso | ansi_inch")] string standard = "ansi_metric",
        [Description("The planar face for all holes (default: the face under each point).")] EntityRef? face = null,
        [Description("Rename the new feature (several holes: name1, name2, ...).")] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.HoleAsync(new HoleRequest
        {
            Doc = doc, Points = points, Face = face, Type = type, Standard = standard, Size = size, DiameterMm = diameterMm, Fit = fit,
            End = end, DepthMm = depthMm, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_hole");

    [McpServerTool(Name = "feature_pattern_linear", Title = "Linear pattern", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Linear pattern / array of features along a model axis (\"+x\") or a straight edge, axis or planar face, optionally in a " +
                 "second direction; counts include the seed. Verified: an LPattern feature without errors, the volume or faces changed, and " +
                 "the instance faces at the requested offsets (a pattern SOLIDWORKS ran backwards is redone flipped); otherwise rolled back.")]
    public Task<string> FeaturePatternLinear(
        IProgress<ProgressNotificationValue> progress,
        [Description(FeaturesDesc)] string[] features,
        [Description("Instances in direction 1, the seed included.")] int count,
        [Description("Distance between instances in mm.")] double spacingMm,
        [Description("+x | -x | +y | -y | +z | -z; with directionRef, the sign along it.")] string? direction = null,
        [Description("Straight edge, axis or planar face (its normal) giving direction 1, instead of a model axis.")] EntityRef? directionRef = null,
        [Description("Second direction, a model axis such as \"+z\".")] string? direction2 = null,
        [Description("Instances in direction 2, the seed row included.")] int? count2 = null,
        [Description("Distance between instances in direction 2 in mm.")] double? spacing2Mm = null,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.LinearPatternAsync(new LinearPatternRequest
        {
            Doc = doc, Features = features, Direction = direction, DirectionRef = directionRef, Count = count, SpacingMm = spacingMm,
            Direction2 = direction2, Count2 = count2, Spacing2Mm = spacing2Mm, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_pattern_linear");

    [McpServerTool(Name = "feature_pattern_circular", Title = "Circular pattern", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Circular / polar pattern of features around an axis: a reference axis, a cylindrical face or a circular or straight edge; " +
                 "count includes the seed. Verified: a CirPattern feature without errors, the volume or faces changed, and the instance faces " +
                 "at the expected rotations; otherwise rolled back.")]
    public Task<string> FeaturePatternCircular(
        IProgress<ProgressNotificationValue> progress,
        [Description(FeaturesDesc)] string[] features,
        [Description("Rotation axis, e.g. {name:'Axis1'} or a cylindrical face {type:'face',at:[x,y,z]}.")] EntityRef axis,
        [Description("Instances, the seed included.")] int count,
        [Description("equalSpacing: the angle the instances span (360 = full circle); otherwise the angle between instances.")] double totalAngleDeg = 360,
        [Description("Spread the instances evenly over totalAngleDeg.")] bool equalSpacing = true,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.CircularPatternAsync(new CircularPatternRequest
        {
            Doc = doc, Features = features, Axis = axis, Count = count, TotalAngleDeg = totalAngleDeg, EqualSpacing = equalSpacing,
            Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_pattern_circular");

    [McpServerTool(Name = "feature_pattern_curve", Title = "Curve-driven pattern", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Patterns features along a curve: an edge or a sketch segment, e.g. {type:'sketch_segment', name:'Arc1', sketch:'Sketch3'}. " +
                 "count includes the seed; spacingMm apart along the curve, or spread equally over it. Verified: no errors, the volume " +
                 "changed and (round seeds) count − 1 new instances; the curve's other sense is tried if the asked one misses the body.")]
    public Task<string> FeaturePatternCurve(
        IProgress<ProgressNotificationValue> progress,
        [Description("Seed features, e.g. [\"Cut-Extrude2\"].")] string[] features,
        [Description("The curve: an edge or a sketch segment.")] EntityRef curve,
        [Description("Instances, the seed included.")] int count,
        [Description("Spacing along the curve, mm (default: equal over the whole curve).")] double? spacingMm = null,
        [Description("Run the other way along the curve.")] bool reverse = false,
        [Description("tangent (turn with the curve) | seed (keep the seed's orientation).")] string align = "tangent",
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.CurvePatternAsync(new CurvePatternRequest
        {
            Doc = doc, Features = features, Curve = curve, Count = count, SpacingMm = spacingMm, Reverse = reverse, Align = align,
            Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_pattern_curve");

    [McpServerTool(Name = "feature_pattern_fill", Title = "Fill pattern", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Fills a flat face with round holes (holeDiameterMm, cut through) or copies of seed features: perforation (staggered " +
                 "rows), square, circular or polygon layout, spacingMm apart, marginMm from the boundary. Vents, perforated panels. " +
                 "Verified: no errors, the volume changed; round holes are counted.")]
    public Task<string> FeaturePatternFill(
        IProgress<ProgressNotificationValue> progress,
        [Description("The flat face to fill, e.g. {at:[0,2,0]}.")] EntityRef face,
        [Description("Spacing between instances, mm.")] double spacingMm,
        [Description("Round holes of this diameter, mm: a seed hole is cut through at the face's interior point (or features).")] double? holeDiameterMm = null,
        [Description("Seed features to copy instead of holes.")] string[]? features = null,
        [Description("perforation | square | circular | polygon")] string layout = "perforation",
        [Description("Distance kept from the boundary, mm.")] double? marginMm = null,
        [Description("perforation/square: a straight edge for the rows (default: the face's longest).")] EntityRef? directionRef = null,
        [Description("perforation: stagger angle, degrees (default 60).")] double? staggerDeg = null,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FillPatternAsync(new FillPatternRequest
        {
            Doc = doc, Face = face, SpacingMm = spacingMm, HoleDiameterMm = holeDiameterMm, Features = features, Layout = layout, MarginMm = marginMm,
            DirectionRef = directionRef, StaggerDeg = staggerDeg, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_pattern_fill");

    [McpServerTool(Name = "feature_mirror", Title = "Mirror features", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Mirror features about a plane or a planar face. Verified: a MirrorPattern feature without errors, the volume or faces " +
                 "changed, and the new faces are the reflections of the seed's faces; otherwise rolled back.")]
    public Task<string> FeatureMirror(
        IProgress<ProgressNotificationValue> progress,
        [Description(FeaturesDesc)] string[] features,
        [Description("Mirror plane: {name:'right'}, a plane's name, or a planar face {at:[x,y,z]}.")] EntityRef plane,
        [Description(NameDesc)] string? name = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MirrorAsync(new MirrorRequest
        {
            Doc = doc, Features = features, Plane = plane, Name = name, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "feature_mirror");
}
