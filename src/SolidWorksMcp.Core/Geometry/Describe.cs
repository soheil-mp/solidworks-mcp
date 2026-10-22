using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>Builds the face/edge/vertex DTOs (world mm) shared by listings and feature results, registering ids.</summary>
internal static class Describe
{
    public static FaceInfo Face(SwContext ctx, IModelDoc2 doc, TopoItem it)
    {
        var g = it.Face ?? Topology.Describe((IFace2)it.Entity);
        return new FaceInfo
        {
            Id = TopoQuery.Id(ctx, doc, it),
            Surface = g.Surface,
            AreaMm2 = Units.Mm2(it.Size),
            PickMm = Units.Mm(it.Pick),
            Normal = g.Normal is Vec3 n ? Units.Unit(it.World(n).Normalized()) : null,
            AxisOriginMm = g.AxisOrigin is Vec3 o ? Units.Mm(it.WorldPoint(o)) : null,
            Axis = g.Axis is Vec3 a ? Units.Unit(it.World(a).Normalized()) : null,
            RadiusMm = g.Radius is double r ? Units.Mm(r) : null,
            HalfAngleDeg = g.HalfAngle is double h ? Units.Deg(h) : null,
            Feature = it.Feature,
            Body = SafeName(it.Scope.Body),
            Component = it.Scope.Component?.Name2,
        };
    }

    public static EdgeInfo Edge(SwContext ctx, IModelDoc2 doc, TopoItem it, bool withFaces)
    {
        var g = it.Edge ?? Topology.Describe((IEdge)it.Entity);
        IReadOnlyList<string>? faces = null;
        if (withFaces)
        {
            faces = ((IEdge)it.Entity).AdjacentFaces().Select(f =>
            {
                var fi = new TopoItem { Entity = f, Kind = EntityKind.Face, Scope = it.Scope, Face = Topology.Describe(f) };
                fi.LocalPick = Topology.InteriorPoint(f);
                return TopoQuery.Id(ctx, doc, fi);
            }).ToList();
        }
        return new EdgeInfo
        {
            Id = TopoQuery.Id(ctx, doc, it),
            Curve = g.Curve,
            LengthMm = Units.Mm(g.Length),
            StartMm = Units.Mm(it.WorldPoint(g.Start)),
            EndMm = Units.Mm(it.WorldPoint(g.End)),
            MidMm = Units.Mm(it.WorldPoint(g.Mid)),
            RadiusMm = g.Radius is double r ? Units.Mm(r) : null,
            CenterMm = g.Center is Vec3 c ? Units.Mm(it.WorldPoint(c)) : null,
            Axis = g.Axis is Vec3 a ? Units.Unit(it.World(a).Normalized()) : null,
            Direction = g.Direction is Vec3 d ? Units.Unit(it.World(d).Normalized()) : null,
            Closed = g.Closed ? true : null,
            Faces = faces,
            Component = it.Scope.Component?.Name2,
        };
    }

    public static VertexInfo Vertex(SwContext ctx, IModelDoc2 doc, TopoItem it) => new()
    {
        Id = TopoQuery.Id(ctx, doc, it),
        PointMm = Units.Mm(it.Pick),
        Component = it.Scope.Component?.Name2,
    };

    /// <summary>Describes faces of a feature (part context), e.g. the faces a new feature created.</summary>
    public static List<FaceInfo> FeatureFaces(SwContext ctx, IModelDoc2 doc, IFeature feature, int max, out int total)
    {
        var faces = ComArgs.Objects<IFace2>((object?)feature.GetFaces());
        total = faces.Count;
        var scope = new BodyScope(null!, null, Xform.Identity, Xform.Identity);
        var list = new List<FaceInfo>();
        foreach (var f in faces.Take(max))
        {
            var it = new TopoItem
            {
                Entity = f,
                Kind = EntityKind.Face,
                Scope = scope with { Body = f.Body()! },
                Face = Topology.Describe(f),
            };
            it.LocalPick = Topology.InteriorPoint(f);
            it.Pick = it.LocalPick;
            it.Size = f.GetArea();
            it.Feature = feature.Name;
            list.Add(Face(ctx, doc, it));
        }
        return list;
    }

    public static string? SafeName(IBody2? b)
    {
        try { return b?.Name; }
        catch { return null; }
    }
}
