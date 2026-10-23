using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs

/// <summary>
/// body_move: a rotation about an axis through a point (applied before the translation). The tool parameter's
/// description explains the fields, so they carry none (it keeps tools/list small).
/// </summary>
public sealed class BodyRotation
{
    /// <summary>Axis direction [x, y, z], e.g. [0, 1, 0].</summary>
    public double[]? Axis { get; set; }

    /// <summary>A point on the axis, mm (default: the origin).</summary>
    public double[]? Point { get; set; }

    /// <summary>Angle in degrees, right-hand rule about the axis.</summary>
    public double AngleDeg { get; set; }
}

/// <summary>Result of the bodies and surfaces tools: the feature, the verification evidence and the bodies it made or changed.</summary>
public sealed class BodyResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
    public required Verified Verified { get; init; }
    public required ModelState Model { get; init; }

    /// <summary>The bodies the call produced or changed, as body_list reports them (the ids work as {id}).</summary>
    public IReadOnlyList<BodyRow>? Bodies { get; init; }

    /// <summary>Bodies that no longer exist after the call (deleted, or merged into another).</summary>
    public IReadOnlyList<string>? RemovedBodies { get; init; }

    /// <summary>The API variant that produced the result, where several are tried (unverified recipes).</summary>
    public string? Method { get; init; }

    /// <summary>surface_knit: whether the knit formed a solid.</summary>
    public bool? SolidFormed { get; init; }

    public string? ConsumedSketch { get; init; }
    public double[]? DirectionModel { get; init; }
    public IReadOnlyList<FaceInfo>? NewFaces { get; init; }
    public int? NewFaceCount { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>body_move options.</summary>
public sealed record BodyMoveRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<EntityRef>? Bodies { get; init; }
    /// <summary>Translation [x, y, z] in mm (applied after the rotation).</summary>
    public double[]? TranslateMm { get; init; }
    public BodyRotation? Rotate { get; init; }
    /// <summary>0 = move; n = keep the bodies and make n copies, copy k moved k times.</summary>
    public int Copies { get; init; }
    public bool KeepOnFailure { get; init; }
}

// ====================================================================== service

/// <summary>
/// Multibody operations (DESIGN §4.7): move/copy, delete/keep, combine and split. Every call is a feature checked by
/// <see cref="FeatureVerifier"/> (Tier A) plus the bodies themselves (Tier B): where each moved body's vertices went, which
/// bodies are gone, the combined volume against the inputs' volumes, and the pieces of a split against the original.
/// Failures roll back. Verified recipes: move/copy (translation), delete, combine subtract; the rest are best guesses
/// that try several API variants and report the one that worked.
/// </summary>
public sealed class BodyService(SwSession session)
{
    private static readonly FeatureSpec MoveSpec = new("body_move", ["MoveCopyBody", "MoveCopyBody2", "BodyMoveCopy"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec DeleteSpec = new("body_delete", ["BodyDelete", "DeleteBody", "BodyDeleteKeep"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec CombineSpec = new("body_combine", ["Combine", "CombineBodies", "BodyCombine"], VolumeRule.Any, RequireFaces: false);
    private static readonly FeatureSpec SplitSpec = new("body_split", ["Split", "SplitBody", "BodySplit"], VolumeRule.Any, RequireFaces: false);

    private const string MoveHint = "Check the bodies (body_list) and the motion; a rotation needs a non-zero axis and angle.";

    private TimeSpan Long => TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120));

    // ================================================================== body_move

    public Task<BodyResult> MoveAsync(BodyMoveRequest q, CancellationToken ct = default) =>
        session.RunAsync("body_move", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            var want = WantedMotion(q, out string what);
            int copies = q.Copies;
            if (copies < 0 || copies > 50) throw SwException.InvalidArgument($"copies must be between 0 (move) and 50 (got {copies}).");
            if (copies > 0 && want.Rotates() && want.T.Length > 0)
                throw SwException.InvalidArgument("copies with both translateMm and rotate is not supported.",
                    "Copy with one motion (translateMm or rotate), then move the copies in a second call.");
            BodyKit.NoOpenSketch(d);
            var targets = BodyKit.ResolveBodies(ctx, d, q.Bodies, "bodies", BodyKind.Any);
            var geoms = targets.Select(t => BodyKit.Geom(ctx, d, (IBody2)t.Obj, withVertices: true)).ToList(); // where they are now
            var resolved = targets.Select(t => t.Info("bodies")).ToList();

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            // The argument reading (angle order, sense, translation order) is checked against the result; a different
            // reading is detected from where the bodies went, and the move is redone with arguments computed for it.
            var used = MoveMath.Assumed;
            var tried = new List<MoveConvention>();
            while (true)
            {
                tried.Add(used);
                var sent = MoveMath.ParamsFor(want, used);
                var state = new MoveState();
                d.ClearSelection2(true);
                BodyKit.SelectAll(d, targets, 1);
                object? api = d.FeatureManager.InsertMoveCopyBody2(sent.Translation.X, sent.Translation.Y, sent.Translation.Z, 0,
                    sent.Point.X, sent.Point.Y, sent.Point.Z, sent.Angles.X, sent.Angles.Y, sent.Angles.Z, copies > 0, Math.Max(1, copies));
                d.ClearSelection2(true);
                try
                {
                    var current = used;
                    var o = BodyKit.Verify(ctx, d, before, MoveSpec,
                        (_, _, checks) => MoveIntent(ctx, part, d, geoms, before.Bodies, want, sent, current, copies, what, state, checks),
                        q.KeepOnFailure, api, MoveHint, guessedType: true);
                    if (used != MoveMath.Assumed) o = o with { Checks = [.. o.Checks, $"redone with the arguments read as: {used}"] };
                    return BodyKit.Result(ctx, d, o, state.Bodies, resolved, method: used == MoveMath.Assumed ? null : $"InsertMoveCopyBody2 ({used})");
                }
                catch (SwException) when (!q.KeepOnFailure && state.Explained is MoveConvention e && !tried.Contains(e) && BodyKit.NothingNew(d, before))
                {
                    used = e;
                }
            }
        }, RunOptions.Write(q.Doc) with { Timeout = Long }, ct);

    private sealed class MoveState
    {
        public MoveConvention? Explained { get; set; }
        public List<BodyGeom> Bodies { get; set; } = [];
    }

    /// <summary>The requested motion: rotate (about the axis through the point), then translate.</summary>
    private static Motion WantedMotion(BodyMoveRequest q, out string what)
    {
        var t = q.TranslateMm is null ? Vec3.Zero : Units.PointM(q.TranslateMm, "translateMm");
        var parts = new List<string>();
        if (t.Length > 0) parts.Add($"translate [{BodyKit.Pt(t)}] mm");
        var m = Motion.Translate(t);
        if (q.Rotate is { } r)
        {
            if (r.Axis is not { Length: 3 } a || a.Any(c => !double.IsFinite(c)))
                throw SwException.InvalidArgument("'rotate.axis' must be a direction [x, y, z], e.g. [0, 1, 0].");
            var axis = new Vec3(a[0], a[1], a[2]);
            if (axis.Length < 1e-9) throw SwException.InvalidArgument("'rotate.axis' must not be [0, 0, 0].");
            if (!double.IsFinite(r.AngleDeg) || r.AngleDeg == 0 || Math.Abs(r.AngleDeg) >= 360)
                throw SwException.InvalidArgument($"'rotate.angleDeg' must be non-zero and between -360 and 360 (got {r.AngleDeg}).");
            var c = r.Point is null ? Vec3.Zero : Units.PointM(r.Point, "rotate.point");
            m = new Motion(Rot3.AxisAngle(axis, Units.Rad(r.AngleDeg)), c, t);
            parts.Insert(0, $"rotate {r.AngleDeg}° about [{BodyKit.Dir(axis)}] through [{BodyKit.Pt(c)}] mm");
        }
        if (parts.Count == 0)
            throw SwException.InvalidArgument("Pass translateMm [x, y, z] and/or rotate {axis, point, angleDeg}; a zero move does nothing.",
                "E.g. translateMm:[10,0,0], or rotate:{axis:[0,1,0], angleDeg:90}.");
        what = string.Join(", then ", parts);
        return m;
    }

    /// <summary>
    /// Tier B: every moved body (and each copy) is found where the motion puts it: its sampled vertices and its center of
    /// mass, on a body of the same volume and area; the body count grew by the copies. When the bodies went elsewhere,
    /// the reading of the arguments that explains it is recorded for a redo.
    /// </summary>
    private static List<string> MoveIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, List<BodyGeom> targets, int bodiesBefore,
        Motion want, MoveParams sent, MoveConvention used, int copies, string what, MoveState state, List<string> checks)
    {
        var after = BodyKit.AllGeoms(ctx, part, d);
        var problems = new List<string>();
        int expected = bodiesBefore + copies * targets.Count;
        if (after.Count != expected) problems.Add($"the part has {after.Count} bodies, expected {expected}");
        var matched = new List<BodyGeom>();
        var missing = Placement(targets, after, want, copies, matched);
        if (missing.Count == 0 && problems.Count == 0)
        {
            state.Bodies = matched;
            int n = targets.Count;
            checks.Add(copies > 0
                ? $"{copies} cop{(copies == 1 ? "y" : "ies")} of {n} bod{(n == 1 ? "y" : "ies")} at the expected places ({what}, copy k moved k times); the originals stay"
                : $"{n} bod{(n == 1 ? "y" : "ies")} at the expected place ({what}): sampled vertices and center of mass");
            if (copies == 0 && !want.Rotates() && targets.Count == 1 && matched.Count == 1 &&
                targets[0].Box is { Length: 6 } b0 && matched[0].Box is { Length: 6 } b1)
                checks.Add($"box shifted by [{BodyKit.Pt(new Vec3(b1[0] - b0[0], b1[1] - b0[1], b1[2] - b0[2]))}] mm");
            return [];
        }
        foreach (var c in MoveMath.All)
        {
            if (c == used) continue;
            if (Placement(targets, after, MoveMath.Applied(sent, c), copies, null).Count == 0)
            {
                state.Explained = c;
                break;
            }
        }
        problems.AddRange(missing.Take(3));
        if (state.Explained is { } e) problems.Add($"the result matches the arguments read as: {e}");
        return problems;
    }

