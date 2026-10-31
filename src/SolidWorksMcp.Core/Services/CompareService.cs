using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>A measure of both documents: this one's value, the other's and the difference (this − other).</summary>
public sealed class CompareMetric
{
    public required string Name { get; init; }
    public double This { get; init; }
    public double Other { get; init; }
    public double Delta { get; init; }
}

public sealed class DimensionDiff
{
    public required string Name { get; init; }
    public double This { get; init; }
    public double Other { get; init; }
    /// <summary>mm | deg | count.</summary>
    public required string Unit { get; init; }
}

public sealed class CompareResult : ToolResult
{
    public required string Other { get; init; }
    /// <summary>No difference found in the measures, the feature tree or the dimensions.</summary>
    public bool Same { get; init; }
    public required IReadOnlyList<string> Differences { get; init; }
    public required IReadOnlyList<CompareMetric> Metrics { get; init; }
    public IReadOnlyList<string>? FeaturesOnlyHere { get; init; }
    public IReadOnlyList<string>? FeaturesOnlyThere { get; init; }
    public IReadOnlyList<DimensionDiff>? Dimensions { get; init; }
}

/// <summary>
/// model_compare: two revisions side by side: volume, surface area, mass and bounding box, the features (by name and type)
/// each has that the other lacks, and every dimension whose value differs (by full name). The other document is an open
/// one (handle, title or path) or a file, opened silently read-only and closed again.
/// </summary>
public sealed class CompareService(SwSession session)
{
    private const double LengthTolMm = 1e-6;

    public Task<CompareResult> CompareAsync(string? doc, string? other, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(other))
            throw SwException.InvalidArgument("'other' is required: the document to compare with (handle, title or path of an open one, or a file).");
        return session.RunAsync("model_compare", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            IModelDoc2? o = null;
            bool opened = false;
            try { o = DocLocator.Find(ctx.Documents, ctx.App.Documents(), other); }
            catch (SwException e) when (e.Code == ErrorCodes.InvalidArgument)
            {
                string path;
                try { path = ctx.Paths.ForRead(other, "other"); }
                catch (SwException io) when (io.Code == ErrorCodes.IoError)
                {
                    throw new SwException(ErrorCodes.EntityNotFound, $"'{other}' is neither an open document nor a file.", "doc_list shows the open documents.");
                }
                int type = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY : (int)swDocumentTypes_e.swDocPART;
                int errors = 0, warnings = 0;
                o = ctx.App.OpenDoc6(path, type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings)
                    ?? throw new SwException(ErrorCodes.IoError, $"SOLIDWORKS could not open '{path}' (error {errors}).",
                        "A document with the same file name may be open already: compare with it by its handle instead.");
                opened = true;
            }
            try
            {
                if (DocLocator.SameDoc(d, o!)) throw SwException.InvalidArgument("'other' is this document; compare two different ones.");
                if (o!.GetType() != d.GetType()) throw SwException.InvalidArgument($"'{ctx.Title(o)}' is not a {(d.GetType() == (int)swDocumentTypes_e.swDocPART ? "part" : "assembly")}.");
                return Compare(ctx, d, o);
            }
            finally
            {
                if (opened && o is not null)
                    try { ctx.App.CloseDoc(o.GetTitle()); }
                    catch (COMException e) when (!ComArgs.IsDisconnect(e)) { ctx.Warn($"'{ctx.Title(o)}' was opened to compare and could not be closed again."); }
            }
        }, RunOptions.Read(doc), ct);
    }

    private static CompareResult Compare(SwContext ctx, IModelDoc2 d, IModelDoc2 o)
    {
        var diffs = new List<string>();
        var metrics = new List<CompareMetric>();
        void Metric(string name, double a, double b, double tol, string unit)
        {
            metrics.Add(new CompareMetric { Name = name, This = a, Other = b, Delta = Math.Round(a - b, 6) });
            if (Math.Abs(a - b) > tol) diffs.Add($"{name}: {a} {unit} here, {b} {unit} there ({(a > b ? "+" : "")}{Math.Round(a - b, 6)})");
        }
        var (va, aa, ma, ba) = Measures(d);
        var (vb, ab, mb, bb) = Measures(o);
        Metric("volumeMm3", Units.Mm3(va), Units.Mm3(vb), 1e-3, "mm³");
        Metric("areaMm2", Units.Mm2(aa), Units.Mm2(ab), 1e-3, "mm²");
        Metric("massKg", Math.Round(ma, 9), Math.Round(mb, 9), 1e-9, "kg");
        for (int i = 0; i < 3; i++)
            if (ba.Length == 6 && bb.Length == 6)
                Metric($"size{"XYZ"[i]}Mm", Units.Mm(ba[i + 3] - ba[i]), Units.Mm(bb[i + 3] - bb[i]), LengthTolMm, "mm");

        var fa = Features(d);
        var fb = Features(o);
        var onlyHere = fa.Except(fb).ToList();
        var onlyThere = fb.Except(fa).ToList();
        if (onlyHere.Count > 0) diffs.Add($"features only here: {string.Join(", ", onlyHere.Take(10))}");
        if (onlyThere.Count > 0) diffs.Add($"features only there: {string.Join(", ", onlyThere.Take(10))}");

        var da = Dimensions(d);
        var db = Dimensions(o);
        var changed = new List<DimensionDiff>();
        foreach (var (name, x) in da)
            if (db.TryGetValue(name, out var y) && Math.Abs(x.Value - y.Value) > 1e-9)
                changed.Add(new DimensionDiff { Name = name, This = x.Value, Other = y.Value, Unit = x.Unit });
        foreach (var c in changed.Take(10)) diffs.Add($"{c.Name}: {c.This} {c.Unit} here, {c.Other} {c.Unit} there");
        return new CompareResult
        {
            Doc = ctx.Title(d),
            Other = ctx.Title(o),
            Same = diffs.Count == 0,
            Differences = diffs,
            Metrics = metrics,
            FeaturesOnlyHere = onlyHere.Count == 0 ? null : onlyHere,
            FeaturesOnlyThere = onlyThere.Count == 0 ? null : onlyThere,
            Dimensions = changed.Count == 0 ? null : changed,
        };
    }

    private static (double Volume, double Area, double Mass, double[] Box) Measures(IModelDoc2 d)
    {
        double v = 0, a = 0, m = 0;
        if ((object?)d.Extension.CreateMassProperty2() is IMassProperty2 mp) (v, a, m) = (mp.Volume, mp.SurfaceArea, mp.Mass);
        double[] box = d is IPartDoc p ? ComArgs.Doubles((object?)p.GetPartBox(true))
                     : d is IAssemblyDoc asm ? ComArgs.Doubles((object?)asm.GetBox((int)swBoundingBoxOptions_e.swBoundingBoxIncludeRefPlanes))
                     : [];
        return (v, a, m, box);
    }

    /// <summary>The user features as "name (type)", sketches included.</summary>
    private static List<string> Features(IModelDoc2 d) =>
        Tree.WalkAll(d).Select(f => (Name: f.Name, Type: f.GetTypeName2())).Where(f => !Tree.SystemTypes.Contains(f.Type))
            .Select(f => $"{f.Name} ({f.Type})").Distinct().ToList();

    private static Dictionary<string, DimensionInfo> Dimensions(IModelDoc2 d)
    {
        var map = new Dictionary<string, DimensionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Tree.WalkAll(d).Where(f => !Tree.SystemTypes.Contains(f.GetTypeName2())))
            foreach (var di in FeatureVerifier.Dimensions(f))
                map.TryAdd(di.Name, di);
        return map;
    }
}
