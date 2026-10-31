using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>A body weldment_finish made (cap, gusset, bead) or changed (a trimmed member).</summary>
public sealed class WeldmentFinishBody
{
    public required string Name { get; init; }
    /// <summary>trim: the member's name before (a trimmed body takes the trim feature's name).</summary>
    public string? PreviousName { get; init; }
    public double VolumeMm3 { get; init; }
    /// <summary>trim: the member's volume before.</summary>
    public double? VolumeBeforeMm3 { get; init; }
    /// <summary>[xmin, ymin, zmin, xmax, ymax, zmax] (mm).</summary>
    public double[]? BoxMm { get; init; }
}

public sealed class WeldmentFinishResult : ToolResult
{
    /// <summary>trim | end_cap | gusset | bead.</summary>
    public required string Kind { get; init; }
    public required FeatureRef Feature { get; init; }
    /// <summary>The new bodies (caps, gussets, beads), or the trimmed members.</summary>
    public required IReadOnlyList<WeldmentFinishBody> Bodies { get; init; }
    /// <summary>bead: how long the edges are that the bead runs along (every side).</summary>
    public double? BeadLengthMm { get; init; }
    public required Verified Verified { get; init; }
    public required ModelState Model { get; init; }
    public IReadOnlyList<FeatureRef>? AlsoCreated { get; init; }
}

/// <summary>
/// weldment_finish (api notes §6): IFeatureManager.InsertWeldmentTrimFeature2 (the bodies on mark 1 and the boundaries on
/// mark 2, also as IDispatch arrays),
/// InsertEndCapFeature3 (the end faces on mark 0), InsertGussetFeature3 (the two faces on mark 1) and
/// InsertCosmeticWeldBead2 (two face sets on marks 4 and 8). Tier B checks each against its geometry: trimmed members
/// changed and the feature holds them; caps and gussets are plates of the asked thickness and size where they belong;
/// a weld bead's size and settings read back and its edges lie where the face sets meet.
/// </summary>
public sealed partial class WeldmentService
{
    private static readonly FeatureSpec TrimSpec = new("weldment_finish", ["WeldCornerFeat"], VolumeRule.Change, RequireFaces: false);
    private static readonly FeatureSpec CapSpec = new("weldment_finish", ["EndCap"], VolumeRule.Increase, RequireFaces: false);
    private static readonly FeatureSpec GussetSpec = new("weldment_finish", ["Gusset"], VolumeRule.Increase, RequireFaces: false);
    private static readonly FeatureSpec BeadSpec = new("weldment_finish", ["CosmeticWeldBead"], VolumeRule.Any, RequireFaces: false);

    private const double PlateTolM = 2e-6; // m: a plate's thickness read back from its vertices