    /// <summary>For each target and instance (copies: the original and copies 1..n; a move: the moved body), the body holding it.</summary>
    private static List<string> Placement(List<BodyGeom> targets, List<BodyGeom> after, Motion m, int copies, List<BodyGeom>? matched)
    {
        var missing = new List<string>();
        int k0 = copies > 0 ? 0 : 1, k1 = copies > 0 ? copies : 1;
        foreach (var t in targets)
        {
            var sample = MoveMath.Sample(t.Vertices, 24);
            for (int k = k0; k <= k1; k++)
            {
                var mk = m.Power(k);
                var center = mk.Apply(t.Center);
                var hit = after.FirstOrDefault(a => a.Solid == t.Solid && BodyKit.SameSize(a, t) && a.Center.DistanceTo(center) <= BodyKit.CenterTol &&
                                                    (sample.Count == 0 || MoveMath.AllNear(sample.Select(mk.Apply), a.Vertices, BodyKit.VertexTol)));
                if (hit is not null) matched?.Add(hit);
                else missing.Add($"{t.Label}{(copies > 0 ? (k == 0 ? " (original)" : $" copy {k}") : "")} is not at its expected place (center of mass [{BodyKit.Pt(center)}] mm)");
            }
        }
        return missing;
    }

    // ================================================================== body_delete

    public Task<BodyResult> DeleteAsync(string? doc, IReadOnlyList<EntityRef>? bodies, string? mode, bool keepOnFailure, CancellationToken ct = default) =>
        session.RunAsync("body_delete", ctx =>
        {
            string m = BodyKit.Norm(mode) switch
            {
                null or "delete" => "delete",
                "keep_only" or "keep" or "keeponly" => "keep_only",
                _ => throw SwException.InvalidArgument($"Unknown mode '{mode}'.", "Use delete (remove the listed bodies) or keep_only (keep them, delete all others)."),
            };
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var targets = BodyKit.ResolveBodies(ctx, d, bodies, "bodies", BodyKind.Any);
            var all = BodyKit.AllGeoms(ctx, part, d);
            var ids = targets.Select(t => t.Id).ToHashSet();
            var removed = all.Where(g => ids.Contains(g.Id) == (m == "delete")).ToList();
            var kept = all.Where(g => !removed.Contains(g)).ToList();
            if (removed.Count == 0)
                throw SwException.InvalidArgument("keep_only with every body listed deletes nothing.", "List only the bodies to keep, or use mode delete.");

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            d.ClearSelection2(true);
            BodyKit.SelectAll(d, targets, 0);
            object? api = d.FeatureManager.InsertDeleteBody2(m == "keep_only");
            d.ClearSelection2(true);
            var left = new List<BodyGeom>();
            var o = BodyKit.Verify(ctx, d, before, DeleteSpec, (_, _, checks) => DeleteIntent(ctx, part, d, removed, kept, left, checks),
                keepOnFailure, api, "Pass bodies from body_list; keep_only needs at least one body that is not listed.", guessedType: true);
            return BodyKit.Result(ctx, d, o, left, targets.Select(t => t.Info("bodies")).ToList(), removed: removed.Select(r => r.Name).ToList());
        }, RunOptions.Write(doc), ct);

