using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Services;

// Pure math for body_move (no SOLIDWORKS types; linked into the offline unit tests).

/// <summary>A 3×3 rotation matrix (row-major) acting on column vectors.</summary>
internal readonly record struct Rot3(double M00, double M01, double M02, double M10, double M11, double M12, double M20, double M21, double M22)
{
    public static readonly Rot3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public Vec3 Apply(Vec3 v) => new(M00 * v.X + M01 * v.Y + M02 * v.Z, M10 * v.X + M11 * v.Y + M12 * v.Z, M20 * v.X + M21 * v.Y + M22 * v.Z);

    public Rot3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    public static Rot3 operator *(Rot3 a, Rot3 b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

    public static Rot3 RotX(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new(1, 0, 0, 0, c, -s, 0, s, c);
    }

    public static Rot3 RotY(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new(c, 0, s, 0, 1, 0, -s, 0, c);
    }

    public static Rot3 RotZ(double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return new(c, -s, 0, s, c, 0, 0, 0, 1);
    }

    /// <summary>Rotation by <paramref name="angle"/> (radians, right-hand rule) about the direction <paramref name="axis"/>.</summary>
    public static Rot3 AxisAngle(Vec3 axis, double angle)
    {
        var u = axis.Normalized();
        double c = Math.Cos(angle), s = Math.Sin(angle), k = 1 - c;
        return new(
            c + u.X * u.X * k, u.X * u.Y * k - u.Z * s, u.X * u.Z * k + u.Y * s,
            u.Y * u.X * k + u.Z * s, c + u.Y * u.Y * k, u.Y * u.Z * k - u.X * s,
            u.Z * u.X * k - u.Y * s, u.Z * u.Y * k + u.X * s, c + u.Z * u.Z * k);
    }

    /// <summary>The largest element-wise difference.</summary>
    public double MaxDiff(Rot3 o) => new[]
    {
        M00 - o.M00, M01 - o.M01, M02 - o.M02, M10 - o.M10, M11 - o.M11, M12 - o.M12, M20 - o.M20, M21 - o.M21, M22 - o.M22,
    }.Max(Math.Abs);

    public bool IsIdentity(double tol = 1e-12) => MaxDiff(Identity) <= tol;
}

/// <summary>The order in which InsertMoveCopyBody2's three rotation angles are applied (about the fixed model axes).</summary>
internal enum EulerOrder
{
    /// <summary>About x, then y, then z: R = Rz·Ry·Rx.</summary>
    Xyz,

    /// <summary>About z, then y, then x: R = Rx·Ry·Rz.</summary>
    Zyx,
}

/// <summary>
/// One reading of InsertMoveCopyBody2's rotation and translation arguments. The API documents neither the order of the
/// three angles, their sense, nor whether the translation is applied before or after the rotation, so body_move tries
/// the assumed reading and, when the result shows another one, redoes the move with arguments computed for it.
/// </summary>
internal readonly record struct MoveConvention(EulerOrder Order, bool Negated, bool TranslateFirst)
{
    public override string ToString() =>
        $"angles applied {(Order == EulerOrder.Xyz ? "x, y, z" : "z, y, x")}, {(Negated ? "negated" : "right-handed")}, " +
        $"translation {(TranslateFirst ? "before" : "after")} the rotation";
}

/// <summary>InsertMoveCopyBody2 arguments in API units: translation (m), rotation point (m), angles about x, y, z (rad).</summary>
internal sealed record MoveParams(Vec3 Translation, Vec3 Point, Vec3 Angles);

/// <summary>A rigid motion p' = R (p − C) + C + T: a rotation about the point C, then a translation.</summary>
internal sealed record Motion(Rot3 R, Vec3 C, Vec3 T)
{
    public static Motion Translate(Vec3 t) => new(Rot3.Identity, Vec3.Zero, t);

    public Vec3 Apply(Vec3 p) => R.Apply(p - C) + C + T;

    /// <summary>This motion applied <paramref name="k"/> times (k ≥ 0; 0 is the identity).</summary>
    public Motion Power(int k)
    {
        var r = Rot3.Identity;
        var t = Vec3.Zero;
        for (int i = 0; i < k; i++)
        {
            // M^(i+1)(p) = R (M^i(p) − C) + C + T = R^(i+1) (p − C) + C + (R t_i + T)
            t = R.Apply(t) + T;
            r = R * r;
        }
        return new Motion(r, C, t);
    }

    public bool Rotates(double tol = 1e-12) => !R.IsIdentity(tol);
}

/// <summary>Maps the motion body_move asks for to InsertMoveCopyBody2 arguments under each reading, and back.</summary>
internal static class MoveMath
{
    /// <summary>The reading tried first: angles about x, then y, then z, right-handed, rotation before translation.</summary>
    public static readonly MoveConvention Assumed = new(EulerOrder.Xyz, false, false);

    /// <summary>Every reading, the assumed one first.</summary>
    public static readonly IReadOnlyList<MoveConvention> All = Build();

    private static List<MoveConvention> Build()
    {
        var list = new List<MoveConvention> { Assumed };
        foreach (var order in new[] { EulerOrder.Xyz, EulerOrder.Zyx })
            foreach (bool negated in new[] { false, true })
                foreach (bool first in new[] { false, true })
                {
                    var c = new MoveConvention(order, negated, first);
                    if (!list.Contains(c)) list.Add(c);
                }
        return list;
    }

    /// <summary>The rotation for angles <paramref name="a"/> (radians) about x, y and z, applied in <paramref name="order"/>.</summary>
    public static Rot3 Euler(EulerOrder order, Vec3 a) => order == EulerOrder.Xyz
        ? Rot3.RotZ(a.Z) * Rot3.RotY(a.Y) * Rot3.RotX(a.X)
        : Rot3.RotX(a.X) * Rot3.RotY(a.Y) * Rot3.RotZ(a.Z);

    /// <summary>Angles a (radians) with <c>Euler(order, a) == r</c>; in gimbal lock the z angle is 0.</summary>
    public static Vec3 Decompose(Rot3 r, EulerOrder order)
    {
        if (order == EulerOrder.Xyz)
        {
            // Rz(c)·Ry(b)·Rx(a): M20 = −sin b, M21 = sin a cos b, M22 = cos a cos b, M10 = cos b sin c, M00 = cos b cos c.
            double sb = Math.Clamp(-r.M20, -1, 1);
            double b = Math.Asin(sb);
            if (Math.Abs(sb) < 1 - 1e-12) return new Vec3(Math.Atan2(r.M21, r.M22), b, Math.Atan2(r.M10, r.M00));
            return new Vec3(Math.Atan2(-r.M12, r.M11), b, 0); // only a − c is defined: take c = 0
        }
        else
        {
            // Rx(a)·Ry(b)·Rz(c): M02 = sin b, M12 = −sin a cos b, M22 = cos a cos b, M01 = −cos b sin c, M00 = cos b cos c.
            double sb = Math.Clamp(r.M02, -1, 1);
            double b = Math.Asin(sb);
            if (Math.Abs(sb) < 1 - 1e-12) return new Vec3(Math.Atan2(-r.M12, r.M22), b, Math.Atan2(-r.M01, r.M00));
            return new Vec3(Math.Atan2(r.M21, r.M11), b, 0); // only a + c is defined: take c = 0
        }
    }

    /// <summary>The motion SOLIDWORKS performs with <paramref name="p"/> if it reads the arguments as <paramref name="c"/>.</summary>
    public static Motion Applied(MoveParams p, MoveConvention c)
    {
        var r = Euler(c.Order, c.Negated ? -p.Angles : p.Angles);
        // Translation first: R (x + T − P) + P = R (x − P) + P + R T.
        return new Motion(r, p.Point, c.TranslateFirst ? r.Apply(p.Translation) : p.Translation);
    }

    /// <summary>The arguments that make SOLIDWORKS, reading them as <paramref name="c"/>, perform <paramref name="want"/>.</summary>
    public static MoveParams ParamsFor(Motion want, MoveConvention c)
    {
        var a = Decompose(want.R, c.Order);
        if (c.Negated) a = -a;
        return new MoveParams(c.TranslateFirst ? want.R.Transpose().Apply(want.T) : want.T, want.C, a);
    }

    /// <summary>True when every predicted point lies within <paramref name="tol"/> of some actual point.</summary>
    public static bool AllNear(IEnumerable<Vec3> predicted, IReadOnlyList<Vec3> actual, double tol) =>
        predicted.All(p => actual.Any(a => a.DistanceTo(p) <= tol));

    /// <summary>Up to <paramref name="max"/> points, evenly spread over the list sorted by x, y, z (deterministic).</summary>
    public static List<Vec3> Sample(IReadOnlyList<Vec3> points, int max)
    {
        var sorted = points.OrderBy(p => Math.Round(p.X, 9)).ThenBy(p => Math.Round(p.Y, 9)).ThenBy(p => Math.Round(p.Z, 9)).ToList();
        if (sorted.Count <= max) return sorted;
        var list = new List<Vec3>(max);
        for (int i = 0; i < max; i++) list.Add(sorted[(int)((long)i * (sorted.Count - 1) / Math.Max(1, max - 1))]);
        return list;
    }
}
