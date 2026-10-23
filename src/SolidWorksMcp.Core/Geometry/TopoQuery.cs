using System.Globalization;
using System.Text.Json;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>One body to search, with the transform from its own coordinates to the document's (identity in parts).</summary>
internal sealed record BodyScope(IBody2 Body, IComponent2? Component, Xform ToWorld, Xform ToLocal)
{
    public string ComponentName => Component?.Name2 ?? "";
}

/// <summary>A topological entity found by a query, with its geometry in document (world) coordinates.</summary>
internal sealed class TopoItem
{
    public required object Entity { get; init; }
    public required EntityKind Kind { get; init; }
    public required BodyScope Scope { get; init; }
    public Vec3 Pick { get; set; }
    public Vec3 LocalPick { get; set; }
    public FaceGeom? Face { get; set; }
    public EdgeGeom? Edge { get; set; }
    public double Size { get; set; } // area m² (faces) or length m (edges)
    public string? Feature { get; set; }
    public string SubKind => Face?.Surface ?? Edge?.Curve ?? "point";

    /// <summary>A direction from local to world (rotation only).</summary>
    public Vec3 World(Vec3 localDir) => Scope.ToWorld.ApplyVector(localDir);
    public Vec3 WorldPoint(Vec3 local) => Scope.ToWorld.ApplyPoint(local);
}

