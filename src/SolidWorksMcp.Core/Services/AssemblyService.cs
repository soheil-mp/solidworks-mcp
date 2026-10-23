using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== inputs

/// <summary>A rotation for asm_insert and asm_component_move: right-hand rule about an axis in assembly space.</summary>
[Description("{axis:[x,y,z], angleDeg, pointMm?}")]
public sealed class AsmRotation
{
    [Description("Axis direction [x,y,z], assembly space.")]
    public required double[] Axis { get; set; }

    [Description("Degrees, right-hand rule about the axis.")]
    public required double AngleDeg { get; set; }

    [Description("Move only: a point on the axis, mm (default: the component origin).")]
    public double[]? PointMm { get; set; }
}

/// <summary>asm_insert options.</summary>
public sealed record AsmInsertRequest
{
    public string? Doc { get; init; }
    /// <summary>A saved .sldprt/.sldasm, or the handle/title of an open, saved document.</summary>
    public string? Path { get; init; }
    /// <summary>Where the component's origin goes, assembly mm (default [0,0,0]).</summary>
    public double[]? OriginMm { get; init; }
    public AsmRotation? Rotation { get; init; }
    public string? Configuration { get; init; }
    /// <summary>null keeps SOLIDWORKS' choice (only the first component is fixed).</summary>
    public bool? Fixed { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>asm_mate_add options.</summary>
public sealed record AsmMateRequest
{
    public string? Doc { get; init; }
    /// <summary>coincident | concentric | parallel | perpendicular | tangent | distance | angle | lock.</summary>
    public string? Type { get; init; }
    public EntityRef? A { get; init; }
    public EntityRef? B { get; init; }
    /// <summary>closest | aligned | anti_aligned.</summary>
    public string? Alignment { get; init; } = "closest";
    public bool Flip { get; init; }
    public double? DistanceMm { get; init; }
    public double? AngleDeg { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>asm_component_set options (null = leave as it is).</summary>
public sealed record AsmComponentSetRequest
{
    public string? Doc { get; init; }
    public string? Component { get; init; }
    public bool? Fixed { get; init; }
    public bool? Suppressed { get; init; }
    /// <summary>true resolves a lightweight component (loads its geometry); false makes it lightweight.</summary>
    public bool? Resolved { get; init; }
    public bool? Visible { get; init; }
    public string? Configuration { get; init; }
}

/// <summary>asm_component_move options.</summary>
public sealed record AsmMoveRequest
{
    public string? Doc { get; init; }
    public string? Component { get; init; }
    /// <summary>Relative: the offset; absolute: the new origin (assembly mm).</summary>
    public double[]? TranslateMm { get; init; }
    public AsmRotation? Rotation { get; init; }
    public bool Absolute { get; init; }
}

/// <summary>asm_replace options.</summary>
public sealed record AsmReplaceRequest
{
    public string? Doc { get; init; }
    public string? Component { get; init; }
    public string? Path { get; init; }
    public bool AllInstances { get; init; }
    public bool ReattachMates { get; init; } = true;
    public string? Configuration { get; init; }
}

// ====================================================================== results

/// <summary>One component (mm). <see cref="Rotation"/> rows R with assembly point = R · part point + origin.</summary>
public sealed class AsmComponentInfo
{
    /// <summary>The component name ("bracket-1"; inside a subassembly "sub-1/bracket-1").</summary>
    public required string Name { get; init; }
    public string? Path { get; init; }
    /// <summary>The referenced configuration.</summary>
    public string? Configuration { get; init; }
    public bool Fixed { get; init; }
    /// <summary>resolved | lightweight | suppressed.</summary>
    public required string Suppression { get; init; }
    public bool Visible { get; init; }
    /// <summary>Where the component's origin is, assembly mm.</summary>
    public double[]? OriginMm { get; init; }
    /// <summary>3×3 rows: assembly = R · part + origin (the columns are the component's x, y, z axes in the assembly).</summary>
    public double[][]? Rotation { get; init; }
    /// <summary>The component's bounding box in assembly space.</summary>
    public BoxMm? Box { get; init; }
    /// <summary>under | fully | over | no_solution | invalid | unknown | autosolve_off (IComponent2.GetConstrainedStatus).</summary>
    public string? Constrained { get; init; }
    /// <summary>The number of mates of this assembly that reference the component.</summary>
    public int? Mates { get; init; }
    /// <summary>Subassemblies: the number of child components.</summary>
    public int? Children { get; init; }
    /// <summary>Components inside a subassembly: the parent's name.</summary>
    public string? Parent { get; init; }
}

public sealed class AsmInsertResult : ToolResult
{
    public required AsmComponentInfo Component { get; init; }
    /// <summary>The file inserted.</summary>
    public required string File { get; init; }
    /// <summary>Top-level components after the insert.</summary>
    public int ComponentCount { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
    public string? Message { get; init; }
}

public sealed class AsmMateEntity
{
    /// <summary>The component it belongs to (null: the assembly's own plane, axis or origin).</summary>
    public string? Component { get; init; }
    /// <summary>point | line | circle | plane | cylinder | sphere | cone | … (IMateEntity2.ReferenceType2).</summary>
    public required string Kind { get; init; }
}

public sealed class AsmMateInfo
{
    public required string Name { get; init; }
    /// <summary>coincident | concentric | parallel | perpendicular | tangent | distance | angle | lock | …</summary>
    public required string Type { get; init; }
    public string? Alignment { get; init; }
    public bool? Flipped { get; init; }
    public required IReadOnlyList<AsmMateEntity> Entities { get; init; }
    public double? DistanceMm { get; init; }
    public double? AngleDeg { get; init; }
    /// <summary>The value's dimension (e.g. D1@Distance1), which dimension_set can drive.</summary>
    public string? Dimension { get; init; }
    public int? Error { get; init; }
    public string? ErrorName { get; init; }
    public bool? Warning { get; init; }
    public bool? Suppressed { get; init; }
}

public sealed class AsmMateAddResult : ToolResult
{
    public required AsmMateInfo Mate { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
    /// <summary>The mated components after solving (where they went, fixed, constrained status).</summary>
    public required IReadOnlyList<AsmComponentInfo> Components { get; init; }
    /// <summary>The components the mate moved.</summary>
    public IReadOnlyList<string>? Moved { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed class AsmMateListResult : ToolResult
{
    public required IReadOnlyList<AsmMateInfo> Mates { get; init; }
    public int Total { get; init; }
    /// <summary>Mates with a rebuild error (not warnings).</summary>
    public int InError { get; init; }
    public bool? Truncated { get; init; }
}

public sealed class AsmComponentListResult : ToolResult
{
    public required IReadOnlyList<AsmComponentInfo> Components { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>The settable state of a component (asm_component_set returns it as 'previous').</summary>
public sealed class AsmComponentState
{
    public bool Fixed { get; init; }
    public required string Suppression { get; init; }
    public bool Visible { get; init; }
    public string? Configuration { get; init; }
}

public sealed class AsmComponentSetResult : ToolResult
{
    public required AsmComponentInfo Component { get; init; }
    public required AsmComponentState Previous { get; init; }
    public required IReadOnlyList<string> Changed { get; init; }
}

public sealed class AsmMoveResult : ToolResult
{
    public required AsmComponentInfo Component { get; init; }
    public required double[] PreviousOriginMm { get; init; }
    public required double[][] PreviousRotation { get; init; }
    /// <summary>How far the origin moved, [dx, dy, dz] mm.</summary>
    public required double[] TranslationMm { get; init; }
    /// <summary>The angle of the orientation change.</summary>
    public double RotatedDeg { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
}

public sealed class AsmInterference
{
    public required IReadOnlyList<string> Components { get; init; }
    public double VolumeMm3 { get; init; }
    /// <summary>The center of the overlap's bounding box, assembly mm (when SOLIDWORKS gives the overlap body).</summary>
    public double[]? CenterMm { get; init; }
}

public sealed class AsmInterferenceResult : ToolResult
{
    public int Count { get; init; }
    public double TotalVolumeMm3 { get; init; }
    public required IReadOnlyList<AsmInterference> Interferences { get; init; }
    /// <summary>The components the report is limited to (null: the whole assembly).</summary>
    public IReadOnlyList<string>? Among { get; init; }
    public bool IncludeCoincident { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class AsmReplaced
{
    public required string From { get; init; }
    public required string To { get; init; }
}

public sealed class AsmReplaceResult : ToolResult
{
    public required IReadOnlyList<AsmReplaced> Replaced { get; init; }
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public required IReadOnlyList<AsmComponentInfo> Components { get; init; }
    /// <summary>Mates with an error or warning after the replacement.</summary>
    public required IReadOnlyList<AsmMateInfo> MatesInError { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
}

// ====================================================================== service

/// <summary>
/// Assemblies (DESIGN §4.9): inserting, placing, mating, listing, setting, moving and replacing components, and
/// interference detection. Component bodies report part-space geometry (api notes §8), so geometry is mapped through
/// IComponent2.Transform2 — always with SOLIDWORKS' own math (MathPoint/MathVector.MultiplyTransform and
/// MathUtility.ComposeTransform), so no result depends on the unverified row/column layout of ArrayData. Every change
/// is read back; a mate is checked against its geometric intent and deleted (and the components moved back) when it
/// doesn't match.
/// </summary>
public sealed partial class AssemblyService(SwSession session)
{
    private const double PosTol = 1e-6;  // m: positions read back (component origins, mate geometry)
    private const double AxisTol = 1e-7; // unit-vector difference for axes read back (0.1 µrad)
    private const int MateMark = 1;      // AddMate5: both entities on mark 1 (VERIFIED)

    private static readonly EntityKind[] MateKinds =
        [EntityKind.Face, EntityKind.Edge, EntityKind.Vertex, EntityKind.Plane, EntityKind.Axis, EntityKind.Point, EntityKind.Origin];

    private TimeSpan Long(double seconds) => TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, seconds));

    // ================================================================== asm_insert

    public Task<AsmInsertResult> InsertAsync(AsmInsertRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Path))
            throw SwException.InvalidArgument("'path' is required: a saved .sldprt/.sldasm, or the handle of an open, saved document.",
                "Save a new part first with doc_save {mode:'as', path:'name.sldprt'}.");
        var origin = q.OriginMm is null ? Vec3.Zero : Units.PointM(q.OriginMm, "originMm");
        var rot = RotationOf(q.Rotation, "rotation", "asm_insert rotates about the component origin; asm_component_move takes pointMm.");
        return session.RunAsync("asm_insert", ctx => Insert(ctx, q, origin, rot?.R ?? AsmMath.Identity, Clean(q.Configuration)),
            RunOptions.Write(q.Doc) with { Timeout = Long(120) }, ct);
    }

    private static AsmInsertResult Insert(SwContext ctx, AsmInsertRequest q, Vec3 origin, double[] rot, string? config)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        var file = ResolveFile(ctx, q.Path!, "path");
        if (SamePath(file.Full, DocumentRegistry.SafePath(d)))
            throw SwException.InvalidArgument("An assembly can't contain itself.", "Insert a part or another assembly.");
        config = MatchConfig(ctx, file, config);
        if (file.Open is IModelDoc2 open && Safe(() => open.GetSaveFlag(), false))
            ctx.Warn($"'{ctx.Title(open)}' has unsaved changes: the assembly shows them, but its file doesn't have them until it is saved (doc_save).");

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        int before = asm.GetComponentCount(true);
        IComponent2? added;
        var opened = EnsureLoaded(ctx, d, file, prefs);
        try
        {
            // AddComponent5 puts the component's bounding-box center at (x, y, z) (VERIFIED); the exact placement follows.
            added = asm.AddComponent5(file.Full, (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                "", false, config ?? "", origin.X, origin.Y, origin.Z);
        }
        finally
        {
            CloseLoaded(ctx, d, opened);
        }
        int after = asm.GetComponentCount(true);
        IComponent2 c = added ?? throw new SwException(ErrorCodes.FeatureFailed,
            $"asm_insert: SOLIDWORKS did not insert '{file.Full}'" + (after > before ? $" ({after - before} component(s) appeared anyway; see asm_component_list)." : "."),
            "Check that the file opens in SOLIDWORKS (doc_open) and is not this assembly's parent.");

        string name = c.Name2;
        var checks = new List<string>();
        bool solidworksFixed = false;
        try
        {
            if (after != before + 1)
                throw new SwException(ErrorCodes.VerifyFailed, $"the component count went from {before} to {after}, not {before + 1}.");
            checks.Add($"component count {before} → {after}");

            string? got = Safe<string?>(() => c.ReferencedConfiguration, null);
            if (config is not null && !Eq(got, config))
            {
                c.ReferencedConfiguration = config;
                d.EditRebuild3();
                got = c.ReferencedConfiguration;
                if (!Eq(got, config))
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' uses configuration '{got}', not '{config}'.");
            }
            if (!string.IsNullOrEmpty(got)) checks.Add($"configuration '{got}'");

            // The first component is fixed automatically (VERIFIED). A fixed component may ignore a new transform, so it is
            // floated while it is placed and fixed again afterwards.
            bool fixedNow = c.IsFixed();
            solidworksFixed = fixedNow && q.Fixed is null;
            bool wantFixed = q.Fixed ?? fixedNow;
            if (fixedNow)
            {
                try { SetFixed(d, asm, c, false); }
                catch (SwException ex) { ctx.Warn($"Could not float '{name}' to place it ({ex.Message}); placing it as it is."); }
            }
            var (x, y, z) = AsmMath.Axes(rot);
            var frame = Place(ctx, c, new Frame(origin, x, y, z, []), checks, allowSolve: true);
            checks.Add($"origin read back at [{Pt(frame.Origin)}] mm (within {Units.Mm(PosTol)} mm)");
            checks.Add($"axes read back: x → [{Dir(frame.X)}], y → [{Dir(frame.Y)}], z → [{Dir(frame.Z)}]");
            if (c.IsFixed() != wantFixed)
            {
                try { SetFixed(d, asm, c, wantFixed); }
                catch (SwException) when (q.Fixed is null)
                {
                    ctx.Warn($"'{name}' was fixed by SOLIDWORKS but could not be fixed again after placing it; it is floating.");
                    solidworksFixed = false;
                }
            }
            checks.Add(c.IsFixed() ? "fixed" : "floating");
        }
        catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
        {
            // Anything after the component went in (a wrong placement, a configuration, a COM error) removes it again.
            string tail = q.KeepOnFailure ? "The component was kept (keepOnFailure)." : RemoveComponent(d, asm, c, before);
            var sw = ex as SwException;
            throw new SwException(sw?.Code ?? ErrorCodes.Internal, $"asm_insert: {(sw is null ? $"{ex.GetType().Name}: " : "")}{ex.Message} {tail}",
                sw?.Hint ?? "Check the file, the configuration and that nothing (mates, a fixed state) holds the component.", ex);
        }
        d.GraphicsRedraw2();
        return new AsmInsertResult
        {
            Doc = ctx.Title(d),
            Component = Info(ctx, c, MateCounts(d)),
            File = file.Full,
            ComponentCount = after,
            Verified = checks,
            Message = solidworksFixed
                ? $"SOLIDWORKS fixed '{name}' (the first component of an assembly is fixed); asm_component_set {{component:'{name}', fixed:false}} floats it."
                : null,
        };
    }

    // ================================================================== asm_mate_add

    public Task<AsmMateAddResult> MateAddAsync(AsmMateRequest q, CancellationToken ct = default)
    {
        var (type, typeName) = AsmParse.MateType(q.Type);
        var (align, _) = AsmParse.Alignment(q.Alignment);
        double dist = 0, ang = 0;
        if (typeName == "distance")
        {
            if (q.DistanceMm is not double dm || !double.IsFinite(dm) || dm <= 0)
                throw SwException.InvalidArgument("type 'distance' needs distanceMm > 0.", "For 0 mm use type 'coincident'.");
            dist = Units.M(dm);
        }
        else if (q.DistanceMm is not null)
            throw SwException.InvalidArgument($"distanceMm only applies to type 'distance' (got '{typeName}').");
        if (typeName == "angle")
        {
            if (q.AngleDeg is not double ad || !double.IsFinite(ad) || ad <= 0 || ad > 180)
                throw SwException.InvalidArgument("type 'angle' needs angleDeg greater than 0 and at most 180.", "For 0° use type 'parallel'.");
            ang = Units.Rad(ad);
        }
        else if (q.AngleDeg is not null)
            throw SwException.InvalidArgument($"angleDeg only applies to type 'angle' (got '{typeName}').");
        if (q.A is null || q.B is null)
            throw SwException.InvalidArgument("'a' and 'b' are required: the two entities to mate.",
                "E.g. a:{component:'base-1', name:'top'}, b:{component:'lid-1', name:'top'}; faces: {component:'lid-1', at:[x,y,z]} (part mm).");
        return session.RunAsync("asm_mate_add", ctx => MateAdd(ctx, q, type, typeName, align, dist, ang),
            RunOptions.Write(q.Doc) with { Timeout = Long(90) }, ct);
    }

    private static AsmMateAddResult MateAdd(SwContext ctx, AsmMateRequest q, int type, string typeName, int align, double dist, double ang)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        bool isLock = typeName == "lock";
        var a = ResolveMateRef(ctx, d, asm, q.A!, "a", isLock);
        var b = ResolveMateRef(ctx, d, asm, q.B!, "b", isLock);
        if (SameEntity(a, b)) throw SwException.InvalidArgument($"'a' and 'b' are the same entity ({a.Label}).");
        if (a.Comp is null && b.Comp is null)
            throw SwException.InvalidArgument($"Neither 'a' ({a.Label}) nor 'b' ({b.Label}) belongs to a component.",
                "Name the component in the reference, e.g. {component:'bracket-1', name:'top'} or {component:'bracket-1', at:[x,y,z]}.");
        if (a.Comp is not null && b.Comp is not null && Eq(a.Comp.Name2, b.Comp.Name2))
            throw SwException.InvalidArgument($"'a' and 'b' are both in '{a.Comp.Name2}'; a mate joins two different components (or a component and the assembly's planes).");

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var rawBefore = RawTransforms(asm);
        Frame? fa0 = a.Comp is null ? null : FrameOf(ctx, a.Comp);
        Frame? fb0 = b.Comp is null ? null : FrameOf(ctx, b.Comp);
        var before = Mates(d).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        d.ClearSelection2(true);
        int status;
        Mate2? api;
        try
        {
            SelectForMate(ctx, d, a, false, "a");
            SelectForMate(ctx, d, b, true, "b");
            int selected = d.Sel().GetSelectedObjectCount2(-1);
            if (selected != 2)
                throw new SwException(ErrorCodes.FeatureFailed, $"Selecting {a.Label} and {b.Label} gave {selected} selected objects instead of 2.");
            api = asm.AddMate5(type, align, q.Flip, dist, dist, dist, 0, 0, ang, ang, ang, false, false, 0, out status);
        }
        finally
        {
            d.ClearSelection2(true);
        }
        d.EditRebuild3();

        var created = Mates(d).Where(m => !before.Contains(m.Name)).ToList();
        string label = $"{typeName} mate between {a.Label} and {b.Label}";
        string hint = MateHint(typeName, status, a, b);
        if (created.Count == 0)
        {
            var back = RestoreMoved(ctx, asm, rawBefore);
            throw new SwException(ErrorCodes.FeatureFailed,
                $"asm_mate_add: SOLIDWORKS created no {label}" +
                (status != (int)swAddMateError_e.swAddMateError_NoError ? $" (it reported {AsmParse.MateStatus(status)})" : api is null ? " (AddMate5 returned nothing)" : "") +
                (back.Count > 0 ? $"; moved {string.Join(", ", back)} back." : "."), hint);
        }

        var main = created.FirstOrDefault(m => Safe(() => m.Mate.Type, -1) == type) ?? created[0];
        var checks = new List<string>();
        string? problem = null;
        string code = ErrorCodes.VerifyFailed;
        try
        {
            int gotType = Safe(() => main.Mate.Type, -1);
            if (gotType != type)
            {
                problem = $"'{main.Name}' is a {AsmParse.MateTypeName(gotType)} mate, not {typeName}";
                code = ErrorCodes.FeatureFailed;
            }
            else if (status != (int)swAddMateError_e.swAddMateError_NoError)
            {
                problem = $"SOLIDWORKS reported {AsmParse.MateStatus(status)}";
                code = ErrorCodes.RebuildError;
            }
            else
            {
                int err = main.Feature.GetErrorCode2(out bool warn);
                if (err != 0 && !warn)
                {
                    problem = $"'{main.Name}' has rebuild error {err} ({FeatureVerifier.ErrorName(err)})";
                    code = ErrorCodes.RebuildError;
                }
                else
                {
                    if (err != 0) ctx.Warn($"'{main.Name}' has rebuild warning {err} ({FeatureVerifier.ErrorName(err)}).");
                    checks.Add($"{main.Name} ({typeName}) created and solved without errors");
                    var problems = isLock
                        ? LockIntent(ctx, a, b, fa0, fb0, checks)
                        : AsmMateIntent.Check(typeName, ToAsm(ctx, a), ToAsm(ctx, b), dist, ang, checks);
                    if (problems.Count > 0) problem = string.Join("; ", problems);
                }
            }
        }
        catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
        {
            // Never leave an unverified mate behind.
            problem = $"it could not be verified ({(ex is SwException ? "" : ex.GetType().Name + ": ")}{ex.Message})";
            code = (ex as SwException)?.Code ?? ErrorCodes.Internal;
        }
        if (problem is not null)
        {
            string tail = q.KeepOnFailure ? "The mate was kept (keepOnFailure)." : RollbackMate(ctx, d, asm, created, rawBefore);
            string what = code == ErrorCodes.VerifyFailed ? "doesn't match the request" : "failed";
            throw new SwException(code, $"asm_mate_add: the {label} {what}: {problem}. {tail}", hint);
        }

        var moved = asm.Components(true)
            .Where(c => rawBefore.TryGetValue(c.Name2, out var r0) && AsmMath.Changed(r0, Safe(() => c.AsmXformData(), r0)))
            .Select(c => c.Name2).ToList();
        var counts = MateCounts(d);
        var infos = new List<AsmComponentInfo>();
        foreach (var c in new[] { a.Comp, b.Comp })
            if (c is not null && !infos.Any(i => Eq(i.Name, c.Name2))) infos.Add(Info(ctx, c, counts));
        d.GraphicsRedraw2();
        return new AsmMateAddResult
        {
            Doc = ctx.Title(d),
            Mate = MateRow(main),
            Verified = checks,
            Components = infos,
            Moved = moved.Count > 0 ? moved : null,
            Resolved = [a.R.Info("a"), b.R.Info("b")],
        };
    }

    private static string MateHint(string typeName, int status, MateRef a, MateRef b)
    {
        if (status == (int)swAddMateError_e.swAddMateError_OverDefinedAssembly || (FixedOrNone(a.Comp) && FixedOrNone(b.Comp)))
            return "Nothing can move to satisfy it: both sides are fixed or already constrained. Float one (asm_component_set fixed=false) " +
                   "or delete a conflicting mate (asm_mate_list).";
        return typeName switch
        {
            "concentric" => "Concentric needs round geometry: cylindrical or conical faces, circular edges or axes, e.g. {component:'shaft-1', at:[r,0,0]}.",
            "tangent" => "Tangent needs a curved face (cylinder, sphere, cone) against a plane or another curved face.",
            "lock" => "Lock takes two components: a:{name:'base-1'}, b:{name:'lid-1'}.",
            _ => "Planes: {component:'part-1', name:'top'}; faces: {component:'part-1', at:[x,y,z]} in that part's mm (type edge or vertex for others). " +
                 "The two entities must be able to take this mate type.",
        };
    }

    /// <summary>Tier B of a lock mate: the two components keep their relative position.</summary>
    private static List<string> LockIntent(SwContext ctx, MateRef a, MateRef b, Frame? fa0, Frame? fb0, List<string> checks)
    {
        if (a.Comp is null || b.Comp is null || fa0 is null || fb0 is null)
        {
            checks.Add("relative position not checked");
            return [];
        }
        var fa1 = FrameOf(ctx, a.Comp);
        var fb1 = FrameOf(ctx, b.Comp);
        var (r0, t0) = AsmMath.Relative(fa0.R, fa0.Origin, fb0.R, fb0.Origin);
        var (r1, t1) = AsmMath.Relative(fa1.R, fa1.Origin, fb1.R, fb1.Origin);
        double dt = t0.DistanceTo(t1), dr = AsmMath.MaxDiff(r0, r1);
        if (dt > PosTol || dr > 1e-7)
            return [$"the relative position of {a.Comp.Name2} and {b.Comp.Name2} changed ({Units.Mm(dt)} mm)"];
        checks.Add($"{a.Comp.Name2} and {b.Comp.Name2} keep their relative position");
        return [];
    }

    /// <summary>Deletes the mates a failed call created and moves the components back to where they were.</summary>
    private static string RollbackMate(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, List<MateFeat> created, Dictionary<string, double[]> rawBefore)
    {
        d.ClearSelection2(true);
        foreach (var m in created) Safe(() => m.Feature.Select2(true, 0), false);
        Safe(() => d.Extension.DeleteSelection2(0), false);
        d.ClearSelection2(true);
        // Re-solve without the deleted mate (clears the error flags it caused on the others), then restore the exact placements.
        Safe(() => d.EditRebuild3(), false);
        var left = Mates(d).Where(m => created.Any(c => Eq(c.Name, m.Name))).Select(m => m.Name).ToList();
        var back = RestoreMoved(ctx, asm, rawBefore);
        d.GraphicsRedraw2();
        if (left.Count > 0) return $"Rollback incomplete: {string.Join(", ", left)} could not be deleted (asm_mate_list shows it).";
        return $"Rolled back: deleted {string.Join(", ", created.Select(m => m.Name))}" +
               (back.Count > 0 ? $" and moved {string.Join(", ", back)} back." : ".");
    }

    // ================================================================== asm_mate_list

    public Task<AsmMateListResult> MateListAsync(string? doc, int limit = 100, int offset = 0, CancellationToken ct = default) =>
        session.RunAsync("asm_mate_list", ctx =>
        {
            var asm = ctx.Assembly();
            var d = (IModelDoc2)asm;
            var mates = Mates(d);
            int skip = Math.Max(0, offset), take = Math.Clamp(limit, 1, 500);
            var rows = mates.Skip(skip).Take(take).Select(MateRow).ToList();
            return new AsmMateListResult
            {
                Doc = ctx.Title(d),
                Mates = rows,
                Total = mates.Count,
                InError = mates.Select(MateError).Count(e => e.Error != 0 && !e.Warning),
                Truncated = skip + rows.Count < mates.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    // ================================================================== asm_component_list

    public Task<AsmComponentListResult> ComponentListAsync(string? doc, bool allLevels = false, int limit = 100, int offset = 0,
        CancellationToken ct = default) =>
        session.RunAsync("asm_component_list", ctx =>
        {
            var asm = ctx.Assembly();
            var d = (IModelDoc2)asm;
            var comps = asm.Components(!allLevels);
            var counts = MateCounts(d);
            int skip = Math.Max(0, offset), take = Math.Clamp(limit, 1, 500);
            var page = comps.Skip(skip).Take(take).Select(c => Info(ctx, c, counts)).ToList();
            return new AsmComponentListResult
            {
                Doc = ctx.Title(d),
                Components = page,
                Total = comps.Count,
                Truncated = skip + page.Count < comps.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    // ================================================================== asm_component_set

    public Task<AsmComponentSetResult> ComponentSetAsync(AsmComponentSetRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Component))
            throw SwException.InvalidArgument("'component' is required, e.g. \"bracket-1\" (asm_component_list shows the names).");
        if (q.Fixed is null && q.Suppressed is null && q.Resolved is null && q.Visible is null && Clean(q.Configuration) is null)
            throw SwException.InvalidArgument("Nothing to set.", "Pass fixed, suppressed, resolved, visible and/or configuration.");
        if (q.Suppressed == true && q.Resolved == true)
            throw SwException.InvalidArgument("suppressed=true and resolved=true contradict each other.");
        return session.RunAsync("asm_component_set", ctx => ComponentSet(ctx, q), RunOptions.Write(q.Doc) with { Timeout = Long(90) }, ct);
    }

    private static AsmComponentSetResult ComponentSet(SwContext ctx, AsmComponentSetRequest q)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        var c = EntityResolver.Component(asm, q.Component!);
        string name = c.Name2;
        var previous = State(c);
        string? config = Clean(q.Configuration);
        if (config is not null) config = MatchComponentConfig(ctx, c, config);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var changed = new List<string>();
        var undo = new List<(string What, Action Revert)>();
        try
        {
            int supp0 = c.GetSuppression2();
            if ((q.Suppressed == false || q.Resolved == true) && !IsResolved(supp0))
            {
                SetSuppression(d, c, (int)swComponentSuppressionState_e.swComponentFullyResolved);
                undo.Add(("suppression", () => SetSuppression(d, c, supp0)));
                changed.Add($"{AsmParse.SuppressionName(supp0)} → resolved");
            }
            if (config is not null)
            {
                string? cur = c.ReferencedConfiguration;
                if (!Eq(cur, config))
                {
                    c.ReferencedConfiguration = config;
                    d.EditRebuild3();
                    string? now = c.ReferencedConfiguration;
                    if (!Eq(now, config))
                        throw new SwException(ErrorCodes.VerifyFailed, $"'{name}' still uses configuration '{now}' (asked for '{config}').");
                    undo.Add(("configuration", () =>
                    {
                        c.ReferencedConfiguration = cur;
                        d.EditRebuild3();
                    }));
                    changed.Add($"configuration '{cur}' → '{now}'");
                }
            }
            if (q.Fixed is bool fix && c.IsFixed() != fix)
            {
                SetFixed(d, asm, c, fix);
                undo.Add(("fixed", () => SetFixed(d, asm, c, !fix)));
                changed.Add(fix ? "fixed" : "floating");
            }
            if (q.Visible is bool vis && IsVisible(c) != vis)
            {
                SetVisible(d, c, vis);
                undo.Add(("visible", () => SetVisible(d, c, !vis)));
                changed.Add(vis ? "shown" : "hidden");
            }
            int supp1 = c.GetSuppression2();
            if (q.Resolved == false && IsResolved(supp1))
            {
                SetSuppression(d, c, (int)swComponentSuppressionState_e.swComponentLightweight);
                undo.Add(("lightweight", () => SetSuppression(d, c, supp1)));
                changed.Add("resolved → lightweight");
            }
            int supp2 = c.GetSuppression2();
            if (q.Suppressed == true && supp2 != (int)swComponentSuppressionState_e.swComponentSuppressed)
            {
                SetSuppression(d, c, (int)swComponentSuppressionState_e.swComponentSuppressed);
                undo.Add(("suppressed", () => SetSuppression(d, c, supp2)));
                changed.Add($"{AsmParse.SuppressionName(supp2)} → suppressed");
            }
        }
        catch (Exception ex) when (undo.Count > 0 && (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex))))
        {
            var reverted = new List<string>();
            foreach (var (what, revert) in Enumerable.Reverse(undo))
            {
                try
                {
                    revert();
                    reverted.Add(what);
                }
                catch (Exception e) when (e is SwException || (e is COMException ce && !ComArgs.IsDisconnect(ce)))
                {
                    /* reported below as not reverted */
                }
            }
            var sw = ex as SwException;
            throw new SwException(sw?.Code ?? ErrorCodes.Internal,
                (sw is null ? $"{ex.GetType().Name}: " : "") + ex.Message +
                $" The earlier changes were {(reverted.Count == undo.Count ? "reverted" : $"partly reverted ({string.Join(", ", reverted)})")}.",
                sw?.Hint, ex);
        }
        d.GraphicsRedraw2();
        return new AsmComponentSetResult
        {
            Doc = ctx.Title(d),
            Component = Info(ctx, c, MateCounts(d)),
            Previous = previous,
            Changed = changed.Count > 0 ? changed : ["nothing: it was already as requested"],
        };
    }

    // ================================================================== asm_component_move

    public Task<AsmMoveResult> MoveAsync(AsmMoveRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Component))
            throw SwException.InvalidArgument("'component' is required, e.g. \"bracket-1\" (asm_component_list shows the names).");
        Vec3? translate = q.TranslateMm is null ? null : Units.PointM(q.TranslateMm, "translateMm");
        var rot = RotationOf(q.Rotation, "rotation",
            q.Absolute ? "With absolute=true the rotation is the new orientation about the component origin; pointMm only applies to relative moves." : null);
        if (translate is null && rot is null)
            throw SwException.InvalidArgument("Pass translateMm and/or rotation.", "E.g. translateMm:[10,0,0] moves the component 10 mm along +x.");
        return session.RunAsync("asm_component_move", ctx => Move(ctx, q, translate, rot), RunOptions.Write(q.Doc), ct);
    }

    private static AsmMoveResult Move(SwContext ctx, AsmMoveRequest q, Vec3? translate, RotSpec? rot)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        var c = EntityResolver.Component(asm, q.Component!);
        string name = c.Name2;
        if (ParentOf(c) is IComponent2 parent)
            throw SwException.InvalidArgument($"'{name}' is inside subassembly '{parent.Name2}', which moves as one.", $"Move '{parent.Name2}' instead.");
        if (c.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
            throw SwException.InvalidArgument($"'{name}' is suppressed.", $"Unsuppress it first: asm_component_set {{component:'{name}', suppressed:false}}.");
        if (c.IsFixed())
            throw SwException.InvalidArgument($"'{name}' is fixed, so it can't be moved.",
                $"Float it first with asm_component_set {{component:'{name}', fixed:false}} (only if it should move).");

        var f0 = FrameOf(ctx, c);
        var (r1, t1) = AsmMath.Move(f0.R, f0.Origin, translate, rot?.R, rot?.Point, q.Absolute);
        var (x, y, z) = AsmMath.Axes(r1);
        var checks = new List<string>();
        Frame got;
        try
        {
            got = Place(ctx, c, new Frame(t1, x, y, z, []), checks, allowSolve: false);
        }
        catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
        {
            string back = f0.Raw.Length >= 13 && ctx.Math.AsmTransform(f0.Raw) is MathTransform xf0 && TrySet(c, xf0, f0.Raw)
                ? "It was put back where it was." : "It could not be put back where it was (asm_component_list shows where it is).";
            var sw = ex as SwException;
            throw new SwException(sw?.Code ?? ErrorCodes.Internal,
                $"asm_component_move: {(sw is null ? $"{ex.GetType().Name}: " : "")}{ex.Message} {back}", sw?.Hint, ex);
        }
        d.GraphicsRedraw2();
        var mates = MateNamesOf(d, name);
        if (mates.Count > 0)
            ctx.Warn($"'{name}' has {mates.Count} mate(s) ({string.Join(", ", mates.Take(5))}{(mates.Count > 5 ? ", …" : "")}): the next rebuild " +
                     "moves it back to satisfy them. Suppress or delete those mates to keep this position.");
        var counts = MateCounts(d);
        return new AsmMoveResult
        {
            Doc = ctx.Title(d),
            Component = Info(ctx, c, counts),
            PreviousOriginMm = Units.Mm(f0.Origin),
            PreviousRotation = AsmMath.Rows(f0.R),
            TranslationMm = Units.Mm(got.Origin - f0.Origin),
            RotatedDeg = Units.Deg(AsmMath.AngleOf(AsmMath.Multiply(got.R, AsmMath.Transpose(f0.R)))),
            Verified =
            [
                .. checks,
                $"origin read back at [{Pt(got.Origin)}] mm (within {Units.Mm(PosTol)} mm)",
                $"axes read back: x → [{Dir(got.X)}], y → [{Dir(got.Y)}], z → [{Dir(got.Z)}]",
            ],
        };
    }

    // ================================================================== asm_interference

    public Task<AsmInterferenceResult> InterferenceAsync(string? doc, IReadOnlyList<string>? components = null, bool includeCoincident = false,
        CancellationToken ct = default)
    {
        if (components is { Count: > 100 }) throw SwException.InvalidArgument($"At most 100 components (got {components.Count}).");
        if (components is not null && components.Any(string.IsNullOrWhiteSpace))
            throw SwException.InvalidArgument("'components' contains an empty name.");
        return session.RunAsync("asm_interference", ctx => Interference(ctx, components, includeCoincident),
            RunOptions.Read(doc) with { Timeout = Long(300) }, ct);
    }

    private static AsmInterferenceResult Interference(SwContext ctx, IReadOnlyList<string>? components, bool includeCoincident)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        List<string>? among = components is { Count: > 0 }
            ? components.Select(n => EntityResolver.Component(asm, n).Name2).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : null;
        var light = asm.Components(false)
            .Where(c => Safe(() => c.GetSuppression2(), 2) is (int)swComponentSuppressionState_e.swComponentLightweight or
                (int)swComponentSuppressionState_e.swComponentFullyLightweight)
            .Select(c => c.Name2).ToList();
        if (light.Count > 0)
            ctx.Warn($"{light.Count} lightweight component(s) ({string.Join(", ", light.Take(5))}) may be skipped; resolve them with asm_component_set resolved=true.");

        var idm = asm.InterferenceDetectionManager
                  ?? throw new SwException(ErrorCodes.NotSupported, "SOLIDWORKS returned no interference detection manager for this assembly.");
        var rows = new List<AsmInterference>();
        var checks = new List<string>();
        bool oldCoincident = idm.TreatCoincidenceAsInterference;
        bool oldSub = idm.TreatSubAssembliesAsComponents;
        try
        {
            // VERIFIED recipe: TreatCoincidenceAsInterference, TreatSubAssembliesAsComponents, GetInterferences, Done.
            idm.TreatCoincidenceAsInterference = includeCoincident;
            idm.TreatSubAssembliesAsComponents = true;
            // VERIFIED live: GetInterferences still answers with the options the manager had when it was obtained;
            // GetInterferenceCount recomputes with the new ones (touching cubes: 0 interferences, then count 1).
            int expected = idm.GetInterferenceCount();
            var found = idm.AsmInterferences();
            if (found.Count != expected)
                ctx.Warn($"SOLIDWORKS counted {expected} interference(s) but listed {found.Count}.");
            int bodiesChecked = 0;
            foreach (var i in found)
            {
                var names = i.AsmInterferingComponents().Select(c => c.Name2).ToList();
                if (among is not null && !AsmMath.Involves(names, among)) continue;
                double v = i.Volume;
                double[]? center = null;
                try
                {
                    if (i.AsmInterferenceBody() is IBody2 body)
                    {
                        var box = body.Box();
                        if (box.Length == 6) center = Units.Mm(new Vec3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2));
                        var mp = body.MassOfBody();
                        if (mp.Length >= 4 && v > 0)
                        {
                            bodiesChecked++;
                            if (Math.Abs(mp[3] - v) > 1e-6 * v + 1e-15)
                                ctx.Warn($"The overlap body of {string.Join(" / ", names)} measures {Units.Mm3(mp[3])} mm³, SOLIDWORKS reports {Units.Mm3(v)} mm³.");
                        }
                    }
                }
                catch (COMException e) when (!ComArgs.IsDisconnect(e))
                {
                    /* the center is optional */
                }
                rows.Add(new AsmInterference { Components = names, VolumeMm3 = Units.Mm3(v), CenterMm = center });
            }
            if (bodiesChecked > 0) checks.Add($"{bodiesChecked} overlap volume(s) cross-checked against the overlap bodies");
        }
        finally
        {
            try
            {
                idm.TreatCoincidenceAsInterference = oldCoincident;
                idm.TreatSubAssembliesAsComponents = oldSub;
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                ctx.Warn("The interference detection options could not be restored.");
            }
            idm.Done();
        }
        rows = rows.OrderByDescending(r => r.VolumeMm3).ToList();
        return new AsmInterferenceResult
        {
            Doc = ctx.Title(d),
            Count = rows.Count,
            TotalVolumeMm3 = Math.Round(rows.Sum(r => r.VolumeMm3), 3),
            Interferences = rows,
            Among = among,
            IncludeCoincident = includeCoincident,
            Checks = checks.Count > 0 ? checks : null,
        };
    }

    // ================================================================== asm_replace

    public Task<AsmReplaceResult> ReplaceAsync(AsmReplaceRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Component))
            throw SwException.InvalidArgument("'component' is required, e.g. \"bracket-1\" (asm_component_list shows the names).");
        if (string.IsNullOrWhiteSpace(q.Path))
            throw SwException.InvalidArgument("'path' is required: the saved .sldprt/.sldasm that replaces the component.");
        return session.RunAsync("asm_replace", ctx => Replace(ctx, q, Clean(q.Configuration)), RunOptions.Write(q.Doc) with { Timeout = Long(180) }, ct);
    }

    private static AsmReplaceResult Replace(SwContext ctx, AsmReplaceRequest q, string? config)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        var c = EntityResolver.Component(asm, q.Component!);
        string oldName = c.Name2;
        if (ParentOf(c) is IComponent2 parent)
            throw SwException.InvalidArgument($"'{oldName}' is inside subassembly '{parent.Name2}'.", "Replace it inside that subassembly, or replace the subassembly.");
        string oldPath = CompPath(c);
        if (oldPath.Length == 0) throw SwException.InvalidArgument($"'{oldName}' has no file (a virtual component?), so it can't be replaced.");
        var file = ResolveFile(ctx, q.Path!, "path");
        if (SamePath(file.Full, oldPath)) throw SwException.InvalidArgument($"'{oldName}' already uses '{file.Full}'.");
        if (SamePath(file.Full, DocumentRegistry.SafePath(d))) throw SwException.InvalidArgument("An assembly can't contain itself.");
        config = MatchConfig(ctx, file, config);

        var beforeRows = asm.Components(true).Select(x => (Name: x.Name2, Path: CompPath(x))).ToList();
        int oldCount = beforeRows.Count(x => SamePath(x.Path, oldPath));
        var matesBefore = Mates(d).Select(m => m.Name).ToList();
        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        bool ok;
        var opened = EnsureLoaded(ctx, d, file, prefs);
        try
        {
            d.ClearSelection2(true);
            if (!SelectComponent(d, c, false, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{oldName}' to replace it.");
            int choice = config is null
                ? (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_MatchName
                : (int)swReplaceComponentsConfiguration_e.swReplaceComponentsConfiguration_ManuallySelect;
            ok = asm.ReplaceComponents2(file.Full, config ?? "", q.AllInstances, choice, q.ReattachMates);
        }
        finally
        {
            d.ClearSelection2(true);
            CloseLoaded(ctx, d, opened);
        }
        d.EditRebuild3();

        var after = asm.Components(true);
        var afterRows = after.Select(x => (Name: x.Name2, Path: CompPath(x), Comp: x)).ToList();
        int oldLeft = afterRows.Count(x => SamePath(x.Path, oldPath));
        int expected = q.AllInstances ? oldCount : 1;
        var fresh = afterRows.Where(x => SamePath(x.Path, file.Full) && !beforeRows.Any(b => Eq(b.Name, x.Name) && SamePath(b.Path, x.Path))).ToList();
        if (oldLeft != oldCount - expected || fresh.Count < expected)
            throw new SwException(ok ? ErrorCodes.VerifyFailed : ErrorCodes.FeatureFailed,
                $"asm_replace: '{oldName}' was not replaced (ReplaceComponents2 returned {ok}): {oldLeft} component(s) still use " +
                $"'{System.IO.Path.GetFileName(oldPath)}' and {fresh.Count} new one(s) use '{System.IO.Path.GetFileName(file.Full)}'.",
                "Check that the new file opens (doc_open) and, with 'configuration', that the configuration exists.");

        var oldNames = beforeRows.Where(x => SamePath(x.Path, oldPath)).Select(x => x.Name).ToList();
        var replaced = new List<AsmReplaced>();
        if (q.AllInstances && oldNames.Count == fresh.Count)
            for (int i = 0; i < fresh.Count; i++) replaced.Add(new AsmReplaced { From = oldNames[i], To = fresh[i].Name });
        else
            replaced.Add(new AsmReplaced { From = oldName, To = fresh[0].Name });

        var checks = new List<string>
        {
            $"{expected} component(s) now reference {System.IO.Path.GetFileName(file.Full)}; {oldLeft} still reference {System.IO.Path.GetFileName(oldPath)}",
        };
        var mates = Mates(d);
        var lost = matesBefore.Where(n => !mates.Any(m => Eq(m.Name, n))).ToList();
        if (lost.Count > 0) ctx.Warn($"The replacement removed {lost.Count} mate(s): {string.Join(", ", lost.Take(10))}.");
        var inError = mates.Where(m => MateError(m).Error != 0).Select(MateRow).ToList();
        checks.Add(inError.Count == 0 ? $"{mates.Count} mate(s), none in error" : $"{inError.Count} of {mates.Count} mate(s) in error or warning");
        ctx.App.GetRunningCommandInfo(out _, out string pm, out _);
        if (!string.IsNullOrWhiteSpace(pm))
            ctx.Warn($"SOLIDWORKS left the '{pm}' PropertyManager open after the replacement; ask the user to close it (the next change is refused until then).");
        var counts = MateCounts(d);
        return new AsmReplaceResult
        {
            Doc = ctx.Title(d),
            Replaced = replaced,
            OldPath = oldPath,
            NewPath = file.Full,
            Components = fresh.Take(20).Select(x => Info(ctx, x.Comp, counts)).ToList(),
            MatesInError = inError,
            Verified = checks,
        };
    }

    // ================================================================== components: frames and info

    /// <summary>
    /// A component's placement read the way SOLIDWORKS applies it: the origin (ArrayData 9..11, VERIFIED) and the images of
    /// the part's x, y and z axes (MathVector.MultiplyTransform). <see cref="Raw"/> is the ArrayData (for exact restore).
    /// </summary>
    internal sealed record Frame(Vec3 Origin, Vec3 X, Vec3 Y, Vec3 Z, double[] Raw)
    {
        public double[] R => AsmMath.FromAxes(X, Y, Z);
    }

    private static Frame FrameOf(SwContext ctx, IComponent2 c)
    {
        MathTransform? xf = c.Transform2;
        if (xf is null) return new Frame(Vec3.Zero, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, []);
        var raw = ComArgs.Doubles((object?)xf.ArrayData);
        var mu = ctx.Math;
        var origin = raw.Length >= 12 ? new Vec3(raw[9], raw[10], raw[11]) : mu.AsmMapPoint(xf, Vec3.Zero);
        return new Frame(origin, mu.AsmMapVector(xf, Vec3.UnitX).Normalized(), mu.AsmMapVector(xf, Vec3.UnitY).Normalized(),
            mu.AsmMapVector(xf, Vec3.UnitZ).Normalized(), raw);
    }

    private static bool Matches(Frame got, Frame want) =>
        got.Origin.DistanceTo(want.Origin) <= PosTol && got.X.DistanceTo(want.X) <= AxisTol && got.Y.DistanceTo(want.Y) <= AxisTol &&
        got.Z.DistanceTo(want.Z) <= AxisTol;

    /// <summary>
    /// Places a component at <paramref name="want"/> through IComponent2.Transform2 and reads it back with
    /// MathPoint/MathVector.MultiplyTransform, so the check doesn't depend on the ArrayData layout. Candidates, in order:
    /// CreateTransform with the row layout (VERIFIED without rotation; the convention Geometry/Xform assumes),
    /// ComposeTransform from the axes, and (rotations only) CreateTransform with the column layout. The first that reads
    /// back wins and is named in <paramref name="checks"/>: with a rotation that names the convention SOLIDWORKS uses.
    /// <paramref name="allowSolve"/> (a new component without mates) adds SetTransformAndSolve2 as the last resort; moves
    /// never solve, so no other component moves.
    /// </summary>
    private static Frame Place(SwContext ctx, IComponent2 c, Frame want, List<string> checks, bool allowSolve)
    {
        var mu = ctx.Math;
        bool rotated = !AsmMath.IsIdentity(want.R, 1e-12);
        var candidates = new List<(string How, MathTransform Xf)>();
        var attempts = new List<string>();
        if (mu.AsmTransform(AsmMath.RowLayout(want.X, want.Y, want.Z, want.Origin)) is MathTransform rows)
            candidates.Add(("CreateTransform (row layout)", rows));
        else attempts.Add("CreateTransform (row layout): refused");
        if (mu.ComposeTransform(mu.AsmVector(want.X), mu.AsmVector(want.Y), mu.AsmVector(want.Z), mu.AsmVector(want.Origin), 1.0) is MathTransform composed)
            candidates.Add(("ComposeTransform", composed));
        else attempts.Add("ComposeTransform: refused");
        if (rotated)
        {
            if (mu.AsmTransform(AsmMath.ColumnLayout(want.X, want.Y, want.Z, want.Origin)) is MathTransform cols)
                candidates.Add(("CreateTransform (column layout)", cols));
            else attempts.Add("CreateTransform (column layout): refused");
        }
        if (candidates.Count == 0) throw new SwException(ErrorCodes.Internal, "SOLIDWORKS could not build the placement transform.");

        var passes = allowSolve ? new[] { false, true } : [false];
        foreach (bool solve in passes)
        {
            foreach (var (how, xf) in candidates)
            {
                string via = solve ? "SetTransformAndSolve2" : "Transform2";
                if (solve)
                {
                    if (!c.SetTransformAndSolve2(xf))
                    {
                        attempts.Add($"{how} via {via}: refused");
                        continue;
                    }
                }
                else
                {
                    c.Transform2 = xf;
                }
                var got = FrameOf(ctx, c);
                if (Matches(got, want))
                {
                    checks.Add($"placed with {how} via {via}{(rotated ? " (rotated: this layout is the one SOLIDWORKS applies)" : "")}");
                    if (attempts.Count > 0)
                        ctx.Warn($"'{c.Name2}' was placed with {how} via {via} after: {string.Join("; ", attempts)}.");
                    return got;
                }
                attempts.Add($"{how} via {via} gave origin [{Pt(got.Origin)}], x [{Dir(got.X)}], y [{Dir(got.Y)}]");
            }
        }
        throw new SwException(ErrorCodes.VerifyFailed,
            $"'{c.Name2}' could not be placed with its origin at [{Pt(want.Origin)}] mm, x axis [{Dir(want.X)}], y axis [{Dir(want.Y)}]: " +
            string.Join("; ", attempts.Take(6)) + ".",
            "Mates or a fixed state may hold it (asm_mate_list, asm_component_list).");
    }

    private static bool TrySet(IComponent2 c, MathTransform xf, double[] raw)
    {
        c.Transform2 = xf;
        return !AsmMath.Changed(raw, Safe(() => c.AsmXformData(), Array.Empty<double>()));
    }

    /// <summary>ArrayData of every top-level component, by name (for moved-component detection and exact restore).</summary>
    private static Dictionary<string, double[]> RawTransforms(IAssemblyDoc asm)
    {
        var map = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in asm.Components(true)) map[c.Name2] = Safe(() => c.AsmXformData(), Array.Empty<double>());
        return map;
    }

    /// <summary>Puts every top-level component that moved since <paramref name="rawBefore"/> back (exact ArrayData round trip).</summary>
    private static List<string> RestoreMoved(SwContext ctx, IAssemblyDoc asm, Dictionary<string, double[]> rawBefore)
    {
        var back = new List<string>();
        foreach (var c in asm.Components(true))
        {
            if (!rawBefore.TryGetValue(c.Name2, out var r0) || r0.Length < 13) continue;
            if (!AsmMath.Changed(r0, Safe(() => c.AsmXformData(), r0))) continue;
            if (ctx.Math.AsmTransform(r0) is MathTransform xf && TrySet(c, xf, r0)) back.Add(c.Name2);
            else ctx.Warn($"'{c.Name2}' could not be moved back to where it was.");
        }
        return back;
    }

    private static AsmComponentInfo Info(SwContext ctx, IComponent2 c, IReadOnlyDictionary<string, int>? mates)
    {
        string name = c.Name2;
        int supp = Safe(() => c.GetSuppression2(), -1);
        Frame? f = Safe<Frame?>(() => FrameOf(ctx, c), null);
        double[] box = supp == (int)swComponentSuppressionState_e.swComponentSuppressed
            ? Array.Empty<double>() : Safe(() => c.AsmCompBox(), Array.Empty<double>());
        int kids = Safe(() => c.AsmChildren().Count, 0);
        string? parent = ParentOf(c)?.Name2;
        string path = CompPath(c);
        string? cfg = Safe<string?>(() => c.ReferencedConfiguration, null);
        int? cs = Safe<int?>(() => c.GetConstrainedStatus(), null);
        return new AsmComponentInfo
        {
            Name = name,
            Path = path.Length == 0 ? null : path,
            Configuration = string.IsNullOrEmpty(cfg) ? null : cfg,
            Fixed = Safe(() => c.IsFixed(), false),
            Suppression = AsmParse.SuppressionName(supp),
            Visible = IsVisible(c),
            OriginMm = f is null ? null : Units.Mm(f.Origin),
            Rotation = f is null ? null : AsmMath.Rows(f.R),
            Box = BoxMm.FromMeters(box.Length == 6 ? box : null),
            Constrained = cs is int s ? AsmParse.ConstrainedName(s) : null,
            Mates = mates is null ? null : mates.TryGetValue(name, out int n) ? n : 0,
            Children = kids > 0 ? kids : null,
            Parent = parent,
        };
    }

    private static AsmComponentState State(IComponent2 c) => new()
    {
        Fixed = c.IsFixed(),
        Suppression = AsmParse.SuppressionName(c.GetSuppression2()),
        Visible = IsVisible(c),
        Configuration = Safe<string?>(() => c.ReferencedConfiguration, null),
    };

    private static bool IsVisible(IComponent2 c) => Safe(() => c.Visible, 1) == (int)swComponentVisibilityState_e.swComponentVisible;

    private static bool IsRootComponent(IComponent2 c) => Safe(() => c.IsRoot(), false);

    /// <summary>The subassembly a component sits in, or null for a top-level component.</summary>
    private static IComponent2? ParentOf(IComponent2 c) =>
        Safe<IComponent2?>(() => c.GetParent(), null) is IComponent2 p && !IsRootComponent(p) ? p : null;

    /// <summary>True for no component (an assembly-level entity) or a fixed one: that side can't move.</summary>
    private static bool FixedOrNone(IComponent2? c) => c is null || IsFixedComponent(c);

    private static bool IsFixedComponent(IComponent2 c) => Safe(() => c.IsFixed(), false);

    private static bool IsResolved(int suppression) =>
        suppression is (int)swComponentSuppressionState_e.swComponentFullyResolved or (int)swComponentSuppressionState_e.swComponentResolved;

    private static string CompPath(IComponent2 c)
    {
        string p = Safe(() => c.GetPathName(), "") ?? "";
        return p.Length == 0 ? "" : FullPath(p);
    }

    // ------------------------------------------------------------------ setting state

    /// <summary>Selects a component (Select4 with a mark; the VERIFIED SelectByID2 "COMPONENT" form as the fallback) and reads it back.</summary>
    private static bool SelectComponent(IModelDoc2 d, IComponent2 c, bool append, int mark)
    {
        var sm = d.Sel();
        int before = append ? sm.GetSelectedObjectCount2(-1) : 0;
        var data = sm.CreateSelectData();
        data.Mark = mark;
        if (c.Select4(append, data, false) && sm.GetSelectedObjectCount2(-1) == before + 1) return true;
        if (!append) d.ClearSelection2(true);
        string id = Safe(() => c.GetSelectByIDString(), "") ?? "";
        return id.Length > 0 && d.Extension.SelectByID2(id, "COMPONENT", 0, 0, 0, append, mark, null, 0) &&
               sm.GetSelectedObjectCount2(-1) == before + 1;
    }

    /// <summary>Fix or float: select, then IAssemblyDoc.FixComponent / UnfixComponent (VERIFIED), read back with IsFixed.</summary>
    private static void SetFixed(IModelDoc2 d, IAssemblyDoc asm, IComponent2 c, bool fix)
    {
        d.ClearSelection2(true);
        try
        {
            if (!SelectComponent(d, c, false, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{c.Name2}' to {(fix ? "fix" : "float")} it.");
            if (fix) asm.FixComponent();
            else asm.UnfixComponent();
        }
        finally
        {
            d.ClearSelection2(true);
        }
        if (c.IsFixed() != fix)
            throw new SwException(ErrorCodes.VerifyFailed, $"'{c.Name2}' is still {(fix ? "floating" : "fixed")} after {(fix ? "FixComponent" : "UnfixComponent")}.");
    }

    /// <summary>IComponent2.SetSuppression2, read back; select + EditSuppress2/EditUnsuppress2 as the fallback.</summary>
    private static void SetSuppression(IModelDoc2 d, IComponent2 c, int state)
    {
        int rc = c.SetSuppression2(state);
        if (SuppressionIs(c.GetSuppression2(), state)) return;
        if (state is (int)swComponentSuppressionState_e.swComponentSuppressed or (int)swComponentSuppressionState_e.swComponentFullyResolved)
        {
            d.ClearSelection2(true);
            try
            {
                if (SelectComponent(d, c, false, 0))
                {
                    if (state == (int)swComponentSuppressionState_e.swComponentSuppressed) d.EditSuppress2();
                    else d.EditUnsuppress2();
                }
            }
            finally
            {
                d.ClearSelection2(true);
            }
            if (SuppressionIs(c.GetSuppression2(), state)) return;
        }
        throw new SwException(ErrorCodes.VerifyFailed,
            $"'{c.Name2}' is {AsmParse.SuppressionName(c.GetSuppression2())} after asking for {AsmParse.SuppressionName(state)} (SetSuppression2 returned {rc}).",
            "A component referenced by in-context features or patterns may refuse; check asm_component_list.");
    }

    private static bool SuppressionIs(int now, int want) => want switch
    {
        (int)swComponentSuppressionState_e.swComponentFullyResolved or (int)swComponentSuppressionState_e.swComponentResolved => IsResolved(now),
        (int)swComponentSuppressionState_e.swComponentLightweight or (int)swComponentSuppressionState_e.swComponentFullyLightweight =>
            now is (int)swComponentSuppressionState_e.swComponentLightweight or (int)swComponentSuppressionState_e.swComponentFullyLightweight,
        _ => now == want,
    };

    /// <summary>IComponent2.Visible (1 visible, 0 hidden), read back; select + ShowComponent2/HideComponent2 as the fallback.</summary>
    private static void SetVisible(IModelDoc2 d, IComponent2 c, bool visible)
    {
        c.Visible = visible ? (int)swComponentVisibilityState_e.swComponentVisible : (int)swComponentVisibilityState_e.swComponentHidden;
        if (IsVisible(c) == visible) return;
        d.ClearSelection2(true);
        try
        {
            if (SelectComponent(d, c, false, 0))
            {
                if (visible) d.ShowComponent2();
                else d.HideComponent2();
            }
        }
        finally
        {
            d.ClearSelection2(true);
        }
        if (IsVisible(c) != visible)
            throw new SwException(ErrorCodes.VerifyFailed, $"'{c.Name2}' is still {(visible ? "hidden" : "visible")}.");
    }

    /// <summary>Removes a component this call inserted (rollback of asm_insert).</summary>
    private static string RemoveComponent(IModelDoc2 d, IAssemblyDoc asm, IComponent2 c, int countBefore)
    {
        string name = Safe(() => c.Name2, "the component") ?? "the component";
        d.ClearSelection2(true);
        try
        {
            if (SelectComponent(d, c, false, 0) && !d.Extension.DeleteSelection2(0)) d.EditDelete();
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            /* reported below */
        }
        finally
        {
            d.ClearSelection2(true);
        }
        return asm.GetComponentCount(true) == countBefore
            ? $"Rolled back: removed '{name}'."
            : $"Rollback incomplete: '{name}' is still in the assembly (asm_component_list shows it).";
    }

    // ------------------------------------------------------------------ files

    private sealed record FileRef(string Full, IModelDoc2? Open, swDocumentTypes_e Type);

    /// <summary>A component file: an open, saved document (handle, title or path) or a .sldprt/.sldasm on disk.</summary>
    private static FileRef ResolveFile(SwContext ctx, string spec, string arg)
    {
        string s = spec.Trim().Trim('"');
        var open = ctx.App.Documents();
        IModelDoc2? doc;
        if (Regex.IsMatch(s, @"^[Dd]\d+$"))
        {
            doc = DocLocator.Find(ctx.Documents, open, s);
        }
        else
        {
            var hits = open.Where(o =>
            {
                string p = DocumentRegistry.SafePath(o);
                return Eq(DocumentRegistry.SafeTitle(o), s) || Eq(p, s) || (p.Length > 0 && Eq(System.IO.Path.GetFileName(p), s));
            }).ToList();
            if (hits.Count > 1)
                throw new SwException(ErrorCodes.EntityAmbiguous, $"'{s}' matches {hits.Count} open documents.", "Pass the handle (doc_list) or the full path.");
            doc = hits.FirstOrDefault();
        }
        if (doc is not null)
        {
            string title = ctx.Title(doc);
            int t = doc.GetType();
            if (t != (int)swDocumentTypes_e.swDocPART && t != (int)swDocumentTypes_e.swDocASSEMBLY)
                throw SwException.InvalidArgument($"'{title}' is a {SwContext.TypeName(t)}; only parts and assemblies can be components.");
            string p = DocumentRegistry.SafePath(doc);
            if (p.Length == 0)
                throw SwException.InvalidArgument($"'{title}' has not been saved yet, so it has no file to insert.",
                    $"Save it first: doc_save {{mode:'as', path:'{SafeFileName(title)}{(t == (int)swDocumentTypes_e.swDocPART ? ".sldprt" : ".sldasm")}', doc:'{ctx.Documents.Track(doc).Handle}'}}.");
            return new FileRef(FullPath(p), doc, (swDocumentTypes_e)t);
        }
        string ext = System.IO.Path.GetExtension(s).ToLowerInvariant();
        var type = ext switch
        {
            ".sldprt" => swDocumentTypes_e.swDocPART,
            ".sldasm" => swDocumentTypes_e.swDocASSEMBLY,
            _ => throw SwException.InvalidArgument($"'{arg}' must be a .sldprt or .sldasm file{(ext.Length == 0 ? "" : $", not '{ext}'")} (got '{s}').",
                "Pass a saved part or assembly, or the handle (D…) of an open, saved document (doc_list)."),
        };
        string full = ctx.Paths.ForRead(s, arg);
        var already = open.FirstOrDefault(o => SamePath(DocumentRegistry.SafePath(o), full));
        return new FileRef(full, already, type);
    }

    private static string? MatchConfig(SwContext ctx, FileRef file, string? want)
    {
        if (want is null) return null;
        var names = file.Open is not null ? file.Open.ConfigNames() : ctx.App.ConfigNamesOf(file.Full);
        if (names.Length == 0)
        {
            ctx.Warn($"The configurations of '{System.IO.Path.GetFileName(file.Full)}' could not be read; '{want}' was passed as given.");
            return want;
        }
        return names.FirstOrDefault(n => Eq(n, want))
               ?? throw SwException.InvalidArgument($"'{System.IO.Path.GetFileName(file.Full)}' has no configuration '{want}'.",
                   $"Its configurations: {string.Join(", ", names.Take(20))}.");
    }

    private static string MatchComponentConfig(SwContext ctx, IComponent2 c, string want)
    {
        var names = c.AsmModelDoc() is IModelDoc2 md ? md.ConfigNames() : ctx.App.ConfigNamesOf(CompPath(c));
        if (names.Length == 0) return want;
        return names.FirstOrDefault(n => Eq(n, want))
               ?? throw SwException.InvalidArgument($"'{c.Name2}' ({System.IO.Path.GetFileName(CompPath(c))}) has no configuration '{want}'.",
                   $"Its configurations: {string.Join(", ", names.Take(20))}.");
    }

    /// <summary>
    /// The component file must be loaded before AddComponent5 / ReplaceComponents2 (API documentation; the VERIFIED insert
    /// had it open). A file that isn't loaded is opened silently and the assembly brought back to the front; the caller
    /// closes it again with <see cref="CloseLoaded"/> (the assembly keeps it loaded).
    /// </summary>
    private static IModelDoc2? EnsureLoaded(SwContext ctx, IModelDoc2 asmDoc, FileRef file, PreferenceScope prefs)
    {
        if (file.Open is not null) return null;
        if (ctx.App.OpenDocNamed(file.Full) is IModelDoc2 named && SamePath(DocumentRegistry.SafePath(named), file.Full)) return null;
        prefs.Integer(SystemPrefIds.RebuildOnActivation, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc);
        int errors = 0, warnings = 0;
        var doc = (object?)ctx.App.OpenDoc6(file.Full, (int)file.Type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
        if (doc is null)
            throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not load '{file.Full}': {LoadFlags.Describe(errors, warnings)}.",
                LoadFlags.Hint(errors, ctx.Session.Revision));
        Reactivate(ctx, asmDoc);
        return doc;
    }

    private static void CloseLoaded(SwContext ctx, IModelDoc2 asmDoc, IModelDoc2? opened)
    {
        if (opened is null) return;
        string title = ctx.Title(opened);
        if (title.Length > 0 && ctx.App.Documents().Count(o => Eq(ctx.Title(o), title)) == 1)
            ctx.App.CloseDoc(title);
        else
            ctx.Warn($"'{title}' was opened to load it and is still open.");
        try
        {
            Reactivate(ctx, asmDoc);
        }
        catch (SwException ex)
        {
            ctx.Warn(ex.Message);
        }
    }

    private static void Reactivate(SwContext ctx, IModelDoc2 d)
    {
        var active = ctx.ActiveDocOrNull;
        if (active is not null && DocLocator.SameDoc(active, d)) return;
        int err = 0;
        ctx.App.ActivateDoc3(ctx.Title(d), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
        var now = ctx.ActiveDocOrNull;
        if (now is null || !DocLocator.SameDoc(now, d))
            throw new SwException(ErrorCodes.VerifyFailed, $"Could not bring '{ctx.Title(d)}' back to the front (error {err}).");
    }

    // ------------------------------------------------------------------ mates

    private sealed record MateFeat(IFeature Feature, IMate2 Mate, string Name);

    /// <summary>The mates of the assembly: IMate2 sub-features of the MateGroup folder (VERIFIED type name), folders included.</summary>
    private static List<MateFeat> Mates(IModelDoc2 d)
    {
        var list = new List<MateFeat>();
        var group = Tree.Walk(d).FirstOrDefault(f => f.GetTypeName2() == "MateGroup");
        if (group is not null) CollectMates(group, list, 0);
        return list;
    }

    private static void CollectMates(IFeature parent, List<MateFeat> list, int depth)
    {
        foreach (var f in Tree.Children(parent))
        {
            if (f.Specific() is IMate2 m) list.Add(new MateFeat(f, m, f.Name));
            else if (depth < 3) CollectMates(f, list, depth + 1);
        }
    }

    private static (int Error, bool Warning) MateError(MateFeat m) => Safe(() =>
    {
        int e = m.Feature.GetErrorCode2(out bool w);
        return (e, w);
    }, (0, false));

    /// <summary>The components a mate references (assembly-level planes and the origin have none).</summary>
    private static List<string> MateComponents(IMate2 m)
    {
        var names = new List<string>();
        foreach (var e in MateEntities(m))
            if (EntityComponent(e) is IComponent2 c) names.Add(c.Name2);
        return names;
    }

    private static List<IMateEntity2> MateEntities(IMate2 m)
    {
        var list = new List<IMateEntity2>();
        int n = Safe(() => m.GetMateEntityCount(), 0);
        for (int i = 0; i < n; i++)
        {
            int k = i;
            if (Safe<IMateEntity2?>(() => m.MateEntity(k), null) is IMateEntity2 e) list.Add(e);
        }
        return list;
    }

    /// <summary>The component a mate entity belongs to (null for the assembly's own planes, axes and origin).</summary>
    private static IComponent2? EntityComponent(IMateEntity2 e) =>
        Safe<IComponent2?>(() => e.ReferenceComponent, null) is IComponent2 c && !IsRootComponent(c) ? c : null;

    private static Dictionary<string, int> MateCounts(IModelDoc2 d)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mates(d))
            foreach (var n in MateComponents(m.Mate).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[n] = counts.GetValueOrDefault(n) + 1;
        return counts;
    }

    private static List<string> MateNamesOf(IModelDoc2 d, string component) =>
        Mates(d).Where(m => MateComponents(m.Mate).Any(n => Eq(n, component))).Select(m => m.Name).ToList();

    private static AsmMateInfo MateRow(MateFeat mf)
    {
        var m = mf.Mate;
        var (err, warn) = MateError(mf);
        var entities = MateEntities(m).Select(e => new AsmMateEntity
        {
            Component = EntityComponent(e)?.Name2,
            // ReferenceType is the geometric kind (swMateEntity2ReferenceType_e); ReferenceType2 is a swSelectType_e
            // (live: 4 = swSelDATUMPLANES for a plane mate, which read as "cylinder" when taken for the other enum).
            Kind = Safe(() => e.ReferenceType, -1) is int rt and >= 0 ? AsmParse.RefTypeName(rt) : AsmParse.SelTypeName(Safe(() => e.ReferenceType2, -1)),
        }).ToList();
        var dims = Safe(() => FeatureVerifier.Dimensions(mf.Feature), new List<DimensionInfo>());
        var dim = dims.FirstOrDefault(x => x.Unit is "mm" or "deg");
        int type = Safe(() => m.Type, -1);
        return new AsmMateInfo
        {
            Name = mf.Name,
            Type = type >= 0 ? AsmParse.MateTypeName(type) : mf.Feature.GetTypeName2(),
            Alignment = Safe<int?>(() => m.Alignment, null) is int al ? AsmParse.AlignmentName(al) : null,
            Flipped = Safe(() => m.Flipped, false) ? true : null,
            Entities = entities,
            DistanceMm = dim is { Unit: "mm" } ? dim.Value : null,
            AngleDeg = dim is { Unit: "deg" } ? dim.Value : null,
            Dimension = dim?.Name,
            Error = err == 0 ? null : err,
            ErrorName = err == 0 ? null : FeatureVerifier.ErrorName(err),
            Warning = err != 0 && warn ? true : null,
            Suppressed = Safe(() => mf.Feature.IsSuppressed(), false) ? true : null,
        };
    }

    /// <summary>A resolved mate entity: what to select, its component (null at the assembly level) and its geometry in its own space.</summary>
    private sealed record MateRef(Resolved R, IComponent2? Comp, AsmGeo Local, string Label);

    private static MateRef ResolveMateRef(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, EntityRef r, string arg, bool lockMate)
    {
        if (lockMate)
        {
            // Lock joins two components: {name:'bracket-1'} or {component:'bracket-1'} (which wins over a plane name).
            var cr = r.Component is not null
                ? new EntityRef { Type = "component", Name = r.Component }
                : Copy(r, type: "component");
            var res = EntityResolver.Resolve(ctx, d, cr, arg, EntityKind.Component);
            var comp = (IComponent2)res.Obj;
            return new MateRef(res, comp, AsmGeo.Other("component"), res.Label);
        }
        // 'at' alone means a face (the usual mate entity); type edge or vertex picks those.
        var rr = r.At is not null && r.Type is null && r.Id is null && r.Where is null ? Copy(r, type: "face") : r;
        var resolved = EntityResolver.Resolve(ctx, d, rr, arg, MateKinds);
        IComponent2? owner = r.Component is not null ? EntityResolver.Component(asm, r.Component)
            : resolved.Obj is IFace2 or IEdge or IVertex ? Safe(() => ((IEntity)resolved.Obj).AsmComponentOf(), null) : null;
        if (owner is not null && IsRootComponent(owner)) owner = null;
        string label = owner is null || resolved.Label.Contains(" of ", StringComparison.Ordinal) ? resolved.Label : $"{resolved.Label} of {owner.Name2}";
        return new MateRef(resolved, owner, LocalGeo(resolved, owner), label);
    }

    private static bool SameEntity(MateRef a, MateRef b)
    {
        if (ReferenceEquals(a.R.Obj, b.R.Obj)) return true;
        if (a.R.Id is not null && a.R.Id == b.R.Id) return true;
        return a.R.Kind == b.R.Kind && a.Label == b.Label && Eq(a.Comp?.Name2, b.Comp?.Name2);
    }

    private static EntityRef Copy(EntityRef r, string? type = null) => new()
    {
        Id = r.Id, Type = type ?? r.Type, At = r.At, Name = r.Name, Where = r.Where, Expect = r.Expect, Component = r.Component, Sketch = r.Sketch,
        TolMm = r.TolMm, Strict = r.Strict,
    };

    /// <summary>The entity's geometry in its own document's coordinates (part space for component entities).</summary>
    private static AsmGeo LocalGeo(Resolved r, IComponent2? comp)
    {
        try
        {
            switch (r.Obj)
            {
                case IFace2 f:
                {
                    var g = Topology.Describe(f);
                    return g.Surface switch
                    {
                        "plane" when g.Normal is Vec3 n => new AsmGeo('p', Topology.InteriorPoint(f), n, 0, "plane"),
                        "cylinder" or "cone" when g.Axis is Vec3 ax && g.AxisOrigin is Vec3 o => new AsmGeo('a', o, ax, g.Radius ?? 0, g.Surface),
                        "sphere" when g.AxisOrigin is Vec3 c => new AsmGeo('s', c, Vec3.Zero, g.Radius ?? 0, "sphere"),
                        _ => AsmGeo.Other(g.Surface),
                    };
                }
                case IEdge e:
                {
                    var g = Topology.Describe(e);
                    return g.Curve switch
                    {
                        "line" when g.Direction is Vec3 u => new AsmGeo('l', g.Start, u, 0, "line"),
                        "circle" when g.Center is Vec3 c && g.Axis is Vec3 ax => new AsmGeo('a', c, ax, g.Radius ?? 0, "circle"),
                        _ => AsmGeo.Other(g.Curve),
                    };
                }
                case IVertex v:
                    return new AsmGeo('v', Vec3.From(v.Point()), Vec3.Zero, 0, "point");
                case IFeature feat:
                {
                    if (r.Kind == EntityKind.Origin) return new AsmGeo('v', Vec3.Zero, Vec3.Zero, 0, "origin");
                    // A component's plane/axis/point is read in the component's own document (part space, like its bodies).
                    IFeature src = feat;
                    if (comp is not null && comp.AsmModelDoc() is IModelDoc2 md && EntityResolver.FeatureByName(md, feat.Name) is IFeature own) src = own;
                    switch (src.Specific())
                    {
                        case IRefPlane rp:
                        {
                            var cs = rp.RefPlaneCorners();
                            if (cs.Count >= 3 && (cs[1] - cs[0]).Cross(cs[2] - cs[0]) is var nrm && nrm.Length > 1e-12)
                                return new AsmGeo('p', cs[0], nrm.Normalized(), 0, "plane");
                            var t = rp.RefPlaneXform();
                            if (t.Length >= 12) return new AsmGeo('p', new Vec3(t[9], t[10], t[11]), new Vec3(t[6], t[7], t[8]).Normalized(), 0, "plane");
                            break;
                        }
                        case IRefAxis ra:
                        {
                            var p = ra.RefAxisEnds();
                            if (p.Length >= 6 && Vec3.From(p, 3) - Vec3.From(p) is var u && u.Length > 1e-12)
                                return new AsmGeo('l', Vec3.From(p), u.Normalized(), 0, "axis");
                            break;
                        }
                        case IRefPoint rpt:
                        {
                            var xyz = rpt.RefPointXyz();
                            if (xyz.Length >= 3) return new AsmGeo('v', Vec3.From(xyz), Vec3.Zero, 0, "point");
                            break;
                        }
                    }
                    return AsmGeo.Other(EntityResolver.KindName(r.Kind));
                }
            }
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            /* geometry unknown: the intent check reports it as not checked */
        }
        return AsmGeo.Other(EntityResolver.KindName(r.Kind));
    }

    /// <summary>The entity's geometry in assembly space now (the component transform applied with SOLIDWORKS' own math).</summary>
    private static AsmGeo ToAsm(SwContext ctx, MateRef m)
    {
        if (m.Comp is null || m.Local.Kind == 'o') return m.Local;
        MathTransform? xf = m.Comp.Transform2;
        if (xf is null) return m.Local;
        var mu = ctx.Math;
        return m.Local with
        {
            P = mu.AsmMapPoint(xf, m.Local.P),
            D = m.Local.D.Length > 0 ? mu.AsmMapVector(xf, m.Local.D).Normalized() : m.Local.D,
        };
    }

    /// <summary>
    /// Selects one mate entity (on mark 1 unless told otherwise). Component planes, axes and points use the VERIFIED SelectByID2 form
    /// ("Front Plane@part-1@Assem1"); everything else (and the fallback) the resolved entity's own Select (Select2/Select4).
    /// </summary>
    private static void SelectForMate(SwContext ctx, IModelDoc2 d, MateRef m, bool append, string arg, int mark = MateMark)
    {
        var sm = d.Sel();
        int before = sm.GetSelectedObjectCount2(-1);
        bool Added() => sm.GetSelectedObjectCount2(-1) == before + 1;
        if (m.R.Obj is IFeature f && m.Comp is not null && m.R.Kind is EntityKind.Plane or EntityKind.Axis or EntityKind.Point)
        {
            string type = m.R.Kind switch { EntityKind.Plane => "PLANE", EntityKind.Axis => "AXIS", _ => "DATUMPOINT" };
            foreach (var id in SelectIds(ctx, d, f.Name, m.Comp))
                if (d.Extension.SelectByID2(id, type, 0, 0, 0, append, mark, null, 0) && Added()) return;
        }
        if (m.R.Select(d, append, mark) && Added()) return;
        throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{arg}' ({m.Label}) for the mate.",
            "Use a plane ({component:'part-1', name:'top'}) or a face picked in that part's coordinates ({component:'part-1', at:[x,y,z]}).");
    }

    /// <summary>SelectByID2 names of a component's feature: "Front Plane@part-1@Assem1" (VERIFIED form), then GetSelectByIDString's form.</summary>
    private static IEnumerable<string> SelectIds(SwContext ctx, IModelDoc2 d, string feature, IComponent2 comp)
    {
        string asmPath = DocumentRegistry.SafePath(d);
        string asmName = asmPath.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(asmPath) : StripExtension(ctx.Title(d));
        var ids = new List<string> { $"{feature}@{comp.Name2}@{asmName}" };
        string byId = Safe(() => comp.GetSelectByIDString(), "") ?? "";
        if (byId.Length > 0) ids.Add($"{feature}@{byId}");
        return ids.Distinct(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ small helpers

    private sealed record RotSpec(double[] R, Vec3? Point);

    private static RotSpec? RotationOf(AsmRotation? r, string arg, string? pointRefusal)
    {
        if (r is null) return null;
        if (r.Axis is not { Length: 3 } a || a.Any(v => !double.IsFinite(v)))
            throw SwException.InvalidArgument($"'{arg}.axis' must be a direction [x, y, z].", "E.g. rotation:{axis:[0,0,1], angleDeg:90}.");
        var axis = new Vec3(a[0], a[1], a[2]);
        if (axis.Length < 1e-12) throw SwException.InvalidArgument($"'{arg}.axis' must not be [0, 0, 0].");
        if (!double.IsFinite(r.AngleDeg)) throw SwException.InvalidArgument($"'{arg}.angleDeg' must be a finite number of degrees.");
        Vec3? point = null;
        if (r.PointMm is not null)
        {
            if (pointRefusal is not null) throw SwException.InvalidArgument($"'{arg}.pointMm' does not apply here.", pointRefusal);
            point = Units.PointM(r.PointMm, arg + ".pointMm");
        }
        return new RotSpec(AsmMath.Rotation(axis, Units.Rad(r.AngleDeg)), point);
    }

    private static void NoOpenSketch(IModelDoc2 d)
    {
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open in the assembly.", "Finish it with sketch_finish first.");
    }

    /// <summary>Reads a value; a COM failure (other than a disconnect) or a failed read gives <paramref name="fallback"/>.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            return fallback;
        }
        catch (Exception e) when (e is SwException or InvalidCastException or ArgumentException)
        {
            return fallback;
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string FullPath(string p)
    {
        try { return System.IO.Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string StripExtension(string title) =>
        title.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) || title.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)
            ? title[..^7] : title;

    private static string SafeFileName(string title)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(StripExtension(title).Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return s.Length == 0 ? "component" : s;
    }

    private static string Pt(Vec3 p) => TopoQuery.Fmt(Units.Mm(p));

    private static string Dir(Vec3 v) => TopoQuery.Fmt(Units.Unit(v.Normalized()));
}

// ====================================================================== pure parts (offline-tested)

/// <summary>Names and values of the assembly enums the tools take and report (snake_case).</summary>
internal static class AsmParse
{
    internal static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    public static (int Type, string Name) MateType(string? s) => Norm(s) switch
    {
        null => throw SwException.InvalidArgument("'type' is required: coincident, concentric, parallel, perpendicular, tangent, distance, angle or lock."),
        "coincident" or "coincidence" => ((int)swMateType_e.swMateCOINCIDENT, "coincident"),
        "concentric" or "coaxial" => ((int)swMateType_e.swMateCONCENTRIC, "concentric"),
        "parallel" => ((int)swMateType_e.swMatePARALLEL, "parallel"),
        "perpendicular" => ((int)swMateType_e.swMatePERPENDICULAR, "perpendicular"),
        "tangent" => ((int)swMateType_e.swMateTANGENT, "tangent"),
        "distance" => ((int)swMateType_e.swMateDISTANCE, "distance"),
        "angle" => ((int)swMateType_e.swMateANGLE, "angle"),
        "lock" => ((int)swMateType_e.swMateLOCK, "lock"),
        "hinge" or "slot" or "gear" or "width" or "symmetric" or "screw" or "cam" or "rack_pinion" or "path" or "linear_coupler" or
            "universal_joint" or "slider" or "profile_center" or "magnetic" =>
            throw new SwException(ErrorCodes.NotSupported, $"'{s}' mates are not available yet.",
                "Use coincident, concentric, parallel, perpendicular, tangent, distance, angle or lock (combine them, e.g. hinge = concentric + coincident)."),
        _ => throw SwException.InvalidArgument($"Unknown mate type '{s}'.",
            "Use coincident, concentric, parallel, perpendicular, tangent, distance, angle or lock."),
    };

    public static (int Value, string Name) Alignment(string? s) => Norm(s) switch
    {
        null or "closest" => ((int)swMateAlign_e.swMateAlignCLOSEST, "closest"),
        "aligned" or "same" => ((int)swMateAlign_e.swMateAlignALIGNED, "aligned"),
        "anti_aligned" or "antialigned" or "opposed" or "opposite" => ((int)swMateAlign_e.swMateAlignANTI_ALIGNED, "anti_aligned"),
        _ => throw SwException.InvalidArgument($"Unknown alignment '{s}'.", "Use closest (default), aligned or anti_aligned."),
    };

    public static string MateTypeName(int t) => t switch
    {
        (int)swMateType_e.swMateCOINCIDENT => "coincident",
        (int)swMateType_e.swMateCONCENTRIC => "concentric",
        (int)swMateType_e.swMatePERPENDICULAR => "perpendicular",
        (int)swMateType_e.swMatePARALLEL => "parallel",
        (int)swMateType_e.swMateTANGENT => "tangent",
        (int)swMateType_e.swMateDISTANCE => "distance",
        (int)swMateType_e.swMateANGLE => "angle",
        (int)swMateType_e.swMateSYMMETRIC => "symmetric",
        (int)swMateType_e.swMateCAMFOLLOWER => "cam",
        (int)swMateType_e.swMateGEAR => "gear",
        (int)swMateType_e.swMateWIDTH => "width",
        (int)swMateType_e.swMateLOCKTOSKETCH => "lock_to_sketch",
        (int)swMateType_e.swMateRACKPINION => "rack_pinion",
        (int)swMateType_e.swMatePATH => "path",
        (int)swMateType_e.swMateLOCK => "lock",
        (int)swMateType_e.swMateSCREW => "screw",
        (int)swMateType_e.swMateLINEARCOUPLER => "linear_coupler",
        (int)swMateType_e.swMateUNIVERSALJOINT => "universal_joint",
        (int)swMateType_e.swMateCOORDINATE => "coordinate",
        (int)swMateType_e.swMateSLOT => "slot",
        (int)swMateType_e.swMateHINGE => "hinge",
        (int)swMateType_e.swMateSLIDER => "slider",
        (int)swMateType_e.swMatePROFILECENTER => "profile_center",
        (int)swMateType_e.swMateMAGNETIC => "magnetic",
        _ => $"type {t}",
    };

    public static string AlignmentName(int a) => a switch
    {
        (int)swMateAlign_e.swMateAlignALIGNED => "aligned",
        (int)swMateAlign_e.swMateAlignANTI_ALIGNED => "anti_aligned",
        (int)swMateAlign_e.swMateAlignCLOSEST => "closest",
        _ => $"alignment {a}",
    };

    /// <summary>swAddMateError_e in words (1 = no error, VERIFIED).</summary>
    public static string MateStatus(int status) => status switch
    {
        (int)swAddMateError_e.swAddMateError_NoError => "no error",
        (int)swAddMateError_e.swAddMateError_IncorrectMateType => "a mate type these entities can't take",
        (int)swAddMateError_e.swAddMateError_IncorrectAlignment => "an alignment these entities can't take",
        (int)swAddMateError_e.swAddMateError_IncorrectSelections => "entities that can't take this mate",
        (int)swAddMateError_e.swAddMateError_OverDefinedAssembly => "an over-defined assembly",
        (int)swAddMateError_e.swAddMateError_IncorrectGearRatios => "invalid gear ratios",
        _ => $"an unknown error (status {status})",
    };

    /// <summary>IComponent2.GetSuppression2 (swComponentSuppressionState_e): 2 = resolved, 0 = suppressed (VERIFIED).</summary>
    public static string SuppressionName(int s) => s switch
    {
        (int)swComponentSuppressionState_e.swComponentSuppressed => "suppressed",
        (int)swComponentSuppressionState_e.swComponentLightweight or (int)swComponentSuppressionState_e.swComponentFullyLightweight => "lightweight",
        (int)swComponentSuppressionState_e.swComponentResolved or (int)swComponentSuppressionState_e.swComponentFullyResolved => "resolved",
        (int)swComponentSuppressionState_e.swComponentInternalIdMismatch => "id_mismatch",
        _ => "unknown",
    };

    /// <summary>swConstrainedStatus_e, named like sketch_finish's 'constrained'.</summary>
    public static string ConstrainedName(int s) => s switch
    {
        (int)swConstrainedStatus_e.swUnderConstrained => "under",
        (int)swConstrainedStatus_e.swFullyConstrained => "fully",
        (int)swConstrainedStatus_e.swOverConstrained => "over",
        (int)swConstrainedStatus_e.swNoSolution => "no_solution",
        (int)swConstrainedStatus_e.swInvalidSolution => "invalid",
        (int)swConstrainedStatus_e.swAutosolveOff => "autosolve_off",
        _ => "unknown",
    };

    /// <summary>swSelectType_e of a mate entity (fallback when the geometric kind can't be read).</summary>
    public static string SelTypeName(int t) => t switch
    {
        (int)swSelectType_e.swSelFACES => "face",
        (int)swSelectType_e.swSelEDGES => "edge",
        (int)swSelectType_e.swSelVERTICES => "point",
        (int)swSelectType_e.swSelDATUMPLANES => "plane",
        (int)swSelectType_e.swSelDATUMAXES => "line",
        (int)swSelectType_e.swSelDATUMPOINTS => "point",
        _ => "unknown",
    };

    /// <summary>swMateEntity2ReferenceType_e.</summary>
    public static string RefTypeName(int t) => t switch
    {
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Point => "point",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Line => "line",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Circle => "circle",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Plane => "plane",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Cylinder => "cylinder",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Sphere => "sphere",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Set => "set",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Cone => "cone",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_SweptSurface => "swept_surface",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_MultipleSurface => "multiple_surfaces",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_GenSurface => "surface",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_Ellipse => "ellipse",
        (int)swMateEntity2ReferenceType_e.swMateEntity2ReferenceType_GeneralCurve => "curve",
        _ => "unknown",
    };
}