    public Task<WeldmentFinishResult> FinishAsync(WeldmentFinishRequest q, CancellationToken ct = default)
    {
        string kind = WeldmentSpec.FinishKind(q.Kind);
        var ignored = WeldmentSpec.CheckFinish(kind, q);
        return session.RunAsync("weldment_finish", ctx =>
        {
            foreach (var p in ignored) ctx.Warn($"'{p}' does not apply to kind {kind}; ignored.");
            return Finish(ctx, kind, q);
        }, RunOptions.Write(q.Doc) with
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)),
        }, ct);
    }

    private static WeldmentFinishResult Finish(SwContext ctx, string kind, WeldmentFinishRequest q)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        if (!HasWeldment(d))
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' is not a weldment.", "Add structural members with weldment_member first.");
        BodyKit.NoOpenSketch(d);
        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        var states = SolidStates(part);
        d.ClearSelection2(true);
        var (spec, api, attempts, intent, report) = kind switch
        {
            "trim" => Trim(ctx, part, d, q, states),
            "end_cap" => EndCap(ctx, part, d, q, states),
            "gusset" => Gusset(ctx, part, d, q, states),
            _ => Bead(ctx, part, d, q, states),
        };
        d.ClearSelection2(true);
        var o = BodyKit.Verify(ctx, d, before, spec, intent, q.KeepOnFailure, api,
            attempts.Count > 0 ? string.Join("; ", attempts) : Hint(kind), guessedType: true);
        var fr = FeatureVerifier.Result(ctx, d, o, null, null, null);
        var (bodies, beadLength) = report(o.Main);
        return new WeldmentFinishResult
        {
            Doc = ctx.Title(d),
            Kind = kind,
            Feature = fr.Feature,
            Bodies = bodies,
            BeadLengthMm = beadLength,
            Verified = fr.Verified,
            Model = fr.Model,
            AlsoCreated = fr.AlsoCreated,
        };
    }

    private static string Hint(string kind) => kind switch
    {
        "trim" => "The boundary must reach the members' ends (or lie in their way); for two members meeting at a corner use corner miter/butt1/butt2.",
        "end_cap" => "End caps go on the flat open end face of a member (its end, not a side); the offset must leave a cap smaller than the profile.",
        "gusset" => "The two faces must be flat, belong to members that meet, and leave room for legs of that size.",
        _ => "The two face sets must meet along an edge (e.g. a beam's top and the post standing on it).",
    };

    private delegate (IReadOnlyList<WeldmentFinishBody> Bodies, double? BeadLengthMm) Report(IFeature main);

    // ------------------------------------------------------------------ trim / extend

    private static (FeatureSpec, object?, List<string>, Func<IFeature, Snapshot, List<string>, IEnumerable<string>>, Report) Trim(
        SwContext ctx, IPartDoc part, IModelDoc2 d, WeldmentFinishRequest q, List<BodyState> states)
    {
        var corner = WeldmentSpec.TrimCorner(q.Corner);
        var toTrim = BodiesOf(ctx, d, q.Bodies, "bodies");
        var boundary = EntityResolver.ResolveAll(ctx, d, AsBodies(q.Boundary), "boundary", EntityKind.Body, EntityKind.Face).Select(r => r.Obj).ToList();
        var names = toTrim.Select(b => b.Name).ToList();
        foreach (var b in boundary.OfType<IBody2>())
            if (names.Contains(b.Name))
                throw SwException.InvalidArgument($"'{b.Name}' is both trimmed and the boundary.", "The boundary is what the members end at: another member or a face.");
        int options = (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_AllowTrimmedExtensionTrim
                      | (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_WeldGap // so gapMm counts, not the last value used in the UI
                      | (q.Coped ? (int)swWeldmentTrimExtendOptionType_e.swWeldmentTrimExtendOption_CopedCut : 0);
        var attempts = new List<string>();
        var before = Snapshot.Take(d);
        object? api = null;
        var bodyRefs = toTrim.Cast<object>().ToList();
        // The help's remarks select the bodies to trim on mark 1 and the boundaries on mark 2; its example passes arrays.
        bool Select()
        {
            d.ClearSelection2(true);
            var data = d.Sel().CreateSelectData();
            bool ok = true;
            foreach (var (o, mark) in bodyRefs.Select(o => (o, 1)).Concat(boundary.Select(o => (o, 2))))
            {
                data.Mark = mark;
                ok &= o switch
                {
                    IBody2 body => body.Select2(true, data),
                    IEntity e => e.Select4(true, data),
                    _ => false,
                };
            }
            return ok;
        }
        // VERIFIED: the selection on marks 1 and 2 together with IDispatch arrays makes the feature; plain arrays, with or
        // without the selection, return a 'Trim/Extend' feature with error 1 that never reaches the tree.
        foreach (var (how, select, bodies, bounds) in new (string, bool, Func<object?>, Func<object?>)[]
                 {
                     ("marks 1 and 2 and IDispatch arrays", true, () => ComArgs.DispatchArray(bodyRefs), () => ComArgs.DispatchArray(boundary)),
                     ("marks 1 and 2 and arrays", true, () => bodyRefs.ToArray(), () => boundary.ToArray()),
                     ("marks 1 and 2 alone", true, () => null, () => null),
                 })
        {
            if (select && !Select())
            {
                attempts.Add($"{how}: SOLIDWORKS would not select them");
                continue;
            }
            try
            {
                api = d.FeatureManager.InsertWeldmentTrimFeature2(corner.Code, options, Units.M(q.GapMm), bodies(), bounds());
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                attempts.Add($"InsertWeldmentTrimFeature2 with {how}: {e.Message}");
            }
            d.ClearSelection2(true);
            if (!BodyKit.NothingNew(d, before)) break;
            attempts.Add($"InsertWeldmentTrimFeature2 with {how}: " + (api is IFeature f
                ? $"returned '{f.Name}' ({f.GetTypeName2()}, error {f.GetErrorCode2(out _)}), which is not in the tree"
                : "nothing"));
            api = null;
        }

        IEnumerable<string> Intent(IFeature main, Snapshot after, List<string> checks)
        {
            if ((object?)main.GetDefinition() is IWeldmentTrimExtendFeatureData td)
            {
                int ct = td.CornerType, nb = td.GetBodiesToBeTrimmedCount(), nt = td.GetTrimmingBoundaryCount();
                if (ct != corner.Code) yield return $"its corner type reads {ct}, not {corner.Code} ({corner.Name})";
                if (nb != toTrim.Count) yield return $"it trims {nb} bod(ies), not {toTrim.Count}";
                if (nt != boundary.Count) yield return $"it has {nt} boundar(ies), not {boundary.Count}";
                checks.Add($"corner {corner.Name}, {nb} bod(ies) to trim and {nt} boundar(ies) read back");
            }
            var matched = Match(part, states, names);
            if (matched.Count < names.Count)
            {
                yield return $"{names.Count - matched.Count} of the bodies to trim can't be found any more";
                yield break;
            }
            var changed = matched.Where(m => Math.Abs(m.Volume - m.Was.Volume) > 1e-12).ToList();
            if (changed.Count == 0) yield return "none of the bodies to trim changed";
            else checks.Add($"{changed.Count} of {names.Count} member(s) trimmed or extended: " +
                            string.Join(", ", changed.Select(m => $"'{m.Was.Name}' {Units.Mm3(m.Was.Volume)} → {Units.Mm3(m.Volume)} mm³" +
                                                                  (m.Now.Name == m.Was.Name ? "" : $" (now '{m.Now.Name}')"))));
        }

        (IReadOnlyList<WeldmentFinishBody>, double?) Trimmed(IFeature main) =>
            (Match(part, states, names).Select(m => Info(m.Now, m.Was.Volume, m.Now.Name == m.Was.Name ? null : m.Was.Name)).ToList(), null);
        return (TrimSpec, api, attempts, Intent, Trimmed);
    }

    // ------------------------------------------------------------------ end cap

    private static (FeatureSpec, object?, List<string>, Func<IFeature, Snapshot, List<string>, IEnumerable<string>>, Report) EndCap(
        SwContext ctx, IPartDoc part, IModelDoc2 d, WeldmentFinishRequest q, List<BodyState> states)
    {
        double t = Units.M(q.ThicknessMm!.Value), offset = Units.M(q.OffsetMm), inset = Units.M(q.InsetMm);
        var known = states.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var (dir, dirName) = WeldmentSpec.CapDirection(q.Direction);
        var faces = Planar(ctx, d, q.Faces, "faces", "end caps go on the flat open end of a member");
        var members = faces.Select(f => (object?)((IFace2)f.R.Obj).GetBody() is IBody2 b ? b.Name : "").ToList();
        foreach (var (r, _, _) in faces)
            if (!r.Select(d, true, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS would not select {r.Label}.");
        var attempts = new List<string>();
        object? api = null;
        try
        {
            api = d.FeatureManager.InsertEndCapFeature3(t, true /*given offset*/, false, offset, 0.5, 0, false /*corner treatment*/, inset, false, dir);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            attempts.Add($"InsertEndCapFeature3: {e.Message}");
        }

        IEnumerable<string> Intent(IFeature main, Snapshot after, List<string> checks)
        {
            if ((object?)main.GetDefinition() is IEndCapFeatureData cd)
            {
                if (Math.Abs(cd.Thickness - t) > 1e-9) yield return $"its thickness reads {Units.Mm(cd.Thickness)} mm, not {q.ThicknessMm} mm";
                if (cd.IsEndCapInward != dir) yield return $"its direction reads {cd.IsEndCapInward}, not {dir} ({dirName})";
                checks.Add("thickness and direction read back");
            }
            var fresh = NewBodies(part, known);
            if (fresh.Count != faces.Count)
            {
                yield return $"{fresh.Count} new bod(ies) for {faces.Count} end face(s) (one cap per face expected)";
                yield break;
            }
            // Measured from the end face's plane along its outward normal: outward caps lie in [0, t] and the member keeps
            // its end; inward ones in [-t, 0] with the member ending at -t; internal ones in [-inset-t, -inset].
            var now = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
            for (int i = 0; i < faces.Count; i++)
            {
                var (r, p0, n) = faces[i];
                var cap = fresh.OrderBy(b => b.Com.DistanceTo(p0)).First();
                var (lo, hi) = Extent(cap.Body, p0, n);
                double wantHi = dir switch { 0 => t, 1 => 0, _ => -inset }, wantLo = wantHi - t;
                if (Math.Abs(lo - wantLo) > PlateTolM || Math.Abs(hi - wantHi) > PlateTolM)
                    yield return $"the cap on {r.Label} spans {Units.Mm(lo)}..{Units.Mm(hi)} mm along the face normal, not {Units.Mm(wantLo)}..{Units.Mm(wantHi)} ({dirName})";
                if (now.FirstOrDefault(b => b.Name == members[i]) is { } member)
                {
                    double end = Extent(member, p0, n).Hi, wantEnd = dir == 1 ? -t : 0;
                    if (Math.Abs(end - wantEnd) > PlateTolM)
                        yield return $"the member '{members[i]}' now ends {Units.Mm(end)} mm from its old end, not {Units.Mm(wantEnd)} ({dirName})";
                }
                fresh.Remove(cap);
            }
            checks.Add($"{faces.Count} cap(s), each a {q.ThicknessMm} mm plate {dirName switch { "outward" => "beyond its end face", "inward" => "in the member's last " + q.ThicknessMm + " mm", _ => "inside the bore" }}");
        }
        return (CapSpec, api, attempts, Intent, _ => (NewBodies(part, known).Select(b => Info(b.Body, null)).ToList(), null));
    }

    // ------------------------------------------------------------------ gusset

    private static (FeatureSpec, object?, List<string>, Func<IFeature, Snapshot, List<string>, IEnumerable<string>>, Report) Gusset(
        SwContext ctx, IPartDoc part, IModelDoc2 d, WeldmentFinishRequest q, List<BodyState> states)
    {
        double t = Units.M(q.ThicknessMm!.Value), d1 = Units.M(q.LegsMm![0]), d2 = Units.M(q.LegsMm[1]);
        var known = states.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var loc = WeldmentSpec.GussetLocation(q.Location);
        var side = WeldmentSpec.GussetSide(q.Side);
        var faces = Planar(ctx, d, q.Faces, "faces", "a gusset joins two flat faces");
        var (n1, n2) = (faces[0].Normal, faces[1].Normal);
        double sin = n1.Cross(n2).Length;
        if (sin < Math.Sin(Units.Rad(5)))
            throw SwException.InvalidArgument($"{faces[0].R.Label} and {faces[1].R.Label} are parallel; a gusset joins faces that meet at an angle.");
        foreach (var (r, _, _) in faces)
            if (!r.Select(d, true, 1))
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS would not select {r.Label}.");
        var attempts = new List<string>();
        object? api = null;
        try
        {
            // Triangle profile: D1, D2 the legs; D3, D4 and the angle belong to the polygon profile. The chamfer (D5, D6) is
            // left out: SOLIDWORKS ignored it both by lengths and by an angle (VERIFIED, the plate kept its full triangle).
            api = d.FeatureManager.InsertGussetFeature3(t, side.Code, loc.Code, false, d1, d2, Math.Min(d1, d2) / 2, Math.PI / 4, Math.Min(d1, d2) / 2,
                false, 0, 0, false, false, false, 0, 0, Math.PI / 4, true, false);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            attempts.Add($"InsertGussetFeature3: {e.Message}");
        }

        IEnumerable<string> Intent(IFeature main, Snapshot after, List<string> checks)
        {
            if ((object?)main.GetDefinition() is IGussetFeatureData gd)
            {
                if (Math.Abs(gd.Thickness - t) > 1e-9) yield return $"its thickness reads {Units.Mm(gd.Thickness)} mm, not {q.ThicknessMm} mm";
                if (Math.Abs(gd.ProfileDistance1 - d1) > 1e-9 || Math.Abs(gd.ProfileDistance2 - d2) > 1e-9)
                    yield return $"its legs read {Units.Mm(gd.ProfileDistance1)} and {Units.Mm(gd.ProfileDistance2)} mm";
                checks.Add("thickness and legs read back");
            }
            var fresh = NewBodies(part, known);
            if (fresh.Count != 1)
            {
                yield return $"{fresh.Count} new bodies (one gusset plate expected)";
                yield break;
            }
            var g = fresh[0];
            var e = n1.Cross(n2).Normalized(); // along the edge where the faces meet: the plate's thickness direction
            var (lo, hi) = Extent(g.Body, g.Com, e);
            if (Math.Abs(hi - lo - t) > PlateTolM) yield return $"the plate is {Units.Mm(hi - lo)} mm thick, not {q.ThicknessMm} mm";
            // Each leg runs along its face: the plate reaches d1 out along faces[0] (away from faces[1]) and d2 along faces[1].
            double reach1 = Extent(g.Body, faces[1].Point, n2).Hi, reach2 = Extent(g.Body, faces[0].Point, n1).Hi;
            if (Math.Abs(reach1 - d1 * sin) > PlateTolM || Math.Abs(reach2 - d2 * sin) > PlateTolM)
                yield return $"the plate reaches {Units.Mm(reach1)} mm from faces[1] and {Units.Mm(reach2)} mm from faces[0], not {q.LegsMm[0]} and {q.LegsMm[1]}";
            double vol = 0.5 * sin * d1 * d2 * t;
            if (Math.Abs(g.Volume - vol) > 0.01 * vol)
                yield return $"its volume is {Units.Mm3(g.Volume)} mm³, not ½·{q.LegsMm[0]}·{q.LegsMm[1]}·{q.ThicknessMm} = {Units.Mm3(vol)} mm³";
            checks.Add($"one gusset plate {q.ThicknessMm} mm thick in the corner, legs {q.LegsMm[0]} along faces[0] and {q.LegsMm[1]} along faces[1], " +
                       $"{Units.Mm3(g.Volume)} mm³ (the triangle's {Units.Mm3(vol)} mm³)");
        }
        return (GussetSpec, api, attempts, Intent, _ => (NewBodies(part, known).Select(b => Info(b.Body, null)).ToList(), null));
    }

    // ------------------------------------------------------------------ weld bead

    /// <summary>
    /// A cosmetic weld bead (IFeatureManager.InsertCosmeticWeldBead2 with the weld-from faces on mark 4 and the weld-to faces
    /// on mark 8, as arrays): the bead SOLIDWORKS uses in weld tables and for weld lengths. The solid fillet bead
    /// (InsertFilletBeadFeature3) made only an empty 'Detail Folder' in SOLIDWORKS 2025 with every argument form tried.
    /// Its side, tangent propagation and intermittent spacing go through ICosmeticWeldBeadFeatureData.
    /// </summary>
    private static (FeatureSpec, object?, List<string>, Func<IFeature, Snapshot, List<string>, IEnumerable<string>>, Report) Bead(
        SwContext ctx, IPartDoc part, IModelDoc2 d, WeldmentFinishRequest q, List<BodyState> states)
    {
        double s = Units.M(q.SizeMm!.Value);
        var side = WeldmentSpec.BeadSide(q.Side);
        bool intermittent = q.LengthMm is not null;
        List<IFace2> From() => EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face).Select(r => (IFace2)r.Obj).ToList();
        List<IFace2> To() => EntityResolver.ResolveAll(ctx, d, q.Faces2, "faces2", EntityKind.Face).Select(r => (IFace2)r.Obj).ToList();
        var attempts = new List<string>();
        var before = Snapshot.Take(d);
        object? api = null;
        foreach (var (how, dispatch) in new[] { ("face arrays", false), ("IDispatch arrays", true) })
        {
            // Found again for each try: a rolled-back attempt rebuilds the part.
            var from = From();
            var to = To();
            d.ClearSelection2(true);
            var data = d.Sel().CreateSelectData();
            bool selected = true;
            foreach (var (f, mark) in from.Select(f => (f, 4)).Concat(to.Select(f => (f, 8))))
            {
                data.Mark = mark;
                selected &= ((IEntity)f).Select4(true, data);
            }
            if (!selected)
            {
                attempts.Add($"{how}: SOLIDWORKS would not select the faces");
                continue;
            }
            try
            {
                api = d.FeatureManager.InsertCosmeticWeldBead2((int)swCosmeticWeldBeadMode_e.swCosmeticWeldBeadMode_WeldGeometry,
                    dispatch ? ComArgs.DispatchArray(from) : from.ToArray(), dispatch ? ComArgs.DispatchArray(to) : to.ToArray(), s);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                attempts.Add($"InsertCosmeticWeldBead2 with {how}: {e.Message}");
            }
            d.ClearSelection2(true);
            var made = BodyKit.NewFeatures(d, before);
            if (made.Any(f => f.GetTypeName2() == "CosmeticWeldBead")) break;
            attempts.Add($"InsertCosmeticWeldBead2 with {how}: " + (made.Count == 0 ? "nothing"
                : string.Join(", ", made.Select(f => $"'{f.Name}' ({f.GetTypeName2()})")) + ", no weld bead"));
            if (made.Count > 0) attempts.Add(FeatureVerifier.Rollback(ctx, d, before));
            api = null;
        }
        var beads = BodyKit.NewFeatures(d, before).Where(f => f.GetTypeName2() == "CosmeticWeldBead").ToList();
        if (beads.Count == 0)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no weld bead ({string.Join("; ", attempts)}).", Hint("bead"));
        if (side.Code != 1 || q.Tangent || intermittent)
            foreach (var bead in beads)
            {
                if ((object?)bead.GetDefinition() is not ICosmeticWeldBeadFeatureData wd) continue;
                wd.AccessSelections((ModelDoc2)d, null);
                if (q.Tangent) wd.TangentPropagation = true;
                else wd.Side = side.Code;
                if (intermittent)
                {
                    wd.IntermittentWeld = true;
                    wd.GapOrPitch = false; // pitch: bead start to bead start
                    wd.IntermittentWeldLength = Units.M(q.LengthMm!.Value);
                    wd.Pitch = Units.M(q.PitchMm!.Value);
                }
                if (!bead.ModifyDefinition(wd, d, null))
                {
                    wd.ReleaseSelectionAccess();
                    attempts.Add($"'{bead.Name}': ModifyDefinition refused the side, tangent or intermittent settings");
                }
            }
        double? beadLength = null;

        IEnumerable<string> Intent(IFeature main, Snapshot after, List<string> checks)
        {
            if (Math.Abs(after.Volume - before.Volume) > 1e-12) yield return "a cosmetic weld bead should change no volume";
            var from = From();
            var to = To();
            double length = 0;
            int edges = 0;
            foreach (var bead in BodyKit.NewFeatures(d, before).Where(f => f.GetTypeName2() == "CosmeticWeldBead"))
            {
                if ((object?)bead.GetDefinition() is not ICosmeticWeldBeadFeatureData wd)
                {
                    yield return $"'{bead.Name}' has no weld bead definition";
                    continue;
                }
                if (Math.Abs(wd.BeadSize - s) > 1e-9) yield return $"'{bead.Name}' reads size {Units.Mm(wd.BeadSize)} mm, not {q.SizeMm} mm";
                if (q.Tangent && !wd.TangentPropagation) yield return $"'{bead.Name}' does not follow tangent edges";
                if (!q.Tangent && wd.Side != side.Code) yield return $"'{bead.Name}' reads side {wd.Side}, not {side.Code} ({side.Name})";
                if (intermittent && (!wd.IntermittentWeld || wd.GapOrPitch || Math.Abs(wd.IntermittentWeldLength - Units.M(q.LengthMm!.Value)) > 1e-9 ||
                                     Math.Abs(wd.Pitch - Units.M(q.PitchMm!.Value)) > 1e-9))
                    yield return $"'{bead.Name}' reads intermittent {wd.IntermittentWeld}, length {Units.Mm(wd.IntermittentWeldLength)} mm, pitch {Units.Mm(wd.Pitch)} mm";
                // The edges the weld runs along lie where the two face sets meet.
                foreach (var e in ComArgs.Objects<IEdge>((object?)wd.GetReferenceEdges()))
                {
                    var g = Topology.Describe(e);
                    double d1 = from.Min(f => Topology.DistanceToFace(f, g.Mid)), d2 = to.Min(f => Topology.DistanceToFace(f, g.Mid));
                    if (side.Code == 1 && !q.Tangent && (d1 > 1e-6 || d2 > 1e-6))
                        yield return $"'{bead.Name}' runs along an edge {Units.Mm(Math.Max(d1, d2))} mm away from where the faces meet";
                    length += g.Length;
                    edges++;
                }
            }
            if (edges == 0) yield return "the weld bead runs along no edge";
            beadLength = Units.Mm(length);
            checks.Add($"weld bead size {q.SizeMm} mm{(side.Code != 1 ? $", {side.Name}" : "")}{(q.Tangent ? ", tangent" : "")}" +
                       $"{(intermittent ? $", {q.LengthMm} mm every {q.PitchMm} mm" : "")} read back; it runs {Units.Mm(length)} mm along {edges} edge(s)" +
                       (side.Code == 1 && !q.Tangent ? " where the face sets meet" : ""));
        }
        return (BeadSpec, api, attempts, Intent, _ => ([], beadLength));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A solid body as it was before the feature: its name, volume (m³) and centre of mass.</summary>
    private sealed record BodyState(string Name, double Volume, Vec3 Com);

    private static List<BodyState> SolidStates(IPartDoc part) =>
        part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).Select(b =>
        {
            var mp = ComArgs.Doubles((object?)b.GetMassProperties(1.0)); // [cx, cy, cz, volume, …]
            return new BodyState(b.Name, mp.Length >= 4 ? mp[3] : 0, mp.Length >= 3 ? new Vec3(mp[0], mp[1], mp[2]) : Vec3.Zero);
        }).ToList();

    /// <summary>
    /// The bodies <paramref name="names"/> after a feature changed them. A modified body takes the feature's name (VERIFIED:
    /// the trimmed post became 'Trim/Extend3'), so each is found again among the bodies that aren't other, untouched ones,
    /// as the one whose centre of mass is nearest where its own was.
    /// </summary>
    private static List<(BodyState Was, IBody2 Now, double Volume)> Match(IPartDoc part, List<BodyState> states, IReadOnlyList<string> names)
    {
        var others = states.Where(s => !names.Contains(s.Name)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var left = NewBodies(part, others);
        var list = new List<(BodyState, IBody2, double)>();
        foreach (var was in states.Where(s => names.Contains(s.Name)))
        {
            var hit = left.OrderBy(b => b.Com.DistanceTo(was.Com)).FirstOrDefault();
            if (hit is null) continue;
            left.Remove(hit);
            list.Add((was, hit.Body, hit.Volume));
        }
        return list;
    }

    /// <summary>
    /// Bodies by reference: a body (by name or id) or any face of it. A weldment member's body has its feature's name
    /// ("square tube 40 x 40 x 4(1)"), so names here are body names.
    /// </summary>
    private static List<IBody2> BodiesOf(SwContext ctx, IModelDoc2 d, IReadOnlyList<EntityRef>? refs, string arg)
    {
        var list = new List<IBody2>();
        foreach (var r in EntityResolver.ResolveAll(ctx, d, AsBodies(refs), arg, EntityKind.Body, EntityKind.Face))
        {
            var b = r.Obj as IBody2 ?? (r.Obj is IFace2 f ? (object?)f.GetBody() as IBody2 : null)
                    ?? throw new SwException(ErrorCodes.EntityNotFound, $"{r.Label} has no body.");
            if (!list.Any(x => x.Name == b.Name)) list.Add(b);
        }
        return list;
    }

    /// <summary>References by name without a type, read as body names (they would otherwise find the member's feature first).</summary>
    private static IReadOnlyList<EntityRef>? AsBodies(IReadOnlyList<EntityRef>? refs) =>
        refs?.Select(r => r.Name is not null && r.Type is null && r.Id is null && r.At is null && r.Where is null
            ? new EntityRef { Name = r.Name, Type = "body" }
            : r).ToList();

    /// <summary>Planar faces with a point inside each and its outward normal.</summary>
    private static List<(Resolved R, Vec3 Point, Vec3 Normal)> Planar(SwContext ctx, IModelDoc2 d, IReadOnlyList<EntityRef>? refs, string arg, string why)
    {
        var list = new List<(Resolved, Vec3, Vec3)>();
        foreach (var r in EntityResolver.ResolveAll(ctx, d, refs, arg, EntityKind.Face))
        {
            var f = (IFace2)r.Obj;
            var g = Topology.Describe(f);
            if (g.Surface != "plane" || g.Normal is not Vec3 n)
                throw SwException.InvalidArgument($"{r.Label} is a {g.Surface}, not a flat face: {why}.");
            list.Add((r, Topology.InteriorPoint(f), n));
        }
        return list;
    }

    /// <summary>A body's extent along <paramref name="n"/> measured from <paramref name="p0"/> (its vertices; m).</summary>
    private static (double Lo, double Hi) Extent(IBody2 body, Vec3 p0, Vec3 n)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in body.Vertices())
        {
            double x = (Vec3.From(v.Point()) - p0).Dot(n);
            lo = Math.Min(lo, x);
            hi = Math.Max(hi, x);
        }
        return lo <= hi ? (lo, hi) : (0, 0);
    }

    private static WeldmentFinishBody Info(IBody2 b, double? volumeBefore, string? previousName = null)
    {
        var mp = ComArgs.Doubles((object?)b.GetMassProperties(1.0));
        var box = b.Box();
        return new WeldmentFinishBody
        {
            Name = b.Name,
            PreviousName = previousName,
            VolumeMm3 = Units.Mm3(mp.Length >= 4 ? mp[3] : 0),
            VolumeBeforeMm3 = volumeBefore is double v ? Units.Mm3(v) : null,
            BoxMm = box.Length == 6 ? Units.Mm(box) : null,
        };
    }
}
