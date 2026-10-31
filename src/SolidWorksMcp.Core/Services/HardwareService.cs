using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>hardware_part options.</summary>
public sealed record HardwarePartRequest
{
    public string? Standard { get; init; }
    public string? Size { get; init; }
    public double? LengthMm { get; init; }
    /// <summary>The library folder (default: "hardware" under the output folder).</summary>
    public string? Folder { get; init; }
}

public sealed class HardwarePartResult : ToolResult
{
    public required string Path { get; init; }
    public required string PartNo { get; init; }
    public required string Description { get; init; }
    /// <summary>false: the library already had it (made by this server, unchanged).</summary>
    public bool Created { get; init; }
    public double VolumeMm3 { get; init; }
    public double? MassKg { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>asm_fastener options.</summary>
public sealed record AsmFastenerRequest
{
    public string? Doc { get; init; }
    /// <summary>ISO 4762 (socket head, default) or ISO 4017 (hex head).</summary>
    public string? Standard { get; init; }
    public string? Size { get; init; }
    /// <summary>null with a nut: the shortest length that stands two threads past it.</summary>
    public double? LengthMm { get; init; }
    /// <summary>Each hole's centre on the face the head sits on, assembly mm.</summary>
    public IReadOnlyList<double[]>? Holes { get; init; }
    /// <summary>An ISO 7089 washer under the head (and under the nut).</summary>
    public bool Washer { get; init; }
    /// <summary>An ISO 4032 nut on the far side of a through hole.</summary>
    public bool Nut { get; init; }
    public string? Folder { get; init; }
}

public sealed class FastenedHole
{
    public required double[] AtMm { get; init; }
    public required string Screw { get; init; }
    /// <summary>The washers and nut of this hole (component names).</summary>
    public IReadOnlyList<string>? Others { get; init; }
    /// <summary>The hole's length from the seat through every part it passes (the grip of a through hole).</summary>
    public double GripMm { get; init; }
    public bool Through { get; init; }
    /// <summary>With a nut: how far the screw stands past it.</summary>
    public double? ProtrusionMm { get; init; }
    /// <summary>A blind hole: its depth less the screw's reach (negative: the screw is too long).</summary>
    public double? TipClearanceMm { get; init; }
}

public sealed class AsmFastenerResult : ToolResult
{
    public required string Screw { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<FastenedHole> Holes { get; init; }
    public int ComponentCount { get; init; }
    public int Mates { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// Standard hardware without Toolbox: hardware_part builds an ISO screw, nut, washer, pin or bearing from the standard's
/// table with this server's own verified features (a cosmetic thread, Alloy Steel, PartNo and Description) into a library
/// folder and reuses it after; asm_fastener finds holes by their centres, inserts screws (with washers and a nut) and mates
/// each concentric with its hole and seated on its face. Parts are modelled on the Top plane: a screw's head underside at
/// the origin with the shank along -y; nuts, washers and bearings stand on the origin plane along +y.
/// </summary>
public sealed class HardwareService(SwSession session)
{
    private const double Tol = 1e-5;     // m: a point on an axis, a face at a seat
    private const int MaxHoles = 50;

    // ================================================================== hardware_part

    public async Task<HardwarePartResult> PartAsync(HardwarePartRequest q, CancellationToken ct = default)
    {
        var spec = HardwareCatalog.Resolve(q.Standard, q.Size, q.LengthMm);
        string path = LibraryPath(q.Folder, spec);
        var existing = await session.RunAsync("hardware_part", ctx => Existing(ctx, path, spec), new RunOptions(), ct);
        if (existing is not null) return existing;

        var docs = new DocumentService(session);
        string doc = (await docs.NewAsync("part", "mmgs", null, Path.GetFileNameWithoutExtension(spec.FileName), ct)).Document.Handle;
        try
        {
            await BuildAsync(doc, spec, ct);
            var props = new PropertiesService(session);
            await props.MaterialSetAsync(doc, HardwareCatalog.Material, null, null, ct);
            foreach (var (name, value) in spec.Properties) await props.PropSetAsync(doc, name, value, null, "text", ct);
            var mass = await new MeasureService(session).MassAsync(doc, null, ct);
            if (Math.Abs(mass.VolumeMm3 - spec.VolumeMm3) > 1e-6 * spec.VolumeMm3 + 1e-3)
                throw new SwException(ErrorCodes.VerifyFailed, $"{spec.Designation} came out {mass.VolumeMm3} mm³, not the table's {Math.Round(spec.VolumeMm3, 3)} mm³.");
            await docs.SaveAsync(doc, "as", path, false, ct);
            return new HardwarePartResult
            {
                Doc = Path.GetFileNameWithoutExtension(path), Path = path, PartNo = spec.Designation, Description = spec.Description, Created = true,
                VolumeMm3 = Math.Round(mass.VolumeMm3, 3), MassKg = Math.Round(mass.MassKg, 6),
                Checks =
                [
                    $"volume {Math.Round(mass.VolumeMm3, 3)} mm³ = the {spec.Standard.Name} table's geometry",
                    spec.IsThreaded ? $"cosmetic thread {spec.Size.Size} over {spec.ThreadText} mm" : "no thread",
                    $"material {HardwareCatalog.Material}; PartNo and Description for the BOM",
                ],
            };
        }
        finally
        {
            try { await docs.CloseAsync(doc, save: false, discard: true, CancellationToken.None); }
            catch (SwException) { /* closed already */ }
        }
    }

    private string LibraryPath(string? folder, HardwareSpec spec)
    {
        string dir = string.IsNullOrWhiteSpace(folder) ? "hardware" : folder.Trim().Trim('"');
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(session.Paths.OutputDir, dir);
        return Path.GetFullPath(Path.Combine(dir, spec.FileName));
    }

    /// <summary>The library's part when it is there: made by this server, unchanged (else refused); null when missing.</summary>
    private static HardwarePartResult? Existing(SwContext ctx, string path, HardwareSpec spec)
    {
        if (!File.Exists(path)) return null;
        var open = ctx.App.Documents().FirstOrDefault(x => string.Equals(DocumentRegistry.SafePath(x), path, StringComparison.OrdinalIgnoreCase));
        var d = open;
        if (d is null)
        {
            int errors = 0, warnings = 0;
            d = ctx.App.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                    "", ref errors, ref warnings)
                ?? throw new SwException(ErrorCodes.IoError, $"'{path}' exists but SOLIDWORKS could not open it (error {errors}).");
        }
        try
        {
            string marker = "";
            if (d.Extension.CustomPropertyManager[""] is ICustomPropertyManager mgr &&
                ComArgs.Strings((object?)mgr.GetNames()).Contains(HardwareCatalog.MarkerProperty, StringComparer.OrdinalIgnoreCase))
                mgr.Get6(HardwareCatalog.MarkerProperty, false, out marker, out _, out _, out _);
            if (marker != HardwareCatalog.MarkerVersion)
                throw new SwException(ErrorCodes.IoError, $"'{path}' exists and wasn't made by hardware_part.",
                    "Use another folder, or move the file away.");
            double v = 0, m = 0;
            if ((object?)d.Extension.CreateMassProperty2() is IMassProperty2 mp) (v, m) = (Units.Mm3(mp.Volume), mp.Mass);
            if (Math.Abs(v - spec.VolumeMm3) > 1e-6 * spec.VolumeMm3 + 1e-3)
                throw new SwException(ErrorCodes.VerifyFailed, $"'{path}' was changed: {Math.Round(v, 3)} mm³, not {Math.Round(spec.VolumeMm3, 3)} mm³.",
                    "Move it away and hardware_part makes it again.");
            return new HardwarePartResult
            {
                Doc = Path.GetFileNameWithoutExtension(path), Path = path, PartNo = spec.Designation, Description = spec.Description, Created = false,
                VolumeMm3 = Math.Round(v, 3), MassKg = Math.Round(m, 6),
                Checks = ["the library already has it (made by hardware_part, volume unchanged)"],
            };
        }
        finally
        {
            if (open is null)
                try { ctx.App.CloseDoc(d.GetTitle()); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { ctx.Warn($"'{path}' was opened to check it and could not be closed again."); }
        }
    }

    private async Task BuildAsync(string doc, HardwareSpec spec, CancellationToken ct)
    {
        var sk = new SketchService(session);
        var fs = new FeatureService(session);
        var s = spec.Size;
        double l = spec.LengthMm ?? 0;
        var top = new EntityRef { Name = "top" };
        static SketchEntity Circle(double dia) => new() { Kind = "circle", Center = [0, 0], DiameterMm = dia };
        static SketchEntity Hexagon(double af) => new() { Kind = "polygon", Center = [0, 0], Sides = 6, InradiusMm = af / 2 };
        async Task Extrude(EntityRef on, SketchEntity[] profile, double depth, string? direction = null, bool cut = false)
        {
            await sk.StartAsync(doc, on, false, ct);
            await sk.AddAsync(doc, profile, ct);
            await fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = depth, Direction = direction, Cut = cut }, ct);
        }
        switch (spec.Kind)
        {
            case HardwareKind.SocketHeadScrew:
                await Extrude(top, [Circle(s.HeadD)], s.H);
                await Extrude(top, [Circle(s.D)], l, "reverse");
                await Extrude(new EntityRef { Type = "face", At = [0, s.H, 0] }, [Hexagon(s.Af)], s.SocketT, cut: true);
                await ThreadAsync(doc, spec, [s.D / 2, -l, 0], ct);
                break;
            case HardwareKind.HexHeadScrew:
                await Extrude(top, [Hexagon(s.Af)], s.H);
                await Extrude(top, [Circle(s.D)], l, "reverse");
                await ThreadAsync(doc, spec, [s.D / 2, -l, 0], ct);
                break;
            case HardwareKind.HexNut:
                await Extrude(top, [Hexagon(s.Af), Circle(s.D)], s.H);
                await ThreadAsync(doc, spec, [s.D / 2, s.H, 0], ct);
                break;
            case HardwareKind.PlainWasher:
                await Extrude(top, [Circle(s.Outer), Circle(s.Inner)], s.H);
                break;
            case HardwareKind.DowelPin:
                await Extrude(top, [Circle(s.D)], l, "reverse");
                break;
            default:
                await Extrude(top, [Circle(s.Outer), Circle(s.D)], s.H);
                break;
        }
    }

    private Task ThreadAsync(string doc, HardwareSpec spec, double[] edgeAt, CancellationToken ct)
    {
        double along = spec.Kind == HardwareKind.HexNut ? spec.Size.H : spec.LengthMm!.Value;
        bool whole = spec.ThreadLengthMm >= along - 1e-9;
        return new AdvancedFeatureService(session).CosmeticThreadAsync(new CosmeticThreadRequest
        {
            Doc = doc, Edge = new EntityRef { Type = "edge", At = edgeAt }, Size = spec.Size.Size,
            DepthMm = whole ? null : spec.ThreadLengthMm, ThroughAll = whole,
        }, ct);
    }

    // ================================================================== asm_fastener

    public async Task<AsmFastenerResult> FastenAsync(AsmFastenerRequest q, CancellationToken ct = default)
    {
        var holes = q.Holes ?? [];
        if (holes.Count is 0 or > MaxHoles) throw SwException.InvalidArgument($"'holes' needs 1..{MaxHoles} hole centres, e.g. [[20,10,15]] (assembly mm).");
        var pts = holes.Select((h, i) => Units.PointM(h, $"holes[{i}]")).ToList();
        var std = HardwareCatalog.Standard(q.Standard ?? "ISO 4762");
        if (!std.IsScrew) throw SwException.InvalidArgument($"asm_fastener fastens screws; {std.Name} is a {std.Title.ToLowerInvariant()}.",
            "Use ISO 4762 or ISO 4017; hardware_part makes the other parts, asm_insert and asm_mate_add place them.");
        var probe = new HardwareSpec(std, HardwareCatalog.SizeOf(std, q.Size), null);
        if (q.LengthMm is null && !q.Nut)
            throw SwException.InvalidArgument("lengthMm is required without a nut.", $"Lengths: {HardwareCatalog.Lengths(std, probe.Size)}.");
        if (q.LengthMm is double given) HardwareCatalog.Resolve(std.Name, q.Size, given); // a length that exists, before anything changes

        // The holes first: nothing is inserted unless every hole is found.
        var fits = await session.RunAsync("asm_fastener", ctx =>
        {
            var d = (IModelDoc2)ctx.Assembly();
            var cylinders = TopoQuery.Find(ctx, d, EntityKind.Face, new EntityFilter { Surface = "cylinder" });
            return pts.Select((p, i) => FindHole(ctx, d, cylinders, p, i)).ToList();
        }, RunOptions.Read(q.Doc), ct);

        var nut = q.Nut ? HardwareCatalog.NutFor(probe) : null;
        var washer = q.Washer ? HardwareCatalog.WasherFor(probe) : null;
        double hw = washer?.Size.H ?? 0, washers = q.Nut ? 2 * hw : hw;
        if (q.Nut && fits.FirstOrDefault(f => !f.Through) is { } blind)
            throw SwException.InvalidArgument($"holes[{fits.IndexOf(blind)}] doesn't go through, so it takes no nut.", "Leave nut out for blind or tapped holes.");
        double length = q.LengthMm ?? HardwareCatalog.ShortestThroughNut(probe, Units.Mm(fits.Max(f => f.DepthM)), washers, nut!.Size.H)
            ?? throw SwException.InvalidArgument($"No {std.Name} {probe.Size.Size} is long enough for a {Units.Mm(fits.Max(f => f.DepthM))} mm grip with its nut.",
                "A bigger size or a stud; or give lengthMm.");
        var screw = HardwareCatalog.Resolve(std.Name, q.Size, length);

        var files = new List<string>();
        async Task<string> File(HardwareSpec s) { var r = await PartAsync(new HardwarePartRequest { Standard = s.Standard.Name, Size = s.Size.Size, LengthMm = s.LengthMm, Folder = q.Folder }, ct); files.Add(r.Path); return r.Path; }
        string screwFile = await File(screw);
        string? washerFile = washer is null ? null : await File(washer);
        string? nutFile = nut is null ? null : await File(nut);

        return await session.RunAsync("asm_fastener", ctx => Fasten(ctx, q, screw, washer, nut, screwFile, washerFile, nutFile, pts, files),
            RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(600, session.Options.DefaultTimeout.TotalSeconds)) }, ct);
    }

    private AsmFastenerResult Fasten(SwContext ctx, AsmFastenerRequest q, HardwareSpec screw, HardwareSpec? washer, HardwareSpec? nut,
        string screwFile, string? washerFile, string? nutFile, List<Vec3> pts, List<string> files)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        string handle = ctx.DocEntry.Handle;
        var svc = new AssemblyService(session);
        var cylinders = TopoQuery.Find(ctx, d, EntityKind.Face, new EntityFilter { Surface = "cylinder" });
        var fits = pts.Select((p, i) => FindHole(ctx, d, cylinders, p, i)).ToList();
        int before = asm.GetComponentCount(true);
        var inserted = new List<string>();
        var results = new List<FastenedHole>();
        var checks = new List<string>();
        int mates = 0;
        double hw = washer?.Size.H ?? 0;

        string Insert(string file, Vec3 origin, Vec3 up)
        {
            var r = svc.InsertAsync(new AsmInsertRequest { Doc = handle, Path = file, OriginMm = Mm(origin), Rotation = RotationTo(up), Fixed = false })
                .GetAwaiter().GetResult();
            inserted.Add(r.Component.Name);
            return r.Component.Name;
        }
        void Mate(string type, string a, string b, string alignment)
        {
            svc.MateAddAsync(new AsmMateRequest { Doc = handle, Type = type, A = new EntityRef { Id = a }, B = new EntityRef { Id = b }, Alignment = alignment })
                .GetAwaiter().GetResult();
            mates++;
        }
        // Places one part: concentric with the hole, its origin-plane face on 'seat' (normal 'up' out of the part).
        string Place(string file, HoleFit fit, Vec3 seat, Vec3 up, double boreR, bool bore, string seatFace)
        {
            string name = Insert(file, seat, up);
            var faces = TopoQuery.Find(ctx, d, EntityKind.Face, new EntityFilter { Component = name });
            Mate("concentric", Cylinder(ctx, d, faces, boreR, bore, name), fit.HoleFace, "closest");
            Mate("coincident", Plane(ctx, d, faces, -up, seat, name), seatFace, "anti_aligned");
            Seated(asm, name, seat, up);
            return name;
        }

        try
        {
            for (int i = 0; i < fits.Count; i++)
            {
                var f = fits[i];
                var up = -f.Into; // out of the material on the head's side
                var others = new List<string>();
                string headSeat = f.EntryFace;
                var screwSeat = f.Seat;
                if (washer is not null)
                {
                    string w = Place(washerFile!, f, f.Seat, up, Units.M(washer.Size.Inner) / 2, true, f.EntryFace);
                    others.Add(w);
                    screwSeat = f.Seat + up * Units.M(hw);
                    headSeat = Plane(ctx, d, TopoQuery.Find(ctx, d, EntityKind.Face, new EntityFilter { Component = w }), up, screwSeat, w);
                }
                string s = Place(screwFile, f, screwSeat, up, Units.M(screw.Size.D) / 2, false, headSeat);
                if (f.RadiusM < Units.M(screw.Size.D) / 2 - 1e-7)
                    checks.Add($"holes[{i}] is Ø{Math.Round(Units.Mm(2 * f.RadiusM), 3)}, smaller than the screw (a tapped hole drawn at its drill size?)");
                double reach = screw.LengthMm!.Value - (washer is null ? 0 : hw);
                double grip = Units.Mm(f.DepthM);
                double? protrusion = null, tip = null;
                if (nut is not null)
                {
                    var exit = f.Seat + f.Into * f.DepthM;
                    string nutFace = f.ExitFace!;
                    var nutSeat = exit;
                    if (washer is not null)
                    {
                        string w2 = Place(washerFile!, f, exit, f.Into, Units.M(washer.Size.Inner) / 2, true, f.ExitFace!);
                        others.Add(w2);
                        nutSeat = exit + f.Into * Units.M(hw);
                        nutFace = Plane(ctx, d, TopoQuery.Find(ctx, d, EntityKind.Face, new EntityFilter { Component = w2 }), f.Into, nutSeat, w2);
                    }
                    others.Add(Place(nutFile!, f, nutSeat, f.Into, Units.M(nut.Size.D) / 2, true, nutFace));
                    protrusion = Math.Round(reach - grip - (washer is null ? 0 : hw) - nut.Size.H, 6);
                    if (protrusion < 2 * screw.Size.Pitch - 1e-9)
                        ctx.Warn($"holes[{i}]: the screw stands {protrusion} mm past the nut, less than two threads ({2 * screw.Size.Pitch} mm).");
                }
                else if (!f.Through)
                {
                    tip = Math.Round(grip - reach, 6);
                    if (tip < 0) ctx.Warn($"holes[{i}]: the screw reaches {-tip} mm past the bottom of the {grip} mm deep hole: use a shorter one.");
                }
                else if (reach > grip + 1e-9) checks.Add($"holes[{i}]: the screw stands {Math.Round(reach - grip, 6)} mm out of the far side");
                results.Add(new FastenedHole
                {
                    AtMm = Mm(f.Seat), Screw = s, Others = others.Count == 0 ? null : others, GripMm = Math.Round(grip, 6), Through = f.Through,
                    ProtrusionMm = protrusion, TipClearanceMm = tip,
                });
            }
        }
        catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
        {
            string removed = Rollback(d, asm, inserted, before);
            var sw = ex as SwException;
            throw new SwException(sw?.Code ?? ErrorCodes.Internal, $"asm_fastener: {ex.Message} {removed}", sw?.Hint, ex);
        }
        int after = asm.GetComponentCount(true);
        if (after != before + inserted.Count)
            throw new SwException(ErrorCodes.VerifyFailed, $"The component count went from {before} to {after}, not {before + inserted.Count}.");
        checks.Insert(0, $"{inserted.Count} component(s) placed, each concentric with its hole and seated (origin within {Units.Mm(Tol) / 10} mm, axis along the hole's)");
        return new AsmFastenerResult
        {
            Doc = ctx.Title(d), Screw = screw.Designation, Files = files.Distinct().ToList(), Holes = results, ComponentCount = after, Mates = mates, Checks = checks,
        };
    }

