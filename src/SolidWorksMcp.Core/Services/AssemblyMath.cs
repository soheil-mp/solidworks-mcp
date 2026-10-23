using System.Globalization;
using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Services;

// Pure parts of the assembly area (no SolidWorks types): linked into the offline unit tests.

/// <summary>
/// Rotation matrices and placement math in plain doubles: 3×3 row-major matrices acting on column vectors (v' = R·v),
/// so R's columns are the images of the x, y and z axes. Nothing here touches COM.
/// </summary>
internal static class AsmMath
{
    public static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>Right-hand rotation by <paramref name="angleRad"/> about <paramref name="axis"/> (Rodrigues).</summary>
    public static double[] Rotation(Vec3 axis, double angleRad)
    {
        var u = axis.Normalized();
        double c = Math.Cos(angleRad), s = Math.Sin(angleRad), k = 1 - c;
        return
        [
            c + u.X * u.X * k, u.X * u.Y * k - u.Z * s, u.X * u.Z * k + u.Y * s,
            u.Y * u.X * k + u.Z * s, c + u.Y * u.Y * k, u.Y * u.Z * k - u.X * s,
            u.Z * u.X * k - u.Y * s, u.Z * u.Y * k + u.X * s, c + u.Z * u.Z * k,
        ];
    }

    public static Vec3 Apply(double[] r, Vec3 v) =>
        new(r[0] * v.X + r[1] * v.Y + r[2] * v.Z, r[3] * v.X + r[4] * v.Y + r[5] * v.Z, r[6] * v.X + r[7] * v.Y + r[8] * v.Z);

    public static double[] Multiply(double[] a, double[] b)
    {
        var m = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[3 * i + j] = a[3 * i] * b[j] + a[3 * i + 1] * b[3 + j] + a[3 * i + 2] * b[6 + j];
        return m;
    }