    /// <summary>Tier B: exactly the expected bodies are gone, the others are untouched (identity, volume and area).</summary>
    private static List<string> DeleteIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, List<BodyGeom> removed, List<BodyGeom> kept,
        List<BodyGeom> left, List<string> checks)
    {
        var after = BodyKit.AllGeoms(ctx, part, d);
        var problems = new List<string>();
        if (after.Count != kept.Count) problems.Add($"the part has {after.Count} bodies, expected {kept.Count}");
        foreach (var r in removed)
            if (after.Any(a => a.Id == r.Id)) problems.Add($"{r.Label} is still there");
        foreach (var k in kept)
            if (!after.Any(a => BodyKit.SameSize(a, k) && (a.Id == k.Id || BodyKit.SameBox(a, k)))) problems.Add($"{k.Label} changed or disappeared");
        if (problems.Count > 0) return problems;
        left.AddRange(after);
        checks.Add($"removed {removed.Count} bod{(removed.Count == 1 ? "y" : "ies")} ({string.Join(", ", removed.Take(6).Select(r => r.Name))}); " +
                   $"{kept.Count} left unchanged");
        return problems;
    }

    // ================================================================== body_combine

    public Task<BodyResult> CombineAsync(string? doc, EntityRef? main, IReadOnlyList<EntityRef>? tools, string? operation, bool keepOnFailure,
        CancellationToken ct = default) =>
        session.RunAsync("body_combine", ctx =>
        {
            string op = BodyKit.Norm(operation) switch
            {
                "add" or "union" => "add",
                "subtract" or "cut" or "difference" => "subtract",
                "common" or "intersect" or "intersection" => "common",
                null => throw SwException.InvalidArgument("'operation' is required: add, subtract or common."),
                _ => throw SwException.InvalidArgument($"Unknown operation '{operation}'.", "Use add, subtract or common."),
            };
            int type = op switch
            {
                "add" => (int)swBodyOperationType_e.SWBODYADD,
                "subtract" => (int)swBodyOperationType_e.SWBODYCUT,
                _ => (int)swBodyOperationType_e.SWBODYINTERSECT,
            };
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var mainRef = BodyKit.ResolveBody(ctx, d, main, "main", BodyKind.Solid);
            var toolRefs = BodyKit.ResolveBodies(ctx, d, tools, "tools", BodyKind.Solid);
            if (toolRefs.Any(t => t.Id == mainRef.Id))
                throw SwException.InvalidArgument($"{mainRef.Label} is both 'main' and one of the 'tools'.", "List the other bodies as tools.");
            var solids = BodyKit.SolidGeoms(ctx, part, d);
            var mg = solids.First(s => s.Id == mainRef.Id);
            var tg = toolRefs.Select(t => solids.First(s => s.Id == t.Id)).ToList();
            var others = solids.Where(s => s.Id != mg.Id && !tg.Contains(s)).ToList();
            var resolved = new List<ResolvedInfo> { mainRef.Info("main") };
            resolved.AddRange(toolRefs.Select(t => t.Info("tools")));

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var tried = new List<string>();
            foreach (var (label, run) in CombineAttempts(ctx, part, d, before, op, type, mainRef, toolRefs))
            {
                d.ClearSelection2(true);
                object? api;
                try
                {
                    api = run();
                }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is COMException or InvalidCastException or ArgumentException or SwException)
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 120)}");
                    BodyKit.CleanUp(ctx, d, before);
                    continue;
                }
                finally
                {
                    d.ClearSelection2(true);
                }
                if (BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: nothing created");
                    continue;
                }
                var result = new List<BodyGeom>();
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, CombineSpec, (_, _, checks) => CombineIntent(ctx, part, d, op, mg, tg, others, result, checks),
                        keepOnFailure, api, CombineHint(op), guessedType: true);
                    if (tried.Count > 0) o = o with { Checks = [.. o.Checks, $"earlier variants: {BodyKit.Tried(tried)}"] };
                    return BodyKit.Result(ctx, d, o, result, resolved, method: label, removed: tg.Select(t => t.Name).ToList());
                }
                catch (SwException ex) when (!keepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 200)}");
                }
            }
            throw new SwException(ErrorCodes.FeatureFailed,
                $"body_combine ({op}): no API variant produced a verified result. Tried: {BodyKit.Tried(tried)}.", CombineHint(op));
        }, RunOptions.Write(doc) with { Timeout = Long }, ct);

    private static string CombineHint(string op) => op switch
    {
        "subtract" => "The tool bodies must overlap the main body (body_list shows boxes and volumes).",
        "common" => "The bodies must overlap; the result is their shared volume.",
        _ => "The bodies must overlap or touch (body_list shows their boxes).",
    };

    /// <summary>
    /// The InsertCombineFeature variants, in order. Subtract: the VERIFIED recipe first (main on mark 1, tools on mark 2,
    /// no body arguments; VBA's Nothing, then VT_EMPTY). Add and common are unresolved: from VBA, marks 2/2, 1/1, 1/2 and
    /// VARIANT arrays (main null + both bodies, main = body) all returned null. So typed VT_DISPATCH arrays come first,
    /// then the mark variants again (now from .NET), every body on mark 0, the obsolete IPartDoc member, and last the
    /// definition route: IFeatureManager.CreateDefinition has no combine type (swFeatureNameID_e lists none), so a
    /// verified subtract is created and redefined through ICombineBodiesFeatureData.
    /// </summary>
    private static List<(string Label, Func<object?> Run)> CombineAttempts(SwContext ctx, IPartDoc part, IModelDoc2 d, Snapshot before, string op,
        int type, Resolved main, List<Resolved> tools)
    {
        var fm = d.FeatureManager;
        var all = new List<Resolved> { main };
        all.AddRange(tools);
        object Array(IEnumerable<Resolved> rs) => ComArgs.DispatchArray(rs.Select(r => r.Obj));
        object? Marks(int mainMark, int toolMark, object? toolArg)
        {
            BodyKit.SelectAll(d, [main], mainMark);
            BodyKit.SelectAll(d, tools, toolMark);
            return fm.InsertCombineFeature(type, null, toolArg);
        }

        if (op == "subtract")
        {
            return
            [
                ("main on mark 1, tools on mark 2", () => Marks(1, 2, ComArgs.Nothing)),
                ("main on mark 1, tools on mark 2, VT_EMPTY tool argument", () => Marks(1, 2, ComArgs.Empty)),
                ("main body + typed array of the tools", () => fm.InsertCombineFeature(type, (Body2)main.Obj, Array(tools))),
            ];
        }
        return
        [
            ("typed array of all bodies, no main body", () => fm.InsertCombineFeature(type, null, Array(all))),
            ("main body + typed array of the tools", () => fm.InsertCombineFeature(type, (Body2)main.Obj, Array(tools))),
            ("all bodies on mark 2", () => Marks(2, 2, ComArgs.Nothing)),
            ("main on mark 1, tools on mark 2", () => Marks(1, 2, ComArgs.Nothing)),
            ("all bodies on mark 0", () => Marks(0, 0, ComArgs.Nothing)),
            ("IPartDoc.InsertCombineFeature, all bodies on mark 2", () =>
            {
                BodyKit.SelectAll(d, all, 2);
                return part.InsertCombineFeature(type, ComArgs.Nothing, ComArgs.Nothing) ? true : null;
            }),
            ("verified subtract redefined via ICombineBodiesFeatureData", () => Redefined(ctx, d, before, type, main, tools)),
        ];
    }

    /// <summary>
    /// The definition route: the verified subtract (main on mark 1, tools on mark 2), then its ICombineBodiesFeatureData
    /// gets the wanted operation and every body as a body to combine, applied with ModifyDefinition (unverified).
    /// </summary>
    private static object? Redefined(SwContext ctx, IModelDoc2 d, Snapshot before, int type, Resolved main, List<Resolved> tools)
    {
        BodyKit.SelectAll(d, [main], 1);
        BodyKit.SelectAll(d, tools, 2);
        var f = d.FeatureManager.InsertCombineFeature((int)swBodyOperationType_e.SWBODYCUT, null, ComArgs.Nothing);
        d.ClearSelection2(true);
        if (f is null) return null;
        var data = f.BdCombineData();
        if (data is null)
        {
            string rb = FeatureVerifier.Rollback(ctx, d, before);
            throw new SwException(ErrorCodes.FeatureFailed, $"'{f.Name}' has no ICombineBodiesFeatureData. {rb}");
        }
        if (!data.AccessSelections((ModelDoc2)d, null))
        {
            string rb = FeatureVerifier.Rollback(ctx, d, before);
            throw new SwException(ErrorCodes.FeatureFailed, $"AccessSelections on '{f.Name}' was refused. {rb}");
        }
        bool ok = false;
        try
        {
            var bodies = new List<object>();
            if (data.MainBody is IBody2 mb) bodies.Add(mb);
            bodies.AddRange(data.BdBodiesToCombine());
            if (bodies.Count < 2) bodies = [main.Obj, .. tools.Select(t => t.Obj)];
            data.OperationType = type;
            data.BodiesToCombine = ComArgs.DispatchArray(bodies);
            ok = f.ModifyDefinition(data, d, null);
        }
        finally
        {
            if (!ok) data.ReleaseSelectionAccess();
        }
        if (!ok)
        {
            string rb = FeatureVerifier.Rollback(ctx, d, before);
            throw new SwException(ErrorCodes.FeatureFailed, $"ModifyDefinition refused the new operation on '{f.Name}'. {rb}");
        }
        return f;
    }

    /// <summary>
    /// Tier B from the bodies' own volumes (IBody2.GetMassProperties): add = one body (per touching group) between the
    /// largest input and the sum of the inputs; subtract = the main body lost between 0 (exclusive) and the tools' volume;
    /// common = a body no larger than the smallest input. Bodies that took no part are unchanged.
    /// </summary>
    private static List<string> CombineIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, string op, BodyGeom main, List<BodyGeom> tools,
        List<BodyGeom> others, List<BodyGeom> result, List<string> checks)
    {
        var solids = BodyKit.SolidGeoms(ctx, part, d);
        var problems = new List<string>();
        int pieces = solids.Count - others.Count;
        double vr = solids.Sum(s => s.Volume) - others.Sum(o => o.Volume);
        double vm = main.Volume, vt = tools.Sum(t => t.Volume);
        double vmax = Math.Max(vm, tools.Max(t => t.Volume)), vmin = Math.Min(vm, tools.Min(t => t.Volume));
        double eps = 1e-13 + 1e-7 * (vm + vt);
        foreach (var o in others)
            if (!solids.Any(s => BodyKit.SameSize(s, o) && (s.Id == o.Id || BodyKit.SameBox(s, o)))) problems.Add($"{o.Label} took no part but changed");
        switch (op)
        {
            case "add":
                if (pieces <= 0) problems.Add("no body is left");
                else if (pieces > tools.Count) problems.Add($"the bodies were not merged ({pieces} bodies left of {tools.Count + 1})");
                else if (vr > vm + vt + eps || vr < vmax - eps)
                    problems.Add($"the result holds {Units.Mm3(vr)} mm³, outside [{Units.Mm3(vmax)}, {Units.Mm3(vm + vt)}] mm³");
                else
                    checks.Add($"union {Units.Mm3(vr)} mm³ = {Units.Mm3(vm)} + {Units.Mm3(vt)} − overlap {Units.Mm3(Math.Max(0, vm + vt - vr))} mm³" +
                               (pieces > 1 ? $" ({pieces} separate bodies)" : ""));
                break;
            case "subtract":
                if (pieces <= 0) problems.Add("nothing of the main body is left");
                else if (vr > vm + eps) problems.Add($"the result holds {Units.Mm3(vr)} mm³, more than the main body's {Units.Mm3(vm)} mm³ (tools not consumed?)");
                else if (vr < vm - vt - eps) problems.Add($"{Units.Mm3(vm - vr)} mm³ were removed, more than the tools hold ({Units.Mm3(vt)} mm³)");
                else if (vr > vm - eps) problems.Add("no material was removed: the tools don't overlap the main body");
                else
                    checks.Add($"removed {Units.Mm3(vm - vr)} mm³ (the overlap) from {main.Label}: {Units.Mm3(vm)} → {Units.Mm3(vr)} mm³" +
                               (pieces > 1 ? $", now {pieces} pieces" : ""));
                break;
            default:
                if (pieces <= 0 || vr <= eps) problems.Add("no common volume: the bodies don't overlap");
                else if (vr > vmin + eps) problems.Add($"the result ({Units.Mm3(vr)} mm³) is larger than the smallest input ({Units.Mm3(vmin)} mm³)");
                else checks.Add($"common volume {Units.Mm3(vr)} mm³ (inputs {Units.Mm3(vm)} and {Units.Mm3(vt)} mm³)");
                break;
        }
        if (problems.Count == 0)
            result.AddRange(solids.Where(s => !others.Any(o => o.Id == s.Id && BodyKit.SameSize(o, s))));
        return problems;
    }

    // ================================================================== body_split

    public Task<BodyResult> SplitAsync(string? doc, EntityRef? body, EntityRef? tool, bool keepOnFailure, CancellationToken ct = default) =>
        session.RunAsync("body_split", ctx =>
        {
            if (tool is null)
                throw SwException.InvalidArgument("'tool' is required: a plane {name:'right'}, a planar face {at:[x,y,z]} or a sketch {name:'Sketch2'}.");
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            BodyKit.NoOpenSketch(d);
            var toolRef = EntityResolver.Resolve(ctx, d, tool, "tool", EntityKind.Plane, EntityKind.Face, EntityKind.Sketch);
            var target = body is null ? null : BodyKit.ResolveBody(ctx, d, body, "body", BodyKind.Solid);
            var solids = BodyKit.SolidGeoms(ctx, part, d);
            if (solids.Count == 0) throw SwException.InvalidArgument("The part has no solid body to split.");
            var targets = target is null ? solids : solids.Where(s => s.Id == target.Id).ToList();
            if (toolRef.Obj is IFace2 tf && Topology.SurfaceKind(tf.Surface()) != "plane")
                throw SwException.InvalidArgument($"{toolRef.Label} is not planar; split with a plane, a planar face or a sketch.");
            if (BodyKit.PlaneOf(toolRef) is { } plane && !targets.Any(t => BodyKit.Crosses(t.Box, plane.Q, plane.N)))
                throw SwException.InvalidArgument($"{toolRef.Label} does not pass through {(target is null ? "any solid body" : target.Label)}.",
                    "Pick a plane that cuts the body, e.g. {name:'right'} (x = 0) through a body centered on the origin.");
            var resolved = new List<ResolvedInfo> { toolRef.Info("tool") };
            if (target is not null) resolved.Add(target.Info("body"));

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var tried = new List<string>();
            // PreSplitBody2 / PostSplitBody2 (unverified): the trim tool selected, then the bodies PreSplitBody2 returns are
            // passed back to PostSplitBody2 (kept in the part, no files saved). Marks and the target-body selection are
            // best guesses; the older PreSplitBody / PostSplitBody pair is the last resort.
            var variants = new List<(string Label, int ToolMark, bool SelectBody, bool Legacy)>
            {
                ("tool on mark 0", 0, target is not null, false),
                ("tool on mark 1", 1, target is not null, false),
            };
            if (target is not null) variants.Add(("tool on mark 0, body not selected", 0, false, false));
            variants.Add(("PreSplitBody/PostSplitBody, tool on mark 0", 0, target is not null, true));
            foreach (var (label, toolMark, selectBody, legacy) in variants)
            {
                IFeature? feat = null;
                string? why = null;
                string pre = legacy ? "PreSplitBody" : "PreSplitBody2", post = legacy ? "PostSplitBody" : "PostSplitBody2";
                try
                {
                    d.ClearSelection2(true);
                    BodyKit.SelectAll(d, [toolRef], toolMark);
                    if (selectBody) BodyKit.SelectAll(d, [target!], 2);
                    var fm = d.FeatureManager;
                    var pieces = legacy ? fm.BdPreSplitLegacy() : fm.BdPreSplit();
                    if (pieces.Count == 0) why = $"{pre} returned no bodies";
                    else
                    {
                        var arr = ComArgs.DispatchArray(pieces);
                        var noPaths = Enumerable.Repeat("", pieces.Count).ToArray(); // "" = don't save that body to a file
                        IFeature? Post(object? origins, object? paths)
                        {
                            try { return legacy ? fm.BdPostSplitLegacy(arr, origins, paths) : fm.BdPostSplit(arr, origins, paths); }
                            catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is COMException or InvalidCastException or ArgumentException)
                            {
                                why = BodyKit.Clip(ex.Message, 120);
                                return null;
                            }
                        }
                        // No origins / save paths as VT_EMPTY first, then VT_DISPATCH null origins with an array of empty paths.
                        feat = Post(ComArgs.Empty, ComArgs.Empty) ?? (BodyKit.NothingNew(d, before) ? Post(ComArgs.Nothing, noPaths) : null);
                        if (feat is null) why ??= $"{post} created nothing ({pieces.Count} bodies from {pre})";
                    }
                }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex) && ex is COMException or InvalidCastException or ArgumentException or SwException)
                {
                    why = BodyKit.Clip(ex.Message, 120);
                }
                finally
                {
                    d.ClearSelection2(true);
                }
                if (BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {why ?? "nothing created"}");
                    continue;
                }
                var pieceGeoms = new List<BodyGeom>();
                try
                {
                    var o = BodyKit.Verify(ctx, d, before, SplitSpec,
                        (_, _, checks) => SplitIntent(ctx, part, d, solids, target is null ? null : targets[0], pieceGeoms, checks),
                        keepOnFailure, feat, "Split with a plane or planar face through the body, or a sketch whose lines cross it.", guessedType: true);
                    return BodyKit.Result(ctx, d, o, pieceGeoms, resolved, method: legacy ? label : $"PreSplitBody2/PostSplitBody2 ({label})");
                }
                catch (SwException ex) when (!keepOnFailure && ex.Code is ErrorCodes.FeatureFailed or ErrorCodes.RebuildError or ErrorCodes.VerifyFailed &&
                                             BodyKit.NothingNew(d, before))
                {
                    tried.Add($"{label}: {BodyKit.Clip(ex.Message, 200)}");
                }
            }
            throw new SwException(ErrorCodes.FeatureFailed, $"body_split: SOLIDWORKS did not split the body. Tried: {BodyKit.Tried(tried)}.",
                "The tool must cut through the body: a plane or planar face through it, or a sketch whose lines cross it.");
        }, RunOptions.Write(doc) with { Timeout = Long }, ct);

    /// <summary>Tier B: more bodies than before, and the pieces of the split body (or of all of them) add up to its volume.</summary>
    private static List<string> SplitIntent(SwContext ctx, IPartDoc part, IModelDoc2 d, List<BodyGeom> before, BodyGeom? target,
        List<BodyGeom> pieces, List<string> checks)
    {
        var after = BodyKit.SolidGeoms(ctx, part, d);
        var problems = new List<string>();
        if (after.Count <= before.Count) return [$"still {after.Count} solid bod{(after.Count == 1 ? "y" : "ies")}: nothing was split"];
        List<BodyGeom> untouched = target is null ? [] : before.Where(b => b.Id != target.Id).ToList();
        foreach (var u in untouched)
            if (!after.Any(a => BodyKit.SameSize(a, u) && (a.Id == u.Id || BodyKit.SameBox(a, u)))) problems.Add($"{u.Label} was split or changed too");
        var made = after.Where(a => !untouched.Any(u => u.Id == a.Id && BodyKit.SameSize(a, u))).ToList();
        double want = target?.Volume ?? before.Sum(b => b.Volume);
        double got = made.Sum(m => m.Volume);
        if (Math.Abs(got - want) > 1e-12 + 1e-6 * want)
            problems.Add($"the pieces hold {Units.Mm3(got)} mm³, not the original {Units.Mm3(want)} mm³");
        if (made.Count < 2) problems.Add($"{made.Count} piece(s) instead of at least 2");
        if (problems.Count > 0) return problems;
        pieces.AddRange(made);
        checks.Add($"{(target is null ? "the solids" : target.Label)} split into {made.Count} bodies: " +
                   $"{string.Join(" + ", made.Take(8).Select(m => Units.Mm3(m.Volume)))} = {Units.Mm3(got)} mm³");
        return problems;
    }
}

