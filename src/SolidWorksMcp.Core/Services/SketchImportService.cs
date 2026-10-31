using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>sketch_import options.</summary>
public sealed record SketchImportRequest
{
    public string? Doc { get; init; }
    public string? Path { get; init; }
    /// <summary>The plane or flat face the sketch goes on (default: the Front plane).</summary>
    public EntityRef? On { get; init; }
    /// <summary>The drawing's length unit: mm (default) | cm | m | in | ft.</summary>
    public string? Units { get; init; }
}

public sealed class SketchImportResult : ToolResult
{
    public required string Sketch { get; init; }
    public int Segments { get; init; }
    public required IReadOnlyDictionary<string, int> ByType { get; init; }
    /// <summary>The segments' extents in the sketch's coordinates, mm: [xmin, ymin], [xmax, ymax].</summary>
    public required double[][] ExtentsMm { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// sketch_import: a DXF or DWG drawing into a new sketch on a plane or face of the part (IFeatureManager.InsertDwgOrDxfFile2
/// with IImportDxfDwgData: import to the existing part, the drawing's unit, coordinates kept, coincident ends merged).
/// Tier B: the sketch has segments; their types and extents are returned for the caller to compare with the drawing.
/// </summary>
public sealed class SketchImportService(SwSession session)
{
    public Task<SketchImportResult> ImportAsync(SketchImportRequest q, CancellationToken ct = default)
    {
        int unit = (q.Units ?? "mm").Trim().ToLowerInvariant() switch
        {
            "mm" => (int)swLengthUnit_e.swMM,
            "cm" => (int)swLengthUnit_e.swCM,
            "m" => (int)swLengthUnit_e.swMETER,
            "in" or "inch" => (int)swLengthUnit_e.swINCHES,
            "ft" or "feet" => (int)swLengthUnit_e.swFEET,
            _ => throw SwException.InvalidArgument($"units '{q.Units}' is not supported.", "Use mm, cm, m, in or ft."),
        };
        if (string.IsNullOrWhiteSpace(q.Path)) throw SwException.InvalidArgument("'path' is required: a .dxf or .dwg file.");
        return session.RunAsync("sketch_import", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            if (d.SketchManager.ActiveSketch is ISketch open)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
            string path = ctx.Paths.ForRead(q.Path!, "path");
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".dxf" or ".dwg")) throw SwException.InvalidArgument($"'{path}' is not a .dxf or .dwg file.");
            var plane = EntityResolver.Resolve(ctx, d, q.On ?? new EntityRef { Name = "front" }, "on", EntityKind.Plane, EntityKind.Face);
            if (plane.Obj is IFace2 f && Topology.Describe(f).Surface != "plane") throw SwException.InvalidArgument($"{plane.Label} is not flat.");
            var data = (object?)ctx.App.GetImportFileData(path) as IImportDxfDwgData
                       ?? throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS can't read '{path}' as a drawing.");
            data.set_ImportMethod("", (int)swImportDxfDwg_ImportMethod_e.swImportDxfDwg_ImportToExistingPart);
            data.set_LengthUnit("", unit);
            data.SetMergePoints("", true, 1e-7);
            var featuresBefore = Tree.WalkAll(d).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            d.ClearSelection2(true);
            if (!plane.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {plane.Label}.");
            var feat = (object?)d.FeatureManager.InsertDwgOrDxfFile2(path, data) as IFeature;
            d.ClearSelection2(true);
            if (d.SketchManager.ActiveSketch is not null) d.SketchManager.InsertSketch(true);
            feat ??= Tree.WalkAll(d).LastOrDefault(x => !featuresBefore.Contains(x.Name) && x.GetTypeName2() == "ProfileFeature")
                     ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no sketch from '{path}'.",
                         "Check that the file has model-space entities (lines, arcs, circles, polylines, splines).");
            var sketch = feat.GetSpecificFeature2() as ISketch
                         ?? throw new SwException(ErrorCodes.FeatureFailed, $"'{feat.Name}' from '{path}' is not a sketch.");
            var segs = ComArgs.Objects<ISketchSegment>((object?)sketch.GetSketchSegments()).Where(s => !s.ConstructionGeometry).ToList();
            if (segs.Count == 0)
            {
                RemoveSketch(d, feat);
                throw new SwException(ErrorCodes.FeatureFailed, $"The sketch from '{path}' has no segments; it was removed.", "Check the drawing's layers and units.");
            }
            var byType = segs.GroupBy(s => TypeName(s.GetType())).ToDictionary(g => g.Key, g => g.Count());
            var (min, max) = Extents(segs);
            ctx.DocEntry.LastSketch = feat.Name;
            return new SketchImportResult
            {
                Doc = ctx.Title(d), Sketch = feat.Name, Segments = segs.Count, ByType = byType,
                ExtentsMm = [[Units.Mm(min.X), Units.Mm(min.Y)], [Units.Mm(max.X), Units.Mm(max.Y)]],
                Checks =
                [
                    $"{segs.Count} segment(s) on {plane.Label}: {string.Join(", ", byType.Select(kv => $"{kv.Value} {kv.Key}"))}",
                    "it is the last sketch: feature_extrude uses it",
                ],
            };
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);
    }

