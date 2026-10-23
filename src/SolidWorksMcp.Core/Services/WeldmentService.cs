using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of weldment_member: structural members (profiles swept along sketch lines).</summary>
public sealed record WeldmentMemberRequest
{
    public string? Doc { get; init; }
    /// <summary>The path sketch (2D or 3D); default: the open sketch, else the last finished one.</summary>
    public string? Sketch { get; init; }
    /// <summary>Which segments get members, by sketch-entity name (e.g. "Line1"); default: every line and arc.</summary>
    public IReadOnlyList<string>? Segments { get; init; }
    public string? Standard { get; init; }
    public string? Type { get; init; }
    public string? Size { get; init; }
    public string? Corner { get; init; }
    public double RotateDeg { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed class WeldmentMember
{
    public required string Body { get; init; }
    /// <summary>The sketch segment it runs along.</summary>
    public required string Segment { get; init; }
    public double SegmentLengthMm { get; init; }
    /// <summary>The member's length along its segment (its body's extent there, corner treatments included).</summary>
    public double LengthMm { get; init; }
    public double VolumeMm3 { get; init; }
}

public sealed class WeldmentResult : ToolResult
{
    public required FeatureRef Feature { get; init; }
    /// <summary>"iso / square tube / 40 x 40 x 4".</summary>
    public required string Profile { get; init; }
    public required string ProfilePath { get; init; }
    public required string Corner { get; init; }
    public required IReadOnlyList<WeldmentMember> Members { get; init; }
    public required Verified Verified { get; init; }
    public required ModelState Model { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
}

/// <summary>
/// Weldments (api notes §6): a Weldment feature (inserted the first time) and structural members along the segments of
/// a sketch, through IStructuralMemberGroup + IFeatureManager.InsertStructuralWeldment5 (the groups as IDispatch
/// wrappers, as SOLIDWORKS' own example passes them). Tier A is the usual feature check; Tier B finds one new solid body
/// per segment, lying along it.
/// </summary>
public sealed class WeldmentService(SwSession session)
{
    private static readonly FeatureSpec MemberSpec = new("weldment_member", ["WeldMemberFeat", "StructuralMember"], VolumeRule.Increase);

    public Task<WeldmentResult> MemberAsync(WeldmentMemberRequest q, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(q.Type))
            throw SwException.InvalidArgument("'type' is required: the profile, e.g. \"square tube\" (iso), \"pipe\", \"c channel\", \"angle iron\".");
        if (string.IsNullOrWhiteSpace(q.Size))
            throw SwException.InvalidArgument("'size' is required: a size of the profile, e.g. \"40 x 40 x 4\" (a wrong one lists the sizes).");
        if (!double.IsFinite(q.RotateDeg) || Math.Abs(q.RotateDeg) > 360) throw SwException.InvalidArgument($"rotateDeg must be between -360 and 360 (got {q.RotateDeg}).");
        var corner = WeldmentSpec.Corner(q.Corner);
        return session.RunAsync("weldment_member", ctx => Member(ctx, q, corner), RunOptions.Write(q.Doc) with
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)),
        }, ct);
    }

    private static WeldmentResult Member(SwContext ctx, WeldmentMemberRequest q, (int Code, string Name) corner)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        var (path, profile, size) = Profile(ctx, q.Standard ?? "iso", q.Type!, q.Size!);
        var (sketchFeat, sketch) = PathSketch(ctx, d, q.Sketch);
        var s2m = sketch.Is3D() ? Xform.Identity : SketchService.SketchToModel(sketch);
        var all = sketch.Segments().Where(s => !s.ConstructionGeometry &&
                                               s.GetType() is (int)swSketchSegments_e.swSketchLINE or (int)swSketchSegments_e.swSketchARC).ToList();
        List<ISketchSegment> segments;
        if (q.Segments is { Count: > 0 })
        {
            segments = [];
            foreach (var n in q.Segments)
                segments.Add(all.FirstOrDefault(s => string.Equals(s.GetName(), n.Trim(), StringComparison.OrdinalIgnoreCase))
                             ?? throw new SwException(ErrorCodes.EntityNotFound, $"'{sketchFeat.Name}' has no line or arc named '{n}'.",
                                 $"Its segments: {WeldmentSpec.Few(all.Select(s => s.GetName()))} (sketch_info lists them)."));
        }
        else segments = all;
        if (segments.Count == 0)
            throw SwException.InvalidArgument($"'{sketchFeat.Name}' has no lines or arcs to put members on (construction lines don't count).");
        if (segments.Count > 200) throw SwException.InvalidArgument($"At most 200 segments per call (got {segments.Count}).");
        var paths = segments.Select(s => (Name: s.GetName(), Ends: Ends(s, s2m))).ToList();

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        var bodiesBefore = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var attempts = new List<string>();
        d.ClearSelection2(true);
        if (!HasWeldment(d))
        {
            if (d.FeatureManager.InsertWeldmentFeature() is null && !HasWeldment(d))
                throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS added no Weldment feature to the part.");
        }
        var fm = d.FeatureManager;
        var group = fm.CreateStructuralMemberGroup()
                    ?? throw new SwException(ErrorCodes.Internal, "CreateStructuralMemberGroup returned nothing.");
        if (!SetSegments(group, segments, attempts))
            throw new SwException(ErrorCodes.FeatureFailed, $"The structural member group took none of the {segments.Count} segment(s) ({string.Join("; ", attempts)}).");
        group.ApplyCornerTreatment = corner.Code != 0;
        if (corner.Code != 0) group.CornerTreatmentType = corner.Code;
        group.Angle = Units.Rad(q.RotateDeg);
        object? api = null;
        try
        {
            api = fm.InsertStructuralWeldment5(path, 1, false, new[] { new DispatchWrapper(group) }, size);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            attempts.Add($"InsertStructuralWeldment5: {e.Message}");
        }
        d.ClearSelection2(true);

        string profileName = $"{profile.Standard} / {profile.Type} / {size}";
        var o = BodyKit.Verify(ctx, d, before, MemberSpec, (main, after, checks) => MemberIntent(part, bodiesBefore, paths, checks),
            q.KeepOnFailure, api, attempts.Count > 0 ? string.Join("; ", attempts) : "The segments must form a path a profile can follow (no zero-length or overlapping lines).",
            guessedType: true);
        var fr = FeatureVerifier.Result(ctx, d, o, null, null, null);
        var members = Members(part, bodiesBefore, paths);
        return new WeldmentResult
        {
            Doc = ctx.Title(d),
            Feature = fr.Feature,
            Profile = profileName,
            ProfilePath = path,
            Corner = corner.Name,
            Members = members,
            Verified = fr.Verified,
            Model = fr.Model,
            AlsoCreated = fr.AlsoCreated,
        };
    }

    // ------------------------------------------------------------------ profiles

    private sealed record ProfileName(string Standard, string Type);

    /// <summary>
    /// The profile file and size: &lt;weldment profiles&gt;\&lt;standard&gt;\&lt;type&gt;.sldlfp and one of its configurations.
    /// Unknown names list what there is.
    /// </summary>
    private static (string Path, ProfileName Profile, string Size) Profile(SwContext ctx, string standard, string type, string size)
    {
        var roots = (ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsWeldmentProfiles) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Directory.Exists).ToList();
        if (roots.Count == 0)
            throw new SwException(ErrorCodes.IoError, "No weldment profile folder is set (Tools > Options > File Locations > Weldment Profiles).");
        var standards = roots.SelectMany(Directory.GetDirectories).ToList();
        var stdDir = standards.FirstOrDefault(s => WeldmentSpec.Norm(Path.GetFileName(s)) == WeldmentSpec.Norm(standard))
                     ?? throw SwException.InvalidArgument($"No weldment standard '{standard}'.",
                         $"Standards: {WeldmentSpec.Few(standards.Select(Path.GetFileName).OfType<string>().Distinct())}.");
        var files = Directory.GetFiles(stdDir, "*.sldlfp").ToList();
        var file = files.FirstOrDefault(f => WeldmentSpec.Norm(Path.GetFileNameWithoutExtension(f)) == WeldmentSpec.Norm(type))
                   ?? throw SwException.InvalidArgument($"No profile '{type}' in the {Path.GetFileName(stdDir)} standard.",
                       $"Profiles: {WeldmentSpec.Few(files.Select(Path.GetFileNameWithoutExtension).OfType<string>())}.");
        var sizes = ComArgs.Strings((object?)ctx.App.GetConfigurationNames(file)).ToList();
        string got = WeldmentSpec.MatchSize(sizes, size)
                     ?? throw SwException.InvalidArgument($"'{Path.GetFileNameWithoutExtension(file)}' has no size '{size}'.",
                         $"Sizes: {WeldmentSpec.Few(sizes, 20)}.");
        return (file, new ProfileName(Path.GetFileName(stdDir), Path.GetFileNameWithoutExtension(file)), got);
    }

    // ------------------------------------------------------------------ sketch and segments

    /// <summary>The path sketch, 2D or 3D: the server's open sketch (closed now), the named one or the last finished one.</summary>
    private static (IFeature Feature, ISketch Sketch) PathSketch(SwContext ctx, IModelDoc2 d, string? name)
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
            if (open.Is3D()) sm.Insert3DSketch(true); else sm.InsertSketch(true);
            if (sm.ActiveSketch is not null) throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' did not close.");
            entry.ServerSketch = null;
            entry.LastSketch = openName;
            name = openName;
        }
        name ??= entry.LastSketch ?? throw SwException.InvalidArgument("No sketch to put members on.",
            "Pass 'sketch' (e.g. \"Sketch1\" or \"3DSketch1\"), or draw the frame's lines with sketch_start / sketch_add first.");
        var feat = EntityResolver.FeatureByName(d, name)
                   ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{name}'.", "feature_list shows the sketches.");
        if (feat.Specific() is not ISketch sk || EntityResolver.FeatureKind(feat) != EntityKind.Sketch)
            throw SwException.InvalidArgument($"'{name}' is a {feat.GetTypeName2()}, not a sketch.");
        return (feat, sk);
    }

    /// <summary>A segment's end points (model m).</summary>
    private static (Vec3 A, Vec3 B) Ends(ISketchSegment seg, Xform s2m)
    {
        ISketchPoint? a, b;
        if (seg is ISketchLine line) { a = (object?)line.GetStartPoint2() as ISketchPoint; b = (object?)line.GetEndPoint2() as ISketchPoint; }
        else if (seg is ISketchArc arc) { a = (object?)arc.GetStartPoint2() as ISketchPoint; b = (object?)arc.GetEndPoint2() as ISketchPoint; }
        else { a = b = null; }
        if (a is null || b is null) throw new SwException(ErrorCodes.Internal, $"Could not read the end points of {seg.GetName()}.");
        return (s2m.ApplyPoint(new Vec3(a.X, a.Y, a.Z)), s2m.ApplyPoint(new Vec3(b.X, b.Y, b.Z)));
    }

    /// <summary>Sets the group's segments (IDispatch wrappers, then a plain array) and reads the count back.</summary>
    private static bool SetSegments(IStructuralMemberGroup group, List<ISketchSegment> segments, List<string> attempts)
    {
        foreach (var (how, value) in new (string, Func<object>)[]
                 {
                     ("IDispatch array", () => ComArgs.DispatchArray(segments)),
                     ("VARIANT array", () => segments.Cast<object>().ToArray()),
                 })
        {
            try
            {
                group.Segments = value();
                int n = group.GetSegmentsCount();
                if (n == segments.Count) return true;
                attempts.Add($"segments as {how}: {n} taken");
            }
            catch (Exception e) when (e is COMException or ArgumentException or InvalidCastException && !ComArgs.IsDisconnect(e))
            {
                attempts.Add($"segments as {how}: {e.Message}");
            }
        }
        return false;
    }

    private static bool HasWeldment(IModelDoc2 d) =>
        ComArgs.Objects<IFeature>((object?)d.FeatureManager.GetFeatures(false)).Any(f => f.GetTypeName2() == "WeldmentFeature");

    // ------------------------------------------------------------------ Tier B: one member along each segment

    private sealed record NewBody(IBody2 Body, string Name, Vec3 Com, double Volume, double[] Box);

    private static List<NewBody> NewBodies(IPartDoc part, HashSet<string> before) =>
        part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Where(b => !before.Contains(b.Name)).Select(b =>
        {
            var mp = ComArgs.Doubles((object?)b.GetMassProperties(1.0)); // [cx, cy, cz, volume, …]
            return new NewBody(b, b.Name, mp.Length >= 3 ? new Vec3(mp[0], mp[1], mp[2]) : Vec3.Zero, mp.Length >= 4 ? mp[3] : 0, b.Box());
        }).ToList();

    /// <summary>The body whose centre of mass is nearest a segment's midpoint, and its extent along the segment.</summary>
    private static (NewBody Body, double Along, double Off)? Along(List<NewBody> bodies, (Vec3 A, Vec3 B) ends)
    {
        var mid = (ends.A + ends.B) / 2;
        var u = (ends.B - ends.A).Normalized();
        var best = bodies.OrderBy(b => b.Com.DistanceTo(mid)).FirstOrDefault();
        if (best is null) return null;
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in best.Body.Vertices())
        {
            double t = (Vec3.From(v.Point()) - ends.A).Dot(u);
            lo = Math.Min(lo, t);
            hi = Math.Max(hi, t);
        }
        return (best, hi > lo ? hi - lo : 0, AsmMath.PointToLine(ends.A, u, best.Com));
    }

    private static IEnumerable<string> MemberIntent(IPartDoc part, HashSet<string> before, List<(string Name, (Vec3 A, Vec3 B) Ends)> paths, List<string> checks)
    {
        var fresh = NewBodies(part, before);
        if (fresh.Count != paths.Count)
        {
            yield return $"{fresh.Count} new solid bodies for {paths.Count} segment(s) (one member per segment expected)";
            yield break;
        }
        var left = fresh.ToList();
        foreach (var (name, ends) in paths)
        {
            double len = ends.A.DistanceTo(ends.B);
            if (Along(left, ends) is not { } hit)
            {
                yield return $"no member along {name}";
                yield break;
            }
            // Corner treatments trim or extend a member by up to its profile size at each end; the body's own section
            // is at most a few profile sizes across, so its centre must sit close to the segment line.
            var b = hit.Body.Box;
            double across = b.Length == 6 ? Math.Max(b[3] - b[0], Math.Max(b[4] - b[1], b[5] - b[2])) : 0;
            if (hit.Off > Math.Max(across, 1e-4) || hit.Along < len - across - 1e-6 || hit.Along > len + across + 1e-6)
            {
                yield return $"the body nearest {name} ({Units.Mm(len)} mm) is {Units.Mm(hit.Along)} mm long along it and {Units.Mm(hit.Off)} mm off its line";
                yield break;
            }
            left.Remove(hit.Body);
        }
        checks.Add($"{paths.Count} member(s), one solid body along each segment");
    }

    private static List<WeldmentMember> Members(IPartDoc part, HashSet<string> before, List<(string Name, (Vec3 A, Vec3 B) Ends)> paths)
    {
        var left = NewBodies(part, before);
        var list = new List<WeldmentMember>();
        foreach (var (name, ends) in paths)
        {
            if (Along(left, ends) is not { } hit) continue;
            left.Remove(hit.Body);
            list.Add(new WeldmentMember
            {
                Body = hit.Body.Name,
                Segment = name,
                SegmentLengthMm = Units.Mm(ends.A.DistanceTo(ends.B)),
                LengthMm = Units.Mm(hit.Along),
                VolumeMm3 = Units.Mm3(hit.Body.Volume),
            });
        }
        return list;
    }
}