// ====================================================================== shared helpers (bodies, surfaces, sheet metal)

internal enum BodyKind
{
    Any,
    Solid,
    Surface,
}

/// <summary>One body's geometry (API units) for before/after comparisons.</summary>
internal sealed class BodyGeom
{
    public required IBody2 Body { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required bool Solid { get; init; }
    public double Volume { get; init; }     // m³ (solids; 0 for surfaces)
    public double Area { get; init; }       // m²
    public Vec3 Center { get; init; }       // m: center of mass (surfaces: of the area)
    public double[] Box { get; init; } = [];
    /// <summary>The body's vertex points (m); read on first use unless captured up front (<see cref="BodyKit.Geom"/> withVertices).</summary>
    public List<Vec3> Vertices
    {
        get => _vertices ??= BodyKit.VertexPoints(Body);
        init => _vertices = value;
    }

    private List<Vec3>? _vertices;
    public int Faces { get; init; }
    public int Edges { get; init; }
    public bool Hidden { get; init; }
    public string Label => $"body '{Name}' ({Id})";
}

/// <summary>Helpers shared by BodyService, SurfaceService and SheetMetalService.</summary>
internal static class BodyKit
{
    public const double PosTol = 2e-6;     // m: positions read back from geometry
    public const double VertexTol = 1e-6;  // m: vertices of a rigidly moved body
    public const double CenterTol = 1e-5;  // m: centers of mass (mass-property accuracy on curved bodies)

