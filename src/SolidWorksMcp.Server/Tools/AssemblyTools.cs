using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Assemblies: insert, mate, list, set, move, replace and pattern components, and interference detection.</summary>
[McpServerToolType, Toolset("assembly")]
public sealed class AssemblyTools(SwSession session)
{
    private const string DocDesc = "Assembly (handle, title or path); default: the target document.";
    private const string CompDesc = "Component name, e.g. \"bracket-1\" (asm_component_list).";
    private const string KeepDesc = "Keep the result even if verification fails (for diagnosis).";
    private const string FileDesc = "A saved .sldprt/.sldasm (relative: the output folder), or an open saved document (handle D2 or title).";

    private readonly AssemblyService _svc = new(session);

    [McpServerTool(Name = "asm_insert", Title = "Insert component", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Insert / add a component (part or subassembly) with its origin at originMm and an optional rotation about it. " +
                 "SOLIDWORKS fixes the first component (reported; fixed=false floats it). Verified: one more component and the placement " +
                 "read back (origin within 1 µm, axes), else removed. Returns its name (bracket-1), origin, rotation, box, fixed.")]
    public Task<string> AsmInsert(
        IProgress<ProgressNotificationValue> progress,
        [Description(FileDesc)] string path,
        [Description("Where the component's origin goes, [x,y,z] assembly mm (default [0,0,0]).")] double[]? originMm = null,
        [Description("{axis:[x,y,z], angleDeg}: right-hand rotation about the component origin.")] AsmRotation? rotation = null,
        [Description("Configuration of the file to use (default: its active one).")] string? configuration = null,
        [Description("true fixes, false floats; default: SOLIDWORKS' choice.")] bool? @fixed = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.InsertAsync(new AsmInsertRequest
        {
            Doc = doc, Path = path, OriginMm = originMm, Rotation = rotation, Configuration = configuration, Fixed = @fixed,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "asm_insert");

    [McpServerTool(Name = "asm_mate_add", Title = "Add mate", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Mate / constrain components. a and b are in different components: planes {component:'base-1', name:'top'}; " +
                 "faces {component:'pin-1', at:[x,y,z]} in that part's mm (type edge|vertex for those); lock: {name:'base-1'}. " +
                 "Also limit distance/angle (minMm/maxMm, minDeg/maxDeg), symmetric (plane), width (a, b width faces; c, d tab faces), " +
                 "hinge (a, b round; c, d planar), gear (ratio), rack_pinion (pitchMm; a = the rack's straight edge). Verified: a new " +
                 "mate without errors and the solved geometry (or the gear/rack numbers) read back, else deleted and the components moved back.")]
    public Task<string> AsmMateAdd(
        IProgress<ProgressNotificationValue> progress,
        [Description("coincident | concentric | parallel | perpendicular | tangent | distance | angle | lock | symmetric | width | hinge | gear | rack_pinion")] string type,
        [Description("First entity.")] EntityRef a,
        [Description("Second entity (another component, or an assembly plane {name:'top'}).")] EntityRef b,
        [Description("width: first tab face; hinge: first coincident face.")] EntityRef? c = null,
        [Description("width: second tab face; hinge: second coincident face.")] EntityRef? d = null,
        [Description("symmetric: the symmetry plane, e.g. {name:'right'}.")] EntityRef? plane = null,
        [Description("closest | aligned | anti_aligned (normals/axes same or opposite).")] string alignment = "closest",
        [Description("Flip to the other side (gear, rack: reverse the direction).")] bool flip = false,
        [Description("distance: the gap in mm (> 0).")] double? distanceMm = null,
        [Description("angle: degrees (0-180).")] double? angleDeg = null,
        [Description("distance: limit mate minimum in mm (with maxMm).")] double? minMm = null,
        [Description("distance: limit mate maximum in mm.")] double? maxMm = null,
        [Description("angle: limit mate minimum in degrees (with maxDeg).")] double? minDeg = null,
        [Description("angle: limit mate maximum in degrees.")] double? maxDeg = null,
        [Description("gear: [a, b], e.g. tooth counts [20, 40].")] double[]? ratio = null,
        [Description("rack_pinion: rack travel per pinion turn in mm (π × pitch diameter).")] double? pitchMm = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MateAddAsync(new AsmMateRequest
        {
            Doc = doc, Type = type, A = a, B = b, C = c, D = d, Plane = plane, Alignment = alignment, Flip = flip, DistanceMm = distanceMm,
            AngleDeg = angleDeg, MinMm = minMm, MaxMm = maxMm, MinDeg = minDeg, MaxDeg = maxDeg, Ratio = ratio, PitchMm = pitchMm,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "asm_mate_add");

    [McpServerTool(Name = "asm_mate_list", Title = "List mates", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the assembly's mates: name, type, alignment, entity kinds and components, distance/angle with its dimension " +
                 "name (dimension_set drives it), error and suppressed state.")]
    public Task<string> AsmMateList(
        [Description("Maximum mates to return.")] int limit = 100,
        [Description("Skip this many first.")] int offset = 0,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Json(_svc.MateListAsync(doc, limit, offset, cancellationToken));

    [McpServerTool(Name = "asm_component_list", Title = "List components", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List components: name, path, configuration, fixed, suppression (resolved | lightweight | suppressed), visible, " +
                 "placement (originMm; rotation rows: assembly = R·part + origin), assembly box, constrained status, mate count, children.")]
    public Task<string> AsmComponentList(
        [Description("Also components inside subassemblies (named sub-1/part-1).")] bool allLevels = false,
        [Description("Maximum components to return.")] int limit = 100,
        [Description("Skip this many first.")] int offset = 0,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Json(_svc.ComponentListAsync(doc, allLevels, limit, offset, cancellationToken));

    [McpServerTool(Name = "asm_component_set", Title = "Set component state", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Fix or float, suppress or unsuppress, resolve or make lightweight, hide or show a component, or switch its " +
                 "configuration. Each change is read back (all reverted if one fails); returns the previous values and the new state.")]
    public Task<string> AsmComponentSet(
        IProgress<ProgressNotificationValue> progress,
        [Description(CompDesc)] string component,
        [Description("true fixes it in place, false floats it.")] bool? @fixed = null,
        [Description("true suppresses, false unsuppresses.")] bool? suppressed = null,
        [Description("true loads a lightweight component fully, false makes it lightweight.")] bool? resolved = null,
        [Description("false hides, true shows.")] bool? visible = null,
        [Description("Configuration of the component's file to use.")] string? configuration = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ComponentSetAsync(new AsmComponentSetRequest
        {
            Doc = doc, Component = component, Fixed = @fixed, Suppressed = suppressed, Resolved = resolved, Visible = visible,
            Configuration = configuration,
        }, cancellationToken), progress, "asm_component_set");

    [McpServerTool(Name = "asm_component_move", Title = "Move component", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Move / rotate a floating component (its transform): translateMm and/or rotation about pointMm (default: its origin); " +
                 "absolute=true makes them the new origin and orientation. Fixed components are refused (float them first). Verified: " +
                 "the placement read back. Its mates pull it back at the next rebuild (warned).")]
    public Task<string> AsmComponentMove(
        [Description(CompDesc)] string component,
        [Description("Offset [dx,dy,dz] mm (absolute: the new origin).")] double[]? translateMm = null,
        [Description("{axis:[x,y,z], angleDeg, pointMm?}, assembly axes, right-hand rule.")] AsmRotation? rotation = null,
        [Description("Set the placement instead of moving by it.")] bool absolute = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Json(_svc.MoveAsync(new AsmMoveRequest
        {
            Doc = doc, Component = component, TranslateMm = translateMm, Rotation = rotation, Absolute = absolute,
        }, cancellationToken));

    [McpServerTool(Name = "asm_interference", Title = "Interference check", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Interference / collision detection between components (SOLIDWORKS Interference Detection). Returns each " +
                 "interfering pair with its overlap volume mm³ and center; count 0 when clear.")]
    public Task<string> AsmInterference(
        IProgress<ProgressNotificationValue> progress,
        [Description("Only interferences involving this component, or among these (default: all).")] string[]? components = null,
        [Description("Also report touching (coincident) faces.")] bool includeCoincident = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.InterferenceAsync(doc, components, includeCoincident, cancellationToken), progress, "asm_interference");

    [McpServerTool(Name = "asm_replace", Title = "Replace component", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Replace a component with another saved part or assembly: this instance, or all instances of its file; mates are " +
                 "re-attached where possible. Verified: the instance(s) now reference the new file. Returns the new names and the " +
                 "mates in error afterwards.")]
    public Task<string> AsmReplace(
        IProgress<ProgressNotificationValue> progress,
        [Description(CompDesc)] string component,
        [Description(FileDesc)] string path,
        [Description("Replace every instance of the component's file.")] bool allInstances = false,
        [Description("Re-attach the mates to the new component.")] bool reattachMates = true,
        [Description("Configuration of the new file (default: the same name).")] string? configuration = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ReplaceAsync(new AsmReplaceRequest
        {
            Doc = doc, Component = component, Path = path, AllInstances = allInstances, ReattachMates = reattachMates,
            Configuration = configuration,
        }, cancellationToken), progress, "asm_replace");

    [McpServerTool(Name = "asm_pattern", Title = "Pattern / mirror components", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Pattern or mirror components as instances of the same files: linear along a direction (optional second one), circular " +
                 "about an axis, or mirror about a plane; oppositeHand:true writes mirrored part files instead (never replacing one). Counts " +
                 "include the seed. Verified: every new instance's placement (the seed moved by i·spacing, turned by k·angle, or " +
                 "reflected; opposite hands vertex by vertex), else rolled back.")]
    public Task<string> AsmPattern(
        IProgress<ProgressNotificationValue> progress,
        [Description("linear | circular | mirror")] string kind,
        [Description("Seed components (top level), e.g. [\"bolt-1\"].")] string[] components,
        [Description("linear/circular: instances, the seed included.")] int? count = null,
        [Description("linear: distance between instances in mm.")] double? spacingMm = null,
        [Description("linear: +x | -x | +y | -y | +z | -z (assembly axes); with directionRef, the sign along it.")] string? direction = null,
        [Description("linear: a straight edge, axis, plane or planar face giving the direction, e.g. {component:'rail-1', type:'edge', at:[x,y,z]}.")] EntityRef? directionRef = null,
        [Description("linear: second direction, an assembly axis such as \"+z\".")] string? direction2 = null,
        [Description("linear: instances in direction 2, the seed row included.")] int? count2 = null,
        [Description("linear: spacing in direction 2 in mm.")] double? spacing2Mm = null,
        [Description("circular: an axis, a cylindrical face or a circular/straight edge, e.g. {component:'hub-1', at:[x,y,z]} (part mm).")] EntityRef? axis = null,
        [Description("circular: with equalSpacing the angle spanned (default 360 = full circle), else the angle between instances.")] double? totalAngleDeg = null,
        [Description("circular: spread the instances evenly (default true).")] bool? equalSpacing = null,
        [Description("mirror: the plane, e.g. {name:'right'} or {component:'base-1', name:'front'}.")] EntityRef? plane = null,
        [Description("mirror: make opposite-hand parts (new mirrored part files) instead of instances.")] bool oppositeHand = false,
        [Description("oppositeHand: appended to the seed part's file name (default \"_mirror\").")] string? suffix = null,
        [Description("oppositeHand: folder for the new files (default: the seed parts' folder).")] string? folder = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.PatternAsync(new AsmPatternRequest
        {
            Doc = doc, Kind = kind, Components = components, Count = count, SpacingMm = spacingMm, Direction = direction,
            DirectionRef = directionRef, Direction2 = direction2, Count2 = count2, Spacing2Mm = spacing2Mm, Axis = axis,
            TotalAngleDeg = totalAngleDeg, EqualSpacing = equalSpacing, Plane = plane, OppositeHand = oppositeHand, Suffix = suffix,
            Folder = folder, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "asm_pattern");

    [McpServerTool(Name = "asm_explode", Title = "Exploded view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Exploded view of the assembly (a new one in the active configuration): steps that move components along assembly " +
                 "axes, applied in order; drawings can show it. Verified: every component moved by the sum of its steps; else the view " +
                 "is deleted. collapse:true shows the assembly collapsed again afterwards.")]
    public Task<string> AsmExplode(
        IProgress<ProgressNotificationValue> progress,
        [Description("Steps in order, e.g. [{components:['lid-1'], direction:'+y', distanceMm:60}].")] AsmExplodeStep[] steps,
        [Description("Show the assembly collapsed again at the end (the exploded view is kept).")] bool collapse = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ExplodeAsync(new AsmExplodeRequest { Doc = doc, Steps = steps, Collapse = collapse }, cancellationToken), progress, "asm_explode");

    private static async Task<string> Json<T>(Task<T> task) where T : ToolResult => ResultJson.Serialize(await task.ConfigureAwait(false));
}