    // ------------------------------------------------------------------ holes

    /// <summary>A hole found at a centre point: where it starts, which way it goes and how far, with the faces to mate to.</summary>
    private sealed record HoleFit(Vec3 Seat, Vec3 Into, string HoleFace, string EntryFace, double RadiusM, double DepthM, string? ExitFace)
    {
        public bool Through => ExitFace is not null;
    }

    /// <summary>A cylinder through the point with its interval along the axis (from the point; the axis as the face gives it).</summary>
    private sealed record OnAxis(TopoItem It, Vec3 Axis, double T0, double T1);

    private static HoleFit FindHole(SwContext ctx, IModelDoc2 d, List<TopoItem> cylinders, Vec3 p, int index)
    {
        string at = $"holes[{index}] [{string.Join(", ", Mm(p))}]";
        var on = new List<OnAxis>();
        foreach (var it in cylinders)
        {
            var g = it.Face!;
            if (g.Axis is not Vec3 la || g.AxisOrigin is not Vec3 lo) continue;
            var a = it.World(la).Normalized();
            var v = p - it.WorldPoint(lo);
            if ((v - a * v.Dot(a)).Length > Tol || !Topology.IsHole((IFace2)it.Entity, g)) continue;
            var ts = Circles((IFace2)it.Entity).Select(c => (it.WorldPoint(c) - p).Dot(a)).ToList();
            if (ts.Count > 0) on.Add(new OnAxis(it, a, ts.Min(), ts.Max()));
        }
        // Holes that start at the point, each with the way into the material and the flat face around its rim.
        var starts = new List<(OnAxis Face, Vec3 Into, TopoItem Entry)>();
        foreach (var c in on)
        {
            Vec3? into = Math.Abs(c.T0) < Tol && c.T1 > Tol ? c.Axis : Math.Abs(c.T1) < Tol && c.T0 < -Tol ? -c.Axis : null;
            if (into is not Vec3 dir) continue;
            if (RimFace(c.It, p, dir, -dir) is TopoItem entry) starts.Add((c, dir, entry));
        }
        if (starts.Count == 0)
            throw new SwException(ErrorCodes.EntityNotFound, $"No hole starts at {at}.",
                on.Count > 0
                    ? "The point is on a hole's axis but not where it opens onto a flat face: give the centre on the face the head sits on (in a counterbore, its floor)."
                    : "Give each hole's centre on the face the head sits on, assembly mm (face_list lists the cylinders with their axes).");
        if (starts.Any(s => s.Into.Dot(starts[0].Into) < 0))
            throw new SwException(ErrorCodes.EntityAmbiguous, $"Holes open both ways at {at} (it lies between two parts).",
                "Give the centre on the outside face, where the screw head goes.");
        var first = starts[0];
        var into0 = first.Into;
        // The hole's length: the coaxial hole faces, end to end from the point, through every part they pass.
        var spans = on.Where(c => c.Axis.IsParallelTo(into0, 1e-6))
            .Select(c => (c, From: c.Axis.Dot(into0) > 0 ? c.T0 : -c.T1, To: c.Axis.Dot(into0) > 0 ? c.T1 : -c.T0))
            .Where(s => s.To > Tol).OrderBy(s => s.From).ToList();
        double reach = 0;
        OnAxis? last = null;
        foreach (var s in spans)
        {
            if (s.From > reach + Tol) break;
            if (s.To > reach) (reach, last) = (s.To, s.c);
        }
        var exit = last is null ? null : RimFace(last.It, p + into0 * reach, into0, into0);
        return new HoleFit(p, into0, TopoQuery.Id(ctx, d, first.Face.It), TopoQuery.Id(ctx, d, first.Entry), first.Face.It.Face!.Radius ?? 0, reach,
            exit is null ? null : TopoQuery.Id(ctx, d, exit));
    }

