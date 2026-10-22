using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>Kinds of entities an <see cref="EntityRef"/> can resolve to.</summary>
public enum EntityKind
{
    Face, Edge, Vertex, Plane, Axis, Point, Sketch, SketchSegment, SketchPoint, Feature, Body, Component, CoordSys, Origin,
}

/// <summary>A resolved entity: the live COM object plus how to select it.</summary>
internal sealed class Resolved
{
    public required object Obj { get; init; }
    public required EntityKind Kind { get; init; }

    /// <summary>Human-readable description for results and errors ("face F3").</summary>
    public required string Label { get; init; }

    /// <summary>The registry id for topological entities (F/E/V/B), else null.</summary>
    public string? Id { get; init; }

    /// <summary>World-space pick point (mm-convertible) when known.</summary>
    public Vec3? Pick { get; init; }

    /// <summary>How far the given point was from the entity (m), for point references.</summary>
    public double? Distance { get; init; }

    /// <summary>Selects the entity with the given mark. Returns false if SOLIDWORKS refused.</summary>
    public bool Select(IModelDoc2 doc, bool append, int mark)
    {
        var sm = doc.Sel();
        var data = sm.CreateSelectData();
        data.Mark = mark;
        return Obj switch
        {
            IFeature f => f.Select2(append, mark),
            IComponent2 c => c.Select4(append, data, false),
            IBody2 b => b.Select2(append, data),
            ISketchSegment s => s.Select4(append, data),
            ISketchPoint p => p.Select4(append, data),
            IEntity e => e.Select4(append, data),
            _ => false,
        };
    }

    /// <summary>"resolved" echo for results: what an input reference turned into.</summary>
    public ResolvedInfo Info(string input) => new()
    {
        Input = input,
        Id = Id,
        Type = EntityResolver.KindName(Kind),
        Label = Label,
        DistanceMm = Distance is double d ? Units.Mm(d) : null,
        PickMm = Pick is Vec3 p ? Units.Mm(p) : null,
    };
}

/// <summary>
/// Turns <see cref="EntityRef"/>s into live entities: by id (persistent reference), by query (<c>where</c>), by point
/// (geometric nearest-entity search over all bodies, independent of the view; component geometry is mapped through the
/// component transform) or by name. Errors are ENTITY_NOT_FOUND / ENTITY_AMBIGUOUS / ENTITY_STALE with fix hints.
/// </summary>
internal static class EntityResolver
{
    public const double DefaultTolMm = 0.01;

