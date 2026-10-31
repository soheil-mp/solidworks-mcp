using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed class MoldFaceRow
{
    public required string Id { get; init; }
    public required string Surface { get; init; }
    public required double AreaMm2 { get; init; }
    /// <summary>draft: positive | negative | straddle | needs_draft; undercut: the side it is trapped on.</summary>
    public required string Class { get; init; }
    public double? MinDraftDeg { get; init; }
    public double? MaxDraftDeg { get; init; }
    /// <summary>thickness: the thinnest wall measured from this face.</summary>
    public double? ThicknessMm { get; init; }
    public double[]? AtMm { get; init; }
}

public sealed class MoldCheckResult : ToolResult
{
    public required string Kind { get; init; }
    public double[]? Pull { get; init; }
    public double? MinDeg { get; init; }
    /// <summary>draft: face count and area per class; undercut: trapped faces per side.</summary>
    public required IReadOnlyDictionary<string, int> Counts { get; init; }
    public IReadOnlyDictionary<string, double>? AreasMm2 { get; init; }
    /// <summary>thickness: the thinnest and thickest wall found, and the limit.</summary>
    public double? MinThicknessMm { get; init; }
    public double? MaxThicknessMm { get; init; }
    public double? LimitMm { get; init; }
    /// <summary>The faces that need attention (draft: needs_draft and straddle; undercut: trapped; thickness: thin), then the rest up to the limit.</summary>
    public required IReadOnlyList<MoldFaceRow> Faces { get; init; }
    public int Samples { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>
/// Moldability checks on the part's solid bodies, from the face geometry and ray casts (IModelDocExtension.RayIntersections):
/// draft angles against a pull direction, undercuts (faces that can't release along it) and wall thickness. DFMXpress has no
/// API; these are the same checks done directly.
/// </summary>
public sealed partial class MoldingService(SwSession session)
{
    private const int MaxSamplesPerFace = 48;
    private const int MaxRows = 60;
    private const double Eps = 2e-5; // m: ray starts this far off the face

    private sealed record Sample(IFace2 Face, Vec3 P, Vec3 N);

    public Task<MoldCheckResult> CheckAsync(string? doc, string kind, string? pull, double minDeg, double? limitMm, CancellationToken ct = default) =>
        session.RunAsync("model_check", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            var bodies = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
            if (bodies.Count == 0) throw new SwException(ErrorCodes.EntityNotFound, $"'{ctx.Title(d)}' has no solid body.");
            return kind switch
            {
                "draft" => Draft(ctx, d, bodies, Direction(pull), minDeg),
                "undercut" => Undercut(ctx, d, bodies, Direction(pull), minDeg),
                _ => Thickness(ctx, d, bodies, limitMm),
            };
        }, RunOptions.Read(doc) with { Timeout = TimeSpan.FromSeconds(300) }, ct);

    /// <summary>"+y", "-x" or "x,y,z" → a unit vector (default +y, SOLIDWORKS' up).</summary>
    internal static Vec3 Direction(string? pull)
    {
        string p = (pull ?? "+y").Trim().ToLowerInvariant();
        Vec3? v = p switch
        {
            "+x" or "x" => new Vec3(1, 0, 0), "-x" => new Vec3(-1, 0, 0),
            "+y" or "y" => new Vec3(0, 1, 0), "-y" => new Vec3(0, -1, 0),
            "+z" or "z" => new Vec3(0, 0, 1), "-z" => new Vec3(0, 0, -1),
            _ => null,
        };
        if (v is Vec3 axis) return axis;
        var parts = p.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(s => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            var u = new Vec3(double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            if (u.Length > 1e-9) return u.Normalized();
        }
        throw SwException.InvalidArgument($"pull must be +x, -x, +y, -y, +z, -z or 'x,y,z' (got '{pull}').");
    }

    /// <summary>Points spread over the face (tessellation centroids projected onto it) with the outward normal at each.</summary>
    private static List<Sample> Samples(IFace2 f)
    {
        var g = Topology.Describe(f);
        var tess = ComArgs.Doubles((object?)f.GetTessTriangles(true));
        var pts = new List<Vec3>();
        for (int i = 0; i + 8 < tess.Length; i += 9)
            pts.Add((Vec3.From(tess, i) + Vec3.From(tess, i + 3) + Vec3.From(tess, i + 6)) / 3);
        if (pts.Count > MaxSamplesPerFace)
        {
            double step = pts.Count / (double)MaxSamplesPerFace;
            pts = Enumerable.Range(0, MaxSamplesPerFace).Select(k => pts[(int)(k * step)]).ToList();
        }
        if (pts.Count == 0) pts.Add(Topology.InteriorPoint(f));
        var list = new List<Sample>();
        foreach (var c in pts)
        {
            var cp = f.ClosestPoint(c);
            var p = cp.Length >= 3 ? Vec3.From(cp) : c;
            var n = Topology.OutwardNormal(f, g, p);
            if (n.Length > 0.5) list.Add(new Sample(f, p, n.Normalized()));
        }
        return list;
    }

    private static double DraftDeg(Vec3 n, Vec3 pull) => Math.Asin(Math.Clamp(n.Dot(pull), -1, 1)) * 180 / Math.PI;

    private static MoldFaceRow Row(SwContext ctx, IModelDoc2 d, IFace2 f, string cls, double? min = null, double? max = null, double? thick = null, Vec3? at = null) => new()
    {
        Id = ctx.Entities.IdFor(d, f, 'F', "face"),
        Surface = Topology.Describe(f).Surface,
        AreaMm2 = Units.Mm2(f.GetArea()),
        Class = cls,
        MinDraftDeg = min is double a ? Math.Round(a, 4) : null,
        MaxDraftDeg = max is double b ? Math.Round(b, 4) : null,
        ThicknessMm = thick is double t ? Units.Mm(t) : null,
        AtMm = at is Vec3 p ? Units.Mm(p) : null,
    };

    /// <summary>
    /// Each face's draft angle range, asin(n · pull) over its samples: positive (all ≥ minDeg, released towards the
    /// pull), negative (all ≤ −minDeg), needs_draft (all within ±minDeg: walls parallel to the pull) or straddle (both
    /// signs, e.g. a cylinder across the pull).
    /// </summary>
    private static MoldCheckResult Draft(SwContext ctx, IModelDoc2 d, List<IBody2> bodies, Vec3 pull, double minDeg)
    {
        var rows = new List<(MoldFaceRow Row, int Rank)>();
        var counts = new Dictionary<string, int> { ["positive"] = 0, ["negative"] = 0, ["needs_draft"] = 0, ["straddle"] = 0 };
        var areas = counts.Keys.ToDictionary(k => k, _ => 0.0);
        int samples = 0;
        foreach (var f in bodies.SelectMany(b => b.Faces()))
        {
            var s = Samples(f);
            samples += s.Count;
            var a = s.Select(x => DraftDeg(x.N, pull)).ToList();
            if (a.Count == 0) continue;
            double min = a.Min(), max = a.Max();
            // Part of a face within ±minDeg of the pull (and the rest drafted one way) still needs draft there.
            string cls = min >= minDeg ? "positive" : max <= -minDeg ? "negative"
                : min <= -minDeg && max >= minDeg ? "straddle" : "needs_draft";
            counts[cls]++;
            areas[cls] += f.GetArea();
            rows.Add((Row(ctx, d, f, cls, min, max), cls is "needs_draft" or "straddle" ? 0 : 1));
        }
        var ordered = rows.OrderBy(r => r.Rank).ThenBy(r => r.Row.MinDraftDeg).Select(r => r.Row).ToList();
        return new MoldCheckResult
        {
            Doc = ctx.Title(d),
            Kind = "draft",
            Pull = Units.Unit(pull),
            MinDeg = minDeg,
            Counts = counts,
            AreasMm2 = areas.ToDictionary(kv => kv.Key, kv => Units.Mm2(kv.Value)),
            Faces = ordered.Take(MaxRows).ToList(),
            Samples = samples,
            Truncated = ordered.Count > MaxRows ? true : null,
        };
    }

    /// <summary>
    /// A face is trapped when a ray from it along its release direction (+pull for faces facing the pull, −pull for the
    /// others) hits the part again. Faces within ±minDeg of the pull release sideways and are left to the draft check.
    /// </summary>
    private static MoldCheckResult Undercut(SwContext ctx, IModelDoc2 d, List<IBody2> bodies, Vec3 pull, double minDeg)
    {
        var all = new List<(Sample S, Vec3 Dir)>();
        foreach (var f in bodies.SelectMany(b => b.Faces()))
            foreach (var s in Samples(f))
            {
                double a = DraftDeg(s.N, pull);
                if (Math.Abs(a) < minDeg) continue;
                all.Add((s, a > 0 ? pull : -pull));
            }
        var hits = Cast(d, bodies, all.Select(x => (x.S.P + x.Dir * Eps, x.Dir)).ToList());
        var trapped = new Dictionary<IFace2, (string Side, Vec3 At)>();
        for (int i = 0; i < all.Count; i++)
            if (hits[i] is double dist && dist > Eps && !trapped.ContainsKey(all[i].S.Face))
                trapped[all[i].S.Face] = (all[i].Dir.Dot(pull) > 0 ? "cavity" : "core", all[i].S.P);
        var rows = trapped.Select(kv => Row(ctx, d, kv.Key, kv.Value.Side, at: kv.Value.At)).ToList();
        return new MoldCheckResult
        {
            Doc = ctx.Title(d),
            Kind = "undercut",
            Pull = Units.Unit(pull),
            MinDeg = minDeg,
            Counts = new Dictionary<string, int>
            {
                ["undercut_faces"] = rows.Count,
                ["cavity_side"] = rows.Count(r => r.Class == "cavity"),
                ["core_side"] = rows.Count(r => r.Class == "core"),
                ["faces_checked"] = all.Select(x => x.S.Face).Distinct().Count(),
            },
            Faces = rows.Take(MaxRows).ToList(),
            Samples = all.Count,
            Truncated = rows.Count > MaxRows ? true : null,
        };
    }

    /// <summary>
    /// The wall behind each sample: a ray into the material along −n; the first hit is the opposite wall, its distance the
    /// local thickness. Faces whose thinnest reading is under the limit are listed first.
    /// </summary>
    private static MoldCheckResult Thickness(SwContext ctx, IModelDoc2 d, List<IBody2> bodies, double? limitMm)
    {
        double? limit = limitMm is double l ? Units.PositiveM(l, "limitMm") : null;
        var all = bodies.SelectMany(b => b.Faces()).SelectMany(Samples).ToList();
        var hits = Cast(d, bodies, all.Select(s => (s.P - s.N * Eps, -s.N)).ToList());
        var perFace = new Dictionary<IFace2, (double Min, Vec3 At)>();
        for (int i = 0; i < all.Count; i++)
        {
            if (hits[i] is not double t) continue;
            t += Eps;
            if (!perFace.TryGetValue(all[i].Face, out var cur) || t < cur.Min) perFace[all[i].Face] = (t, all[i].P);
        }
        if (perFace.Count == 0) throw new SwException(ErrorCodes.VerifyFailed, "No ray found an opposite wall (an open or surface body?).");
        var rows = perFace.Select(kv => (kv.Value.Min, Row: Row(ctx, d, kv.Key, limit is double lim && kv.Value.Min < lim ? "thin" : "ok", thick: kv.Value.Min, at: kv.Value.At)))
            .OrderBy(x => x.Min).Select(x => x.Row).ToList();
        return new MoldCheckResult
        {
            Doc = ctx.Title(d),
            Kind = "thickness",
            Counts = new Dictionary<string, int> { ["thin_faces"] = rows.Count(r => r.Class == "thin"), ["faces_checked"] = rows.Count },
            MinThicknessMm = Units.Mm(perFace.Values.Min(v => v.Min)),
            MaxThicknessMm = Units.Mm(perFace.Values.Max(v => v.Min)),
            LimitMm = limitMm,
            Faces = rows.Take(MaxRows).ToList(),
            Samples = all.Count,
            Truncated = rows.Count > MaxRows ? true : null,
        };
    }

    /// <summary>
    /// One RayIntersections call for all rays (IModelDocExtension; results through IModelDoc2.GetRayIntersectionsPoints:
    /// body index, ray index, hit type, x, y, z per hit). Returns the nearest hit distance per ray (m), or null.
    /// </summary>
    private static double?[] Cast(IModelDoc2 d, List<IBody2> bodies, List<(Vec3 From, Vec3 Dir)> rays)
    {
        var result = new double?[rays.Count];
        const int Batch = 2000;
        for (int start = 0; start < rays.Count; start += Batch)
        {
            var chunk = rays.Skip(start).Take(Batch).ToList();
            var pts = chunk.SelectMany(r => new[] { r.From.X, r.From.Y, r.From.Z }).ToArray();
            var dirs = chunk.SelectMany(r => new[] { r.Dir.X, r.Dir.Y, r.Dir.Z }).ToArray();
            int n = d.Extension.RayIntersections(ComArgs.DispatchArray(bodies), pts, dirs, 0, 1e-7, 1e-7, true);
            if (n <= 0) continue;
            var h = ComArgs.Doubles((object?)d.GetRayIntersectionsPoints());
            for (int k = 0; k + 5 < h.Length; k += 6)
            {
                int ray = (int)h[k + 1];
                if (ray < 0 || ray >= chunk.Count) continue;
                var hit = new Vec3(h[k + 3], h[k + 4], h[k + 5]);
                double dist = (hit - chunk[ray].From).Dot(chunk[ray].Dir);
                if (dist <= 0) continue;
                int idx = start + ray;
                if (result[idx] is not double cur || dist < cur) result[idx] = dist;
            }
        }
        return result;
    }
}