    /// <summary>Feature types that are never the main result of a body or surface tool (sketches, reference geometry, folders).</summary>
    private static readonly HashSet<string> NotMain = new(Tree.SystemTypes, StringComparer.Ordinal)
    {
        "ProfileFeature", "3DProfileFeature", "RefPlane", "RefAxis", "RefPoint", "CoordSys", "OriginProfileFeature",
    };

    /// <summary>
    /// Tier A through <see cref="FeatureVerifier.Check"/>, plus the tool's hint on errors without one. With
    /// <paramref name="guessedType"/> the expected type names are best guesses: when no new feature has one of them, the
    /// newest non-sketch feature's own type is accepted with a warning (the Tier-B geometry checks decide), so a wrong
    /// guess of a type name can't roll back a correct result.
    /// </summary>
    public static FeatureOutcome Verify(SwContext ctx, IModelDoc2 d, Snapshot before, FeatureSpec spec,
        Func<IFeature, Snapshot, List<string>, IEnumerable<string>>? intent, bool keep, object? api, string hint, bool guessedType = false)
    {
        if (guessedType) spec = AcceptActualType(ctx, d, before, spec);
        try
        {
            return FeatureVerifier.Check(ctx, d, before, spec, intent, keep, api);
        }
        catch (SwException ex) when (ex.Hint is null)
        {
            throw new SwException(ex.Code, ex.Message, hint, ex);
        }
    }