    /// <summary>Resolves exactly one entity.</summary>
    public static Resolved Resolve(SwContext ctx, IModelDoc2 doc, EntityRef? r, string argName, params EntityKind[] allowed)
    {
        if (r is null) throw SwException.InvalidArgument($"'{argName}' is required.");
        var all = ResolveMany(ctx, doc, r, argName, allowed);
        if (all.Count != 1)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"'{argName}' matched {all.Count} entities: {string.Join(", ", all.Take(8).Select(x => x.Label))}.",
                "Narrow the query or pass one id.");
        return all[0];
    }

    /// <summary>Resolves a list of references (a 'where' reference may expand to several entities).</summary>
    public static List<Resolved> ResolveAll(SwContext ctx, IModelDoc2 doc, IEnumerable<EntityRef>? refs, string argName,
        params EntityKind[] allowed)
    {
        var list = new List<Resolved>();
        int i = 0;
        foreach (var r in refs ?? [])
            list.AddRange(ResolveMany(ctx, doc, r, $"{argName}[{i++}]", allowed));
        if (list.Count == 0) throw SwException.InvalidArgument($"'{argName}' must contain at least one entity reference.");
        // De-duplicate (the same edge from two references).
        return list.GroupBy(x => x.Id ?? x.Label).Select(g => g.First()).ToList();
    }

    private static List<Resolved> ResolveMany(SwContext ctx, IModelDoc2 doc, EntityRef r, string argName, EntityKind[] allowed)
    {
        List<Resolved> res;
        if (!string.IsNullOrWhiteSpace(r.Id)) res = [ById(ctx, doc, r.Id!)];
        else if (r.Where is not null) res = ByQuery(ctx, doc, r, argName, allowed);
        else if (r.At is not null) res = [ByPoint(ctx, doc, r, argName, allowed)];
        else if (!string.IsNullOrWhiteSpace(r.Name)) res = [ByName(ctx, doc, r, argName, allowed)];
        else throw SwException.InvalidArgument($"'{argName}' needs 'id', 'where', 'at' or 'name'.");

        foreach (var x in res)
            if (allowed.Length > 0 && !allowed.Contains(x.Kind))
                throw new SwException(ErrorCodes.InvalidArgument,
                    $"'{argName}' resolved to {x.Label}, but this parameter needs {string.Join(" or ", allowed.Select(KindName))}.");
        return res;
    }

    public static string KindName(EntityKind k) => k switch
    {
        EntityKind.SketchSegment => "sketch_segment",
        EntityKind.SketchPoint => "sketch_point",
        EntityKind.CoordSys => "coordsys",
        _ => k.ToString().ToLowerInvariant(),
    };

    public static EntityKind? ParseKind(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "face" => EntityKind.Face,
        "edge" => EntityKind.Edge,
        "vertex" => EntityKind.Vertex,
        "plane" => EntityKind.Plane,
        "axis" => EntityKind.Axis,
        "point" or "refpoint" => EntityKind.Point,
        "sketch" => EntityKind.Sketch,
        "sketch_segment" or "segment" or "sketchsegment" => EntityKind.SketchSegment,
        "sketch_point" or "sketchpoint" => EntityKind.SketchPoint,
        "feature" => EntityKind.Feature,
        "body" => EntityKind.Body,
        "component" => EntityKind.Component,
        "coordsys" or "coordinate_system" => EntityKind.CoordSys,
        "origin" => EntityKind.Origin,
        _ => throw SwException.InvalidArgument($"Unknown entity type '{type}'.",
            "Use face, edge, vertex, plane, axis, point, sketch, sketch_segment, sketch_point, feature, body, component or origin."),
    };

    public static char Prefix(EntityKind k) => k switch
    {
        EntityKind.Face => 'F',
        EntityKind.Edge => 'E',
        EntityKind.Vertex => 'V',
        EntityKind.Body => 'B',
        _ => 'X',
    };

    // ---------------------------------------------------------------- by id

    private static Resolved ById(SwContext ctx, IModelDoc2 doc, string id)
    {
        object obj = ctx.Entities.Resolve(doc, id);
        ctx.Entities.TryGet(id, out var rec);
        var kind = obj switch
        {
            IFace2 => EntityKind.Face,
            IEdge => EntityKind.Edge,
            IVertex => EntityKind.Vertex,
            IBody2 => EntityKind.Body,
            IFeature => EntityKind.Feature,
            _ => throw new SwException(ErrorCodes.Internal, $"Id '{id}' resolved to an unsupported object."),
        };
        return new Resolved { Obj = obj, Kind = kind, Id = rec.Id, Label = $"{KindName(kind)} {rec.Id}" };
    }

    // ---------------------------------------------------------------- by query

    private static List<Resolved> ByQuery(SwContext ctx, IModelDoc2 doc, EntityRef r, string argName, EntityKind[] allowed)
    {
        var filter = r.WhereFilter(argName)!;
        var kind = ParseKind(filter.Type ?? r.Type)
                   ?? (allowed.Length == 1 && allowed[0] is EntityKind.Face or EntityKind.Edge or EntityKind.Vertex ? allowed[0] : (EntityKind?)null)
                   ?? (allowed.Contains(EntityKind.Edge) ? EntityKind.Edge : allowed.Contains(EntityKind.Face) ? EntityKind.Face : (EntityKind?)null)
                   ?? throw SwException.InvalidArgument($"'{argName}.where' needs a 'type' (face, edge or vertex).");
        if (r.Component is not null && filter.Component is null) filter.Component = r.Component;
        var items = TopoQuery.Find(ctx, doc, kind, filter);
        if (r.Expect is int n && items.Count != n)
        {
            var sample = items.Take(10).Select(i => $"{TopoQuery.Id(ctx, doc, i)} at [{TopoQuery.Fmt(Units.Mm(i.Pick))}]");
            throw new SwException(items.Count == 0 ? ErrorCodes.EntityNotFound : ErrorCodes.EntityAmbiguous,
                $"'{argName}.where' matched {items.Count} {KindName(kind)}(s), expected {n}{(items.Count > 0 ? ": " + string.Join("; ", sample) : "")}.",
                "Adjust the filter (face_list / edge_list accept the same 'where' fields) or pass ids.");
        }
        if (items.Count == 0)
            throw new SwException(ErrorCodes.EntityNotFound, $"'{argName}.where' matched no {KindName(kind)}.",
                "Check the filter with face_list / edge_list.");
        return items.Select(i =>
        {
            string id = TopoQuery.Id(ctx, doc, i);
            return new Resolved { Obj = i.Entity, Kind = kind, Id = id, Label = $"{KindName(kind)} {id}", Pick = i.Pick };
        }).ToList();
    }

    // ---------------------------------------------------------------- by point

    private sealed record Hit(object Obj, double Dist, Vec3 LocalClosest, BodyScope Scope);

    private static Resolved ByPoint(SwContext ctx, IModelDoc2 doc, EntityRef r, string argName, EntityKind[] allowed)
    {
        // Only faces, edges, vertices and sketch points can be found by point: infer the type from what the parameter allows.
        var byPoint = allowed.Where(a => a is EntityKind.Face or EntityKind.Edge or EntityKind.Vertex or EntityKind.SketchPoint).ToArray();
        var kind = ParseKind(r.Type)
                   ?? (byPoint.Length == 1 ? byPoint[0] : (EntityKind?)null)
                   ?? throw SwException.InvalidArgument($"'{argName}': 'at' needs a 'type' (face, edge or vertex) here.");
        if (kind == EntityKind.SketchPoint) return SketchPointAt(ctx, doc, r, argName);
        if (kind is not (EntityKind.Face or EntityKind.Edge or EntityKind.Vertex))
            throw SwException.InvalidArgument($"'{argName}': 'at' works for face, edge, vertex and sketch_point; use 'name' for {KindName(kind)}.");

        var world = Units.PointM(r.At, argName + ".at");
        double tol = Units.M(r.TolMm is > 0 ? r.TolMm.Value : DefaultTolMm);
        bool strict = r.Strict == true;
        var scopes = TopoQuery.Scopes(ctx, doc, r.Component);

        // With 'component', 'at' is in that part's own coordinates; otherwise in document coordinates.
        bool partSpace = r.Component is not null;
        double diag = 0;
        foreach (var s in scopes)
        {
            var b = s.Body.Box();
            if (b.Length == 6) diag = Math.Max(diag, new Vec3(b[3] - b[0], b[4] - b[1], b[5] - b[2]).Length);
        }
        double loose = strict ? tol : Math.Max(tol, Math.Max(1e-3, 0.01 * diag));

        var hits = new List<Hit>();
        foreach (var s in scopes)
        {
            var local = partSpace ? world : s.ToLocal.ApplyPoint(world);
            var box = s.Body.Box();
            if (box.Length == 6 && !InBox(local, box, loose + 1e-6)) continue;
            switch (kind)
            {
                case EntityKind.Face:
                    foreach (var f in s.Body.Faces()) Add(hits, f, f.ClosestPoint(local), local, loose, s);
                    break;
                case EntityKind.Edge:
                    foreach (var e in s.Body.Edges()) Add(hits, e, e.ClosestPoint(local), local, loose, s);
                    break;
                case EntityKind.Vertex:
                    foreach (var v in s.Body.Vertices()) Add(hits, v, v.Point(), local, loose, s);
                    break;
            }
        }
        hits.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        string where = $"[{TopoQuery.Fmt(r.At!)}] mm{(partSpace ? $" (in {r.Component}'s coordinates)" : "")}";
        if (hits.Count == 0)
            throw new SwException(ErrorCodes.EntityNotFound, $"No {KindName(kind)} within {Units.Mm(loose)} mm of {where}.",
                $"Use {(kind == EntityKind.Face ? "face_list" : kind == EntityKind.Edge ? "edge_list" : "vertex_list")} for ids and pick points.");

        var best = hits[0];
        double eps = Math.Max(1e-9, 1e-3 * tol);
        var tied = hits.Where(h => h.Dist <= best.Dist + eps).ToList();
        if (tied.Count > 1)
        {
            var cands = tied.Take(6).Select(h => Candidate(ctx, doc, kind, h));
            throw new SwException(ErrorCodes.EntityAmbiguous,
                $"{tied.Count} {KindName(kind)}s touch {where}: {string.Join("; ", cands)}.",
                $"Pass one of these ids (or its pick point, which lies inside it).");
        }
        if (best.Dist > tol)
        {
            // Tolerant rule: accept the nearest when it is clearly nearer than the runner-up.
            double runner = hits.Count > 1 ? hits[1].Dist : double.MaxValue;
            if (strict || runner < 2 * best.Dist + 1e-4)
            {
                var cands = hits.Take(3).Select(h => $"{Candidate(ctx, doc, kind, h)} at {Units.Mm(h.Dist)} mm");
                throw new SwException(ErrorCodes.EntityNotFound,
                    $"No {KindName(kind)} within {Units.Mm(tol)} mm of {where}; nearest: {string.Join("; ", cands)}.",
                    "Pass an id, a point on the intended entity, or a larger tolMm.");
            }
            ctx.Warn($"{argName}: no {KindName(kind)} exactly at {where}; used the nearest one, {Units.Mm(best.Dist)} mm away.");
        }
        string id = ctx.Entities.IdFor(doc, best.Obj, Prefix(kind), KindName(kind),
            best.Obj switch { IFace2 f => Topology.SurfaceKind(f.Surface()), IEdge e => Topology.CurveKind(e.Curve()), _ => null },
            best.LocalClosest);
        return new Resolved
        {
            Obj = best.Obj, Kind = kind, Id = id, Label = $"{KindName(kind)} {id}",
            Pick = best.Scope.ToWorld.ApplyPoint(best.LocalClosest), Distance = best.Dist,
        };
    }

    private static string Candidate(SwContext ctx, IModelDoc2 doc, EntityKind kind, Hit h)
    {
        string id = ctx.Entities.IdFor(doc, h.Obj, Prefix(kind), KindName(kind));
        string pick = h.Obj switch
        {
            IFace2 f => TopoQuery.Fmt(Units.Mm(h.Scope.ToWorld.ApplyPoint(Topology.InteriorPoint(f)))),
            IEdge e => TopoQuery.Fmt(Units.Mm(h.Scope.ToWorld.ApplyPoint(Topology.Describe(e).Mid))),
            _ => TopoQuery.Fmt(Units.Mm(h.Scope.ToWorld.ApplyPoint(h.LocalClosest))),
        };
        return $"{id} (pick [{pick}])";
    }

    private static void Add(List<Hit> hits, object obj, double[] cp, Vec3 p, double maxDist, BodyScope s)
    {
        if (cp.Length < 3) return;
        var c = new Vec3(cp[0], cp[1], cp[2]);
        double d = c.DistanceTo(p);
        if (d <= maxDist) hits.Add(new Hit(obj, d, c, s));
    }

    private static bool InBox(Vec3 p, double[] b, double pad) =>
        p.X >= b[0] - pad && p.X <= b[3] + pad && p.Y >= b[1] - pad && p.Y <= b[4] + pad && p.Z >= b[2] - pad && p.Z <= b[5] + pad;

    public static IComponent2 Component(IAssemblyDoc asm, string name)
    {
        string want = name.Trim();
        IComponent2? c = asm.GetComponentByName(want);
        if (c is not null) return c;
        var all = asm.Components(false);
        string bare = want.Split('@')[0];
        c = all.FirstOrDefault(x => string.Equals(x.Name2, bare, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(x => x.Name2.EndsWith("/" + bare, StringComparison.OrdinalIgnoreCase));
        return c ?? throw new SwException(ErrorCodes.EntityNotFound, $"No component named '{name}'.",
            $"Components: {string.Join(", ", all.Take(20).Select(x => x.Name2))}{(all.Count > 20 ? ", …" : "")}.");
    }

    private static Resolved SketchPointAt(SwContext ctx, IModelDoc2 doc, EntityRef r, string argName)
    {
        var sketch = SketchFor(ctx, doc, r.Sketch);
        if (r.At is null || r.At.Length < 2) throw SwException.InvalidArgument($"'{argName}.at' must be [x, y] (sketch mm).");
        double tol = Units.M(r.TolMm is > 0 ? r.TolMm.Value : DefaultTolMm);
        double x = Units.M(r.At[0]), y = Units.M(r.At[1]);
        ISketchPoint? best = null;
        double bestD = double.MaxValue;
        foreach (var sp in sketch.Points())
        {
            double d = Math.Sqrt((sp.X - x) * (sp.X - x) + (sp.Y - y) * (sp.Y - y));
            if (d < bestD) { bestD = d; best = sp; }
        }
        if (best is null || bestD > tol)
            throw new SwException(ErrorCodes.EntityNotFound, $"No sketch point within {Units.Mm(tol)} mm of [{TopoQuery.Fmt(r.At)}] in the sketch.",
                "Use sketch_info to see the sketch's points.");
        return new Resolved { Obj = best, Kind = EntityKind.SketchPoint, Label = $"sketch point at [{TopoQuery.Fmt(r.At)}]" };
    }

    // ---------------------------------------------------------------- by name

    private static Resolved ByName(SwContext ctx, IModelDoc2 doc, EntityRef r, string argName, EntityKind[] allowed)
    {
        string name = r.Name!.Trim();
        var kind = ParseKind(r.Type) ?? (allowed.Length == 1 ? allowed[0] : null);

        if (r.Component is not null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY && kind != EntityKind.Component)
            return InComponent(ctx, doc, r, name, kind);

        if (kind == EntityKind.Component)
        {
            var comp = Component(ctx.Assembly(doc), name);
            return new Resolved { Obj = comp, Kind = EntityKind.Component, Label = $"component {comp.Name2}" };
        }
        if (kind == EntityKind.Body) return BodyByName(ctx, doc, name);
        if (kind == EntityKind.SketchSegment) return SegmentByName(ctx, doc, r, name);
        if (kind == EntityKind.Origin || (kind is null && name.Equals("origin", StringComparison.OrdinalIgnoreCase)))
        {
            var o = Tree.Walk(doc).FirstOrDefault(f => f.GetTypeName2() == "OriginProfileFeature")
                    ?? throw new SwException(ErrorCodes.EntityNotFound, "This document has no origin feature.");
            return new Resolved { Obj = o, Kind = EntityKind.Origin, Label = $"origin '{o.Name}'" };
        }

        var feat = (kind is null or EntityKind.Plane ? StandardPlane(doc, name) : null) ?? FeatureByName(doc, name);
        if (feat is null)
        {
            if (kind is null && allowed.Contains(EntityKind.Body)) return BodyByName(ctx, doc, name);
            throw new SwException(ErrorCodes.EntityNotFound, $"No {(kind is null ? "feature" : KindName(kind.Value))} named '{name}'.",
                "feature_list shows feature, sketch, plane and axis names; standard planes also answer to front / top / right.");
        }
        var fk = FeatureKind(feat);
        if (kind is not null && kind != fk && kind != EntityKind.Feature)
            throw new SwException(ErrorCodes.InvalidArgument, $"'{name}' is a {KindName(fk)} ({feat.GetTypeName2()}), not a {KindName(kind.Value)}.");
        return new Resolved { Obj = feat, Kind = kind == EntityKind.Feature ? EntityKind.Feature : fk, Label = $"{KindName(fk)} '{feat.Name}'" };
    }

    public static EntityKind FeatureKind(IFeature f) => f.GetTypeName2() switch
    {
        "RefPlane" => EntityKind.Plane,
        "RefAxis" => EntityKind.Axis,
        "RefPoint" => EntityKind.Point,
        "ProfileFeature" or "3DProfileFeature" => EntityKind.Sketch,
        "CoordSys" => EntityKind.CoordSys,
        "OriginProfileFeature" => EntityKind.Origin,
        _ => EntityKind.Feature,
    };

    /// <summary>Finds a feature by name (exact, then case-insensitive; Ø/⌀/ø treated alike).</summary>
    public static IFeature? FeatureByName(IModelDoc2 doc, string name)
    {
        object? f = doc.GetType() switch
        {
            (int)swDocumentTypes_e.swDocPART => (object?)((IPartDoc)doc).FeatureByName(name),
            (int)swDocumentTypes_e.swDocASSEMBLY => (object?)((IAssemblyDoc)doc).FeatureByName(name),
            (int)swDocumentTypes_e.swDocDRAWING => (object?)((IDrawingDoc)doc).FeatureByName(name),
            _ => null,
        };
        if (f is IFeature feat) return feat;
        string want = NormalizeName(name);
        foreach (var x in Tree.WalkAll(doc))
            if (NormalizeName(x.Name) == want) return x;
        return null;
    }

    private static string NormalizeName(string s) =>
        s.Replace('⌀', 'Ø').Replace('ø', 'Ø').Trim().ToUpperInvariant();

    /// <summary>
    /// Language-independent standard planes: "front"/"top"/"right" (also "xy"/"xz"/"yz", or the English names) resolve to
    /// the reference plane through the origin with normal ±Z / ±Y / ±X among the first three planes of the tree.
    /// </summary>
    public static IFeature? StandardPlane(IModelDoc2 doc, string name)
    {
        string n = name.Trim().ToLowerInvariant().Replace(" plane", "");
        int idx = n switch { "front" or "xy" => 0, "top" or "xz" or "zx" => 1, "right" or "yz" or "zy" => 2, _ => -1 };
        if (idx < 0) return null;
        var planes = Tree.Walk(doc).Where(f => f.GetTypeName2() == "RefPlane").Take(3).ToList();
        var want = idx switch { 0 => Vec3.UnitZ, 1 => Vec3.UnitY, _ => Vec3.UnitX };
        foreach (var p in planes)
        {
            if (p.Specific() is not IRefPlane rp) continue;
            var t = ComArgs.Doubles((object?)rp.Transform?.ArrayData);
            if (t.Length < 12) continue;
            // The plane's normal is the transform's Z row; it passes through the origin when the translation is 0.
            var normal = new Vec3(t[6], t[7], t[8]);
            if (normal.IsParallelTo(want, 1e-6) && new Vec3(t[9], t[10], t[11]).Length < 1e-9) return p;
        }
        return idx < planes.Count ? planes[idx] : null;
    }

    private static Resolved BodyByName(SwContext ctx, IModelDoc2 doc, string name)
    {
        var scopes = TopoQuery.Scopes(ctx, doc);
        foreach (var s in scopes)
            if (string.Equals(s.Body.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                string id = ctx.Entities.IdFor(doc, s.Body, 'B', "body");
                return new Resolved { Obj = s.Body, Kind = EntityKind.Body, Id = id, Label = $"body '{s.Body.Name}' ({id})" };
            }
        throw new SwException(ErrorCodes.EntityNotFound, $"No body named '{name}'.",
            scopes.Count == 0 ? "The model has no bodies yet." : $"Bodies: {string.Join(", ", scopes.Select(s => s.Body.Name))} (names change when features modify a body; body_list gives stable ids).");
    }

    private static Resolved SegmentByName(SwContext ctx, IModelDoc2 doc, EntityRef r, string name)
    {
        var sketch = SketchFor(ctx, doc, r.Sketch);
        foreach (var seg in sketch.Segments())
            if (string.Equals(seg.GetName(), name, StringComparison.OrdinalIgnoreCase))
                return new Resolved { Obj = seg, Kind = EntityKind.SketchSegment, Label = $"sketch segment {seg.GetName()}" };
        throw new SwException(ErrorCodes.EntityNotFound, $"No sketch segment named '{name}' in {((IFeature)sketch).Name}.",
            "sketch_info lists segment names (circles are named ArcN).");
    }

    /// <summary>The open sketch, or the named one.</summary>
    public static ISketch SketchFor(SwContext ctx, IModelDoc2 doc, string? sketchName)
    {
        if (string.IsNullOrWhiteSpace(sketchName))
        {
            return doc.SketchManager.ActiveSketch
                   ?? throw new SwException(ErrorCodes.SketchState, "No sketch is open.",
                       "Open one with sketch_start / sketch_edit, or pass 'sketch' with the sketch name.");
        }
        var f = FeatureByName(doc, sketchName!) ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{sketchName}'.");
        return f.Specific() as ISketch
               ?? throw new SwException(ErrorCodes.InvalidArgument, $"'{sketchName}' is a {f.GetTypeName2()}, not a sketch.");
    }

    /// <summary>A plane/axis/feature of a component, mapped into the assembly context.</summary>
    private static Resolved InComponent(SwContext ctx, IModelDoc2 doc, EntityRef r, string name, EntityKind? kind)
    {
        var asm = ctx.Assembly(doc);
        var comp = Component(asm, r.Component!);
        var compDoc = (object?)comp.GetModelDoc2() as IModelDoc2
                      ?? throw new SwException(ErrorCodes.NotSupported, $"Component '{comp.Name2}' is suppressed or lightweight.",
                          "Resolve it (asm_component_set) first.");
        var feat = (kind is null or EntityKind.Plane ? StandardPlane(compDoc, name) : null) ?? FeatureByName(compDoc, name)
                   ?? throw new SwException(ErrorCodes.EntityNotFound, $"No feature named '{name}' in component '{comp.Name2}'.");
        var inAsm = (object?)comp.FeatureByName(feat.Name) as IFeature
                    ?? throw new SwException(ErrorCodes.EntityNotFound, $"'{name}' could not be mapped into the assembly for '{comp.Name2}'.");
        var fk = FeatureKind(inAsm);
        return new Resolved { Obj = inAsm, Kind = kind == EntityKind.Feature ? EntityKind.Feature : fk, Label = $"{KindName(fk)} '{feat.Name}' of {comp.Name2}" };
    }
}
