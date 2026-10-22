using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>One body of a part (mm, model space).</summary>
public sealed class BodyRow
{
    /// <summary>Registry id ("B2"), usable as {id} wherever a body is referenced.</summary>
    public required string Id { get; init; }

    /// <summary>The body's name (the last feature that changed it; it changes as features are added).</summary>
    public required string Name { get; init; }

    /// <summary>solid | surface.</summary>
    public required string Type { get; init; }

    /// <summary>Solids only.</summary>
    public double? VolumeMm3 { get; init; }

    public double AreaMm2 { get; init; }
    public BoxMm? Box { get; init; }
    public int Faces { get; init; }
    public int Edges { get; init; }
    public bool? Hidden { get; init; }
}

public sealed class BodyListResult : ToolResult
{
    public required IReadOnlyList<BodyRow> Bodies { get; init; }
    public int Total { get; init; }
    public int Solids { get; init; }
    public int Surfaces { get; init; }
}

/// <summary>What SOLIDWORKS' Measure reports for one or two entities (mm, mm², degrees); values that don't apply are omitted.</summary>
public sealed class MeasureResult : ToolResult
{
    /// <summary>Two entities: their distance (arcs/circles per <see cref="ArcOption"/>).</summary>
    public double? DistanceMm { get; init; }

    public double? NormalDistanceMm { get; init; }
    public double? CenterDistanceMm { get; init; }
    public double? AngleDeg { get; init; }

    /// <summary>Two entities: [dx, dy, dz] between the measured points.</summary>
    public double[]? DeltaMm { get; init; }

    public double? LengthMm { get; init; }
    public double? AreaMm2 { get; init; }
    public double? PerimeterMm { get; init; }
    public double? RadiusMm { get; init; }
    public double? DiameterMm { get; init; }
    public double? ArcLengthMm { get; init; }

    /// <summary>One vertex or point: its coordinates.</summary>
    public double[]? PointMm { get; init; }

    public bool? Parallel { get; init; }
    public bool? Perpendicular { get; init; }

    /// <summary>How arcs and circles are measured: center_to_center | minimum | maximum | custom (only when one is involved).</summary>
    public string? ArcOption { get; init; }

    public required IReadOnlyList<ResolvedInfo> Resolved { get; init; }
}

/// <summary>Mass properties with labelled units (kg, mm, mm², mm³, kg·mm²).</summary>
public sealed class MassResult : ToolResult
{
    /// <summary>"document", or the body's label.</summary>
    public required string Of { get; init; }

    public double MassKg { get; init; }
    public double VolumeMm3 { get; init; }
    public double AreaMm2 { get; init; }
    public double DensityKgM3 { get; init; }
    public required double[] CenterOfMassMm { get; init; }

    /// <summary>Principal moments of inertia at the center of mass, as SOLIDWORKS orders them (Px, Py, Pz).</summary>
    public double[]? PrincipalMomentsKgMm2 { get; init; }

    /// <summary>The principal axes (unit vectors), in the order of the moments.</summary>
    public double[][]? PrincipalAxes { get; init; }

    /// <summary>The inertia tensor at the center of mass along the model axes, rows [Lxx, Lxy, Lxz] … as SOLIDWORKS reports it.</summary>
    public double[][]? MomentsAtCenterKgMm2 { get; init; }

    public string? Material { get; init; }

    /// <summary>Parts: the number of solid bodies measured.</summary>
    public int? Bodies { get; init; }

    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>One fault IBody2.Check3 found.</summary>
public sealed class BodyFault
{
    /// <summary>swFaultEntityErrorCode_e value.</summary>
    public int Code { get; init; }

    /// <summary>Its name, e.g. FaceSelfIntersecting.</summary>
    public required string Error { get; init; }

    /// <summary>face | edge | vertex | body | entity, when SOLIDWORKS names the faulty entity.</summary>
    public string? Entity { get; init; }

    public string? Id { get; init; }

    /// <summary>A point on the faulty entity (mm), when it can be computed.</summary>
    public double[]? AtMm { get; init; }
}

public sealed class BodyCheckRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public bool Valid { get; init; }

    /// <summary>Empty when the body is valid.</summary>
    public required IReadOnlyList<BodyFault> Faults { get; init; }
}