    private static FeatureSpec AcceptActualType(SwContext ctx, IModelDoc2 d, Snapshot before, FeatureSpec spec)
    {
        var created = NewFeatures(d, before);
        if (created.Count == 0 || created.Any(f => spec.Types.Contains(f.GetTypeName2()))) return spec;
        var main = created.FirstOrDefault(f => !NotMain.Contains(f.GetTypeName2()));
        if (main is null) return spec;
        string type = main.GetTypeName2();
        ctx.Warn($"'{main.Name}' is a '{type}' feature (expected {string.Join("/", spec.Types)}); accepted on the geometry checks.");
        return spec with { Types = [.. spec.Types, type] };
    }

    /// <summary>Features created since <paramref name="before"/> (sub-features included).</summary>
    public static List<IFeature> NewFeatures(IModelDoc2 d, Snapshot before) =>
        d.BdAllFeatures().Where(f =>
        {
            try { return !before.Features.ContainsKey(f.GetID()); }
            catch (COMException) { return false; }
        }).ToList();

    /// <summary>True when no feature created since <paramref name="before"/> is in the tree.</summary>
    public static bool NothingNew(IModelDoc2 d, Snapshot before) => NewFeatures(d, before).Count == 0;

    /// <summary>Removes whatever a failed attempt left behind (features and an open sketch); best effort.</summary>
    public static void CleanUp(SwContext ctx, IModelDoc2 d, Snapshot before)
    {
        try
        {
            CloseOpenSketch(d);
            if (!NothingNew(d, before)) FeatureVerifier.Rollback(ctx, d, before);
        }
        catch (Exception e) when (!ComArgs.IsDisconnect(e))
        {
            ctx.Warn($"Cleaning up a failed attempt failed ({Clip(e.Message, 120)}); check feature_list.");
        }
    }

    public static void CloseOpenSketch(IModelDoc2 d)
    {
        var sm = d.SketchManager;
        if (sm.ActiveSketch is not ISketch open) return;
        if (open.Is3D()) sm.Insert3DSketch(true);
        else sm.InsertSketch(true);
    }

    public static void NoOpenSketch(IModelDoc2 d)
    {
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
    }

    // ------------------------------------------------------------------ bodies

    private const string BodyRefHint = "A body is {id:'B1'} from body_list, {name:'Boss-Extrude1'} (its name), or {at:[x,y,z]} on one of its faces.";

    /// <summary>A list of body references; each may be an id, a name, a point on a face or a face query (de-duplicated).</summary>
    public static List<Resolved> ResolveBodies(SwContext ctx, IModelDoc2 d, IReadOnlyList<EntityRef>? refs, string arg, BodyKind kind)
    {
        if (refs is null || refs.Count == 0) throw SwException.InvalidArgument($"'{arg}' must name at least one body.", BodyRefHint);
        var list = new List<Resolved>();
        for (int i = 0; i < refs.Count; i++)
        {
            var r = refs[i] ?? throw SwException.InvalidArgument($"{arg}[{i}] is null.", BodyRefHint);
            foreach (var b in BodiesOf(ctx, d, r, $"{arg}[{i}]", kind))
                if (!list.Any(x => x.Id == b.Id)) list.Add(b);
        }
        return list;
    }

    /// <summary>Exactly one body.</summary>
    public static Resolved ResolveBody(SwContext ctx, IModelDoc2 d, EntityRef? r, string arg, BodyKind kind)
    {
        if (r is null) throw SwException.InvalidArgument($"'{arg}' is required.", BodyRefHint);
        var all = BodiesOf(ctx, d, r, arg, kind);
        if (all.Count != 1)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"'{arg}' matched {all.Count} bodies: {string.Join(", ", all.Take(6).Select(x => x.Label))}.",
                "Pass one body, e.g. its id from body_list.");
        return all[0];
    }

    private static List<Resolved> BodiesOf(SwContext ctx, IModelDoc2 d, EntityRef r, string arg, BodyKind kind)
    {
        var res = new List<Resolved>();
        string? type = Norm(r.Type);
        if (!string.IsNullOrWhiteSpace(r.Id))
        {
            var x = EntityResolver.Resolve(ctx, d, r, arg, EntityKind.Body, EntityKind.Face);
            var b = x.Obj as IBody2 ?? (x.Obj as IFace2)?.Body()
                    ?? throw new SwException(ErrorCodes.EntityNotFound, $"'{arg}' ({x.Label}) belongs to no body.");
            res.Add(FromBody(ctx, d, b, x.Pick, x.Distance));
        }
        else if (r.At is not null || r.Where is not null)
        {
            if (type is not (null or "body" or "face"))
                throw SwException.InvalidArgument($"'{arg}': a body is found from a face ('at' or 'where'), not from a {type}.", BodyRefHint);
            var face = new EntityRef { At = r.At, Where = r.Where, Expect = r.Expect, Type = "face", TolMm = r.TolMm, Strict = r.Strict };
            var faces = r.Where is not null
                ? EntityResolver.ResolveAll(ctx, d, [face], arg, EntityKind.Face)
                : [EntityResolver.Resolve(ctx, d, face, arg, EntityKind.Face)];
            foreach (var f in faces)
            {
                var b = ((IFace2)f.Obj).Body() ?? throw new SwException(ErrorCodes.EntityNotFound, $"{f.Label} belongs to no body.");
                var x = FromBody(ctx, d, b, f.Pick, f.Distance);
                if (!res.Any(y => y.Id == x.Id)) res.Add(x);
            }
        }
        else if (!string.IsNullOrWhiteSpace(r.Name))
        {
            var x = EntityResolver.Resolve(ctx, d, new EntityRef { Name = r.Name, Type = "body" }, arg, EntityKind.Body);
            res.Add(FromBody(ctx, d, (IBody2)x.Obj, null, null));
        }
        else
        {
            throw SwException.InvalidArgument($"'{arg}' needs 'id', 'name', 'at' or 'where'.", BodyRefHint);
        }
        foreach (var x in res)
        {
            bool solid = ((IBody2)x.Obj).GetType() == (int)swBodyType_e.swSolidBody;
            if (kind == BodyKind.Solid && !solid)
                throw SwException.InvalidArgument($"'{arg}' is {x.Label}, a surface body; this needs a solid body.", "surface_thicken or surface_knit {trySolid:true} make solids from surfaces.");
            if (kind == BodyKind.Surface && solid)
                throw SwException.InvalidArgument($"'{arg}' is {x.Label}, a solid body; this needs a surface body.", "The surface_* tools make surface bodies.");
        }
        return res;
    }

    private static Resolved FromBody(SwContext ctx, IModelDoc2 d, IBody2 b, Vec3? pick, double? distance)
    {
        string id = ctx.Entities.IdFor(d, b, 'B', "body");
        return new Resolved { Obj = b, Kind = EntityKind.Body, Id = id, Label = $"body '{SafeName(b)}' ({id})", Pick = pick, Distance = distance };
    }

    /// <summary>
    /// Selects every item with <paramref name="mark"/> (appending) and checks the selection count grew accordingly. Bodies
    /// are selected the way the recipes were verified, SelectByID2(name, "SOLIDBODY" | "SURFACEBODY") with the mark, and
    /// only accepted when that picked this very body (same persistent id); IBody2.Select2 is the fallback.
    /// </summary>
    public static void SelectAll(IModelDoc2 d, IEnumerable<Resolved> items, int mark)
    {
        var sel = d.Sel();
        foreach (var r in items)
        {
            int n = sel.GetSelectedObjectCount2(-1);
            if (r.Obj is IBody2 b && SelectBodyById(d, sel, b, n, mark)) continue;
            if (!r.Select(d, true, mark) || sel.GetSelectedObjectCount2(-1) != n + 1)
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");
        }
    }

