using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;
using static SolidWorksMcp.Core.Services.AsmPatternMath;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of asm_pattern: a linear, circular or mirror pattern of components.</summary>
public sealed record AsmPatternRequest
{
    public string? Doc { get; init; }
    /// <summary>linear | circular | mirror.</summary>
    public string? Kind { get; init; }
    public IReadOnlyList<string>? Components { get; init; }
    public int? Count { get; init; }
    public double? SpacingMm { get; init; }
    public string? Direction { get; init; }
    public EntityRef? DirectionRef { get; init; }
    public string? Direction2 { get; init; }
    public int? Count2 { get; init; }
    public double? Spacing2Mm { get; init; }
    public EntityRef? Axis { get; init; }
    public double? TotalAngleDeg { get; init; }
    public bool? EqualSpacing { get; init; }
    public EntityRef? Plane { get; init; }
    /// <summary>mirror: opposite-hand parts (new, mirrored part files) instead of instances of the seeds.</summary>
    public bool OppositeHand { get; init; }
    /// <summary>oppositeHand: the new file's name is the seed part's name plus this (default "_mirror").</summary>
    public string? Suffix { get; init; }
    /// <summary>oppositeHand: the folder for the new files (default: the seed parts' folder).</summary>
    public string? Folder { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed class AsmPatternResult : ToolResult
{
    /// <summary>The pattern feature (LocalLPattern, LocalCirPattern or MirrorComponent).</summary>
    public required string Feature { get; init; }
    public required string FeatureType { get; init; }
    public required string Kind { get; init; }
    /// <summary>The new instances (the first 20).</summary>
    public required IReadOnlyList<AsmComponentInfo> Instances { get; init; }
    public int InstanceCount { get; init; }
    /// <summary>Top-level components after the pattern.</summary>
    public int ComponentCount { get; init; }
    /// <summary>oppositeHand: the part files written (new; nothing was overwritten).</summary>
    public IReadOnlyList<string>? Files { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed partial class AssemblyService
{
    private const double RotTol = 1e-7; // per rotation-matrix element, instances read back

    // ================================================================== asm_pattern

    public Task<AsmPatternResult> PatternAsync(AsmPatternRequest q, CancellationToken ct = default)
    {
        string kind = AsmParse.Norm(q.Kind) switch
        {
            "linear" => "linear",
            "circular" or "polar" => "circular",
            "mirror" => "mirror",
            null => throw SwException.InvalidArgument("'kind' is required: linear, circular or mirror."),
            _ => throw SwException.InvalidArgument($"Unknown kind '{q.Kind}'.", "Use linear, circular or mirror."),
        };
        if (q.Components is not { Count: > 0 } || q.Components.Any(string.IsNullOrWhiteSpace))
            throw SwException.InvalidArgument("'components' is required: the seed components, e.g. [\"bolt-1\"] (asm_component_list).");
        if (q.Components.Count > 50) throw SwException.InvalidArgument($"At most 50 seed components (got {q.Components.Count}).");
        var spec = PatternSpecOf(kind, q);
        return session.RunAsync("asm_pattern", ctx => Pattern(ctx, q, spec), RunOptions.Write(q.Doc) with { Timeout = Long(180) }, ct);
    }

    /// <summary>The validated numbers of a pattern request (lengths in m, angles in rad).</summary>
    private sealed record PatternSpec(string Kind, int N1, double S1, int N2, double S2, double Step, double TotalRad, bool Equal, bool OppositeHand = false);

    private static PatternSpec PatternSpecOf(string kind, AsmPatternRequest q)
    {
        void NotFor(string what, bool given)
        {
            if (given) throw SwException.InvalidArgument($"'{what}' does not apply to kind '{kind}'.");
        }
        if (!(kind == "mirror" && q.OppositeHand))
        {
            NotFor("oppositeHand", q.OppositeHand);
            if (q.Suffix is not null || q.Folder is not null)
                throw SwException.InvalidArgument("'suffix' and 'folder' are for opposite-hand mirrors (kind 'mirror' with oppositeHand:true).");
        }
        else if (q.Suffix is { } sfx && (sfx.Length is 0 or > 64 || sfx.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || sfx.Trim() != sfx))
            throw SwException.InvalidArgument($"'suffix' must be 1 to 64 characters that can go in a file name (got \"{sfx}\").", "For example \"_mirror\" or \"-LH\".");
        switch (kind)
        {
            case "linear":
            {
                NotFor("axis", q.Axis is not null);
                NotFor("plane", q.Plane is not null);
                NotFor("totalAngleDeg", q.TotalAngleDeg is not null);
                NotFor("equalSpacing", q.EqualSpacing is not null);
                int n1 = q.Count ?? throw SwException.InvalidArgument("kind 'linear' needs count (instances, the seed included) and spacingMm.");
                if (n1 < 1 || n1 > 1000) throw SwException.InvalidArgument($"count must be between 1 and 1000, the seed included (got {n1}).");
                double s1 = Units.PositiveM(q.SpacingMm ?? throw SwException.InvalidArgument("kind 'linear' needs spacingMm."), "spacingMm");
                if (string.IsNullOrWhiteSpace(q.Direction) && q.DirectionRef is null)
                    throw SwException.InvalidArgument("kind 'linear' needs a direction: \"+x\" … \"-z\" (assembly axes) or directionRef (an edge, axis or planar face).");
                int n2 = 1;
                double s2 = s1;
                if (!string.IsNullOrWhiteSpace(q.Direction2))
                {
                    n2 = q.Count2 ?? throw SwException.InvalidArgument("direction2 needs count2 (instances in direction 2, the seed row included).");
                    if (n2 < 1 || n2 > 1000) throw SwException.InvalidArgument($"count2 must be between 1 and 1000 (got {n2}).");
                    s2 = Units.PositiveM(q.Spacing2Mm ?? throw SwException.InvalidArgument("direction2 needs spacing2Mm."), "spacing2Mm");
                }
                else if (q.Count2 is not null || q.Spacing2Mm is not null)
                    throw SwException.InvalidArgument("count2 and spacing2Mm need direction2 (e.g. \"+z\").");
                if ((long)n1 * n2 < 2) throw SwException.InvalidArgument("The pattern needs at least 2 instances (count includes the seed).");
                if ((long)n1 * n2 * q.Components!.Count > 2000)
                    throw SwException.InvalidArgument($"{n1 * n2} instances of {q.Components.Count} components is more than 2000 components.");
                return new PatternSpec(kind, n1, s1, n2, s2, 0, 0, false);
            }
            case "circular":
            {
                NotFor("spacingMm", q.SpacingMm is not null);
                NotFor("direction", !string.IsNullOrWhiteSpace(q.Direction));
                NotFor("directionRef", q.DirectionRef is not null);
                NotFor("direction2", !string.IsNullOrWhiteSpace(q.Direction2));
                NotFor("count2", q.Count2 is not null);
                NotFor("spacing2Mm", q.Spacing2Mm is not null);
                NotFor("plane", q.Plane is not null);
                if (q.Axis is null)
                    throw SwException.InvalidArgument("kind 'circular' needs 'axis': an axis, a cylindrical face or a circular edge, e.g. {component:'hub-1', at:[x,y,z]}.");
                int n = q.Count ?? throw SwException.InvalidArgument("kind 'circular' needs count (instances, the seed included).");
                if (n < 2 || n > 1000) throw SwException.InvalidArgument($"count must be between 2 and 1000, the seed included (got {n}).");
                if ((long)n * q.Components!.Count > 2000)
                    throw SwException.InvalidArgument($"{n} instances of {q.Components.Count} components is more than 2000 components.");
                double a = q.TotalAngleDeg ?? 360;
                bool equal = q.EqualSpacing ?? true;
                if (!double.IsFinite(a) || a <= 0) throw SwException.InvalidArgument($"totalAngleDeg must be positive (got {a}).");
                if (equal && a > 360 + 1e-9) throw SwException.InvalidArgument($"totalAngleDeg must be at most 360 with equalSpacing (got {a}).");
                if (!equal && (n - 1) * a > 360 + 1e-9)
                    throw SwException.InvalidArgument($"{n} instances {a}° apart go past 360°.",
                        "Lower the count or the angle (without equalSpacing, totalAngleDeg is the angle between instances).");
                double total = Units.Rad(Math.Min(a, 360));
                return new PatternSpec(kind, n, 0, 1, 0, Step(total, n, equal), total, equal);
            }
            default:
            {
                foreach (var (what, given) in new[]
                         {
                             ("count", q.Count is not null), ("spacingMm", q.SpacingMm is not null), ("direction", !string.IsNullOrWhiteSpace(q.Direction)),
                             ("directionRef", q.DirectionRef is not null), ("direction2", !string.IsNullOrWhiteSpace(q.Direction2)),
                             ("count2", q.Count2 is not null), ("spacing2Mm", q.Spacing2Mm is not null), ("axis", q.Axis is not null),
                             ("totalAngleDeg", q.TotalAngleDeg is not null), ("equalSpacing", q.EqualSpacing is not null),
                         })
                    NotFor(what, given);
                if (q.Plane is null) throw SwException.InvalidArgument("kind 'mirror' needs 'plane', e.g. {name:'right'} or {component:'base-1', name:'front'}.");
                return new PatternSpec(kind, 2, 0, 1, 0, 0, 0, false, q.OppositeHand);
            }
        }
    }

    private static AsmPatternResult Pattern(SwContext ctx, AsmPatternRequest q, PatternSpec spec)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);

        var seeds = new List<IComponent2>();
        foreach (var name in q.Components!)
        {
            var c = EntityResolver.Component(asm, name);
            if (ParentOf(c) is IComponent2 parent)
                throw SwException.InvalidArgument($"'{c.Name2}' is inside the subassembly '{parent.Name2}'; pattern top-level components (the subassembly itself).");
            if (Safe(() => c.GetSuppression2(), -1) == (int)swComponentSuppressionState_e.swComponentSuppressed)
                throw SwException.InvalidArgument($"'{c.Name2}' is suppressed.", "Unsuppress it first (asm_component_set suppressed=false).");
            if (seeds.Any(s => Eq(s.Name2, c.Name2))) throw SwException.InvalidArgument($"'{c.Name2}' is listed twice in 'components'.");
            seeds.Add(c);
        }
        var seedFrames = seeds.Select(c => FrameOf(ctx, c)).ToList();
        var seedPlacements = seedFrames.Select(f => new Placement(f.Origin, f.R)).ToList();
        var seedFiles = seeds.Select(CompPath).ToList();

        var refs = new List<ResolvedInfo>();
        // The geometry the pattern runs along, in assembly space, and how to hand it to SOLIDWORKS.
        PatternRef? dir1 = null, dir2 = null, axis = null, plane = null;
        switch (spec.Kind)
        {
            case "linear":
                dir1 = LinearDirection(ctx, d, asm, q.Direction, q.DirectionRef, "direction");
                if (!string.IsNullOrWhiteSpace(q.Direction2))
                {
                    dir2 = LinearDirection(ctx, d, asm, q.Direction2, null, "direction2");
                    if (dir2.U.IsParallelTo(dir1.U, Units.Rad(1)))
                        throw SwException.InvalidArgument("direction2 is parallel to direction.", "Pick two different directions, e.g. +x and +z.");
                }
                if (dir1.Info is not null) refs.Add(dir1.Info);
                break;
            case "circular":
                axis = PatternAxis(ctx, d, asm, q.Axis!);
                refs.Add(axis.Info!);
                break;
            default:
                plane = MirrorPlane(ctx, d, asm, q.Plane!);
                refs.Add(plane.Info!);
                break;
        }
        var hand = spec.OppositeHand ? PlanOppositeHand(ctx, q, seeds, seedFiles) : null;

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        var namesBefore = asm.Components(true).Select(c => c.Name2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int countBefore = namesBefore.Count;
        int expectedNew = spec.Kind == "mirror" ? seeds.Count : (spec.N1 * spec.N2 - 1) * seeds.Count;

        bool flip1 = dir1?.StartFlipped ?? false, flip2 = dir2?.StartFlipped ?? false;
        var tried = new List<(bool, bool)>();
        while (true)
        {
            tried.Add((flip1, flip2));
            var attempts = new List<string>();
            IFeature? feat = spec.Kind switch
            {
                "linear" => CreateLinear(ctx, d, seeds, dir1!, dir2, spec, flip1, flip2, attempts),
                "circular" => CreateCircular(ctx, d, seeds, axis!, spec, attempts),
                _ => hand is null ? CreateMirror(ctx, d, asm, seeds, plane!, attempts) : CreateOppositeHand(d, seeds, plane!, hand, attempts),
            };
            d.ClearSelection2(true);
            Safe(() => d.EditRebuild3(), false);

            var created = Snapshot.Take(d).Features.Where(kv => !before.Features.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
            var fresh = asm.Components(true).Where(c => !namesBefore.Contains(c.Name2)).ToList();
            string what = $"{(hand is null ? spec.Kind : "opposite-hand mirror")} pattern of {string.Join(", ", seeds.Select(s => s.Name2))}";
            if (created.Count == 0 && fresh.Count == 0)
                throw new SwException(ErrorCodes.FeatureFailed,
                    $"asm_pattern: SOLIDWORKS created no {what}" + (attempts.Count > 0 ? $" ({string.Join("; ", attempts)})." : ".") +
                    (hand is null ? "" : DropHandFiles(ctx, hand)),
                    PatternHint(spec.Kind));

            var main = feat is not null && created.Any(f => Safe(() => f.GetID(), -1) == Safe(() => feat.GetID(), -2))
                ? feat
                : created.FirstOrDefault(f => PatternTypes.Contains(Safe(() => f.GetTypeName2(), "")));
            var checks = new List<string>();
            string? problem = null;
            string code = ErrorCodes.VerifyFailed;
            (bool, bool)? redo = null;
            try
            {
                if (main is null) { problem = "no pattern feature was created"; code = ErrorCodes.FeatureFailed; }
                else if (main.GetErrorCode2(out bool warn) is int err && err != 0 && !warn)
                {
                    problem = $"'{main.Name}' has rebuild error {err} ({FeatureVerifier.ErrorName(err)})";
                    code = ErrorCodes.RebuildError;
                }
                else if (fresh.Count != expectedNew)
                    problem = $"{fresh.Count} new component(s) appeared instead of {expectedNew}";
                else
                {
                    checks.Add($"{main.Name} ({main.GetTypeName2()}) created without errors; {fresh.Count} new {(hand is null ? "instance" : "component")}(s), " +
                               $"components {countBefore} → {countBefore + fresh.Count}");
                    var files = fresh.Select(CompPath).ToList();
                    var stray = files.Where(f => !seedFiles.Any(s => SamePath(s, f))).Distinct().ToList();
                    if (hand is not null) problem = CheckOppositeHand(ctx, seeds, fresh, plane!, hand, checks);
                    else if (stray.Count > 0) problem = $"instances reference other files ({string.Join(", ", stray.Select(System.IO.Path.GetFileName))})";
                    else
                    {
                        var actual = fresh.Select(c => FrameOf(ctx, c)).Select(f => new Placement(f.Origin, f.R)).ToList();
                        problem = spec.Kind switch
                        {
                            "linear" => CheckLinear(seedPlacements, actual, dir1!, dir2, spec, flip1, flip2, checks, out redo),
                            "circular" => CheckCircular(seedPlacements, actual, axis!, spec, checks),
                            _ => CheckMirror(ctx, seeds, seedFrames, fresh, plane!, checks),
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
            {
                problem = $"it could not be verified ({(ex is SwException ? "" : ex.GetType().Name + ": ")}{ex.Message})";
                code = (ex as SwException)?.Code ?? ErrorCodes.Internal;
            }

            if (redo is not null && q.KeepOnFailure) problem = "it runs opposite to the requested direction";
            else if (redo is (bool, bool) fix)
            {
                // SOLIDWORKS ran a direction whose sign the caller gave the other way (it picks its own sense along a
                // plane normal or an edge): delete the pattern (its instances go with it, VERIFIED) and redo it flipped.
                string rb = FeatureVerifier.Rollback(ctx, d, before);
                if (asm.GetComponentCount(true) != countBefore || NewFeature(d, before))
                    throw new SwException(ErrorCodes.FeatureFailed, $"asm_pattern: the pattern came out reversed and could not be removed to redo it: {rb}");
                if (tried.Contains(fix))
                    throw new SwException(ErrorCodes.VerifyFailed, $"asm_pattern: no sense of the direction gives the requested {what}. {rb}", PatternHint(spec.Kind));
                (flip1, flip2) = fix;
                continue;
            }
            if (problem is not null)
            {
                string tail = q.KeepOnFailure ? "The pattern was kept (keepOnFailure)."
                    : FeatureVerifier.Rollback(ctx, d, before) + (hand is null ? "" : DropHandFiles(ctx, hand));
                string verb = code == ErrorCodes.VerifyFailed ? "doesn't match the request" : "failed";
                throw new SwException(code, $"asm_pattern: the {what} {verb}: {problem}. {tail}", PatternHint(spec.Kind));
            }

            if (tried.Count > 1) checks.Add("redone with the direction flipped (SOLIDWORKS ran it the other way first)");
            if (hand is not null)
                foreach (var t in hand.Targets) ctx.Paths.RecordWritten(t);
            var counts = MateCounts(d);
            d.GraphicsRedraw2();
            return new AsmPatternResult
            {
                Doc = ctx.Title(d),
                Feature = main!.Name,
                FeatureType = main.GetTypeName2(),
                Kind = spec.Kind,
                Instances = fresh.Take(20).Select(c => Info(ctx, c, counts)).ToList(),
                InstanceCount = fresh.Count,
                ComponentCount = asm.GetComponentCount(true),
                Files = hand?.Targets.Select(OnDisk).ToList(),
                Verified = checks,
                Resolved = refs.Count > 0 ? refs : null,
            };
        }
    }

    private static readonly HashSet<string> PatternTypes = new(StringComparer.OrdinalIgnoreCase)
        { "LocalLPattern", "LocalCirPattern", "MirrorCompFeat" }; // VERIFIED type names; the instances are "ReferencePattern"

    private static string PatternHint(string kind) => kind switch
    {
        "linear" => "Give the direction as an assembly axis (\"+x\") or a straight edge, axis or planar face; counts include the seed.",
        "circular" => "The axis must be an axis, a cylindrical face or a circular or straight edge; count includes the seed.",
        _ => "The plane must be an assembly plane ({name:'right'}) or a component's plane or planar face.",
    };

    private static bool NewFeature(IModelDoc2 d, Snapshot before) => Snapshot.Take(d).Features.Keys.Any(id => !before.Features.ContainsKey(id));

    // ------------------------------------------------------------------ references

    /// <summary>
    /// A pattern reference: its assembly-space point P and unit vector U (direction, axis or plane normal), whether the
    /// caller gave U's sign, how to select it (doc, mark, append) and the object to hand an API call that takes it.
    /// </summary>
    private sealed record PatternRef(Vec3 P, Vec3 U, bool SignGiven, string Label, ResolvedInfo? Info,
        Func<IModelDoc2, int, bool, bool> Select, Func<IModelDoc2, object?> Api)
    {
        /// <summary>Start reversed: SOLIDWORKS runs a standard plane's normal in + (VERIFIED), and "-x" wants the other way.</summary>
        public bool StartFlipped { get; init; }
    }

    private static PatternRef LinearDirection(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, string? axisSpec, EntityRef? reference, string arg)
    {
        Vec3? want = string.IsNullOrWhiteSpace(axisSpec) ? null : AxisSpec.Parse(axisSpec!, arg);
        if (reference is null)
        {
            var w = want!.Value;
            string planeName = (Math.Abs(Math.Abs(w.X) - 1) < 1e-9 ? "right" : Math.Abs(Math.Abs(w.Y) - 1) < 1e-9 ? "top" : Math.Abs(Math.Abs(w.Z) - 1) < 1e-9 ? "front" : null)
                               ?? throw SwException.InvalidArgument($"'{arg}' must be an assembly axis (+x, -x, +y, -y, +z, -z)." +
                                                                    (arg == "direction" ? " For other directions pass directionRef (a straight edge, axis or planar face)." : ""));
            var feat = EntityResolver.StandardPlane(d, planeName)
                       ?? throw new SwException(ErrorCodes.EntityNotFound, $"The assembly's standard {planeName} plane was not found.", "feature_list shows the planes.");
            return new PatternRef(Vec3.Zero, w, true, $"{axisSpec} (normal of {feat.Name})", null, (_, mark, append) => feat.Select2(append, mark), _ => feat)
            {
                StartFlipped = w.X + w.Y + w.Z < 0,
            };
        }
        var m = ResolveMateRef(ctx, d, asm, reference, arg + "Ref", false);
        var g = ToAsm(ctx, m);
        Vec3 u = g.Kind switch
        {
            'l' or 'p' => g.D,
            _ => throw SwException.InvalidArgument($"{m.Label} can't give a pattern direction ({g.What}); use a straight edge, an axis or a planar face or plane."),
        };
        bool signGiven = false;
        if (want is Vec3 w2)
        {
            if (!u.IsParallelTo(w2, Units.Rad(1)) && !u.IsParallelTo(-w2, Units.Rad(1)))
                throw SwException.InvalidArgument($"'{arg}' [{Dir(w2)}] is not parallel to {m.Label} [{Dir(u)}].",
                    $"Leave '{arg}' out, or give its sign as \"+\"/\"-\" of the reference's own direction.");
            if (u.Dot(w2) < 0) u = -u;
            signGiven = true;
        }
        return FromMateRef(ctx, m, g.P, u.Normalized(), signGiven, arg + "Ref");
    }

    private static PatternRef PatternAxis(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, EntityRef reference)
    {
        var m = ResolveMateRef(ctx, d, asm, reference, "axis", false); // 'at' alone means a face
        var g = ToAsm(ctx, m);
        if (g.Kind is not ('a' or 'l'))
            throw SwException.InvalidArgument($"{m.Label} is not an axis ({g.What}).",
                "Use an axis, a cylindrical or conical face, or a circular or straight edge, e.g. {component:'hub-1', at:[x,y,z]}.");
        var u = g.D.Normalized();
        return FromMateRef(ctx, m, g.P - u * u.Dot(g.P), u, false, "axis"); // the axis point nearest the origin
    }

    private static PatternRef MirrorPlane(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, EntityRef reference)
    {
        var m = ResolveMateRef(ctx, d, asm, reference, "plane", false);
        var g = ToAsm(ctx, m);
        if (g.Kind != 'p')
            throw SwException.InvalidArgument($"{m.Label} is not planar ({g.What}).", "Use a plane ({name:'right'}, {component:'base-1', name:'front'}) or a planar face.");
        var n = g.D.Normalized();
        return FromMateRef(ctx, m, n * n.Dot(g.P), n, false, "plane"); // the plane point nearest the origin
    }

    private static PatternRef FromMateRef(SwContext ctx, MateRef m, Vec3 p, Vec3 u, bool signGiven, string arg) =>
        new(p, u, signGiven, m.Label, m.R.Info(arg),
            (doc, mark, append) =>
            {
                SelectForMate(ctx, doc, m, append, arg, mark);
                return true;
            },
            doc =>
            {
                // An assembly feature as it is; a component entity as SOLIDWORKS selects it (the assembly-context object).
                if (m.Comp is null && m.R.Obj is IFeature f) return f;
                doc.ClearSelection2(true);
                SelectForMate(ctx, doc, m, false, arg);
                object? o = doc.Sel().GetSelectedObject6(1, -1);
                doc.ClearSelection2(true);
                return o;
            });

    // ------------------------------------------------------------------ creation

    // Component patterns take their references from the selection when the definition is made (as SOLIDWORKS' own
    // examples do): seeds on mark 1, direction 1 or the axis on mark 2, direction 2 on mark 4. Setting the data's
    // SeedComponentArray and D1Axis on an empty definition doesn't take: CreateFeature returns nothing (VERIFIED).
    private const int SeedMark = 1, Dir1Mark = 2, Dir2Mark = 4;

    private static bool SelectPattern(IModelDoc2 d, List<IComponent2> seeds, PatternRef first, PatternRef? second, List<string> attempts)
    {
        d.ClearSelection2(true);
        if (!first.Select(d, Dir1Mark, false)) { attempts.Add($"could not select {first.Label}"); return false; }
        if (second is not null && !second.Select(d, Dir2Mark, true)) { attempts.Add($"could not select {second.Label}"); return false; }
        foreach (var c in seeds)
            if (!SelectComponent(d, c, true, SeedMark)) { attempts.Add($"could not select {c.Name2}"); return false; }
        int want = seeds.Count + 1 + (second is null ? 0 : 1);
        int got = d.Sel().GetSelectedObjectCount2(-1);
        if (got != want) { attempts.Add($"{got} objects selected instead of {want}"); return false; }
        return true;
    }

    private static IFeature? CreateLinear(SwContext ctx, IModelDoc2 d, List<IComponent2> seeds, PatternRef dir1, PatternRef? dir2,
        PatternSpec spec, bool flip1, bool flip2, List<string> attempts)
    {
        var fm = d.FeatureManager;
        if (!SelectPattern(d, seeds, dir1, dir2, attempts)) return null;
        if ((object?)fm.CreateDefinition((int)swFeatureNameID_e.swFmLocalLPattern) is not ILocalLinearPatternFeatureData data)
        {
            attempts.Add("CreateDefinition(LocalLPattern) returned nothing");
            return null;
        }
        data.D1TotalInstances = spec.N1;
        data.D1Spacing = spec.S1;
        data.D1ReverseDirection = flip1;
        data.D2TotalInstances = spec.N2;
        data.D2Spacing = spec.S2;
        data.D2ReverseDirection = flip2;
        data.D2PatternSeedOnly = false;
        data.SynchronizeFlexibleComponents = false;
        var f = Create(fm, data, "LocalLPattern", attempts);
        if (f is null)
            attempts.Add($"definition: {Safe(() => data.GetSeedComponentCount(), -1)} seed(s), direction types {Safe(() => data.GetD1AxisType(), -1)}/" +
                         $"{Safe(() => data.GetD2AxisType(), -1)}, {Safe(() => data.D1TotalInstances, -1)} x {Safe(() => data.D1Spacing, -1.0)} m");
        return f;
    }

    private static IFeature? CreateCircular(SwContext ctx, IModelDoc2 d, List<IComponent2> seeds, PatternRef axis, PatternSpec spec, List<string> attempts)
    {
        var fm = d.FeatureManager;
        if (!SelectPattern(d, seeds, axis, null, attempts)) return null;
        if ((object?)fm.CreateDefinition((int)swFeatureNameID_e.swFmLocalCirPattern) is not ILocalCircularPatternFeatureData data)
        {
            attempts.Add("CreateDefinition(LocalCirPattern) returned nothing");
            return null;
        }
        data.TotalInstances = spec.N1;
        data.EqualSpacing = spec.Equal;
        data.Spacing = spec.Equal ? spec.TotalRad : spec.Step;
        data.SynchronizeFlexibleComponents = false;
        var f = Create(fm, data, "LocalCirPattern", attempts);
        if (f is null)
            attempts.Add($"definition: {Safe(() => data.GetSeedComponentCount(), -1)} seed(s), axis type {Safe(() => data.GetAxisType(), -1)}, " +
                         $"{Safe(() => data.TotalInstances, -1)} x {Safe(() => data.Spacing, -1.0)} rad");
        return f;
    }

    /// <summary>
    /// IAssemblyDoc.MirrorComponents3 with every seed copied as an instance (ComponentsToInstance) oriented about its
    /// bounding-box centre; nothing goes into ComponentsToMirror, so no opposite-hand files are written. The caller
    /// finds the new feature and components.
    /// </summary>
    private static IFeature? CreateMirror(SwContext ctx, IModelDoc2 d, IAssemblyDoc asm, List<IComponent2> seeds, PatternRef plane, List<string> attempts)
    {
        object planeObj = plane.Api(d) ?? throw new SwException(ErrorCodes.FeatureFailed, $"Could not get {plane.Label} as the mirror plane.");
        int[] orient = seeds.Select(_ => (int)swMirrorComponentOrientation_e.swMirrorComponentOrientation_RotatePlaneY).ToArray();
        int before = asm.GetComponentCount(true);
        foreach (var (how, comps) in new (string, Func<object>)[]
                 {
                     ("IDispatch array", () => ComArgs.DispatchArray(seeds)),
                     ("VARIANT array", () => seeds.Cast<object>().ToArray()),
                 })
        {
            try
            {
                d.ClearSelection2(true);
                object? r = asm.MirrorComponents3(planeObj, comps(), orient, false, null, false, null,
                    (int)swMirrorComponentNameModifier_e.swMirrorComponentName_Suffix, "", "", 0, false, false, false);
                if (asm.GetComponentCount(true) > before) return null;
                attempts.Add($"MirrorComponents3 ({how}) {(r is null ? "returned nothing" : "added no component")}");
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                attempts.Add($"MirrorComponents3 ({how}): {e.Message}");
            }
        }
        return null;
    }

    /// <summary>The opposite-hand part files to write: for each seed part its name plus the suffix, in one folder.</summary>
    private sealed record OppositeHandPlan(string Folder, List<string> Names, List<string> Targets);

    /// <summary>Plans the opposite-hand files before anything is made: parts only, one per part, none existing or open yet.</summary>
    private static OppositeHandPlan PlanOppositeHand(SwContext ctx, AsmPatternRequest q, List<IComponent2> seeds, List<string> seedFiles)
    {
        for (int k = 0; k < seeds.Count; k++)
        {
            if (!seedFiles[k].EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"'{seeds[k].Name2}' is not a part; opposite-hand copies are made of parts.",
                    "Mirror subassemblies as instances (leave oppositeHand out).");
            for (int j = 0; j < k; j++)
                if (SamePath(seedFiles[j], seedFiles[k]))
                    throw SwException.InvalidArgument($"'{seeds[j].Name2}' and '{seeds[k].Name2}' are instances of the same part; list one of them.",
                        "One opposite-hand file is made per part.");
        }
        string? folder = q.Folder;
        if (folder is null)
        {
            var dirs = seedFiles.Select(f => System.IO.Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dirs.Count != 1) throw SwException.InvalidArgument("The seed parts are in different folders.", "Pass 'folder' for the opposite-hand files.");
            folder = dirs[0];
        }
        var names = new List<string>();
        var targets = new List<string>();
        foreach (var f in seedFiles)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(f) + (q.Suffix ?? "_mirror");
            string target;
            try
            {
                target = ctx.Paths.ForWrite(System.IO.Path.Combine(folder, name + ".sldprt"), overwrite: false, out _, "folder");
            }
            catch (SwException e) when (e.Code == ErrorCodes.IoError)
            {
                throw new SwException(ErrorCodes.IoError, e.Message, "Choose another suffix or folder: existing files are never replaced.");
            }
            if (targets.Any(t => string.Equals(t, target, StringComparison.OrdinalIgnoreCase)))
                throw SwException.InvalidArgument($"Two seed parts would both become '{System.IO.Path.GetFileName(target)}'.", "Mirror them in separate calls.");
            if ((object?)ctx.App.GetOpenDocumentByName(target) is not null)
                throw new SwException(ErrorCodes.IoError, $"'{target}' is open in SOLIDWORKS.", "Choose another suffix or folder.");
            names.Add(name);
            targets.Add(target);
        }
        return new OppositeHandPlan(System.IO.Path.GetDirectoryName(targets[0])!, names, targets);
    }

    /// <summary>
    /// Opposite-hand copies through IMirrorComponentFeatureData (VERIFIED 2026-10-24): the seeds as OppositeHandComponents,
    /// placed about their bounding-box centres, new part files under the planned names in one folder (SOLIDWORKS writes
    /// them at once), solid bodies transferred, PreserveZAxis false (true left the copy turned 180° off its mirror image).
    /// </summary>
    private static IFeature? CreateOppositeHand(IModelDoc2 d, List<IComponent2> seeds, PatternRef plane, OppositeHandPlan hand, List<string> attempts)
    {
        var fm = d.FeatureManager;
        if ((object?)fm.CreateDefinition((int)swFeatureNameID_e.swFmMirrorComponent) is not IMirrorComponentFeatureData data)
        {
            attempts.Add("CreateDefinition(MirrorComponent) returned nothing");
            return null;
        }
        data.MirrorPlane = plane.Api(d) ?? throw new SwException(ErrorCodes.FeatureFailed, $"Could not get {plane.Label} as the mirror plane.");
        data.MirrorType = (int)swMirrorComponentMirrorType_e.swMirrorType_CenterOfBoundingBox;
        data.OppositeHandComponents = ComArgs.DispatchArray(seeds);
        data.CreateDerivedConfigurations = false;
        data.PlaceFilesInOneFolder = true;
        data.MirrorComponentsFolderLocation = hand.Folder;
        data.MirroredComponentFilenames = hand.Names.ToArray();
        data.NameModifierType = (int)swMirrorComponentNameModifier_e.swMirrorComponentName_Custom;
        data.ReplaceFileLocations = hand.Names.Select(_ => "").ToArray();
        data.MirrorTransferOptions = (int)swMirrorPartOptions_e.swMirrorPartOptions_ImportSolids;
        data.BreakLinksToOriginalPart = false;
        data.PreserveZAxis = false;
        data.PropagateFromOriginalPart = false;
        data.SyncFlexibleSubAssemblies = false;
        d.ClearSelection2(true);
        return Create(fm, data, "MirrorComponent", attempts);
    }

    /// <summary>
    /// Each seed got one new component on its opposite-hand file, written by this call, whose part holds the seed part's
    /// volume and whose vertices, in the assembly, are the seed's vertices reflected about the plane: a mirror image, which
    /// a turned copy of a handed part can't be.
    /// </summary>
    private static string? CheckOppositeHand(SwContext ctx, List<IComponent2> seeds, List<IComponent2> fresh, PatternRef plane, OppositeHandPlan hand,
        List<string> checks)
    {
        var mu = ctx.Math;
        int vertices = 0;
        for (int k = 0; k < seeds.Count; k++)
        {
            string file = System.IO.Path.GetFileName(hand.Targets[k]);
            if (!File.Exists(hand.Targets[k])) return $"'{file}' was not written";
            var c = fresh.FirstOrDefault(x => SamePath(CompPath(x), hand.Targets[k]));
            if (c is null) return $"no new component references '{file}'";
            var seedPts = PartVertices(seeds[k]);
            var newPts = PartVertices(c);
            if (seedPts.Count == 0) return $"the vertices of '{seeds[k].Name2}' could not be read";
            if (newPts.Count != seedPts.Count) return $"'{c.Name2}' has {newPts.Count} vertices, its seed {seedPts.Count}";
            var got = newPts.Select(p => mu.AsmMapPoint(c.Transform2, p)).ToList();
            int step = Math.Max(1, seedPts.Count / 500); // at most about 500 vertices compared per part
            for (int i = 0; i < seedPts.Count; i += step)
            {
                var want = Reflect(mu.AsmMapPoint(seeds[k].Transform2, seedPts[i]), plane.P, plane.U);
                if (!got.Any(g => g.DistanceTo(want) <= PosTol * 10))
                    return $"'{c.Name2}' has no vertex at [{Pt(want)}], the reflection of a vertex of '{seeds[k].Name2}': it is not the seed's mirror image";
            }
            double v0 = PartVolume(seeds[k]), v1 = PartVolume(c);
            if (Math.Abs(v1 - v0) > 1e-6 * v0 + 1e-15) return $"'{c.Name2}' holds {Units.Mm3(v1)} mm³, its seed {Units.Mm3(v0)} mm³";
            vertices += seedPts.Count;
        }
        checks.Add($"each new part file ({string.Join(", ", hand.Targets.Select(System.IO.Path.GetFileName))}) is placed as the mirror image of its seed " +
                   $"about the plane through [{Pt(plane.P)}] with normal [{Dir(plane.U)}]: all {vertices} vertices reflected, the same volume");
        return null;
    }

    /// <summary>The vertices of a part component's solid bodies, in part space.</summary>
    private static List<Vec3> PartVertices(IComponent2 c) =>
        c.AsmModelDoc() is IPartDoc p
            ? p.AllBodies(visibleOnly: false).Where(b => b.GetType() == (int)swBodyType_e.swSolidBody)
                .SelectMany(b => b.Vertices()).Select(v => Vec3.From(v.Point())).ToList()
            : [];

    /// <summary>The volume of a part component's solid bodies (m³).</summary>
    private static double PartVolume(IComponent2 c) => c.AsmModelDoc() is IModelDoc2 md ? Snapshot.Take(md).Volume : 0;

    /// <summary>After a failed opposite-hand mirror: closes and deletes the files it wrote (none of them existed before).</summary>
    private static string DropHandFiles(SwContext ctx, OppositeHandPlan hand)
    {
        var gone = new List<string>();
        foreach (var t in hand.Targets)
        {
            Safe(() =>
            {
                ctx.App.CloseDoc(t);
                return true;
            }, false);
            if (!File.Exists(t)) continue;
            try
            {
                File.Delete(t);
                gone.Add(System.IO.Path.GetFileName(t));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ctx.Warn($"The new file '{t}' could not be deleted ({e.Message}).");
            }
        }
        return gone.Count == 0 ? "" : $" The new file(s) {string.Join(", ", gone)} were deleted.";
    }

    /// <summary>The path as the file system spells it (SOLIDWORKS lower-cases the names of the files it writes).</summary>
    private static string OnDisk(string path)
    {
        try
        {
            if (System.IO.Path.GetDirectoryName(path) is { } dir && Directory.EnumerateFiles(dir, System.IO.Path.GetFileName(path)).FirstOrDefault() is { } hit)
                return hit;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return path;
    }

    private static IFeature? Create(IFeatureManager fm, object data, string what, List<string> attempts)
    {
        try
        {
            var f = fm.CreateFeature(data);
            if (f is null) attempts.Add($"CreateFeature({what}) returned nothing");
            return f;
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            attempts.Add($"CreateFeature({what}): {e.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ verification (Tier B: the placements)

    private static string? CheckLinear(List<Placement> seeds, List<Placement> actual, PatternRef dir1, PatternRef? dir2, PatternSpec spec,
        bool flip1, bool flip2, List<string> checks, out (bool, bool)? redo)
    {
        redo = null;
        // Which sense SOLIDWORKS ran each direction: the one whose instances all land where expected.
        var senses = new List<(int S1, int S2, int Matched)>();
        foreach (int s1 in new[] { 1, -1 })
            foreach (int s2 in dir2 is null ? new[] { 1 } : [1, -1])
            {
                var exp = Linear(seeds, dir1.U * s1, spec.S1, spec.N1, dir2 is null ? null : dir2.U * s2, spec.S2, spec.N2);
                int matched = Match(actual, exp.Select(e => e.P).ToList(), PosTol, RotTol).Count(i => i >= 0);
                senses.Add((s1, s2, matched));
            }
        var best = senses.OrderByDescending(s => s.Matched).ThenByDescending(s => s.S1).ThenByDescending(s => s.S2).First();
        if (best.Matched < actual.Count)
            return $"{actual.Count - best.Matched} of {actual.Count} instance(s) are not at the seed moved by i·{Units.Mm(spec.S1)} mm along ±[{Dir(dir1.U)}]" +
                   (dir2 is null ? "" : $" and j·{Units.Mm(spec.S2)} mm along ±[{Dir(dir2.U)}]") + " with the seed's orientation";
        bool wrong1 = dir1.SignGiven && best.S1 < 0 && !senses.Any(s => s.S1 > 0 && s.S2 == best.S2 && s.Matched == best.Matched);
        bool wrong2 = dir2 is { SignGiven: true } && best.S2 < 0 && !senses.Any(s => s.S2 > 0 && s.S1 == best.S1 && s.Matched == best.Matched);
        if (wrong1 || wrong2)
        {
            redo = (wrong1 ? !flip1 : flip1, wrong2 ? !flip2 : flip2); // the flips that give the requested senses
            return null;
        }
        var u1 = dir1.U * best.S1;
        checks.Add($"every instance at the seed moved by i x {Units.Mm(spec.S1)} mm along [{Dir(u1)}]" +
                   (dir2 is null ? "" : $" and j x {Units.Mm(spec.S2)} mm along [{Dir(dir2.U * best.S2)}]") +
                   $" ({(dir2 is null ? $"{spec.N1} instances" : $"{spec.N1} x {spec.N2} grid")}), seed orientation kept (within {Units.Mm(PosTol)} mm)");
        return null;
    }

    private static string? CheckCircular(List<Placement> seeds, List<Placement> actual, PatternRef axis, PatternSpec spec, List<string> checks)
    {
        var senses = new List<(int S, int Matched)>();
        foreach (int s in new[] { 1, -1 })
        {
            var exp = Circular(seeds, axis.P, axis.U * s, spec.Step, spec.N1);
            senses.Add((s, Match(actual, exp.Select(e => e.P).ToList(), PosTol, RotTol).Count(i => i >= 0)));
        }
        var best = senses.OrderByDescending(s => s.Matched).First();
        if (best.Matched < actual.Count)
            return $"{actual.Count - best.Matched} of {actual.Count} instance(s) are not the seed turned by k x {Units.Deg(spec.Step)}° about " +
                   $"[{Dir(axis.U)}] through [{Pt(axis.P)}]";
        bool full = spec.Equal && spec.TotalRad >= 2 * Math.PI - 1e-9;
        string sense = full || senses[0].Matched == senses[1].Matched ? "" : best.S > 0 ? ", counter-clockwise" : ", clockwise";
        checks.Add($"every instance is the seed turned by k x {Units.Deg(spec.Step)}° about [{Dir(axis.U)}] through [{Pt(axis.P)}]{sense} " +
                   $"({spec.N1} instances, within {Units.Mm(PosTol)} mm)");
        return null;
    }

    /// <summary>
    /// A mirrored instance sits where the seed's mirror image would: the centre of its part's bounding box is the
    /// reflection of the seed's, and each of its axes is the reflection of the seed's (up to sign: a rigid copy).
    /// </summary>
    private static string? CheckMirror(SwContext ctx, List<IComponent2> seeds, List<Frame> seedFrames, List<IComponent2> fresh, PatternRef plane,
        List<string> checks)
    {
        var mu = ctx.Math;
        var left = fresh.ToList();
        var signsSeen = new HashSet<string>();
        for (int k = 0; k < seeds.Count; k++)
        {
            var center = PartBoxCenter(seeds[k]);
            if (center is not Vec3 cp) return $"the bounding box of '{seeds[k].Name2}' could not be read";
            var seedCenter = mu.AsmMapPoint(seeds[k].Transform2, cp);
            var want = Reflect(seedCenter, plane.P, plane.U);
            IComponent2? hit = null;
            int[]? signs = null;
            foreach (var c in left.Where(c => SamePath(CompPath(c), CompPath(seeds[k]))))
            {
                var at = mu.AsmMapPoint(c.Transform2, cp);
                if (at.DistanceTo(want) > PosTol * 10) continue;
                var s = MirrorSigns(seedFrames[k].R, FrameOf(ctx, c).R, plane.U, AxisTol * 10);
                if (s is null) continue;
                hit = c;
                signs = s;
                break;
            }
            if (hit is null)
                return $"no new instance of '{seeds[k].Name2}' has its box centre at the reflection [{Pt(want)}] with mirrored axes";
            left.Remove(hit);
            signsSeen.Add(string.Join("", signs!.Select(s => s > 0 ? '+' : '-')));
        }
        checks.Add($"each copy's bounding-box centre is the reflection of its seed's about the plane through [{Pt(plane.P)}] with normal " +
                   $"[{Dir(plane.U)}], and its axes are the mirrored seed axes (signs x, y, z: {string.Join(", ", signsSeen)})");
        return null;
    }

    /// <summary>The centre of the component's own (part-space) bounding box, or null.</summary>
    private static Vec3? PartBoxCenter(IComponent2 c)
    {
        var md = c.AsmModelDoc();
        double[] box = md switch
        {
            IPartDoc p => Safe(() => ComArgs.Doubles((object?)p.GetPartBox(true)), Array.Empty<double>()),
            IAssemblyDoc a => Safe(() => ComArgs.Doubles((object?)a.GetBox(0)), Array.Empty<double>()),
            _ => [],
        };
        return box.Length == 6 ? new Vec3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2) : null;
    }
}