    public static double[] Transpose(double[] r) => [r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]];

    /// <summary>The matrix whose columns are <paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>.</summary>
    public static double[] FromAxes(Vec3 x, Vec3 y, Vec3 z) => [x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z];

    /// <summary>The columns of <paramref name="r"/>: the images of the x, y and z axes.</summary>
    public static (Vec3 X, Vec3 Y, Vec3 Z) Axes(double[] r) => (new(r[0], r[3], r[6]), new(r[1], r[4], r[7]), new(r[2], r[5], r[8]));

    /// <summary>The rotation angle of <paramref name="r"/> (0..π).</summary>
    public static double AngleOf(double[] r) => Math.Acos(Math.Clamp((r[0] + r[4] + r[8] - 1) / 2, -1, 1));

    public static double MaxDiff(double[] a, double[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).DefaultIfEmpty(0).Max();

    /// <summary>Rows of <paramref name="r"/>, rounded for output.</summary>
    public static double[][] Rows(double[] r) =>
        [[Units.R(r[0]), Units.R(r[1]), Units.R(r[2])], [Units.R(r[3]), Units.R(r[4]), Units.R(r[5])], [Units.R(r[6]), Units.R(r[7]), Units.R(r[8])]];

    public static bool IsIdentity(double[] r, double tol = 1e-12) => MaxDiff(r, Identity) <= tol;

    /// <summary>
    /// The 16-element ArrayData of a placement if SOLIDWORKS applies transforms to row vectors (p' = p·M + t, the layout
    /// Geometry/Xform assumes): M's rows are the images of the axes, then the translation and scale 1. With no rotation
    /// this is the VERIFIED layout (rotation 0..8, translation 9..11, scale 12); placements are always read back.
    /// </summary>
    public static double[] RowLayout(Vec3 x, Vec3 y, Vec3 z, Vec3 t) =>
        [x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z, t.X, t.Y, t.Z, 1, 0, 0, 0];

    /// <summary>The same placement if SOLIDWORKS applied ArrayData to column vectors (p' = M·p + t): the transpose of <see cref="RowLayout"/>.</summary>
    public static double[] ColumnLayout(Vec3 x, Vec3 y, Vec3 z, Vec3 t) =>
        [x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z, t.X, t.Y, t.Z, 1, 0, 0, 0];

    /// <summary>
    /// The placement after a move. Relative: rotate about <paramref name="center"/> (default: the origin
    /// <paramref name="t0"/>), then translate. Absolute: the rotation (about the assembly axes) and translation are the new
    /// orientation and origin; what is not given stays.
    /// </summary>
    public static (double[] R, Vec3 T) Move(double[] r0, Vec3 t0, Vec3? translate, double[]? rotation, Vec3? center, bool absolute)
    {
        if (absolute) return (rotation ?? r0, translate ?? t0);
        var rr = rotation ?? Identity;
        var c = center ?? t0;
        return (Multiply(rr, r0), Apply(rr, t0 - c) + c + (translate ?? Vec3.Zero));
    }

    /// <summary>B's placement seen from A: (Raᵀ·Rb, Raᵀ·(tb − ta)).</summary>
    public static (double[] R, Vec3 T) Relative(double[] ra, Vec3 ta, double[] rb, Vec3 tb)
    {
        var rt = Transpose(ra);
        return (Multiply(rt, rb), Apply(rt, tb - ta));
    }

    /// <summary>True when two ArrayData differ in rotation, translation or scale (elements 0..12).</summary>
    public static bool Changed(double[] a, double[] b)
    {
        if (a.Length < 13 || b.Length < 13) return a.Length != b.Length;
        for (int i = 0; i < 13; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-9) return true;
        return false;
    }

    /// <summary>asm_interference's filter: with one name, anything that involves it; with more, interferences among them.</summary>
    public static bool Involves(IReadOnlyList<string> components, IReadOnlyList<string> among)
    {
        int hits = components.Count(n => among.Contains(n, StringComparer.OrdinalIgnoreCase));
        return among.Count == 1 ? hits >= 1 : hits >= 2;
    }

    /// <summary>Signed distance of <paramref name="q"/> from the plane through <paramref name="p"/> with unit normal <paramref name="n"/>.</summary>
    public static double PointToPlane(Vec3 p, Vec3 n, Vec3 q) => n.Dot(q - p);

    /// <summary>Distance of <paramref name="q"/> from the line through <paramref name="p"/> along the unit vector <paramref name="d"/>.</summary>
    public static double PointToLine(Vec3 p, Vec3 d, Vec3 q)
    {
        var v = q - p;
        return (v - d * v.Dot(d)).Length;
    }

    /// <summary>The angle between two directions, 0..π.</summary>
    public static double Angle(Vec3 a, Vec3 b) => Math.Acos(Math.Clamp(a.Normalized().Dot(b.Normalized()), -1, 1));

    /// <summary>The angle between two undirected lines or normals, 0..π/2.</summary>
    public static double LineAngle(Vec3 a, Vec3 b) => Math.Acos(Math.Clamp(Math.Abs(a.Normalized().Dot(b.Normalized())), 0, 1));
}

/// <summary>
/// The geometry of one mate entity: 'p' plane (point P, unit normal D), 'l' straight line or axis (P, D), 'a' the axis of
/// a round face or edge (P on the axis, D, radius R), 's' sphere (center P, R), 'v' point (P), 'o' anything else.
/// </summary>
internal readonly record struct AsmGeo(char Kind, Vec3 P, Vec3 D, double R, string What)
{
    public static AsmGeo Other(string what) => new('o', Vec3.Zero, Vec3.Zero, 0, what);
}

/// <summary>
/// Tier B of asm_mate_add: does the solved geometry (assembly space) satisfy the mate? Returns problems (empty = ok)
/// and appends passed checks. Combinations it can't judge are reported as not checked, never as failures.
/// </summary>
internal static class AsmMateIntent
{
    public const double PosTol = 1e-6; // m
    public const double AngTol = 1e-6; // rad

