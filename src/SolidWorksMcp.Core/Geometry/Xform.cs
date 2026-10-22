namespace SolidWorksMcp.Core.Geometry;

/// <summary>
/// A rigid transform in SOLIDWORKS' <c>IMathTransform.ArrayData</c> layout: elements 0–8 are the 3×3 rotation
/// (row-major), 9–11 the translation (m), 12 the scale. SOLIDWORKS applies it to row vectors:
/// <c>p' = s · (p · R) + t</c>, i.e. <c>x' = s(x·a + y·d + z·g) + j</c>. (Checked live in the assembly tests.)
/// Pure math, so listings don't need a COM round trip per point.
/// </summary>
public sealed class Xform
{
    private readonly double[] _r; // 9, row-major
    private readonly Vec3 _t;
    private readonly double _s;

    public Xform(double[] arrayData)
    {
        if (arrayData.Length < 13) throw new ArgumentException("ArrayData needs 16 elements.", nameof(arrayData));
        _r = arrayData[..9];
        _t = new Vec3(arrayData[9], arrayData[10], arrayData[11]);
        _s = arrayData[12] == 0 ? 1 : arrayData[12];
    }

    private Xform(double[] r, Vec3 t, double s)
    {
        _r = r;
        _t = t;
        _s = s;
    }

    public static Xform Identity { get; } = new([1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);

    public Vec3 Translation => _t;

    /// <summary>p · R (row vector times matrix).</summary>
    private Vec3 Rotate(Vec3 p) => new(
        p.X * _r[0] + p.Y * _r[3] + p.Z * _r[6],
        p.X * _r[1] + p.Y * _r[4] + p.Z * _r[7],
        p.X * _r[2] + p.Y * _r[5] + p.Z * _r[8]);

    public Vec3 ApplyPoint(Vec3 p) => Rotate(p) * _s + _t;

    public Vec3 ApplyVector(Vec3 v) => Rotate(v);

    /// <summary>The inverse transform (rotation transposed; R is orthonormal for SOLIDWORKS component transforms).</summary>
    public Xform Inverse()
    {
        double[] rt = [_r[0], _r[3], _r[6], _r[1], _r[4], _r[7], _r[2], _r[5], _r[8]];
        double si = 1 / _s;
        // p = ((p' - t) · Rᵀ) / s  →  p = p' · (Rᵀ/s) - t · (Rᵀ/s)
        var inv = new Xform(rt, Vec3.Zero, si);
        var t = -(inv.Rotate(_t) * si);
        return new Xform(rt, t, si);
    }

    public double[] ToArrayData() => [.. _r, _t.X, _t.Y, _t.Z, _s, 0, 0, 0];

    public bool IsIdentity(double tol = 1e-12) =>
        Math.Abs(_r[0] - 1) < tol && Math.Abs(_r[4] - 1) < tol && Math.Abs(_r[8] - 1) < tol &&
        Math.Abs(_r[1]) < tol && Math.Abs(_r[2]) < tol && Math.Abs(_r[3]) < tol &&
        Math.Abs(_r[5]) < tol && Math.Abs(_r[6]) < tol && Math.Abs(_r[7]) < tol &&
        _t.Length < tol && Math.Abs(_s - 1) < tol;
}