    private static string TypeName(int t) => t switch
    {
        (int)swSketchSegments_e.swSketchLINE => "line",
        (int)swSketchSegments_e.swSketchARC => "arc",
        (int)swSketchSegments_e.swSketchELLIPSE => "ellipse",
        (int)swSketchSegments_e.swSketchSPLINE => "spline",
        (int)swSketchSegments_e.swSketchPARABOLA => "parabola",
        _ => "other",
    };

    /// <summary>The segments' extents in sketch coordinates (m): ends, and the extreme points of arcs.</summary>
    private static (Vec3 Min, Vec3 Max) Extents(List<ISketchSegment> segs)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double x, double y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
        foreach (var s in segs)
        {
            switch (s)
            {
                case ISketchLine l when (object?)l.GetStartPoint2() is ISketchPoint a && (object?)l.GetEndPoint2() is ISketchPoint b:
                    Add(a.X, a.Y); Add(b.X, b.Y);
                    break;
                case ISketchArc arc when (object?)arc.GetCenterPoint2() is ISketchPoint c && (object?)arc.GetStartPoint2() is ISketchPoint st && (object?)arc.GetEndPoint2() is ISketchPoint en:
                {
                    double r = arc.GetRadius();
                    Add(st.X, st.Y); Add(en.X, en.Y);
                    double a0 = Math.Atan2(st.Y - c.Y, st.X - c.X), a1 = Math.Atan2(en.Y - c.Y, en.X - c.X);
                    bool full = Math.Abs(st.X - en.X) < 1e-12 && Math.Abs(st.Y - en.Y) < 1e-12;
                    bool ccw = arc.GetRotationDir() == 1;
                    double sweep = full ? 2 * Math.PI : Norm(ccw ? a1 - a0 : a0 - a1);
                    for (int k = 0; k < 4; k++)
                    {
                        double ang = k * Math.PI / 2, rel = Norm(ccw ? ang - a0 : a0 - ang);
                        if (full || rel <= sweep) Add(c.X + r * Math.Cos(ang), c.Y + r * Math.Sin(ang));
                    }
                    break;
                }
                default:
                    var curve = (object?)s.GetCurve() as ICurve;
                    foreach (var p in ComArgs.Doubles((object?)curve?.GetTessPts(1e-4, 1e-4, ComArgs.Empty, ComArgs.Empty)).Chunk(3).Where(c => c.Length == 3))
                        Add(p[0], p[1]);
                    break;
            }
        }
        return x0 > x1 ? (Vec3.Zero, Vec3.Zero) : (new Vec3(x0, y0, 0), new Vec3(x1, y1, 0));
    }

    private static double Norm(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    private static void RemoveSketch(IModelDoc2 d, IFeature f)
    {
        d.ClearSelection2(true);
        try { if (f.Select2(false, 0)) d.Extension.DeleteSelection2(0); }
        catch (System.Runtime.InteropServices.COMException e) when (!ComArgs.IsDisconnect(e)) { /* reported by the caller */ }
        d.ClearSelection2(true);
    }
}