    private static bool IsPlane(AsmGeo g) => g.Kind == 'p';
    private static bool IsLine(AsmGeo g) => g.Kind == 'l';
    private static bool IsAxis(AsmGeo g) => g.Kind is 'l' or 'a';
    private static bool IsPoint(AsmGeo g) => g.Kind == 'v';
    private static bool IsSphere(AsmGeo g) => g.Kind == 's';
    private static bool IsCylinder(AsmGeo g) => g.Kind == 'a' && g.What == "cylinder";
    private static bool HasDir(AsmGeo g) => g.Kind is 'p' or 'l' or 'a';

    private static bool Pick(AsmGeo a, AsmGeo b, Func<AsmGeo, bool> first, Func<AsmGeo, bool> second, out AsmGeo x, out AsmGeo y)
    {
        if (first(a) && second(b)) { x = a; y = b; return true; }
        if (first(b) && second(a)) { x = b; y = a; return true; }
        x = y = default;
        return false;
    }

    private static string Mm(double m) => Units.Mm(m).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Deg(double rad) => Units.Deg(rad).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>True when <paramref name="checks"/> holds a real geometric check (not only "… not checked …").</summary>
    public static bool Judged(IEnumerable<string> checks) => checks.Any(c => !c.Contains("not checked", StringComparison.Ordinal));

