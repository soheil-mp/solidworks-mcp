using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Services;

/// <summary>
/// Where the instances of a component pattern or mirror should be (pure; offline-tested). A placement is a component's
/// origin and rotation: assembly = R·part + origin, R row-major with the part axes as columns (AsmMath.FromAxes).
/// </summary>
internal static class AsmPatternMath
{
    internal readonly record struct Placement(Vec3 Origin, double[] R);

    /// <summary>One expected instance: which seed, its grid index (i, j) or rotation step (i), and its placement.</summary>
    internal readonly record struct Expected(int Seed, int I, int J, Placement P);

    /// <summary>Linear grid: every (i, j) but the seed's (0, 0); the seed moves by i·s1 along u1 and j·s2 along u2.</summary>
    public static List<Expected> Linear(IReadOnlyList<Placement> seeds, Vec3 u1, double s1, int n1, Vec3? u2, double s2, int n2)
    {
        var list = new List<Expected>();
        for (int k = 0; k < seeds.Count; k++)
            for (int j = 0; j < n2; j++)
                for (int i = 0; i < n1; i++)
                    if (i != 0 || j != 0)
                        list.Add(new Expected(k, i, j, seeds[k] with { Origin = seeds[k].Origin + u1 * (i * s1) + (u2 ?? Vec3.Zero) * (j * s2) }));
        return list;
    }

    /// <summary>Circular: instance i (1 … n−1) is the seed turned by i·step (right hand about u) about the line through p.</summary>
    public static List<Expected> Circular(IReadOnlyList<Placement> seeds, Vec3 p, Vec3 u, double step, int n)
    {
        var list = new List<Expected>();
        for (int k = 0; k < seeds.Count; k++)
            for (int i = 1; i < n; i++)
            {
                var r = AsmMath.Rotation(u, i * step);
                list.Add(new Expected(k, i, 0, new Placement(p + AsmMath.Apply(r, seeds[k].Origin - p), AsmMath.Multiply(r, seeds[k].R))));
            }
        return list;
    }

    /// <summary>The angle between instances: a full circle shared out (360/n), else the span over n−1 gaps; or the angle itself.</summary>
    public static double Step(double totalRad, int n, bool equalSpacing) =>
        !equalSpacing ? totalRad : totalRad >= 2 * Math.PI - 1e-9 ? 2 * Math.PI / n : totalRad / (n - 1);

    public static Vec3 Reflect(Vec3 x, Vec3 q0, Vec3 m) => x - m * (2 * (x - q0).Dot(m));

    public static Vec3 ReflectDir(Vec3 v, Vec3 m) => v - m * (2 * v.Dot(m));

    /// <summary>
    /// Matches actual placements to expected ones one to one (nearest first): a pair counts when the origins are within
    /// <paramref name="posTol"/> and the rotations within <paramref name="rotTol"/> per element. Returns, per actual
    /// placement, the index of its expected one or −1.
    /// </summary>
    public static int[] Match(IReadOnlyList<Placement> actual, IReadOnlyList<Placement> expected, double posTol, double rotTol)
    {
        var pairs = new List<(double D, int A, int E)>();
        for (int a = 0; a < actual.Count; a++)
            for (int e = 0; e < expected.Count; e++)
            {
                double d = actual[a].Origin.DistanceTo(expected[e].Origin);
                if (d <= posTol && AsmMath.MaxDiff(actual[a].R, expected[e].R) <= rotTol) pairs.Add((d, a, e));
            }
        var assign = Enumerable.Repeat(-1, actual.Count).ToArray();
        var used = new bool[expected.Count];
        foreach (var (_, a, e) in pairs.OrderBy(p => p.D))
        {
            if (assign[a] >= 0 || used[e]) continue;
            assign[a] = e;
            used[e] = true;
        }
        return assign;
    }

    /// <summary>
    /// The signs s with instance axis = s·(mirror of the seed axis), per axis x, y, z; null when an instance axis is not
    /// the mirror image of a seed axis (up to sign). A rigid copy can't mirror all three, so one or three come out −1.
    /// </summary>
    public static int[]? MirrorSigns(double[] seedR, double[] instR, Vec3 m, double tol)
    {
        var (sx, sy, sz) = AsmMath.Axes(seedR);
        var (ix, iy, iz) = AsmMath.Axes(instR);
        var signs = new int[3];
        var pairs = new[] { (sx, ix), (sy, iy), (sz, iz) };
        for (int k = 0; k < 3; k++)
        {
            var want = ReflectDir(pairs[k].Item1, m);
            if (pairs[k].Item2.DistanceTo(want) <= tol) signs[k] = 1;
            else if (pairs[k].Item2.DistanceTo(-want) <= tol) signs[k] = -1;
            else return null;
        }
        return signs;
    }
}
