using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed class BodySummary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public double VolumeMm3 { get; init; }
    public int Faces { get; init; }
}

public sealed class ModelInfoResult : ToolResult
{
    public required string Type { get; init; }
    public string? Path { get; init; }
    public string? Units { get; init; }
    public string? Configuration { get; init; }
    public IReadOnlyList<string>? Configurations { get; init; }
    public string? Material { get; init; }
    public string? OpenSketch { get; init; }
    public int Features { get; init; }
    public IReadOnlyList<BodySummary>? Bodies { get; init; }
    public double? VolumeMm3 { get; init; }
    public double? AreaMm2 { get; init; }
    public double? MassKg { get; init; }
    public double[]? CenterOfMassMm { get; init; }
    public BoxMm? Box { get; init; }
    public int? Components { get; init; }
    public int? TopLevelComponents { get; init; }
}

public sealed class FeatureRow
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int? Error { get; init; }
    public bool? Warning { get; init; }
    public bool? Suppressed { get; init; }
    public bool? RolledBack { get; init; }
    public IReadOnlyList<string>? Children { get; init; }
    public IReadOnlyList<string>? Parents { get; init; }
}

public sealed class FeatureListResult : ToolResult
{
    public required IReadOnlyList<FeatureRow> Features { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

public sealed class FaceListResult : ToolResult
{
    public required IReadOnlyList<FaceInfo> Faces { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

public sealed class EdgeListResult : ToolResult
{
    public required IReadOnlyList<EdgeInfo> Edges { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

public sealed class VertexListResult : ToolResult
{
    public required IReadOnlyList<VertexInfo> Vertices { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>Read-only inspection: model summary, feature tree, faces, edges, vertices.</summary>
public sealed class InspectService(SwSession session)
{
    public Task<ModelInfoResult> ModelInfoAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("model_info", ctx =>
        {
            var d = ctx.Doc;
            int type = d.GetType();
            var sketch = d.SketchManager?.ActiveSketch;
            string? sketchName = sketch is null ? null : ((IFeature)sketch).Name;
            string? material = null;
            List<BodySummary>? bodies = null;
            double? vol = null, area = null, mass = null;
            double[]? com = null;
            BoxMm? box = null;
            int? comps = null, top = null;
            if (type == (int)swDocumentTypes_e.swDocPART)
            {
                var part = (IPartDoc)d;
                try { material = part.GetMaterialPropertyName2("", out _) is { Length: > 0 } m ? m : null; } catch { /* none */ }
                var snap = Snapshot.Take(d);
                bodies = [];
                foreach (var b in part.AllBodies(visibleOnly: false))
                {
                    var mp = ComArgs.Doubles((object?)b.GetMassProperties(1.0));
                    bodies.Add(new BodySummary
                    {
                        Id = ctx.Entities.IdFor(d, b, 'B', "body"),
                        Name = b.Name,
                        Type = b.GetType() == (int)swBodyType_e.swSolidBody ? "solid" : "surface",
                        VolumeMm3 = mp.Length >= 4 ? Units.Mm3(mp[3]) : 0,
                        Faces = b.GetFaceCount(),
                    });
                }
                box = BoxMm.FromMeters(snap.Box);
                vol = Units.Mm3(snap.Volume);
                area = Units.Mm2(snap.Area);
                if (sketch is null)
                {
                    // Document mass properties include the material density; they read zero while a sketch is open.
                    if ((object?)d.Extension.CreateMassProperty2() is IMassProperty2 mp2)
                    {
                        mass = Units.R(mp2.Mass);
                        com = ComArgs.Doubles((object?)mp2.CenterOfMass) is { Length: 3 } c ? Units.Mm(c) : null;
                    }
                }
                else
                {
                    ctx.Warn($"Sketch '{sketchName}' is open: mass and center of mass are unavailable until sketch_finish (volumes are per-body and still valid).");
                }
            }
            else if (type == (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                var asm = (IAssemblyDoc)d;
                comps = asm.GetComponentCount(false);
                top = asm.GetComponentCount(true);
            }
            var names = ComArgs.Strings((object?)d.GetConfigurationNames());
            return new ModelInfoResult
            {
                Doc = ctx.Title(d),
                Type = SwContext.TypeName(type),
                Path = d.GetPathName() is { Length: > 0 } p ? p : null,
                Units = DocumentService.UnitName(d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0)),
                Configuration = d.ConfigurationManager?.ActiveConfiguration?.Name,
                Configurations = names.Length > 1 ? names : null,
                Material = material,
                OpenSketch = sketchName,
                Features = Tree.CountUserFeatures(d),
                Bodies = bodies,
                VolumeMm3 = vol,
                AreaMm2 = area,
                MassKg = mass,
                CenterOfMassMm = com,
                Box = box,
                Components = comps,
                TopLevelComponents = top,
            };
        }, RunOptions.Read(doc), ct);

    public Task<FeatureListResult> FeatureListAsync(string? doc, bool includeSystem, int limit, int offset, CancellationToken ct = default) =>
        session.RunAsync("feature_list", ctx =>
        {
            var d = ctx.Doc;
            var all = Tree.Walk(d).Where(f => includeSystem || !Tree.SystemTypes.Contains(f.GetTypeName2())).ToList();
            var page = all.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 500)).ToList();
            var rows = page.Select(f =>
            {
                int err = f.GetErrorCode2(out bool warn);
                var kids = Tree.Children(f).Select(c => c.Name).ToList();
                var parents = ComArgs.Objects<IFeature>((object?)f.GetParents()).Select(p => p.Name).ToList();
                bool suppressed = false;
                try { suppressed = f.IsSuppressed(); } catch { /* not applicable */ }
                bool rolledBack = false;
                try { rolledBack = f.IsRolledBack(); } catch { /* not applicable */ }
                return new FeatureRow
                {
                    Name = f.Name,
                    Type = f.GetTypeName2(),
                    Error = err == 0 ? null : err,
                    Warning = warn && err != 0 ? true : null,
                    Suppressed = suppressed ? true : null,
                    RolledBack = rolledBack ? true : null,
                    Children = kids.Count == 0 ? null : kids,
                    Parents = parents.Count == 0 ? null : parents,
                };
            }).ToList();
            return new FeatureListResult
            {
                Doc = ctx.Title(d),
                Features = rows,
                Total = all.Count,
                Truncated = offset + rows.Count < all.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    public Task<FaceListResult> FaceListAsync(string? doc, EntityFilter? filter, int limit, int offset, CancellationToken ct = default) =>
        session.RunAsync("face_list", ctx =>
        {
            var d = ctx.Doc;
            var items = TopoQuery.Find(ctx, d, EntityKind.Face, filter);
            var page = items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).Select(i => Describe.Face(ctx, d, i)).ToList();
            return new FaceListResult
            {
                Doc = ctx.Title(d),
                Faces = page,
                Total = items.Count,
                Truncated = offset + page.Count < items.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    public Task<EdgeListResult> EdgeListAsync(string? doc, EntityFilter? filter, int limit, int offset, bool withFaces, CancellationToken ct = default) =>
        session.RunAsync("edge_list", ctx =>
        {
            var d = ctx.Doc;
            var items = TopoQuery.Find(ctx, d, EntityKind.Edge, filter);
            var page = items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).Select(i => Describe.Edge(ctx, d, i, withFaces)).ToList();
            return new EdgeListResult
            {
                Doc = ctx.Title(d),
                Edges = page,
                Total = items.Count,
                Truncated = offset + page.Count < items.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    public Task<VertexListResult> VertexListAsync(string? doc, EntityFilter? filter, int limit, int offset, CancellationToken ct = default) =>
        session.RunAsync("vertex_list", ctx =>
        {
            var d = ctx.Doc;
            var items = TopoQuery.Find(ctx, d, EntityKind.Vertex, filter);
            var page = items.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).Select(i => Describe.Vertex(ctx, d, i)).ToList();
            return new VertexListResult
            {
                Doc = ctx.Title(d),
                Vertices = page,
                Total = items.Count,
                Truncated = offset + page.Count < items.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct);
}
