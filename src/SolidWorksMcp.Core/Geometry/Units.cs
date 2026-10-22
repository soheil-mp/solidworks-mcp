namespace SolidWorksMcp.Core.Geometry;

/// <summary>
/// The single place where tool units (mm, degrees, kg) meet SolidWorks API units (meters, radians, kg).
/// Outputs are rounded so results stay compact and free of float noise (1e-9 mm is far below modeling tolerance).
/// </summary>
public static class Units
{
    public const double MmPerM = 1000.0;

    /// <summary>mm → m.</summary>
    public static double M(double mm) => mm / MmPerM;

    /// <summary>m → mm, rounded to 6 decimals (1 nm).</summary>
    public static double Mm(double m) => Math.Round(m * MmPerM, 6);

    /// <summary>m² → mm², rounded.</summary>
    public static double Mm2(double m2) => Math.Round(m2 * 1e6, 4);

    /// <summary>m³ → mm³, rounded.</summary>
    public static double Mm3(double m3) => Math.Round(m3 * 1e9, 3);

    /// <summary>degrees → radians.</summary>
    public static double Rad(double deg) => deg * Math.PI / 180.0;

    /// <summary>radians → degrees, rounded to 6 decimals.</summary>
    public static double Deg(double rad) => Math.Round(rad * 180.0 / Math.PI, 6);

    /// <summary>kg·m² → kg·mm².</summary>
    public static double KgMm2(double kgm2) => Math.Round(kgm2 * 1e6, 6);

    /// <summary>Rounds a dimensionless or SI value to 9 significant decimals (for unit vectors, ratios, kg).</summary>
    public static double R(double v) => Math.Round(v, 9);

    /// <summary>[x,y,z] in m → mm.</summary>
    public static double[] Mm(double[] m) => m.Select(Mm).ToArray();

    /// <summary>[x,y,z] in mm → m.</summary>
    public static double[] M(double[] mm) => mm.Select(M).ToArray();

    public static double[] Mm(Vec3 v) => [Mm(v.X), Mm(v.Y), Mm(v.Z)];

    public static double[] Unit(Vec3 v) => [R(v.X), R(v.Y), R(v.Z)];

    /// <summary>Validates and converts a user [x,y,z] (mm) to a <see cref="Vec3"/> in meters.</summary>
    public static Vec3 PointM(double[]? mm, string name)
    {
        if (mm is null || mm.Length != 3 || mm.Any(c => !double.IsFinite(c)))
            throw SwException.InvalidArgument($"'{name}' must be [x, y, z] in mm.");
        return new Vec3(M(mm[0]), M(mm[1]), M(mm[2]));
    }

    /// <summary>Validates and converts a user [x,y] (mm) sketch point to meters.</summary>
    public static (double X, double Y) Point2M(double[]? mm, string name)
    {
        if (mm is null || mm.Length != 2 || mm.Any(c => !double.IsFinite(c)))
            throw SwException.InvalidArgument($"'{name}' must be [x, y] in mm (sketch coordinates).");
        return (M(mm[0]), M(mm[1]));
    }

    /// <summary>Validates a positive finite length in mm and returns meters.</summary>
    public static double PositiveM(double mm, string name)
    {
        if (!double.IsFinite(mm) || mm <= 0) throw SwException.InvalidArgument($"'{name}' must be a positive length in mm (got {mm}).");
        return M(mm);
    }
}