public sealed class ModelCheckResult : ToolResult
{
    public bool Valid { get; init; }
    public int FaultCount { get; init; }
    public required IReadOnlyList<BodyCheckRow> Bodies { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>
/// Read-only measurement (DESIGN §4.3): bodies, Measure, mass properties and body checks. Measure selects the entities
/// the verified way (select, then IMeasure.Calculate on the selection); in documents the server doesn't own, the user's
/// selection is suspended and restored around it.
/// </summary>
public sealed class MeasureService(SwSession session)
{
    private const double NotApplicable = -1; // what IMeasure reports for a value that doesn't apply

    private static readonly EntityKind[] MeasureKinds =
    [
        EntityKind.Face, EntityKind.Edge, EntityKind.Vertex, EntityKind.Plane, EntityKind.Axis, EntityKind.Point,
        EntityKind.SketchSegment, EntityKind.SketchPoint, EntityKind.Origin, EntityKind.CoordSys,
    ];

    // ------------------------------------------------------------------ body_list

    public Task<BodyListResult> BodyListAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("body_list", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            var rows = new List<BodyRow>();
            foreach (var b in part.AllBodies(visibleOnly: false))
            {
                bool solid = b.GetType() == (int)swBodyType_e.swSolidBody;
                var mp = b.MassOfBody(); // [cx, cy, cz, volume, area, mass, ...]
                rows.Add(new BodyRow
                {
                    Id = ctx.Entities.IdFor(d, b, 'B', "body"),
                    Name = b.Name,
                    Type = solid ? "solid" : "surface",
                    VolumeMm3 = solid && mp.Length >= 4 ? Units.Mm3(mp[3]) : null,
                    AreaMm2 = mp.Length >= 5 ? Units.Mm2(mp[4]) : 0,
                    Box = BoxMm.FromMeters(b.Box()),
                    Faces = b.GetFaceCount(),
                    Edges = b.GetEdgeCount(),
                    Hidden = b.Visible ? null : true,
                });
            }
            return new BodyListResult
            {
                Doc = ctx.Title(d),
                Bodies = rows,
                Total = rows.Count,
                Solids = rows.Count(r => r.Type == "solid"),
                Surfaces = rows.Count(r => r.Type == "surface"),
            };
        }, RunOptions.Read(doc), ct);

    // ------------------------------------------------------------------ model_measure

    public Task<MeasureResult> MeasureAsync(string? doc, EntityRef? a, EntityRef? b, CancellationToken ct = default) =>
        session.RunAsync("model_measure", ctx =>
        {
            if (a is null)
                throw SwException.InvalidArgument("'a' is required: the entity to measure (or the first of two).",
                    "E.g. a:{type:'face', at:[0,20,0]}, b:{type:'face', at:[0,0,0]}.");
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var ra = EntityResolver.Resolve(ctx, d, a, "a", MeasureKinds);
            var rb = b is null ? null : EntityResolver.Resolve(ctx, d, b, "b", MeasureKinds);
            if (rb is not null && (ra.Id ?? ra.Label) == (rb.Id ?? rb.Label))
                throw SwException.InvalidArgument($"'a' and 'b' are the same entity ({ra.Label}).", "Omit 'b' to measure one entity.");
            var resolved = new List<ResolvedInfo> { ra.Info("a") };
            if (rb is not null) resolved.Add(rb.Info("b"));
            bool two = rb is not null;

            return WithSelection(ctx, d, sel =>
            {
                if (!ra.Select(d, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {ra.Label}.");
                if (rb is not null && !rb.Select(d, true, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {rb.Label}.");
                int n = sel.GetSelectedObjectCount2(-1);
                if (n != (two ? 2 : 1))
                    throw new SwException(ErrorCodes.Internal, $"Selecting the {(two ? "two entities" : "entity")} gave {n} selected objects.");
                var m = d.Extension.CreateMeasure()
                        ?? throw new SwException(ErrorCodes.Internal, "SOLIDWORKS returned no Measure object.");
                // The verified recipe passes VBA's Nothing; C# examples pass null (VT_EMPTY). Both mean "the selection".
                if (!m.Calculate(ComArgs.Nothing) && !m.Calculate(ComArgs.Empty))
                    throw new SwException(ErrorCodes.NotSupported, $"SOLIDWORKS could not measure {Describe(ra, rb)}.",
                        "Measure faces, edges, vertices, planes, axes, points or sketch entities; for mass use model_mass.");

                bool pointLike = !two && ra.Kind is EntityKind.Vertex or EntityKind.Point or EntityKind.SketchPoint or EntityKind.Origin;
                double[]? point = null;
                if (pointLike)
                {
                    // A coordinate of exactly -1 m would read as "not applicable", so the entity's own position is reported
                    // and Measure's X/Y/Z only cross-check it.
                    var own = OwnPoint(ra);
                    Vec3? reported = Applies(m.X) && Applies(m.Y) && Applies(m.Z) ? new Vec3(m.X, m.Y, m.Z) : null;
                    if (own is Vec3 o && reported is Vec3 rep && o.DistanceTo(rep) > 1e-7)
                        ctx.Warn($"Measure reports [{TopoQuery.Fmt(Clean(Units.Mm(rep)))}] mm for {ra.Label}, its geometry [{TopoQuery.Fmt(Clean(Units.Mm(o)))}] mm (reported).");
                    point = (own ?? reported) is Vec3 p ? Clean(Units.Mm(p)) : null;
                }
                double dx = m.DeltaX, dy = m.DeltaY, dz = m.DeltaZ;
                var r = new MeasureResult
                {
                    Doc = ctx.Title(d),
                    DistanceMm = two ? Len(m.Distance) : null,
                    NormalDistanceMm = two ? Len(m.NormalDistance) : null,
                    CenterDistanceMm = two ? Len(m.CenterDistance) : null,
                    AngleDeg = two ? Ang(m.Angle) : null,
                    DeltaMm = two && Applies(dx) && Applies(dy) && Applies(dz) ? [Mm(dx), Mm(dy), Mm(dz)] : null,
                    LengthMm = Len(m.Length),
                    AreaMm2 = Area(m.Area),
                    PerimeterMm = Len(m.Perimeter),
                    RadiusMm = Len(m.Radius),
                    DiameterMm = Len(m.Diameter),
                    ArcLengthMm = Len(m.ArcLength),
                    PointMm = point,
                    Parallel = two ? m.IsParallel : null,
                    Perpendicular = two ? m.IsPerpendicular : null,
                    ArcOption = IsArcLike(ra) || (rb is not null && IsArcLike(rb)) ? ArcOptionName(m.ArcOption) : null,
                    Resolved = resolved,
                };
                if (r.DistanceMm is null && r.NormalDistanceMm is null && r.CenterDistanceMm is null && r.AngleDeg is null && r.DeltaMm is null &&
                    r.LengthMm is null && r.AreaMm2 is null && r.PerimeterMm is null && r.RadiusMm is null && r.ArcLengthMm is null && r.PointMm is null)
                    throw new SwException(ErrorCodes.NotSupported, $"SOLIDWORKS reported no applicable measurement for {Describe(ra, rb)}.",
                        "Try other entities (e.g. faces or edges instead of features).");
                return r;
            });
        }, RunOptions.Read(doc), ct);

    private static string Describe(Resolved a, Resolved? b) => b is null ? a.Label : $"{a.Label} and {b.Label}";

    /// <summary>The model position (m) of a vertex, reference point, sketch point or the origin; null if unknown.</summary>
    private static Vec3? OwnPoint(Resolved r)
    {
        try
        {
            switch (r.Obj)
            {
                case IVertex v:
                    return Vec3.From(v.Point());
                case IFeature when r.Kind == EntityKind.Origin:
                    return Vec3.Zero;
                case IFeature f when f.Specific() is IRefPoint rp && rp.RefPointXyz() is { Length: >= 3 } xyz:
                    return Vec3.From(xyz);
                case ISketchPoint sp:
                    var local = new Vec3(sp.X, sp.Y, sp.Z);
                    return sp.GetSketch() is ISketch sk && !sk.Is3D() ? SketchService.SketchToModel(sk).ApplyPoint(local) : local;
            }
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* fall back to what Measure reports */ }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a clean selection and clears it afterwards. In documents the server doesn't own,
    /// the user's selection is suspended first and resumed afterwards (DESIGN §3.8). Never while the user is in a command:
    /// the selections would land in that command's PropertyManager.
    /// </summary>
    private static T WithSelection<T>(SwContext ctx, IModelDoc2 d, Func<ISelectionMgr, T> work)
    {
        ctx.CheckUserCommand();
        var sel = d.Sel();
        bool suspend = ctx.DocEntry.Class != DocClass.Owned;
        if (suspend) sel.SuspendSelectionList();
        else d.ClearSelection2(true);
        try
        {
            return work(sel);
        }
        finally
        {
            try
            {
                d.ClearSelection2(true);
                if (suspend) sel.ResumeSelectionList();
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
                ctx.Warn($"The selection could not be restored ({e.Message}).");
            }
        }
    }

    private static bool IsArcLike(Resolved r) => r.Obj switch
    {
        IEdge e => Topology.CurveKind(e.Curve()) == "circle",
        IFace2 f => Topology.SurfaceKind(f.Surface()) is "cylinder" or "cone" or "sphere" or "torus",
        ISketchSegment s => s.GetType() == (int)swSketchSegments_e.swSketchARC,
        _ => false,
    };

    private static string ArcOptionName(int option) => option switch
    {
        (int)swMeasureArcCircleOption_e.swMeasureArcCircle_CenterToCenter => "center_to_center",
        (int)swMeasureArcCircleOption_e.swMeasureArcCircle_MinimumDistance => "minimum",
        (int)swMeasureArcCircleOption_e.swMeasureArcCircle_MaximumDistance => "maximum",
        _ => "custom",
    };

    private static bool Applies(double v) => v != NotApplicable && double.IsFinite(v);

    private static double? Len(double v) => Applies(v) ? Mm(v) : null;

    private static double? Area(double v) => Applies(v) ? Clean(Units.Mm2(v)) : null;

    private static double? Ang(double v) => Applies(v) ? Clean(Units.Deg(v)) : null;

    // ------------------------------------------------------------------ model_mass

    /// <summary>One reading of a mass-property object (SI units: kg, m, m², m³, kg·m²).</summary>
    private sealed record MassData(double Mass, double Volume, double Area, double Density, double[] Center, double[] Moments,
        double[][] Axes, double[] Tensor);

    public Task<MassResult> MassAsync(string? doc, EntityRef? body, CancellationToken ct = default) =>
        session.RunAsync("model_mass", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            bool isPart = d.GetType() == (int)swDocumentTypes_e.swDocPART;
            if (d.SketchManager.ActiveSketch is ISketch open)
                throw new SwException(ErrorCodes.SketchState,
                    $"Sketch '{((IFeature)open).Name}' is open, and SOLIDWORKS reports zero mass properties while a sketch is open.",
                    "Finish it with sketch_finish first (body_list gives per-body volumes even with a sketch open).");
            Resolved? rb = null;
            if (body is not null)
            {
                if (!isPart) throw SwException.InvalidArgument("'body' applies to parts; for an assembly omit it (the whole assembly is measured).");
                rb = ResolveBody(ctx, d, body, "body");
                if (((IBody2)rb.Obj).GetType() != (int)swBodyType_e.swSolidBody)
                    throw SwException.InvalidArgument($"{rb.Label} is a surface body; mass properties need a solid body.");
            }
            var part = isPart ? (IPartDoc)d : null;
            var solids = part?.Bodies(swBodyType_e.swSolidBody, visibleOnly: false) ?? [];
            if (isPart && solids.Count == 0)
                throw new SwException(ErrorCodes.EntityNotFound, $"'{ctx.Title(d)}' has no solid bodies, so it has no mass properties.",
                    "Create a solid first (sketch_start, sketch_add, feature_extrude); body_list shows the bodies.");

            var checks = new List<string>();
            MassData m;
            if (rb is not null)
            {
                m = BodyMass(d, (IBody2)rb.Obj, rb.Label, checks);
            }
            else
            {
                var mp = NewMass2(d);
                m = Read(mp);
                if (part is not null) CheckDocumentVolume(m, part, solids, checks);
                else if (!(m.Volume > 0))
                    throw new SwException(ErrorCodes.EntityNotFound, $"'{ctx.Title(d)}' has no solid geometry to measure.", "Insert components first.");
            }

            string? material = null;
            try
            {
                material = rb is not null
                    ? ((IBody2)rb.Obj).GetMaterialPropertyName("", out _)
                    : part?.GetMaterialPropertyName2("", out _);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* no material information */ }

            return new MassResult
            {
                Doc = ctx.Title(d),
                Of = rb?.Label ?? "document",
                MassKg = Units.R(m.Mass),
                VolumeMm3 = Units.Mm3(m.Volume),
                AreaMm2 = Units.Mm2(m.Area),
                DensityKgM3 = Math.Round(m.Density, 6),
                CenterOfMassMm = m.Center.Length >= 3 ? Clean(Units.Mm(m.Center[..3])) : [],
                PrincipalMomentsKgMm2 = m.Moments.Length >= 3 ? m.Moments[..3].Select(x => Clean(Units.KgMm2(x))).ToArray() : null,
                PrincipalAxes = m.Axes.Length == 3 && m.Axes.All(x => x.Length >= 3)
                    ? m.Axes.Select(x => Clean(Units.Unit(new Vec3(x[0], x[1], x[2]).Normalized()))).ToArray()
                    : null,
                MomentsAtCenterKgMm2 = m.Tensor.Length >= 9
                    ? [Row(m.Tensor, 0), Row(m.Tensor, 3), Row(m.Tensor, 6)]
                    : null,
                Material = string.IsNullOrWhiteSpace(material) ? null : material,
                Bodies = isPart ? (rb is null ? part!.Bodies(swBodyType_e.swSolidBody, visibleOnly: true).Count : 1) : null,
                Checks = checks.Count == 0 ? null : checks,
                Resolved = rb is null ? null : [rb.Info("body")],
            };
        }, RunOptions.Read(doc), ct);

    private static double[] Row(double[] t, int i) => [Clean(Units.KgMm2(t[i])), Clean(Units.KgMm2(t[i + 1])), Clean(Units.KgMm2(t[i + 2]))];

    private static IMassProperty2 NewMass2(IModelDoc2 d) =>
        (object?)d.Extension.CreateMassProperty2() as IMassProperty2
        ?? throw new SwException(ErrorCodes.Internal, "SOLIDWORKS returned no mass-property object.");

    private static MassData Read(IMassProperty2 mp)
    {
        double[][] axes = [];
        double[] tensor = [];
        try
        {
            axes = [mp.MassPrincipalAxis(0), mp.MassPrincipalAxis(1), mp.MassPrincipalAxis(2)];
            tensor = mp.MassTensor((int)swMassPropertyMoment_e.swMassPropertyMomentAboutCenterOfMass);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* moments are optional extras */ }
        return new MassData(mp.Mass, mp.Volume, mp.SurfaceArea, mp.Density, mp.MassCenter(), mp.MassPrincipalMoments(), axes, tensor);
    }

    private static MassData Read(IMassProperty mp)
    {
        double[][] axes = [];
        double[] tensor = [];
        try
        {
            axes = [mp.MassPrincipalAxis(0), mp.MassPrincipalAxis(1), mp.MassPrincipalAxis(2)];
            tensor = mp.MassTensor((int)swMassPropertyMoment_e.swMassPropertyMomentAboutCenterOfMass);
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* moments are optional extras */ }
        return new MassData(mp.Mass, mp.Volume, mp.SurfaceArea, mp.Density, mp.MassCenter(), mp.MassPrincipalMoments(), axes, tensor);
    }

    /// <summary>
    /// The document's result must match the solid bodies' own volumes (IBody2.GetMassProperties, always SI): that proves
    /// the units and what was included.
    /// </summary>
    private static void CheckDocumentVolume(MassData m, IPartDoc part, List<IBody2> solids, List<string> checks)
    {
        double visible = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: true).Sum(VolumeOf);
        double all = solids.Sum(VolumeOf);
        if (SameVolume(m.Volume, visible)) checks.Add("volume matches the visible solid bodies' own volumes");
        else if (SameVolume(m.Volume, all)) checks.Add("volume matches all solid bodies (hidden ones included)");
        else
            throw new SwException(ErrorCodes.VerifyFailed,
                $"The mass properties report {Units.Mm3(m.Volume)} mm³, but the solid bodies hold {Units.Mm3(visible)} mm³ (visible) / {Units.Mm3(all)} mm³ (all).",
                "Use body_list for per-body volumes; the document's mass-property options may differ from the defaults.");
    }

    /// <summary>
    /// Mass properties of one body. SelectedItems (IMassProperty2) and AddBodies (IMassProperty) are tried with both array
    /// flavours; a result counts only if its volume and center of mass match the body's own (IBody2.GetMassProperties).
    /// </summary>
    private static MassData BodyMass(IModelDoc2 d, IBody2 body, string label, List<string> checks)
    {
        var own = body.MassOfBody();
        if (own.Length < 4) throw new SwException(ErrorCodes.Internal, $"{label} reports no mass properties.");
        var center = Vec3.From(own, 0);
        double volume = own[3];
        var box = body.Box();
        double size = box.Length == 6 ? new Vec3(box[3] - box[0], box[4] - box[1], box[5] - box[2]).Length : 0;
        double comTol = Math.Max(1e-9, 1e-4 * size);

        var attempts = new (string How, Func<MassData?> Get)[]
        {
            ("SelectedItems", () =>
            {
                var mp = NewMass2(d);
                mp.SelectedItems = ComArgs.DispatchArray([body]);
                mp.Recalculate();
                return Read(mp);
            }),
            ("SelectedItems (variant array)", () =>
            {
                var mp = NewMass2(d);
                mp.SelectedItems = ComArgs.VariantArray(body);
                mp.Recalculate();
                return Read(mp);
            }),
            ("AddBodies", () =>
            {
                var mp = d.Extension.CreateMassProperty();
                return mp is not null && mp.AddBodies(ComArgs.VariantArray(body)) ? Read(mp) : null;
            }),
            ("AddBodies (dispatch array)", () =>
            {
                var mp = d.Extension.CreateMassProperty();
                return mp is not null && mp.AddBodies(ComArgs.DispatchArray([body])) ? Read(mp) : null;
            }),
        };
        var tried = new List<string>();
        foreach (var (how, get) in attempts)
        {
            MassData? m;
            try { m = get(); }
            catch (Exception e) when (!ComArgs.IsDisconnect(e) && e is COMException or InvalidCastException or ArgumentException)
            {
                tried.Add($"{how}: {e.GetType().Name}");
                continue;
            }
            if (m is null) { tried.Add($"{how}: refused"); continue; }
            if (SameVolume(m.Volume, volume) && m.Center.Length >= 3 && Vec3.From(m.Center).DistanceTo(center) <= comTol)
            {
                checks.Add($"restricted to {label} ({how}): volume and center of mass match the body's own");
                return m;
            }
            tried.Add($"{how}: {Units.Mm3(m.Volume)} mm³");
        }
        throw new SwException(ErrorCodes.VerifyFailed,
            $"Could not restrict the mass properties to {label} ({Units.Mm3(volume)} mm³); got {string.Join("; ", tried)}.",
            "Omit 'body' for the whole part; body_list gives each body's volume.");
    }

    private static double VolumeOf(IBody2 b)
    {
        var mp = b.MassOfBody();
        return mp.Length >= 4 ? mp[3] : 0;
    }

    /// <summary>Equal within 0.01 % (mass-property accuracy settings differ slightly between the two routes on curved bodies).</summary>
    private static bool SameVolume(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-12, 1e-4 * Math.Max(Math.Abs(a), Math.Abs(b)));

    // ------------------------------------------------------------------ model_check

    public Task<ModelCheckResult> CheckAsync(string? doc, EntityRef? body, CancellationToken ct = default) =>
        session.RunAsync("model_check", ctx =>
        {
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            Resolved? rb = body is null ? null : ResolveBody(ctx, d, body, "body");
            var bodies = rb is null ? part.AllBodies(visibleOnly: false) : [(IBody2)rb.Obj];
            if (bodies.Count == 0)
                throw new SwException(ErrorCodes.EntityNotFound, $"'{ctx.Title(d)}' has no bodies to check.",
                    "Create geometry first; body_list shows the bodies.");
            var rows = new List<BodyCheckRow>();
            foreach (var b in bodies)
            {
                var faults = b.FaultList().Select(f => Fault(ctx, d, f.Code, f.Entity)).ToList();
                rows.Add(new BodyCheckRow
                {
                    Id = ctx.Entities.IdFor(d, b, 'B', "body"),
                    Name = b.Name,
                    Type = b.GetType() == (int)swBodyType_e.swSolidBody ? "solid" : "surface",
                    Valid = faults.Count == 0,
                    Faults = faults,
                });
            }
            int count = rows.Sum(r => r.Faults.Count);
            return new ModelCheckResult
            {
                Doc = ctx.Title(d),
                Valid = count == 0,
                FaultCount = count,
                Bodies = rows,
                Resolved = rb is null ? null : [rb.Info("body")],
            };
        }, RunOptions.Read(doc), ct);

    /// <summary>Describes one fault: its error name and, where possible, the entity with an id and a point on it.</summary>
    private static BodyFault Fault(SwContext ctx, IModelDoc2 d, int code, object? entity)
    {
        string error = Enum.IsDefined(typeof(swFaultEntityErrorCode_e), code) && ((swFaultEntityErrorCode_e)code).ToString() is var n
            ? (n.StartsWith("sw", StringComparison.Ordinal) ? n[2..] : n)
            : $"code {code}";
        string? kind = null, id = null;
        double[]? at = null;
        try
        {
            switch (entity)
            {
                case IFace2 f:
                    kind = "face";
                    var fp = Topology.InteriorPoint(f);
                    at = Clean(Units.Mm(fp));
                    id = ctx.Entities.IdFor(d, f, 'F', "face", Topology.SurfaceKind(f.Surface()), fp);
                    break;
                case IEdge e:
                    kind = "edge";
                    var mid = Topology.Describe(e).Mid;
                    at = Clean(Units.Mm(mid));
                    id = ctx.Entities.IdFor(d, e, 'E', "edge", Topology.CurveKind(e.Curve()), mid);
                    break;
                case IVertex v:
                    kind = "vertex";
                    var vp = Vec3.From(v.Point());
                    at = Clean(Units.Mm(vp));
                    id = ctx.Entities.IdFor(d, v, 'V', "vertex", null, vp);
                    break;
                case IBody2 b:
                    kind = "body";
                    id = ctx.Entities.IdFor(d, b, 'B', "body");
                    break;
                case not null:
                    kind = "entity";
                    break;
            }
        }
        catch (Exception e) when (!ComArgs.IsDisconnect(e) && e is COMException or SwException or InvalidCastException or ArgumentException)
        {
            /* a broken entity may have no persistent reference or geometry: report what is known */
        }
        return new BodyFault { Code = code, Error = error, Entity = kind, Id = id, AtMm = at };
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>
    /// A body reference: {id:'B1'}, {name:'Boss-Extrude1'}, or {at:[x,y,z]} (the body of the face at that point).
    /// </summary>
    private static Resolved ResolveBody(SwContext ctx, IModelDoc2 d, EntityRef r, string arg)
    {
        bool byPoint = r.At is not null && string.IsNullOrWhiteSpace(r.Id) && r.Where is null &&
                       (r.Type is null || r.Type.Trim().Equals("body", StringComparison.OrdinalIgnoreCase) ||
                        r.Type.Trim().Equals("face", StringComparison.OrdinalIgnoreCase));
        if (!byPoint) return EntityResolver.Resolve(ctx, d, r, arg, EntityKind.Body);
        var face = EntityResolver.Resolve(ctx, d,
            new EntityRef { At = r.At, Type = "face", TolMm = r.TolMm, Strict = r.Strict, Component = r.Component }, arg, EntityKind.Face);
        var b = ((IFace2)face.Obj).Body() ?? throw new SwException(ErrorCodes.EntityNotFound, $"{face.Label} belongs to no body.");
        string id = ctx.Entities.IdFor(d, b, 'B', "body");
        return new Resolved { Obj = b, Kind = EntityKind.Body, Id = id, Label = $"body '{b.Name}' ({id})", Pick = face.Pick, Distance = face.Distance };
    }

    private static double Mm(double m) => Clean(Units.Mm(m));

    private static double Clean(double v) => v == 0 ? 0 : v; // no "-0" in results

    private static double[] Clean(double[] v) => v.Select(Clean).ToArray();
}
