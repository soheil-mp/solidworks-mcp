namespace SolidWorksMcp.Core.Geometry;

/// <summary>Immutable 3D vector/point in API units (meters).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => a * s;
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double DistanceTo(Vec3 b) => (this - b).Length;

    public Vec3 Normalized()
    {
        double l = Length;
        return l < 1e-15 ? this : this / l;
    }

    /// <summary>True when the directions are parallel or anti-parallel within <paramref name="maxAngleRad"/>.</summary>
    public bool IsParallelTo(Vec3 b, double maxAngleRad = 1e-6) =>
        Math.Abs(Normalized().Dot(b.Normalized())) >= Math.Cos(maxAngleRad) - 1e-15;

    public double[] ToArray() => [X, Y, Z];

    /// <summary>Reads three doubles starting at <paramref name="offset"/> from a COM variant array.</summary>
    public static Vec3 From(object? array, int offset = 0)
    {
        var a = Com.ComArgs.Doubles(array);
        if (a.Length < offset + 3) throw new SwException(ErrorCodes.Internal, $"Expected at least {offset + 3} doubles, got {a.Length}.");
        return new Vec3(a[offset], a[offset + 1], a[offset + 2]);
    }

    public static Vec3 From(double[] a, int offset = 0) => new(a[offset], a[offset + 1], a[offset + 2]);

    public override string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
}