    private static bool SelectBodyById(IModelDoc2 d, ISelectionMgr sel, IBody2 b, int n, int mark)
    {
        string name = SafeName(b);
        if (name.Length == 0) return false;
        string type = b.GetType() == (int)swBodyType_e.swSolidBody ? "SOLIDBODY" : "SURFACEBODY";
        bool ok;
        try { ok = d.Extension.SelectByID2(name, type, 0, 0, 0, true, mark, null, 0); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { ok = false; }
        if (ok && sel.GetSelectedObjectCount2(-1) == n + 1 && sel.BdSelected(n + 1) is IBody2 got && SamePersist(d, got, b)) return true;
        for (int guard = 0; guard < 4 && sel.GetSelectedObjectCount2(-1) > n; guard++) sel.DeSelect2(n + 1, -1); // undo a wrong pick
        return false;
    }

    private static bool SamePersist(IModelDoc2 d, IBody2 a, IBody2 b)
    {
        if (ReferenceEquals(a, b)) return true;
        try
        {
            var pa = ComArgs.Bytes((object?)d.Extension.GetPersistReference3(a));
            var pb = ComArgs.Bytes((object?)d.Extension.GetPersistReference3(b));
            return pa is not null && pb is not null && pa.AsSpan().SequenceEqual(pb);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            return false;
        }
    }

    /// <summary>One body's geometry now; <paramref name="withVertices"/> captures its vertices too (before the body changes).</summary>
    public static BodyGeom Geom(SwContext ctx, IModelDoc2 d, IBody2 b, bool withVertices = false)
    {
        bool solid = b.GetType() == (int)swBodyType_e.swSolidBody;
        var mp = b.MassOfBody(); // [cx, cy, cz, volume, area, mass, ...]
        string id;
        try { id = ctx.Entities.IdFor(d, b, 'B', "body"); }
        catch (SwException) { id = "?"; }
        bool hidden = false;
        try { hidden = !b.Visible; }
        catch (COMException) { /* not available */ }
        var g = new BodyGeom
        {
            Body = b,
            Id = id,
            Name = SafeName(b),
            Solid = solid,
            Volume = solid && mp.Length >= 4 ? mp[3] : 0,
            Area = mp.Length >= 5 ? mp[4] : 0,
            Center = mp.Length >= 3 ? Vec3.From(mp) : Vec3.Zero,
            Box = b.Box(),
            Faces = b.GetFaceCount(),
            Edges = b.GetEdgeCount(),
            Hidden = hidden,
        };
        if (withVertices) _ = g.Vertices;
        return g;
    }

    /// <summary>The vertex points of a body (m).</summary>
    public static List<Vec3> VertexPoints(IBody2 b) =>
        b.Vertices().Select(v => v.Point()).Where(p => p.Length >= 3).Select(p => Vec3.From(p)).ToList();

    /// <summary>Every body of the part, hidden ones included (solids first).</summary>
    public static List<BodyGeom> AllGeoms(SwContext ctx, IPartDoc part, IModelDoc2 d) =>
        part.AllBodies(visibleOnly: false).Select(b => Geom(ctx, d, b)).ToList();

    public static List<BodyGeom> SolidGeoms(SwContext ctx, IPartDoc part, IModelDoc2 d) =>
        part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Select(b => Geom(ctx, d, b)).ToList();

    public static List<BodyGeom> SurfaceGeoms(SwContext ctx, IPartDoc part, IModelDoc2 d) =>
        part.Bodies(swBodyType_e.swSheetBody, visibleOnly: false).Select(b => Geom(ctx, d, b)).ToList();

    /// <summary>Same volume and area (a rigid motion keeps both).</summary>
    public static bool SameSize(BodyGeom a, BodyGeom b) =>
        Math.Abs(a.Volume - b.Volume) <= 1e-15 + 1e-6 * Math.Max(Math.Abs(a.Volume), Math.Abs(b.Volume)) &&
        Math.Abs(a.Area - b.Area) <= 1e-12 + 1e-6 * Math.Max(a.Area, b.Area);

    public static bool SameBox(BodyGeom a, BodyGeom b) =>
        a.Box.Length == 6 && b.Box.Length == 6 && Enumerable.Range(0, 6).All(i => Math.Abs(a.Box[i] - b.Box[i]) <= PosTol);

    /// <summary>The bodies of <paramref name="after"/> that are new or changed compared with <paramref name="before"/>.</summary>
    public static List<BodyGeom> NewOrChanged(List<BodyGeom> before, List<BodyGeom> after) =>
        after.Where(a => !before.Any(b => b.Id == a.Id && SameSize(a, b) && SameBox(a, b))).ToList();

    public static BodyRow Row(BodyGeom g) => new()
    {
        Id = g.Id,
        Name = g.Name,
        Type = g.Solid ? "solid" : "surface",
        VolumeMm3 = g.Solid ? Units.Mm3(g.Volume) : null,
        AreaMm2 = Units.Mm2(g.Area),
        Box = BoxMm.FromMeters(g.Box),
        Faces = g.Faces,
        Edges = g.Edges,
        Hidden = g.Hidden ? true : null,
    };

    public static string SafeName(IBody2 b)
    {
        try { return b.Name ?? ""; }
        catch (COMException) { return ""; }
    }

    /// <summary>The standard result of the bodies and surfaces tools.</summary>
    public static BodyResult Result(SwContext ctx, IModelDoc2 d, FeatureOutcome o, IEnumerable<BodyGeom>? bodies, IReadOnlyList<ResolvedInfo>? resolved,
        string? method = null, IReadOnlyList<string>? removed = null, bool? solidFormed = null, string? consumedSketch = null, Vec3? direction = null)
    {
        var fr = FeatureVerifier.Result(ctx, d, o, consumedSketch, direction, resolved is { Count: > 0 } ? resolved : null);
        var rows = bodies?.Take(20).Select(Row).ToList();
        return new BodyResult
        {
            Doc = fr.Doc,
            Feature = fr.Feature,
            AlsoCreated = fr.AlsoCreated,
            Verified = fr.Verified,
            Model = fr.Model,
            Bodies = rows is { Count: > 0 } ? rows : null,
            RemovedBodies = removed is { Count: > 0 } ? removed : null,
            Method = method,
            SolidFormed = solidFormed,
            ConsumedSketch = fr.ConsumedSketch,
            DirectionModel = fr.DirectionModel,
            NewFaces = fr.NewFaces is { Count: > 0 } ? fr.NewFaces : null,
            NewFaceCount = fr.NewFaceCount is > 0 ? fr.NewFaceCount : null,
            Dimensions = fr.Dimensions is { Count: > 0 } ? fr.Dimensions : null,
            Resolved = fr.Resolved,
        };
    }

    // ------------------------------------------------------------------ sketches

    /// <summary>
    /// The profile sketch: the server's open sketch (closed now, or left open with <paramref name="keepOpen"/>; Open then
    /// tells), the named one, or the last finished one.
    /// </summary>
    public static (IFeature Feature, ISketch Sketch, bool Open) ProfileSketch(SwContext ctx, IModelDoc2 d, string? name, string verb, bool keepOpen = false)
    {
        var entry = ctx.DocEntry;
        var sm = d.SketchManager;
        if (sm.ActiveSketch is ISketch open)
        {
            string openName = ((IFeature)open).Name;
            if (entry.ServerSketch != openName)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' is open, but this server didn't open it.", "Ask the user to exit the sketch first.");
            if (name is not null && !string.Equals(name, openName, StringComparison.OrdinalIgnoreCase))
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' is open; finish it before using '{name}'.");
            if (open.Is3D()) throw SwException.InvalidArgument($"A 3D sketch can't be used to {verb}.");
            if (keepOpen && EntityResolver.FeatureByName(d, openName) is { } openFeat) return (openFeat, open, true);
            sm.InsertSketch(true); // close it: features need it closed (and selected)
            if (sm.ActiveSketch is not null) throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' did not close.");
            entry.ServerSketch = null;
            entry.LastSketch = openName;
            name = openName;
        }
        name ??= entry.LastSketch ?? throw SwException.InvalidArgument($"No sketch to {verb}.",
            "Pass 'sketch' (e.g. \"Sketch1\"), or draw one with sketch_start / sketch_add first.");
        var feat = EntityResolver.FeatureByName(d, name)
                   ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.", "feature_list shows the sketches.");
        if (feat.Specific() is not ISketch sk || EntityResolver.FeatureKind(feat) != EntityKind.Sketch)
            throw SwException.InvalidArgument($"'{name}' is a {feat.GetTypeName2()}, not a sketch.");
        if (sk.Is3D()) throw SwException.InvalidArgument($"'{name}' is a 3D sketch; {verb} needs a 2D sketch.");
        return (feat, sk, false);
    }

    /// <summary>
    /// Runs <paramref name="call"/> with the profile sketch in edit mode: as it is when it is still open, else re-opened
    /// with EditSketch (the extruded-surface recipe was verified on the open sketch). Afterwards no sketch is left open
    /// (a consumed sketch is closed by SOLIDWORKS already) and the registry says so. False when the sketch could not be
    /// opened (nothing was called).
    /// </summary>
    public static bool OnOpenSketch(SwContext ctx, IModelDoc2 d, string sketchName, Action call)
    {
        var sm = d.SketchManager;
        try
        {
            if (sm.ActiveSketch is not ISketch active || ((IFeature)active).Name != sketchName)
            {
                if (sm.ActiveSketch is not null) return false; // another sketch is open
                var f = EntityResolver.FeatureByName(d, sketchName);
                d.ClearSelection2(true);
                if (f is null || !f.Select2(false, 0)) return false;
                d.EditSketch();
                if (sm.ActiveSketch is not ISketch reopened) return false;
                if (((IFeature)reopened).Name != sketchName)
                {
                    CloseOpenSketch(d); // something else opened: put it back
                    return false;
                }
            }
            d.ClearSelection2(true);
            call();
            return true;
        }
        finally
        {
            EndSketch(ctx, d, sketchName); // never closes another sketch
            d.ClearSelection2(true);
        }
    }

    /// <summary>
    /// Ends a tool that may have kept the profile sketch open: closes it if it is still open and records it as finished
    /// (a sketch consumed by the new feature is closed already).
    /// </summary>
    public static void EndSketch(SwContext ctx, IModelDoc2 d, string sketchName)
    {
        var sm = d.SketchManager;
        if (sm.ActiveSketch is ISketch still && ((IFeature)still).Name == sketchName) CloseOpenSketch(d);
        var entry = ctx.DocEntry;
        if (sm.ActiveSketch is null && entry.ServerSketch == sketchName)
        {
            entry.ServerSketch = null;
            entry.LastSketch = sketchName;
        }
    }

    /// <summary>Selects the closed sketch (plus <paramref name="extra"/>, e.g. an axis) and runs <paramref name="call"/>.</summary>
    public static object? OnSelectedSketch(IModelDoc2 d, IFeature sketchFeat, Func<object?> call, Action? extra = null)
    {
        d.ClearSelection2(true);
        if (!sketchFeat.Select2(false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{sketchFeat.Name}'.");
        extra?.Invoke();
        try
        {
            return call();
        }
        finally
        {
            d.ClearSelection2(true);
        }
    }

    /// <summary>The sketch plane in model space: origin and unit normal.</summary>
    public static (Vec3 Origin, Vec3 Normal) SketchPlane(ISketch sketch)
    {
        var s2m = SketchService.SketchToModel(sketch);
        return (s2m.ApplyPoint(Vec3.Zero), s2m.ApplyVector(Vec3.UnitX).Cross(s2m.ApplyVector(Vec3.UnitY)).Normalized());
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>A plane (point, unit normal) of a reference plane or a planar face; null for other entities.</summary>
    public static (Vec3 Q, Vec3 N)? PlaneOf(Resolved r)
    {
        switch (r.Obj)
        {
            case IFeature f when f.Specific() is IRefPlane rp:
            {
                var t = rp.RefPlaneXform();
                if (t.Length < 12) return null;
                var n = new Vec3(t[6], t[7], t[8]);
                return n.Length < 1e-12 ? null : (new Vec3(t[9], t[10], t[11]), n.Normalized());
            }
            case IFace2 face when Topology.Describe(face) is { Surface: "plane", Normal: Vec3 fn }:
                return (Topology.InteriorPoint(face), fn);
        }
        return null;
    }

    /// <summary>True when the plane through <paramref name="q"/> with normal <paramref name="n"/> passes through the box.</summary>
    public static bool Crosses(double[] box, Vec3 q, Vec3 n)
    {
        if (box.Length != 6) return true;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var x in new[] { box[0], box[3] })
            foreach (var y in new[] { box[1], box[4] })
                foreach (var z in new[] { box[2], box[5] })
                {
                    double s = (new Vec3(x, y, z) - q).Dot(n);
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                }
        return lo < -1e-9 && hi > 1e-9;
    }

    /// <summary>The range of signed offsets along <paramref name="n"/> from <paramref name="origin"/> of the faces' edge points.</summary>
    public static (double Min, double Max) Span(IEnumerable<IFace2> faces, Vec3 origin, Vec3 n)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var f in faces)
            foreach (var e in f.Edges())
            {
                var g = Topology.Describe(e);
                foreach (var p in new[] { g.Start, g.End, g.Mid })
                {
                    double s = (p - origin).Dot(n);
                    lo = Math.Min(lo, s);
                    hi = Math.Max(hi, s);
                }
            }
        return lo > hi ? (0, 0) : (lo, hi);
    }

    // ------------------------------------------------------------------ text

    public static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    public static string Pt(Vec3 p) => TopoQuery.Fmt(Clean(Units.Mm(p)));

    /// <summary>m → mm for messages, without "-0".</summary>
    public static double Mm(double m) => Units.Mm(m) is var v && v == 0 ? 0 : v;

    public static string Dir(Vec3 v) => TopoQuery.Fmt(Clean(Units.Unit(v.Normalized())));

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>The attempted API variants and why each failed, kept short enough for an error message.</summary>
    public static string Tried(IEnumerable<string> tried) => Clip(string.Join("; ", tried), 640);

    private static double[] Clean(double[] v) => v.Select(x => x == 0 ? 0 : x).ToArray(); // no "-0"
}