    /// <summary>The centres of a face's circular edges (local coordinates).</summary>
    private static IEnumerable<Vec3> Circles(IFace2 f) =>
        f.Edges().Select(Topology.Describe).Where(e => e.Curve == "circle" && e.Center is not null).Select(e => e.Center!.Value);

    /// <summary>The flat face next to a hole face's rim at <paramref name="rim"/> whose outward normal is <paramref name="normal"/>.</summary>
    private static TopoItem? RimFace(TopoItem hole, Vec3 rim, Vec3 axis, Vec3 normal)
    {
        foreach (var e in ((IFace2)hole.Entity).Edges())
        {
            var eg = Topology.Describe(e);
            if (eg.Curve != "circle" || eg.Center is not Vec3 c || Math.Abs((hole.WorldPoint(c) - rim).Dot(axis)) > Tol) continue;
            foreach (var f in e.AdjacentFaces())
            {
                if (ReferenceEquals(f, hole.Entity)) continue;
                var g = Topology.Describe(f);
                if (g.Surface != "plane" || g.Normal is not Vec3 n || hole.World(n).Normalized().Dot(normal) < 1 - 1e-6) continue;
                var local = Topology.InteriorPoint(f);
                return new TopoItem { Entity = f, Kind = EntityKind.Face, Scope = hole.Scope, Face = g, LocalPick = local, Pick = hole.WorldPoint(local), Size = f.GetArea() };
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ placing

    private static string Cylinder(SwContext ctx, IModelDoc2 d, List<TopoItem> faces, double radius, bool hole, string comp)
    {
        var it = faces.FirstOrDefault(f => f.Face!.Surface == "cylinder" && f.Face.Radius is double r && Math.Abs(r - radius) < 1e-7
                                            && Topology.IsHole((IFace2)f.Entity, f.Face) == hole)
                 ?? throw new SwException(ErrorCodes.Internal, $"'{comp}' has no {(hole ? "bore" : "shank")} of radius {Units.Mm(radius)} mm.");
        return TopoQuery.Id(ctx, d, it);
    }

    /// <summary>The flat face of a component through <paramref name="point"/> with outward normal <paramref name="normal"/>.</summary>
    private static string Plane(SwContext ctx, IModelDoc2 d, List<TopoItem> faces, Vec3 normal, Vec3 point, string comp)
    {
        var it = faces.Where(f => f.Face!.Surface == "plane" && f.Face.Normal is Vec3 n && f.World(n).Normalized().Dot(normal) > 1 - 1e-6
                                  && Math.Abs((f.Pick - point).Dot(normal)) < Tol)
                     .OrderByDescending(f => f.Size).FirstOrDefault()
                 ?? throw new SwException(ErrorCodes.Internal, $"'{comp}' has no flat face at [{string.Join(", ", Mm(point))}] facing [{string.Join(", ", Mm(normal * 1000))}].");
        return TopoQuery.Id(ctx, d, it);
    }

    /// <summary>Verifies a placed part: its origin on the seat and its +y along <paramref name="up"/>.</summary>
    private static void Seated(IAssemblyDoc asm, string name, Vec3 seat, Vec3 up)
    {
        var c = EntityResolver.Component(asm, name);
        var xf = c.Transform2 is IMathTransform t ? new Xform(ComArgs.Doubles((object?)t.ArrayData)) : Xform.Identity;
        var origin = xf.ApplyPoint(Vec3.Zero);
        var y = xf.ApplyVector(new Vec3(0, 1, 0)).Normalized();
        if (origin.DistanceTo(seat) > Tol / 10 || y.Dot(up) < 1 - 1e-9)
            throw new SwException(ErrorCodes.VerifyFailed,
                $"'{name}' ended up at [{string.Join(", ", Mm(origin))}] along [{string.Join(", ", Mm(y * 1000))}], not at [{string.Join(", ", Mm(seat))}] along [{string.Join(", ", Mm(up * 1000))}].");
    }

    private static string Rollback(IModelDoc2 d, IAssemblyDoc asm, List<string> names, int before)
    {
        for (int i = names.Count - 1; i >= 0; i--)
        {
            try { AssemblyService.RemoveComponent(d, asm, EntityResolver.Component(asm, names[i]), asm.GetComponentCount(true) - 1); }
            catch (SwException) { /* counted below */ }
        }
        return names.Count == 0 ? "Nothing was inserted."
            : asm.GetComponentCount(true) == before ? $"Rolled back: the {names.Count} part(s) it inserted were removed."
            : "Rollback incomplete: asm_component_list shows what is left.";
    }

    /// <summary>The rotation turning a part's +y onto <paramref name="up"/> (null: none needed).</summary>
    private static AsmRotation? RotationTo(Vec3 up)
    {
        var y = new Vec3(0, 1, 0);
        double c = Math.Clamp(y.Dot(up), -1, 1);
        if (c > 1 - 1e-12) return null;
        if (c < -1 + 1e-12) return new AsmRotation { Axis = [1, 0, 0], AngleDeg = 180 };
        var ax = y.Cross(up).Normalized();
        return new AsmRotation { Axis = [ax.X, ax.Y, ax.Z], AngleDeg = Math.Acos(c) * 180 / Math.PI };
    }

    private static double[] Mm(Vec3 p) => [Math.Round(Units.Mm(p.X), 6) + 0.0, Math.Round(Units.Mm(p.Y), 6) + 0.0, Math.Round(Units.Mm(p.Z), 6) + 0.0];
}
