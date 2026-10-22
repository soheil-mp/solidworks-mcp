using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>The sketch plane in model space (mm / unit vectors).</summary>
public sealed class SketchFrame
{
    public required double[] OriginMm { get; init; }
    public required double[] XAxis { get; init; }
    public required double[] YAxis { get; init; }
    public required double[] Normal { get; init; }
}

public sealed class ContourStatus
{
    /// <summary>closed | open | mixed | empty | invalid.</summary>
    public required string Status { get; init; }
    public int ClosedContours { get; init; }
    public int OpenContours { get; init; }
    public int Regions { get; init; }
    public int Segments { get; init; }
    public string? Problem { get; init; }
}

public sealed class SketchStartResult : ToolResult
{
    public required string Sketch { get; init; }
    public required string On { get; init; }
    public SketchFrame? Frame { get; init; }
    /// <summary>For a face: its extents in sketch coordinates [xMin, yMin, xMax, yMax] mm.</summary>
    public double[]? FaceBoundsMm { get; init; }
    public bool? ThreeD { get; init; }
}

public sealed class SketchEntityResult
{
    public required string Kind { get; init; }
    public string? Label { get; init; }
    public required IReadOnlyList<string> Segments { get; init; }
    /// <summary>rectangle: {bottom, right, top, left} → segment name.</summary>
    public IReadOnlyDictionary<string, string>? Sides { get; init; }
    public string? ConstructionCircle { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class SketchAddResult : ToolResult
{
    public required string Sketch { get; init; }
    public required IReadOnlyList<SketchEntityResult> Entities { get; init; }
    public required ContourStatus Contours { get; init; }
}

public sealed class SketchFinishResult : ToolResult
{
    public required string Sketch { get; init; }
    public int Segments { get; init; }
    public int Relations { get; init; }
    /// <summary>under | fully | over | unknown | no_solution | invalid | autosolve_off.</summary>
    public required string Constrained { get; init; }
    public required ContourStatus Contours { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
}

/// <summary>Opening, drawing in and closing sketches (inference off while the server draws).</summary>
public sealed class SketchService(SwSession session)
{
    private const double Tol = 1e-6; // m

    // ------------------------------------------------------------------ start

    public Task<SketchStartResult> StartAsync(string? doc, EntityRef? on, bool threeD, CancellationToken ct = default) =>
        session.RunAsync("sketch_start", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var entry = ctx.DocEntry;
            var sm = d.SketchManager;
            if (sm.ActiveSketch is ISketch open)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is already open.",
                    entry.ServerSketch == ((IFeature)open).Name ? "Finish it with sketch_finish first." : "The user is editing it; ask them to exit the sketch.");
            d.ClearSelection2(true);
            string label;
            double[]? faceBounds = null;
            IFace2? face = null;
            if (threeD)
            {
                sm.Insert3DSketch(true);
                label = "3D sketch";
            }
            else
            {
                if (on is null) throw SwException.InvalidArgument("'on' is required: a plane ({name:'top'}) or a planar face ({id:'F3'} or {at:[x,y,z]}).");
                var r = EntityResolver.Resolve(ctx, d, on, "on", EntityKind.Plane, EntityKind.Face);
                if (r.Obj is IFace2 f)
                {
                    if (Topology.SurfaceKind(f.Surface()) != "plane")
                        throw SwException.InvalidArgument($"{r.Label} is not planar; sketches need a plane or a planar face.");
                    face = f;
                }
                if (!r.Select(d, false, 0))
                    throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");
                sm.InsertSketch(true);
                label = r.Label;
            }
            var sketch = sm.ActiveSketch
                         ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not open a sketch on {label}.");
            string name = ((IFeature)sketch).Name;
            entry.ServerSketch = name;
            d.ClearSelection2(true);

            SketchFrame? frame = null;
            if (!threeD)
            {
                var s2m = SketchToModel(sketch);
                frame = Frame(s2m);
                if (face is not null)
                {
                    var m2s = s2m.Inverse();
                    var b = face.Box();
                    if (b.Length == 6)
                    {
                        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                        foreach (var c in Corners(b))
                        {
                            var p = m2s.ApplyPoint(c);
                            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                        }
                        faceBounds = [Units.Mm(x0), Units.Mm(y0), Units.Mm(x1), Units.Mm(y1)];
                    }
                }
            }
            return new SketchStartResult
            {
                Doc = ctx.Title(d), Sketch = name, On = label, Frame = frame, FaceBoundsMm = faceBounds,
                ThreeD = threeD ? true : null,
            };
        }, RunOptions.Write(doc), ct);

    // ------------------------------------------------------------------ add

    public Task<SketchAddResult> AddAsync(string? doc, IReadOnlyList<SketchEntity> entities, CancellationToken ct = default) =>
        session.RunAsync("sketch_add", ctx =>
        {
            if (entities is null || entities.Count == 0) throw SwException.InvalidArgument("'entities' must contain at least one entity.");
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var sketch = ServerSketch(ctx, d);
            var sm = d.SketchManager;
            bool is3D = sketch.Is3D();
            var m2s = is3D ? Xform.Identity : SketchToModel(sketch).Inverse();

            // Validate everything before drawing anything (all-or-nothing).
            for (int i = 0; i < entities.Count; i++) Validate(entities[i], i);

            bool oldAddToDb = sm.AddToDB;
            sm.AddToDB = true; // no inference snapping or automatic relations while the server draws
            var created = new List<ISketchSegment>();
            var results = new List<SketchEntityResult>();
            try
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    var e = entities[i];
                    var before = SegmentNames(sketch);
                    int pointsBefore = sketch.Points().Count;
                    var conv = new PointConverter(m2s, is3D, $"entities[{i}]", ctx);
                    var checks = new List<string>();
                    Draw(d, sm, e, conv, checks);
                    var segs = sketch.Segments().Where(s => !before.Contains(s.GetName())).ToList();
                    created.AddRange(segs);
                    results.Add(Check(e, i, segs, sketch.Points().Count - pointsBefore, checks, conv));
                }
            }
            catch
            {
                DeleteSegments(d, created);
                throw;
            }
            finally
            {
                sm.AddToDB = oldAddToDb;
                d.ClearSelection2(true);
            }
            return new SketchAddResult
            {
                Doc = ctx.Title(d),
                Sketch = ((IFeature)sketch).Name,
                Entities = results,
                Contours = Contours(sketch),
            };
        }, RunOptions.Write(doc), ct);

    // ------------------------------------------------------------------ finish

    public Task<SketchFinishResult> FinishAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("sketch_finish", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var entry = ctx.DocEntry;
            var sketch = ServerSketch(ctx, d);
            string name = ((IFeature)sketch).Name;
            bool is3D = sketch.Is3D();
            if (is3D) d.SketchManager.Insert3DSketch(true);
            else d.SketchManager.InsertSketch(true);
            if (d.SketchManager.ActiveSketch is not null)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' did not close.", "Check dialog_list; a dialog may be open.");
            entry.ServerSketch = null;
            entry.LastSketch = name;
            var feat = EntityResolver.FeatureByName(d, name);
            var closed = feat?.Specific() as ISketch ?? sketch;
            int relations = 0;
            try { relations = closed.RelationManager?.GetRelationsCount((int)swSketchRelationFilterType_e.swAll) ?? 0; }
            catch { /* older API */ }
            return new SketchFinishResult
            {
                Doc = ctx.Title(d),
                Sketch = name,
                Segments = closed.Segments().Count(s => !s.ConstructionGeometry),
                Relations = relations,
                Constrained = ConstrainedName(closed.GetConstrainedStatus()),
                Contours = Contours(closed),
                Dimensions = feat is null ? null : Verify.FeatureVerifier.Dimensions(feat) is { Count: > 0 } dims ? dims : null,
            };
        }, RunOptions.Write(doc), ct);

    // ------------------------------------------------------------------ helpers

    /// <summary>The open sketch, which must be the one this server opened.</summary>
    private static ISketch ServerSketch(SwContext ctx, IModelDoc2 d)
    {
        var entry = ctx.DocEntry;
        var sketch = d.SketchManager.ActiveSketch
                     ?? throw new SwException(ErrorCodes.SketchState, "No sketch is open.", "Open one with sketch_start.");
        string name = ((IFeature)sketch).Name;
        if (entry.ServerSketch != name)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open, but this server didn't open it (the user is editing it).",
                "Ask the user to exit the sketch, then use sketch_start (or sketch_edit).");
        return sketch;
    }

    /// <summary>Sketch → model transform (the inverse of ISketch.ModelToSketchTransform).</summary>
    internal static Xform SketchToModel(ISketch sketch)
    {
        var t = sketch.ModelToSketchTransform;
        return new Xform(ComArgs.Doubles((object?)t.ArrayData)).Inverse();
    }

    internal static SketchFrame Frame(Xform s2m)
    {
        var o = s2m.ApplyPoint(Vec3.Zero);
        var x = s2m.ApplyVector(Vec3.UnitX).Normalized();
        var y = s2m.ApplyVector(Vec3.UnitY).Normalized();
        return new SketchFrame { OriginMm = Units.Mm(o), XAxis = Units.Unit(x), YAxis = Units.Unit(y), Normal = Units.Unit(x.Cross(y).Normalized()) };
    }

    private static IEnumerable<Vec3> Corners(double[] b)
    {
        foreach (var x in new[] { b[0], b[3] })
            foreach (var y in new[] { b[1], b[4] })
                foreach (var z in new[] { b[2], b[5] })
                    yield return new Vec3(x, y, z);
    }

    private static HashSet<string> SegmentNames(ISketch s) => s.Segments().Select(x => x.GetName()).ToHashSet(StringComparer.Ordinal);

    private static void DeleteSegments(IModelDoc2 d, List<ISketchSegment> segs)
    {
        if (segs.Count == 0) return;
        try
        {
            d.ClearSelection2(true);
            foreach (var s in segs) s.Select4(true, null);
            d.EditDelete();
        }
        catch { /* best effort */ }
        finally { d.ClearSelection2(true); }
    }

    /// <summary>Converts [x,y] sketch or [x,y,z] model points (mm) to sketch coordinates (m).</summary>
    private sealed class PointConverter(Xform modelToSketch, bool is3D, string arg, SwContext ctx)
    {
        public bool UsedModelSpace { get; private set; }

        public Vec3 P(double[]? p, string field)
        {
            if (p is null) throw SwException.InvalidArgument($"{arg}.{field} is required.");
            if (p.Any(v => !double.IsFinite(v))) throw SwException.InvalidArgument($"{arg}.{field} must contain finite numbers.");
            if (p.Length == 2)
            {
                if (is3D) throw SwException.InvalidArgument($"{arg}.{field}: a 3D sketch needs [x, y, z] model points.");
                return new Vec3(Units.M(p[0]), Units.M(p[1]), 0);
            }
            if (p.Length == 3)
            {
                var m = new Vec3(Units.M(p[0]), Units.M(p[1]), Units.M(p[2]));
                if (is3D) return m;
                UsedModelSpace = true;
                var s = modelToSketch.ApplyPoint(m);
                if (Math.Abs(s.Z) > Tol)
                    ctx.Warn($"{arg}.{field} [{string.Join(", ", p)}] lies {Units.Mm(s.Z)} mm off the sketch plane; projected onto it.");
                return new Vec3(s.X, s.Y, 0);
            }
            throw SwException.InvalidArgument($"{arg}.{field} must be [x, y] (sketch mm) or [x, y, z] (model mm).");
        }
    }

    private static void Validate(SketchEntity e, int i)
    {
        string a = $"entities[{i}]";
        double Pos(double? v, string f) => v is double x && double.IsFinite(x) && x > 0 ? x
            : throw SwException.InvalidArgument($"{a}.{f} must be a positive number (mm).");
        switch (e.Kind?.Trim().ToLowerInvariant())
        {
            case "line" or "centerline":
                if (e.From is null || e.To is null) throw SwException.InvalidArgument($"{a} ({e.Kind}) needs 'from' and 'to'.");
                break;
            case "polyline":
                if (e.Points is null || e.Points.Length < 2) throw SwException.InvalidArgument($"{a} (polyline) needs at least 2 'points'.");
                break;
            case "rectangle":
                if (e.Corners is { Length: 2 }) break;
                if (e.Center is null) throw SwException.InvalidArgument($"{a} (rectangle) needs 'corners' [[x1,y1],[x2,y2]] or 'center' + widthMm + heightMm.");
                Pos(e.WidthMm, "widthMm"); Pos(e.HeightMm, "heightMm");
                break;
            case "circle":
                if (e.Center is null) throw SwException.InvalidArgument($"{a} (circle) needs 'center'.");
                if (e.RadiusMm is null && e.DiameterMm is null) throw SwException.InvalidArgument($"{a} (circle) needs radiusMm or diameterMm.");
                if (e.RadiusMm is not null) Pos(e.RadiusMm, "radiusMm"); else Pos(e.DiameterMm, "diameterMm");
                break;
            case "arc":
                if (e.Start is not null && e.Mid is not null && e.End is not null) break;
                if (e.Center is null || e.StartDeg is null || e.EndDeg is null)
                    throw SwException.InvalidArgument($"{a} (arc) needs start+mid+end, or center+radiusMm+startDeg+endDeg.");
                Pos(e.RadiusMm, "radiusMm");
                break;
            case "slot":
                if (e.From is null || e.To is null) throw SwException.InvalidArgument($"{a} (slot) needs 'from' and 'to' (centers of its ends).");
                Pos(e.WidthMm, "widthMm");
                break;
            case "polygon":
                if (e.Center is null) throw SwException.InvalidArgument($"{a} (polygon) needs 'center'.");
                if (e.Sides is not (>= 3 and <= 40)) throw SwException.InvalidArgument($"{a} (polygon) needs 'sides' between 3 and 40.");
                if (e.CircumradiusMm is null && e.InradiusMm is null) throw SwException.InvalidArgument($"{a} (polygon) needs circumradiusMm or inradiusMm.");
                break;
            case "ellipse":
                if (e.Center is null) throw SwException.InvalidArgument($"{a} (ellipse) needs 'center'.");
                Pos(e.MajorRadiusMm, "majorRadiusMm"); Pos(e.MinorRadiusMm, "minorRadiusMm");
                if (e.MinorRadiusMm > e.MajorRadiusMm) throw SwException.InvalidArgument($"{a}: minorRadiusMm must not exceed majorRadiusMm.");
                break;
            case "spline":
                if (e.Points is null || e.Points.Length < 2) throw SwException.InvalidArgument($"{a} (spline) needs at least 2 'points'.");
                break;
            case "point":
                if (e.At is null) throw SwException.InvalidArgument($"{a} (point) needs 'at'.");
                break;
            default:
                throw SwException.InvalidArgument($"{a}: unknown kind '{e.Kind}'.",
                    "Use line, centerline, polyline, rectangle, circle, arc, slot, polygon, ellipse, spline or point.");
        }
    }

    private static void Draw(IModelDoc2 d, ISketchManager sm, SketchEntity e, PointConverter c, List<string> notes)
    {
        string kind = e.Kind.Trim().ToLowerInvariant();
        bool construction = e.Construction == true;
        object? made;
        switch (kind)
        {
            case "line":
            case "centerline":
            {
                var a = c.P(e.From, "from");
                var b = c.P(e.To, "to");
                if (a.DistanceTo(b) < Tol) throw SwException.InvalidArgument("A line needs two distinct points.");
                made = kind == "line" ? sm.CreateLine(a.X, a.Y, a.Z, b.X, b.Y, b.Z) : sm.CreateCenterLine(a.X, a.Y, a.Z, b.X, b.Y, b.Z);
                break;
            }
            case "polyline":
            {
                var pts = e.Points!.Select((p, k) => c.P(p, $"points[{k}]")).ToList();
                if (e.Closed == true && pts[0].DistanceTo(pts[^1]) > Tol) pts.Add(pts[0]);
                made = null;
                for (int k = 0; k + 1 < pts.Count; k++)
                {
                    var seg = sm.CreateLine(pts[k].X, pts[k].Y, pts[k].Z, pts[k + 1].X, pts[k + 1].Y, pts[k + 1].Z)
                              ?? throw new SwException(ErrorCodes.FeatureFailed, $"Polyline segment {k} could not be created.");
                    if (construction) seg.ConstructionGeometry = true;
                    made = seg;
                }
                construction = false; // already applied
                break;
            }
            case "rectangle":
            {
                Vec3 p1, p2;
                if (e.Corners is { Length: 2 })
                {
                    p1 = c.P(e.Corners[0], "corners[0]");
                    p2 = c.P(e.Corners[1], "corners[1]");
                    made = (object?)sm.CreateCornerRectangle(p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z);
                }
                else
                {
                    var ctr = c.P(e.Center, "center");
                    double w = Units.M(e.WidthMm!.Value) / 2, h = Units.M(e.HeightMm!.Value) / 2;
                    made = (object?)sm.CreateCenterRectangle(ctr.X, ctr.Y, ctr.Z, ctr.X + w, ctr.Y + h, ctr.Z);
                }
                break;
            }
            case "circle":
            {
                var ctr = c.P(e.Center, "center");
                double r = e.RadiusMm is double rm ? Units.M(rm) : Units.M(e.DiameterMm!.Value) / 2;
                made = sm.CreateCircleByRadius(ctr.X, ctr.Y, ctr.Z, r);
                break;
            }
            case "arc":
            {
                if (e.Start is not null && e.Mid is not null && e.End is not null)
                {
                    var s = c.P(e.Start, "start");
                    var m = c.P(e.Mid, "mid");
                    var en = c.P(e.End, "end");
                    made = sm.Create3PointArc(s.X, s.Y, s.Z, en.X, en.Y, en.Z, m.X, m.Y, m.Z);
                }
                else
                {
                    var ctr = c.P(e.Center, "center");
                    double r = Units.M(e.RadiusMm!.Value);
                    double a0 = Units.Rad(e.StartDeg!.Value), a1 = Units.Rad(e.EndDeg!.Value);
                    var s = new Vec3(ctr.X + r * Math.Cos(a0), ctr.Y + r * Math.Sin(a0), ctr.Z);
                    var en = new Vec3(ctr.X + r * Math.Cos(a1), ctr.Y + r * Math.Sin(a1), ctr.Z);
                    made = sm.CreateArc(ctr.X, ctr.Y, ctr.Z, s.X, s.Y, s.Z, en.X, en.Y, en.Z, 1); // +1 = counter-clockwise
                }
                break;
            }
            case "slot":
            {
                // Built from two lines and two tangent semicircles (deterministic; no dependence on slot options).
                var a = c.P(e.From, "from");
                var b = c.P(e.To, "to");
                double r = Units.M(e.WidthMm!.Value) / 2;
                var dir = (b - a);
                if (dir.Length < Tol) throw SwException.InvalidArgument("A slot needs two distinct end centers.");
                dir = dir.Normalized();
                var n = new Vec3(-dir.Y, dir.X, 0) * r;
                var a1 = a + n; var a2 = a - n; var b1 = b + n; var b2 = b - n;
                var l1 = sm.CreateLine(a1.X, a1.Y, 0, b1.X, b1.Y, 0);
                var l2 = sm.CreateLine(b2.X, b2.Y, 0, a2.X, a2.Y, 0);
                var arcB = sm.CreateArc(b.X, b.Y, 0, b1.X, b1.Y, 0, b2.X, b2.Y, 0, -1);
                var arcA = sm.CreateArc(a.X, a.Y, 0, a2.X, a2.Y, 0, a1.X, a1.Y, 0, -1);
                if (l1 is null || l2 is null || arcB is null || arcA is null)
                    throw new SwException(ErrorCodes.FeatureFailed, "The slot could not be drawn.");
                if (construction) foreach (var s in new[] { l1, l2, arcA, arcB }) s.ConstructionGeometry = true;
                construction = false;
                made = l1;
                break;
            }
            case "polygon":
            {
                var ctr = c.P(e.Center, "center");
                int sides = e.Sides!.Value;
                double rCirc = e.CircumradiusMm is double cr ? Units.M(cr) : Units.M(e.InradiusMm!.Value) / Math.Cos(Math.PI / sides);
                double rot = Units.Rad(e.RotationDeg ?? 90);
                var v = new Vec3(ctr.X + rCirc * Math.Cos(rot), ctr.Y + rCirc * Math.Sin(rot), ctr.Z);
                // Inscribed = true: the construction circle passes through the vertices.
                made = (object?)sm.CreatePolygon(ctr.X, ctr.Y, ctr.Z, v.X, v.Y, v.Z, sides, true);
                break;
            }
            case "ellipse":
            {
                var ctr = c.P(e.Center, "center");
                double a = Units.M(e.MajorRadiusMm!.Value), b = Units.M(e.MinorRadiusMm!.Value);
                double rot = Units.Rad(e.RotationDeg ?? 0);
                var major = new Vec3(ctr.X + a * Math.Cos(rot), ctr.Y + a * Math.Sin(rot), ctr.Z);
                // The minor point exactly perpendicular to the major axis (a skewed one distorts the ellipse).
                var minor = new Vec3(ctr.X - b * Math.Sin(rot), ctr.Y + b * Math.Cos(rot), ctr.Z);
                made = sm.CreateEllipse(ctr.X, ctr.Y, ctr.Z, major.X, major.Y, major.Z, minor.X, minor.Y, minor.Z);
                break;
            }
            case "spline":
            {
                var pts = e.Points!.Select((p, k) => c.P(p, $"points[{k}]")).SelectMany(p => new[] { p.X, p.Y, p.Z }).ToArray();
                made = sm.CreateSpline2(pts, true);
                break;
            }
            case "point":
            {
                var p = c.P(e.At, "at");
                made = sm.CreatePoint(p.X, p.Y, p.Z);
                construction = false;
                break;
            }
            default:
                throw SwException.InvalidArgument($"Unknown kind '{e.Kind}'.");
        }
        if (made is null)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS could not create the {kind}.",
                "Check the coordinates (degenerate geometry such as zero size or coincident points fails).");
        if (construction)
        {
            foreach (var s in made switch
                     {
                         ISketchSegment one => [one],
                         Array arr => arr.OfType<ISketchSegment>().ToArray(),
                         _ => Array.Empty<ISketchSegment>(),
                     })
                s.ConstructionGeometry = true;
        }
    }

    /// <summary>Tier-B check of one entity: expected segment signature and key geometry read back.</summary>
    private static SketchEntityResult Check(SketchEntity e, int i, List<ISketchSegment> segs, int newPoints, List<string> checks, PointConverter c)
    {
        string kind = e.Kind.Trim().ToLowerInvariant();
        string a = $"entities[{i}] ({kind})";
        var lines = segs.Where(s => s.GetType() == (int)swSketchSegments_e.swSketchLINE).ToList();
        var arcs = segs.Where(s => s.GetType() == (int)swSketchSegments_e.swSketchARC).ToList();
        void Expect(bool ok, string what)
        {
            if (!ok) throw new SwException(ErrorCodes.VerifyFailed, $"{a}: {what}. Nothing from this call was kept.",
                "Check for degenerate input (zero size, duplicate points) and retry.");
            checks.Add(what);
        }
        IReadOnlyDictionary<string, string>? sides = null;
        string? constructionCircle = null;
        switch (kind)
        {
            case "line" or "centerline":
                Expect(lines.Count == 1, "1 line created");
                break;
            case "polyline":
                Expect(lines.Count == e.Points!.Length - 1 + (e.Closed == true ? 1 : 0) || lines.Count == e.Points.Length - 1, $"{lines.Count} lines created");
                break;
            case "rectangle":
            {
                var solid = lines.Where(l => !l.ConstructionGeometry).ToList();
                Expect(solid.Count == 4, "4 lines created");
                var mids = solid.Select(l => (Name: l.GetName(), Mid: Mid((ISketchLine)l))).ToList();
                sides = new Dictionary<string, string>
                {
                    ["bottom"] = mids.MinBy(m => m.Mid.Y).Name,
                    ["top"] = mids.MaxBy(m => m.Mid.Y).Name,
                    ["left"] = mids.MinBy(m => m.Mid.X).Name,
                    ["right"] = mids.MaxBy(m => m.Mid.X).Name,
                };
                break;
            }
            case "circle":
            {
                Expect(arcs.Count == 1, "1 circle created");
                var arc = (ISketchArc)arcs[0];
                double want = e.RadiusMm is double rm ? Units.M(rm) : Units.M(e.DiameterMm!.Value) / 2;
                Expect(Math.Abs(arc.GetRadius() - want) < Tol, $"radius {Units.Mm(arc.GetRadius())} mm");
                var ctr = c.P(e.Center, "center");
                var cp = (object?)arc.GetCenterPoint2() as ISketchPoint;
                Expect(cp is not null && Math.Abs(cp.X - ctr.X) < Tol && Math.Abs(cp.Y - ctr.Y) < Tol, "center at the requested point");
                break;
            }
            case "arc":
                Expect(arcs.Count == 1, "1 arc created");
                if (e.RadiusMm is double ar) Expect(Math.Abs(((ISketchArc)arcs[0]).GetRadius() - Units.M(ar)) < Tol, $"radius {ar} mm");
                break;
            case "slot":
                Expect(lines.Count == 2 && arcs.Count == 2, "2 lines + 2 arcs created");
                break;
            case "polygon":
            {
                var solid = lines.Where(l => !l.ConstructionGeometry).ToList();
                Expect(solid.Count == e.Sides, $"{e.Sides} sides created");
                constructionCircle = segs.FirstOrDefault(s => s.ConstructionGeometry && s.GetType() == (int)swSketchSegments_e.swSketchARC)?.GetName();
                break;
            }
            case "ellipse":
                Expect(segs.Count(s => s.GetType() == (int)swSketchSegments_e.swSketchELLIPSE) == 1, "1 ellipse created");
                break;
            case "spline":
                Expect(segs.Count(s => s.GetType() == (int)swSketchSegments_e.swSketchSPLINE) == 1, "1 spline created");
                break;
            case "point":
                Expect(newPoints >= 1, "1 point created");
                break;
        }
        return new SketchEntityResult
        {
            Kind = kind,
            Label = e.Label,
            Segments = segs.Select(s => s.GetName()).ToList(),
            Sides = sides,
            ConstructionCircle = constructionCircle,
            Checks = checks.Count == 0 ? null : checks,
        };
    }

    private static Vec3 Mid(ISketchLine l)
    {
        var s = (object?)l.GetStartPoint2() as ISketchPoint;
        var e = (object?)l.GetEndPoint2() as ISketchPoint;
        if (s is null || e is null) return Vec3.Zero;
        return new Vec3((s.X + e.X) / 2, (s.Y + e.Y) / 2, 0);
    }

    internal static ContourStatus Contours(ISketch s)
    {
        int segs = s.Segments().Count(x => !x.ConstructionGeometry);
        if (segs == 0) return new ContourStatus { Status = "empty", Segments = 0 };
        int open = 0, closed = 0;
        int status = s.CheckFeatureUse((int)swSketchCheckFeatureProfileUsage_e.swSketchCheckFeature_BASEEXTRUDE, ref open, ref closed);
        int regions = 0;
        try { regions = s.GetSketchRegionCount(); } catch { /* 3D sketch */ }
        string st = open == 0 && closed > 0 ? "closed" : closed == 0 && open > 0 ? "open" : open > 0 && closed > 0 ? "mixed" : "invalid";
        string? problem = status == 0 ? null : Enum.IsDefined(typeof(swSketchCheckFeatureStatus_e), status)
            ? ((swSketchCheckFeatureStatus_e)status).ToString().Replace("swSketchCheckFeatureStatus_", "") : $"status {status}";
        return new ContourStatus { Status = st, ClosedContours = closed, OpenContours = open, Regions = regions, Segments = segs, Problem = problem };
    }

    private static string ConstrainedName(int s) => s switch
    {
        (int)swConstrainedStatus_e.swUnderConstrained => "under",
        (int)swConstrainedStatus_e.swFullyConstrained => "fully",
        (int)swConstrainedStatus_e.swOverConstrained => "over",
        (int)swConstrainedStatus_e.swNoSolution => "no_solution",
        (int)swConstrainedStatus_e.swInvalidSolution => "invalid",
        (int)swConstrainedStatus_e.swAutosolveOff => "autosolve_off",
        _ => "unknown",
    };
}