/// <summary>
/// Collects faces / edges / vertices of a part or assembly that match an <see cref="EntityFilter"/>. Cheap tests
/// (types, box prefilters) run before expensive ones (pick points), and every result carries world-space geometry.
/// Component bodies report part-space geometry, so assembly queries transform with the component transform.
/// </summary>
internal static class TopoQuery
{
    public static List<BodyScope> Scopes(SwContext ctx, IModelDoc2 doc, string? component = null, string? body = null)
    {
        var list = new List<BodyScope>();
        int type = doc.GetType();
        if (type == (int)swDocumentTypes_e.swDocPART)
        {
            if (component is not null) throw SwException.InvalidArgument("'component' only applies in assemblies.");
            foreach (var b in ((IPartDoc)doc).AllBodies())
                list.Add(new BodyScope(b, null, Xform.Identity, Xform.Identity));
        }
        else if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
        {
            var asm = (IAssemblyDoc)doc;
            IEnumerable<IComponent2> comps = component is null ? asm.Components(false) : [EntityResolver.Component(asm, component)];
            foreach (var c in comps)
            {
                int supp = c.GetSuppression2();
                if (supp == (int)swComponentSuppressionState_e.swComponentSuppressed) continue;
                if (supp is (int)swComponentSuppressionState_e.swComponentLightweight or (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                {
                    if (component is not null)
                        throw new SwException(ErrorCodes.NotSupported, $"Component '{c.Name2}' is lightweight (no geometry loaded).",
                            "Resolve it first (asm_component_set resolved=true).");
                    ctx.Warn($"Lightweight component '{c.Name2}' was skipped (no geometry loaded).");
                    continue;
                }
                var xf = c.Transform2 is IMathTransform t ? new Xform(ComArgs.Doubles((object?)t.ArrayData)) : Xform.Identity;
                var inv = xf.Inverse();
                foreach (var b in c.Bodies(swBodyType_e.swSolidBody)) list.Add(new BodyScope(b, c, xf, inv));
                foreach (var b in c.Bodies(swBodyType_e.swSheetBody)) list.Add(new BodyScope(b, c, xf, inv));
            }
        }
        else
        {
            throw new SwException(ErrorCodes.WrongDocType, "Geometry queries need a part or an assembly.",
                "In drawings, the drw_* tools take model coordinates.");
        }
        if (body is not null)
        {
            list = list.Where(s => string.Equals(s.Body.Name, body, StringComparison.OrdinalIgnoreCase) ||
                                   (ctx.Entities.TryGet(body, out var r) && r.Kind == "body" && ReferenceEquals(r.Doc, doc) &&
                                    ctx.Entities.IdFor(doc, s.Body, 'B', "body") == r.Id)).ToList();
            if (list.Count == 0) throw new SwException(ErrorCodes.EntityNotFound, $"No body '{body}'.", "body_list shows the bodies.");
        }
        return list;
    }

    /// <summary>All entities of <paramref name="kind"/> matching <paramref name="f"/>, in stable order.</summary>
    public static List<TopoItem> Find(SwContext ctx, IModelDoc2 doc, EntityKind kind, EntityFilter? f)
    {
        f ??= new EntityFilter();
        var scopes = Scopes(ctx, doc, f.Component, f.Body);
        double angTol = Units.Rad(f.AngleTolDeg ?? 1.0);
        Vec3? normal = f.Normal is null ? null : Axis(f.Normal, "where.normal");
        Vec3? parallel = f.ParallelTo is null ? null : Axis(f.ParallelTo, "where.parallelTo");
        Vec3? near = f.Near is null ? null : Units.PointM(f.Near, "where.near");
        double within = Units.M(f.WithinMm ?? 1.0);
        double rTol = Units.M(f.RadiusTolMm ?? 0.001);

        // Edges of one face.
        HashSet<object>? faceEdges = null;
        HashSet<object>? outerEdges = null;
        if (f.OfFace is not null)
        {
            if (kind != EntityKind.Edge && kind != EntityKind.Vertex)
                throw SwException.InvalidArgument("'ofFace' filters edges (or vertices).");
            var face = (IFace2)EntityResolver.Resolve(ctx, doc, new EntityRef { Id = f.OfFace }, "where.ofFace", EntityKind.Face).Obj;
            faceEdges = new HashSet<object>(face.Edges(), ReferenceEqualityComparer.Instance);
            if (f.Loop is not null)
            {
                outerEdges = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var loop in ComArgs.Objects<ILoop2>((object?)face.GetLoops()))
                    if (loop.IsOuter())
                        foreach (var e in ComArgs.Objects<IEdge>((object?)loop.GetEdges())) outerEdges.Add(e);
            }
        }

        var items = new List<TopoItem>();
        foreach (var s in scopes)
        {
            if (near is Vec3 nw)
            {
                var box = s.Body.Box();
                var nl = s.ToLocal.ApplyPoint(nw);
                if (box.Length == 6 && !InBox(nl, box, within)) continue;
            }
            switch (kind)
            {
                case EntityKind.Face:
                    foreach (var face in s.Body.Faces())
                    {
                        var item = new TopoItem { Entity = face, Kind = kind, Scope = s, Face = Topology.Describe(face) };
                        if (f.Surface is not null && !Eq(item.Face.Surface, f.Surface)) continue;
                        if (f.RadiusMm is double r && (item.Face.Radius is not double fr || Math.Abs(fr - Units.M(r)) > rTol)) continue;
                        if (normal is Vec3 n && (item.Face.Normal is not Vec3 fn || item.World(fn).Normalized().Dot(n) < Math.Cos(angTol))) continue;
                        if (parallel is Vec3 pa && (item.Face.Axis is not Vec3 ax || !item.World(ax).IsParallelTo(pa, angTol))) continue;
                        if (f.Feature is not null && !Eq(face.Feature()?.Name, f.Feature)) continue;
                        if (near is Vec3 nn && Topology.DistanceToFace(face, s.ToLocal.ApplyPoint(nn)) > within) continue;
                        items.Add(item);
                    }
                    break;
                case EntityKind.Edge:
                    foreach (var edge in s.Body.Edges())
                    {
                        if (faceEdges is not null && !faceEdges.Contains(edge)) continue;
                        if (outerEdges is not null)
                        {
                            bool outer = outerEdges.Contains(edge);
                            if (Eq(f.Loop, "outer") != outer) continue;
                        }
                        var item = new TopoItem { Entity = edge, Kind = kind, Scope = s, Edge = Topology.Describe(edge) };
                        var eg = item.Edge;
                        if (f.Curve is not null && !Eq(eg.Curve, f.Curve)) continue;
                        if (f.RadiusMm is double r && (eg.Radius is not double er || Math.Abs(er - Units.M(r)) > rTol)) continue;
                        if (f.LengthMm is double len && Math.Abs(eg.Length - Units.M(len)) > 1e-6) continue;
                        if (parallel is Vec3 pa)
                        {
                            var dir = eg.Direction ?? eg.Axis;
                            if (dir is not Vec3 d || !item.World(d).IsParallelTo(pa, angTol)) continue;
                        }
                        if (f.Feature is not null &&
                            !edge.AdjacentFaces().Any(af => Eq(af.Feature()?.Name, f.Feature))) continue;
                        if (near is Vec3 nn && Topology.DistanceToEdge(edge, s.ToLocal.ApplyPoint(nn)) > within) continue;
                        items.Add(item);
                    }
                    break;
                case EntityKind.Vertex:
                    foreach (var v in s.Body.Vertices())
                    {
                        if (faceEdges is not null &&
                            !faceEdges.Cast<IEdge>().Any(e => ReferenceEquals(e.StartVertex(), v) || ReferenceEquals(e.EndVertex(), v))) continue;
                        var lp = Vec3.From(v.Point());
                        if (near is Vec3 nn && s.ToLocal.ApplyPoint(nn).DistanceTo(lp) > within) continue;
                        items.Add(new TopoItem { Entity = v, Kind = kind, Scope = s, LocalPick = lp, Pick = s.ToWorld.ApplyPoint(lp) });
                    }
                    break;
                default:
                    throw SwException.InvalidArgument($"Queries list faces, edges or vertices, not {EntityResolver.KindName(kind)}.");
            }
        }

        // Pick points and sizes for the survivors (the expensive part).
        foreach (var it in items)
        {
            if (it.Entity is IFace2 face)
            {
                it.LocalPick = Topology.InteriorPoint(face);
                it.Size = face.GetArea();
                it.Feature = face.Feature()?.Name;
            }
            else if (it.Entity is IEdge && it.Edge is not null)
            {
                it.LocalPick = it.Edge.Mid;
                it.Size = it.Edge.Length;
            }
            it.Pick = it.Scope.ToWorld.ApplyPoint(it.LocalPick);
        }

        if (f.Extreme is not null)
        {
            var (axis, sign) = ExtremeAxis(f.Extreme);
            if (items.Count > 0)
            {
                double best = items.Max(i => sign * i.Pick.Dot(axis));
                items = items.Where(i => sign * i.Pick.Dot(axis) >= best - 1e-6).ToList();
            }
        }

        // Stable order: by component, then pick point (x, y, z).
        return items.OrderBy(i => i.Scope.ComponentName, StringComparer.Ordinal)
            .ThenBy(i => Math.Round(i.Pick.X, 9)).ThenBy(i => Math.Round(i.Pick.Y, 9)).ThenBy(i => Math.Round(i.Pick.Z, 9))
            .ToList();
    }

    /// <summary>Registers (or re-uses) the id of a found entity, with its signature.</summary>
    public static string Id(SwContext ctx, IModelDoc2 doc, TopoItem it) =>
        ctx.Entities.IdFor(doc, it.Entity, EntityResolver.Prefix(it.Kind), EntityResolver.KindName(it.Kind), it.Kind == EntityKind.Vertex ? null : it.SubKind, it.LocalPick);

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool InBox(Vec3 p, double[] b, double pad) =>
        p.X >= b[0] - pad && p.X <= b[3] + pad && p.Y >= b[1] - pad && p.Y <= b[4] + pad && p.Z >= b[2] - pad && p.Z <= b[5] + pad;

    /// <summary>Parses an axis spec (see <see cref="AxisSpec"/>).</summary>
    public static Vec3 Axis(object spec, string argName) => AxisSpec.Parse(spec, argName);

    private static (Vec3 Axis, double Sign) ExtremeAxis(string spec) => AxisSpec.Extreme(spec);

    public static string Fmt(double[] a) => string.Join(", ", a.Select(v => (Math.Round(v, 3) + 0.0).ToString("0.###", CultureInfo.InvariantCulture)));
}