    public static List<string> Check(string type, AsmGeo a, AsmGeo b, double distance, double angle, List<string> checks)
    {
        var problems = new List<string>();
        string pair = $"{a.What}/{b.What}";
        void NotChecked(string why = "") => checks.Add($"{type} geometry not checked for {pair}{why}");
        AsmGeo x, y;
        switch (type)
        {
            case "coincident":
                if (Pick(a, b, IsPlane, IsPlane, out x, out y))
                {
                    double ang = AsmMath.LineAngle(x.D, y.D);
                    double dist = Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P));
                    if (ang > AngTol) problems.Add($"the planes are {Deg(ang)}° apart");
                    else if (dist > PosTol) problems.Add($"the planes are parallel but {Mm(dist)} mm apart");
                    else checks.Add("planes coincide (0 mm apart)");
                }
                else if (Pick(a, b, IsPlane, IsPoint, out x, out y)) Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), 0, "the point is", "off the plane", "point lies on the plane");
                else if (Pick(a, b, IsPoint, IsPoint, out x, out y)) Within(problems, checks, x.P.DistanceTo(y.P), 0, "the points are", "apart", "points coincide");
                else if (Pick(a, b, IsPlane, IsLine, out x, out y))
                {
                    double tilt = Math.Abs(x.D.Dot(y.D));
                    if (tilt > AngTol) problems.Add($"the line is not parallel to the plane ({Deg(Math.Asin(Math.Min(1, tilt)))}°)");
                    else Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), 0, "the line is", "off the plane", "line lies in the plane");
                }
                else if (Pick(a, b, IsLine, IsLine, out x, out y)) Coaxial(problems, checks, x, y, "lines");
                else if (Pick(a, b, IsPoint, IsLine, out x, out y)) Within(problems, checks, AsmMath.PointToLine(y.P, y.D, x.P), 0, "the point is", "off the line", "point lies on the line");
                else NotChecked();
                break;

            case "concentric":
                if (Pick(a, b, IsAxis, IsAxis, out x, out y)) Coaxial(problems, checks, x, y, "axes");
                else if (Pick(a, b, IsSphere, IsSphere, out x, out y)) Within(problems, checks, x.P.DistanceTo(y.P), 0, "the centers are", "apart", "sphere centers coincide");
                else if (Pick(a, b, IsSphere, IsAxis, out x, out y)) Within(problems, checks, AsmMath.PointToLine(y.P, y.D, x.P), 0, "the sphere center is", "off the axis", "sphere center lies on the axis");
                else if (Pick(a, b, IsPoint, IsAxis, out x, out y)) Within(problems, checks, AsmMath.PointToLine(y.P, y.D, x.P), 0, "the point is", "off the axis", "point lies on the axis");
                else NotChecked();
                break;

            case "parallel":
            case "perpendicular":
            {
                if (!HasDir(a) || !HasDir(b)) { NotChecked(); break; }
                // Planes compare normals; a plane and a line are parallel when the line is perpendicular to the normal.
                bool mixed = IsPlane(a) != IsPlane(b);
                double ang = AsmMath.LineAngle(a.D, b.D); // 0..90°
                double want = (type == "parallel") != mixed ? 0 : Math.PI / 2;
                string what = mixed ? "line and plane" : IsPlane(a) ? "planes" : "directions";
                if (Math.Abs(ang - want) > AngTol)
                    problems.Add($"the {what} are {Deg(mixed ? Math.PI / 2 - ang : ang)}° apart, expected {(type == "parallel" ? 0 : 90)}°");
                else checks.Add($"{what} {type}");
                break;
            }

            case "tangent":
                if (Pick(a, b, IsPlane, IsCylinder, out x, out y))
                {
                    double tilt = Math.Abs(x.D.Dot(y.D));
                    if (tilt > AngTol) problems.Add($"the cylinder axis is not parallel to the plane ({Deg(Math.Asin(Math.Min(1, tilt)))}°)");
                    else Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), y.R, "the cylinder axis is", "from the plane", $"cylinder touches the plane (axis {Mm(y.R)} mm away)");
                }
                else if (Pick(a, b, IsPlane, IsSphere, out x, out y))
                    Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), y.R, "the sphere center is", "from the plane", "sphere touches the plane");
                else if (Pick(a, b, IsCylinder, IsCylinder, out x, out y))
                {
                    double ang = AsmMath.LineAngle(x.D, y.D);
                    if (ang > AngTol) { NotChecked(" (axes not parallel)"); break; }
                    double dist = AsmMath.PointToLine(x.P, x.D, y.P);
                    if (Math.Abs(dist - (x.R + y.R)) <= PosTol || Math.Abs(dist - Math.Abs(x.R - y.R)) <= PosTol)
                        checks.Add($"cylinders touch (axes {Mm(dist)} mm apart)");
                    else problems.Add($"the cylinder axes are {Mm(dist)} mm apart, expected {Mm(x.R + y.R)} or {Mm(Math.Abs(x.R - y.R))} mm");
                }
                else if (Pick(a, b, IsSphere, IsSphere, out x, out y))
                {
                    double dist = x.P.DistanceTo(y.P);
                    if (Math.Abs(dist - (x.R + y.R)) <= PosTol || Math.Abs(dist - Math.Abs(x.R - y.R)) <= PosTol) checks.Add("spheres touch");
                    else problems.Add($"the sphere centers are {Mm(dist)} mm apart, expected {Mm(x.R + y.R)} or {Mm(Math.Abs(x.R - y.R))} mm");
                }
                else NotChecked();
                break;

            case "distance":
                if (Pick(a, b, IsPlane, IsPlane, out x, out y))
                {
                    double ang = AsmMath.LineAngle(x.D, y.D);
                    if (ang > AngTol) problems.Add($"the planes are {Deg(ang)}° apart, so no distance applies");
                    else Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), distance, "the planes are", "apart", $"planes {Mm(distance)} mm apart");
                }
                else if (Pick(a, b, IsPlane, IsPoint, out x, out y))
                    Within(problems, checks, Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)), distance, "the point is", "from the plane", $"point {Mm(distance)} mm from the plane");
                else if (Pick(a, b, IsPoint, IsPoint, out x, out y))
                    Within(problems, checks, x.P.DistanceTo(y.P), distance, "the points are", "apart", $"points {Mm(distance)} mm apart");
                else if (Pick(a, b, IsLine, IsLine, out x, out y) && AsmMath.LineAngle(x.D, y.D) <= AngTol)
                    Within(problems, checks, AsmMath.PointToLine(x.P, x.D, y.P), distance, "the lines are", "apart", $"lines {Mm(distance)} mm apart");
                else if (Pick(a, b, IsPoint, IsLine, out x, out y))
                    Within(problems, checks, AsmMath.PointToLine(y.P, y.D, x.P), distance, "the point is", "from the line", $"point {Mm(distance)} mm from the line");
                else NotChecked(" (round entities are measured from their center or their surface)");
                break;

            case "angle":
                if (Pick(a, b, IsPlane, IsPlane, out x, out y) || Pick(a, b, IsAxis, IsAxis, out x, out y))
                {
                    double got = AsmMath.Angle(x.D, y.D); // 0..180°, either sense accepted
                    if (Math.Abs(got - angle) <= AngTol || Math.Abs(got - (Math.PI - angle)) <= AngTol) checks.Add($"{(IsPlane(x) ? "planes" : "axes")} at {Deg(angle)}°");
                    else problems.Add($"the {(IsPlane(x) ? "planes" : "axes")} are at {Deg(got)}°, expected {Deg(angle)}°");
                }
                else NotChecked();
                break;

            default:
                NotChecked();
                break;
        }
        return problems;
    }

    /// <summary>The distance or angle between two entities as a distance or angle mate measures it, when that is clear.</summary>
    public static double? Measure(string type, AsmGeo a, AsmGeo b)
    {
        AsmGeo x, y;
        if (type == "angle")
            return Pick(a, b, IsPlane, IsPlane, out x, out y) || Pick(a, b, IsAxis, IsAxis, out x, out y) ? AsmMath.Angle(x.D, y.D) : null;
        if (Pick(a, b, IsPlane, IsPlane, out x, out y))
            return AsmMath.LineAngle(x.D, y.D) <= AngTol ? Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P)) : null;
        if (Pick(a, b, IsPlane, IsPoint, out x, out y)) return Math.Abs(AsmMath.PointToPlane(x.P, x.D, y.P));
        if (Pick(a, b, IsPoint, IsPoint, out x, out y)) return x.P.DistanceTo(y.P);
        if (Pick(a, b, IsLine, IsLine, out x, out y)) return AsmMath.LineAngle(x.D, y.D) <= AngTol ? AsmMath.PointToLine(x.P, x.D, y.P) : null;
        if (Pick(a, b, IsPoint, IsLine, out x, out y)) return AsmMath.PointToLine(y.P, y.D, x.P);
        return null;
    }

    /// <summary>A limit mate: the distance or angle it holds now lies between its limits (either sense of an angle).</summary>
    public static List<string> Range(string type, AsmGeo a, AsmGeo b, double min, double max, List<string> checks)
    {
        var problems = new List<string>();
        bool angular = type == "angle";
        if (Measure(type, a, b) is not double got)
        {
            checks.Add($"limit {type} not checked for {a.What}/{b.What}");
            return problems;
        }
        double tol = angular ? AngTol : PosTol;
        bool inside = got >= min - tol && got <= max + tol || angular && Math.PI - got >= min - tol && Math.PI - got <= max + tol;
        string show = angular ? $"{Deg(got)}°" : $"{Mm(got)} mm";
        string range = angular ? $"{Deg(min)}..{Deg(max)}°" : $"{Mm(min)}..{Mm(max)} mm";
        if (inside) checks.Add($"{type} {show}, within the limits {range}");
        else problems.Add($"the {type} is {show}, outside the limits {range}");
        return problems;
    }

    /// <summary>Symmetric: b is the mirror image of a in the symmetry plane (planes, axes and points).</summary>
    public static List<string> Symmetric(AsmGeo a, AsmGeo b, AsmGeo plane, List<string> checks)
    {
        var problems = new List<string>();
        if (!IsPlane(plane)) { checks.Add("symmetry not checked (the symmetry entity is not a plane)"); return problems; }
        var n = plane.D.Normalized();
        Vec3 Mirror(Vec3 p) => p - n * (2 * (p - plane.P).Dot(n));
        Vec3 MirrorDir(Vec3 v) => v - n * (2 * v.Dot(n));
        if (a.Kind != b.Kind) { checks.Add($"symmetry not checked for {a.What}/{b.What}"); return problems; }
        switch (a.Kind)
        {
            case 'p':
            {
                var mp = Mirror(a.P);
                var md = MirrorDir(a.D);
                double ang = AsmMath.LineAngle(md, b.D), off = Math.Abs(AsmMath.PointToPlane(b.P, b.D.Normalized(), mp));
                if (ang > AngTol || off > PosTol) problems.Add($"the mirror image of the first plane is {Deg(ang)}° and {Mm(off)} mm off the second");
                else checks.Add("the planes are mirror images in the symmetry plane");
                break;
            }
            case 'l' or 'a':
            {
                var mp = Mirror(a.P);
                var md = MirrorDir(a.D);
                double ang = AsmMath.LineAngle(md, b.D), off = AsmMath.PointToLine(b.P, b.D.Normalized(), mp);
                if (ang > AngTol || off > PosTol) problems.Add($"the mirror image of the first axis is {Deg(ang)}° and {Mm(off)} mm off the second");
                else checks.Add("the axes are mirror images in the symmetry plane");
                break;
            }
            case 'v' or 's':
            {
                double off = Mirror(a.P).DistanceTo(b.P);
                if (off > PosTol) problems.Add($"the mirror image of the first point is {Mm(off)} mm from the second");
                else checks.Add("the points are mirror images in the symmetry plane");
                break;
            }
            default:
                checks.Add($"symmetry not checked for {a.What}/{b.What}");
                break;
        }
        return problems;
    }

    /// <summary>Width (centred): the tab faces c, d sit centred between the width faces a, b (all parallel planes).</summary>
    public static List<string> Width(AsmGeo a, AsmGeo b, AsmGeo c, AsmGeo d, List<string> checks)
    {
        var problems = new List<string>();
        if (!(IsPlane(a) && IsPlane(b) && IsPlane(c) && IsPlane(d)))
        {
            checks.Add("width not checked (all four entities must be planar)");
            return problems;
        }
        var n = a.D.Normalized();
        foreach (var (g, name) in new[] { (b, "second width face"), (c, "first tab face"), (d, "second tab face") })
            if (AsmMath.LineAngle(n, g.D) > AngTol) { problems.Add($"the {name} is {Deg(AsmMath.LineAngle(n, g.D))}° off the first width face"); return problems; }
        double mid1 = (n.Dot(a.P) + n.Dot(b.P)) / 2, mid2 = (n.Dot(c.P) + n.Dot(d.P)) / 2;
        if (Math.Abs(mid1 - mid2) > PosTol) problems.Add($"the tab's mid-plane is {Mm(Math.Abs(mid1 - mid2))} mm from the width's");
        else checks.Add("the tab is centred between the width faces");
        return problems;
    }

    private static void Coaxial(List<string> problems, List<string> checks, AsmGeo x, AsmGeo y, string what)
    {
        double ang = AsmMath.LineAngle(x.D, y.D);
        if (ang > AngTol)
        {
            problems.Add($"the {what} are {Deg(ang)}° apart");
            return;
        }
        double dist = AsmMath.PointToLine(x.P, x.D, y.P);
        if (dist > PosTol) problems.Add($"the {what} are parallel but {Mm(dist)} mm apart");
        else checks.Add($"{what} coincide");
    }

    private static void Within(List<string> problems, List<string> checks, double got, double want, string subject, string relation, string ok)
    {
        if (Math.Abs(got - want) <= PosTol) checks.Add(ok);
        else problems.Add($"{subject} {Mm(got)} mm {relation}{(want > 0 ? $", expected {Mm(want)} mm" : "")}");
    }
}
