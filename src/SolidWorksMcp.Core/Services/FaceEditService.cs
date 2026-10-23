using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of face_edit: direct edits of a body's faces (no feature history needed, e.g. on an imported STEP).</summary>
public sealed record FaceEditRequest
{
    public string? Doc { get; init; }
    /// <summary>delete | offset | move.</summary>
    public string? Op { get; init; }
    public IReadOnlyList<EntityRef>? Faces { get; init; }
    /// <summary>delete: patch (default) | fill | tangent_fill | none.</summary>
    public string? Heal { get; init; }
    /// <summary>offset: mm along each face's outward normal (positive adds material).</summary>
    public double? DistanceMm { get; init; }
    /// <summary>move: [dx, dy, dz] mm.</summary>
    public double[]? TranslateMm { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

/// <summary>
/// face_edit: Delete Face (patch / fill / tangent fill / delete only) and Move Face (offset along the normals, translate)
/// on a part's bodies (api notes §6). Tier A is the usual feature check; Tier B checks where each face ended up: a
/// deleted face is gone from its spot, an offset or moved plane lies at its new distance, an offset cylinder has its new
/// radius and a moved one its new axis.
/// </summary>
public sealed class FaceEditService(SwSession session)
{
    private const double PosTol = 1e-6; // m

    private static readonly FeatureSpec DeleteSpec = new("face_edit", ["DelFace"], VolumeRule.Any, RequireFaces: false); // VERIFIED type names
    // A moved hole keeps its volume and face count: Tier B (the new positions) decides, not a volume rule.
    private static readonly FeatureSpec MoveSpec = new("face_edit", ["MoveFace"], VolumeRule.Any, RequireFaces: false);

    public Task<FeatureResult> EditAsync(FaceEditRequest q, CancellationToken ct = default)
    {
        string op = AsmParse.Norm(q.Op) switch
        {
            "delete" or "remove" => "delete",
            "offset" => "offset",
            "move" or "translate" => "move",
            null => throw SwException.InvalidArgument("'op' is required: delete, offset or move."),
            _ => throw SwException.InvalidArgument($"Unknown op '{q.Op}'.", "Use delete (with heal), offset (distanceMm) or move (translateMm)."),
        };
        if (q.Faces is not { Count: > 0 })
            throw SwException.InvalidArgument("'faces' is required, e.g. [{id:'F3'}] or [{where:{surface:'cylinder', radiusMm:3}, expect:4}].");
        int heal = op == "delete" ? HealOption(q.Heal) : -1;
        if (op != "delete" && q.Heal is not null) throw SwException.InvalidArgument("'heal' only applies to op 'delete'.");
        double dist = 0;
        Vec3 move = Vec3.Zero;
        if (op == "offset")
        {
            if (q.DistanceMm is not double dm || !double.IsFinite(dm) || dm == 0)
                throw SwException.InvalidArgument("op 'offset' needs distanceMm (non-zero; positive moves the faces out of the material, adding to it).");
            dist = Units.M(dm);
        }
        else if (q.DistanceMm is not null) throw SwException.InvalidArgument("'distanceMm' only applies to op 'offset'.");
        if (op == "move")
        {
            move = Units.PointM(q.TranslateMm ?? throw SwException.InvalidArgument("op 'move' needs translateMm [dx, dy, dz]."), "translateMm");
            if (move.Length < 1e-9) throw SwException.InvalidArgument("translateMm must not be [0, 0, 0].");
        }
        else if (q.TranslateMm is not null) throw SwException.InvalidArgument("'translateMm' only applies to op 'move'.");
        return session.RunAsync("face_edit", ctx => Edit(ctx, q, op, heal, dist, move), RunOptions.Write(q.Doc), ct);
    }

    private static int HealOption(string? s) => AsmParse.Norm(s) switch
    {
        null or "patch" or "heal" => (int)swFaceDeleteOption_e.swFaceDelete_Patch,
        "fill" => (int)swFaceDeleteOption_e.swFaceDelete_Fill,
        "tangent_fill" or "fill_tangent" => (int)swFaceDeleteOption_e.swFaceDelete_FillWithTangent,
        "none" or "delete" => (int)swFaceDeleteOption_e.swFaceDelete_Default,
        _ => throw SwException.InvalidArgument($"Unknown heal '{s}'.", "Use patch (default: the neighbours grow over the gap), fill, tangent_fill or none."),
    };

    private static string HealName(int h) => h switch
    {
        (int)swFaceDeleteOption_e.swFaceDelete_Patch => "patch",
        (int)swFaceDeleteOption_e.swFaceDelete_Fill => "fill",
        (int)swFaceDeleteOption_e.swFaceDelete_FillWithTangent => "tangent_fill",
        _ => "none",
    };

    /// <summary>What a face was before the edit: its geometry, a point inside it and its outward normal there.</summary>
    private sealed record Before(string Label, FaceGeom G, Vec3 P, Vec3 N, double AreaM2);

    private static FeatureResult Edit(SwContext ctx, FaceEditRequest q, string op, int heal, double dist, Vec3 move)
    {
        var part = ctx.Part();
        var d = (IModelDoc2)part;
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
        var refs = EntityResolver.ResolveAll(ctx, d, q.Faces, "faces", EntityKind.Face);
        var faces = refs.Select(r => (IFace2)r.Obj).ToList();
        var was = refs.Select((r, i) => Describe(r.Label, faces[i])).ToList();
        if (op != "delete")
            foreach (var b in was.Where(b => b.G.Surface is not ("plane" or "cylinder")))
                ctx.Warn($"{b.Label} is a {b.G.Surface}: its new position is not checked (only planes and cylinders are).");

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var before = Snapshot.Take(d);
        d.ClearSelection2(true);
        foreach (var r in refs)
            if (!r.Select(d, true, 1))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");

        object? api;
        FeatureSpec spec;
        string hint;
        if (op == "delete")
        {
            api = d.Extension.InsertDeleteFace(heal);
            spec = DeleteSpec;
            hint = heal == (int)swFaceDeleteOption_e.swFaceDelete_Default
                ? "heal 'none' leaves the body open (a surface body)."
                : "Patch needs neighbours that can grow over the gap (holes, fillets, bosses); try heal 'fill', or fewer faces at once.";
        }
        else
        {
            // Offset moves each face along its normal; translate moves them all by the triad vector (end condition blind).
            api = op == "offset"
                ? d.FeatureManager.InsertMoveFace3((int)swMoveFaceType_e.swMoveFaceTypeOffset, dist < 0, 0, Math.Abs(dist), null, null,
                    (int)swEndConditions_e.swEndCondBlind, 0)
                : d.FeatureManager.InsertMoveFace3((int)swMoveFaceType_e.swMoveFaceTypeTranslate, false, 0, 0, new[] { move.X, move.Y, move.Z }, null,
                    (int)swEndConditions_e.swEndCondBlind, 0);
            spec = MoveSpec;
            hint = "The neighbouring faces must be able to stretch to the new position; a smaller distance or fewer faces may work.";
        }
        d.ClearSelection2(true);

        var o = BodyKit.Verify(ctx, d, before, spec, (_, after, checks) => op switch
        {
            "delete" => DeleteIntent(part, was, heal, before, after, checks),
            "offset" => OffsetIntent(part, was, dist, checks),
            _ => MoveIntent(part, was, move, checks),
        }, q.KeepOnFailure, api, hint, guessedType: true);
        if (!string.IsNullOrWhiteSpace(q.Name)) Rename(ctx, o.Main, q.Name!);
        return FeatureVerifier.Result(ctx, d, o, resolved: refs.Select(r => r.Info("faces")).ToList());
    }

    private static Before Describe(string label, IFace2 f)
    {
        var g = Topology.Describe(f);
        var p = Topology.InteriorPoint(f);
        double area = f.GetArea();
        return new Before(label, g, p, Topology.OutwardNormal(f, g, p), area);
    }

    // ------------------------------------------------------------------ Tier B

    private static List<(IFace2 F, FaceGeom G)> SolidFaces(IPartDoc part) =>
        part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false).SelectMany(b => b.Faces()).Select(f => (f, Topology.Describe(f))).ToList();

    private static List<(IFace2 F, FaceGeom G)> AllFaces(IPartDoc part) =>
        part.AllBodies(visibleOnly: false).SelectMany(b => b.Faces()).Select(f => (f, Topology.Describe(f))).ToList();

    /// <summary>True when a face of the same surface (plane: normal and offset; cylinder: axis and radius) passes through p.</summary>
    private static bool SameSurfaceAt(List<(IFace2 F, FaceGeom G)> faces, Before b, Vec3 p) =>
        faces.Any(x => Topology.DistanceToFace(x.F, p) <= PosTol && SameSurface(x.G, b.G));

    private static bool SameSurface(FaceGeom a, FaceGeom b)
    {
        if (a.Surface != b.Surface) return false;
        return a.Surface switch
        {
            "plane" => a.Normal is Vec3 na && b.Normal is Vec3 nb && na.IsParallelTo(nb, 1e-6),
            "cylinder" or "cone" => Coaxial(a, b) && Math.Abs((a.Radius ?? 0) - (b.Radius ?? 0)) < PosTol,
            _ => true,
        };
    }

    /// <summary>The same axis line (either sense).</summary>
    private static bool Coaxial(FaceGeom a, FaceGeom b) =>
        a.Axis is Vec3 xa && b.Axis is Vec3 xb && a.AxisOrigin is Vec3 oa && b.AxisOrigin is Vec3 ob &&
        xa.IsParallelTo(xb, 1e-6) && AsmMath.PointToLine(oa, xa, ob) < PosTol;

    private static IEnumerable<string> DeleteIntent(IPartDoc part, List<Before> was, int heal, Snapshot before, Snapshot after, List<string> checks)
    {
        var faces = AllFaces(part);
        var stayed = was.Where(b => SameSurfaceAt(faces, b, b.P)).Select(b => b.Label).ToList();
        if (stayed.Count > 0)
        {
            yield return $"{string.Join(", ", stayed)} still there";
            yield break;
        }
        checks.Add($"{was.Count} face(s) gone from where they were (heal {HealName(heal)})");
        if (heal == (int)swFaceDeleteOption_e.swFaceDelete_Default)
        {
            checks.Add($"solid bodies {before.SolidBodies} → {after.SolidBodies} (delete only leaves the body open)");
            yield break;
        }
        if (after.SolidBodies < before.SolidBodies)
        {
            yield return $"the body is no longer solid (solid bodies {before.SolidBodies} → {after.SolidBodies}); the gap was not closed";
            yield break;
        }
        checks.Add($"still {after.SolidBodies} solid body(ies), ΔV {Units.Mm3(after.Volume - before.Volume)} mm³");
    }

    private static IEnumerable<string> OffsetIntent(IPartDoc part, List<Before> was, double dist, List<string> checks)
    {
        var faces = SolidFaces(part);
        int ok = 0, skipped = 0;
        var missing = new List<string>();
        foreach (var b in was)
        {
            if (b.N.Length < 0.5 || b.G.Surface is not ("plane" or "cylinder")) { skipped++; continue; }
            var target = b.P + b.N * dist;
            bool hit = b.G.Surface switch
            {
                "plane" => faces.Any(x => x.G.Surface == "plane" && x.G.Normal is Vec3 n && n.Dot(b.N) > 1 - 1e-9 &&
                                         Math.Abs(n.Dot(target) - n.Dot(PointOn(x.F, target))) < PosTol),
                _ => faces.Any(x => x.G.Surface == "cylinder" && Coaxial(x.G, b.G) && Math.Abs((x.G.Radius ?? 0) - CylinderRadiusAfter(b, dist)) < PosTol),
            };
            if (hit) ok++;
            else missing.Add(b.Label);
        }
        if (missing.Count > 0)
        {
            yield return $"{string.Join(", ", missing)} not found {Units.Mm(Math.Abs(dist))} mm {(dist > 0 ? "out of" : "into")} the material";
            yield break;
        }
        if (ok > 0) checks.Add($"{ok} face(s) now {Units.Mm(Math.Abs(dist))} mm {(dist > 0 ? "outward (more material)" : "inward (less material)")} along their normals");
        if (skipped > 0) checks.Add($"{skipped} face(s) not position-checked (not a plane or cylinder)");
    }

    /// <summary>A cylinder face moved outward by d: a boss grows (r + d), a hole shrinks (r − d).</summary>
    private static double CylinderRadiusAfter(Before b, double d)
    {
        var radial = b.P - b.G.AxisOrigin!.Value;
        var ax = b.G.Axis!.Value;
        radial -= ax * radial.Dot(ax);
        bool boss = radial.Dot(b.N) > 0;
        return (b.G.Radius ?? 0) + (boss ? d : -d);
    }

    private static IEnumerable<string> MoveIntent(IPartDoc part, List<Before> was, Vec3 move, List<string> checks)
    {
        var faces = SolidFaces(part);
        int ok = 0, skipped = 0;
        var missing = new List<string>();
        foreach (var b in was)
        {
            bool hit;
            switch (b.G.Surface)
            {
                case "plane" when b.G.Normal is Vec3 n0:
                {
                    // A plane moved by t lies at n·x = D + n·t; its own in-plane motion is invisible.
                    double want = n0.Dot(b.P + move);
                    hit = faces.Any(x => x.G.Surface == "plane" && x.G.Normal is Vec3 n && n.Dot(n0) > 1 - 1e-9 &&
                                         Math.Abs(n.Dot(PointOn(x.F, b.P + move)) - want) < PosTol);
                    break;
                }
                case "cylinder" when b.G.Axis is Vec3 && b.G.AxisOrigin is Vec3 o:
                {
                    var o2 = o + move;
                    var moved = b.G with { AxisOrigin = o2 };
                    hit = faces.Any(x => x.G.Surface == "cylinder" && Coaxial(x.G, moved) && Math.Abs((x.G.Radius ?? 0) - (b.G.Radius ?? 0)) < PosTol);
                    break;
                }
                default:
                    skipped++;
                    continue;
            }
            if (hit) ok++;
            else missing.Add(b.Label);
        }
        if (missing.Count > 0)
        {
            yield return $"{string.Join(", ", missing)} not found moved by [{TopoQuery.Fmt(Units.Mm(move))}] mm";
            yield break;
        }
        if (ok > 0) checks.Add($"{ok} face(s) moved by [{TopoQuery.Fmt(Units.Mm(move))}] mm");
        if (skipped > 0) checks.Add($"{skipped} face(s) not position-checked (not a plane or cylinder)");
    }

    /// <summary>The point of <paramref name="f"/> closest to <paramref name="p"/> (p itself when it can't be read).</summary>
    private static Vec3 PointOn(IFace2 f, Vec3 p)
    {
        var cp = f.ClosestPoint(p);
        return cp.Length >= 3 ? Vec3.From(cp) : p;
    }

    private static void Rename(SwContext ctx, IFeature f, string name)
    {
        try
        {
            f.Name = name.Trim();
            if (!string.Equals(f.Name, name.Trim(), StringComparison.Ordinal))
                ctx.Warn($"SOLIDWORKS kept the name '{f.Name}' (the name '{name}' may be taken).");
        }
        catch (System.Runtime.InteropServices.COMException e) when (!ComArgs.IsDisconnect(e))
        {
            ctx.Warn($"Could not rename '{f.Name}' to '{name}': {e.Message}");
        }
    }
}
