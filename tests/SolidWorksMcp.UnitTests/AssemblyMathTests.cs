using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>The pure placement math of the assembly area (AsmMath): rotations, moves, ArrayData layouts.</summary>
public class AssemblyMathTests
{
    private const double E = 1e-12;

    private static void Near(Vec3 want, Vec3 got, double tol = E)
    {
        Assert.True(want.DistanceTo(got) <= tol, $"expected {want}, got {got}");
    }

    [Fact]
    public void Rotation_follows_the_right_hand_rule()
    {
        var rz = AsmMath.Rotation(Vec3.UnitZ, Math.PI / 2);
        Near(Vec3.UnitY, AsmMath.Apply(rz, Vec3.UnitX));
        Near(-Vec3.UnitX, AsmMath.Apply(rz, Vec3.UnitY));
        Near(Vec3.UnitZ, AsmMath.Apply(rz, Vec3.UnitZ));
        Assert.Equal(Math.PI / 2, AsmMath.AngleOf(rz), 12);

        // 120° about [1,1,1] permutes the axes x → y → z → x (and the axis needn't be a unit vector).
        var r3 = AsmMath.Rotation(new Vec3(2, 2, 2), Units.Rad(120));
        Near(Vec3.UnitY, AsmMath.Apply(r3, Vec3.UnitX));
        Near(Vec3.UnitZ, AsmMath.Apply(r3, Vec3.UnitY));
        Near(Vec3.UnitX, AsmMath.Apply(r3, Vec3.UnitZ));
    }

    [Fact]
    public void Rotations_are_orthonormal_and_the_transpose_inverts_them()
    {
        var r = AsmMath.Rotation(new Vec3(0.3, -1, 2), Units.Rad(37));
        Assert.True(AsmMath.IsIdentity(AsmMath.Multiply(r, AsmMath.Transpose(r)), 1e-12));
        Assert.True(AsmMath.IsIdentity(AsmMath.Rotation(Vec3.UnitX, 0)));
        var (x, y, z) = AsmMath.Axes(r);
        Assert.Equal(1, x.Length, 12);
        Near(z, x.Cross(y)); // right-handed
        Assert.Equal(r, AsmMath.FromAxes(x, y, z));
    }

    [Fact]
    public void Row_layout_maps_like_Xform_and_the_column_layout_is_its_transpose()
    {
        // Xform (Geometry) assumes SOLIDWORKS applies ArrayData to row vectors; RowLayout encodes "assembly = R·part + t" in it.
        var r = AsmMath.Rotation(Vec3.UnitZ, Math.PI / 2);
        var (x, y, z) = AsmMath.Axes(r);
        var t = new Vec3(0.1, 0.2, 0.3);
        var p = new Vec3(0.04, 0.01, 0.02); // the far corner of a 40 x 10 x 20 box
        var rows = new Xform(AsmMath.RowLayout(x, y, z, t));
        Near(t + AsmMath.Apply(r, p), rows.ApplyPoint(p));
        Near(new Vec3(0.09, 0.24, 0.32), rows.ApplyPoint(p));

        // Read with the row convention, the column layout is the transposed rotation (what a column-vector SOLIDWORKS would want).
        var cols = new Xform(AsmMath.ColumnLayout(x, y, z, t));
        Near(t + AsmMath.Apply(AsmMath.Transpose(r), p), cols.ApplyPoint(p));

        // Without rotation both layouts are the VERIFIED one: rotation 0..8, translation 9..11, scale 12.
        Assert.Equal(AsmMath.RowLayout(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, t), AsmMath.ColumnLayout(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, t));
        Assert.Equal(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0.2, 0.3, 1, 0, 0, 0 }, AsmMath.RowLayout(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, t));
    }

    [Fact]
    public void Relative_moves_translate_and_rotate_about_the_origin_or_a_point()
    {
        var r0 = AsmMath.Identity;
        var t0 = new Vec3(1, 0, 0);
        var (r1, t1) = AsmMath.Move(r0, t0, new Vec3(0, 2, 0), null, null, absolute: false);
        Assert.Equal(r0, r1);
        Near(new Vec3(1, 2, 0), t1);

        // About the component origin (default): the origin stays, the axes turn.
        var rz = AsmMath.Rotation(Vec3.UnitZ, Math.PI / 2);
        var (r2, t2) = AsmMath.Move(r0, t0, null, rz, null, absolute: false);
        Near(t0, t2);
        Near(Vec3.UnitY, AsmMath.Axes(r2).X);

        // About the assembly origin: the component origin swings from +x to +y, then the translation is added.
        var (_, t3) = AsmMath.Move(r0, t0, new Vec3(0, 0, 5), rz, Vec3.Zero, absolute: false);
        Near(new Vec3(0, 1, 5), t3);

        // Rotations compose: a second 90° turn gives 180°.
        var (r4, _) = AsmMath.Move(r2, t2, null, rz, null, absolute: false);
        Assert.Equal(Math.PI, AsmMath.AngleOf(r4), 9);
    }

    [Fact]
    public void Absolute_moves_replace_only_what_is_given()
    {
        var r0 = AsmMath.Rotation(Vec3.UnitX, 0.5);
        var t0 = new Vec3(1, 2, 3);
        var (r1, t1) = AsmMath.Move(r0, t0, new Vec3(0, 0, 0), null, null, absolute: true);
        Assert.Equal(r0, r1);
        Near(Vec3.Zero, t1);
        var rz = AsmMath.Rotation(Vec3.UnitZ, 1);
        var (r2, t2) = AsmMath.Move(r0, t0, null, rz, null, absolute: true);
        Assert.Equal(rz, r2);
        Near(t0, t2);
    }

    [Fact]
    public void Relative_placement_is_independent_of_a_common_motion()
    {
        var ra = AsmMath.Rotation(Vec3.UnitY, 0.3);
        var rb = AsmMath.Rotation(Vec3.UnitZ, 1.1);
        var ta = new Vec3(1, 2, 3);
        var tb = new Vec3(-4, 5, 0.5);
        var (rel, trel) = AsmMath.Relative(ra, ta, rb, tb);
        // Move both by the same rigid motion: the relative placement stays.
        var m = AsmMath.Rotation(new Vec3(1, 1, 0), 0.7);
        var d = new Vec3(0.2, -0.1, 9);
        var (ra2, ta2) = AsmMath.Move(ra, ta, d, m, Vec3.Zero, absolute: false);
        var (rb2, tb2) = AsmMath.Move(rb, tb, d, m, Vec3.Zero, absolute: false);
        var (rel2, trel2) = AsmMath.Relative(ra2, ta2, rb2, tb2);
        Assert.True(AsmMath.MaxDiff(rel, rel2) < 1e-12);
        Near(trel, trel2, 1e-12);
        var (same, zero) = AsmMath.Relative(ra, ta, ra, ta);
        Assert.True(AsmMath.IsIdentity(same, 1e-12));
        Near(Vec3.Zero, zero);
    }

    [Fact]
    public void Transform_changes_and_interference_filters()
    {
        double[] a = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0.1, 0, 0, 1, 0, 0, 0];
        double[] b = (double[])a.Clone();
        Assert.False(AsmMath.Changed(a, b));
        b[10] += 1e-6;
        Assert.True(AsmMath.Changed(a, b));
        Assert.True(AsmMath.Changed(a, []));
        Assert.False(AsmMath.Changed([], []));

        Assert.True(AsmMath.Involves(["base-1", "lid-1"], ["LID-1"]));
        Assert.False(AsmMath.Involves(["base-1", "lid-1"], ["pin-1"]));
        Assert.True(AsmMath.Involves(["base-1", "lid-1"], ["base-1", "lid-1", "pin-1"]));
        Assert.False(AsmMath.Involves(["base-1", "pin-2"], ["base-1", "lid-1"]));
    }

    [Fact]
    public void Distances_and_angles()
    {
        Assert.Equal(-2, AsmMath.PointToPlane(new Vec3(0, 1, 0), Vec3.UnitY, new Vec3(5, -1, 7)), 12);
        Assert.Equal(5, AsmMath.PointToLine(Vec3.Zero, Vec3.UnitY, new Vec3(3, 100, 4)), 12);
        Assert.Equal(0, AsmMath.LineAngle(Vec3.UnitY, -Vec3.UnitY), 12);
        Assert.Equal(Math.PI, AsmMath.Angle(Vec3.UnitY, -Vec3.UnitY), 12);
        Assert.Equal(Math.PI / 4, AsmMath.LineAngle(new Vec3(1, 1, 0), Vec3.UnitX), 12);
    }
}

/// <summary>Tier B of asm_mate_add (AsmMateIntent): solved geometry against the mate's intent, in meters.</summary>
public class AssemblyMateIntentTests
{
    private static AsmGeo Plane(Vec3 p, Vec3 n) => new('p', p, n.Normalized(), 0, "plane");
    private static AsmGeo Cyl(Vec3 p, Vec3 d, double r) => new('a', p, d.Normalized(), r, "cylinder");
    private static AsmGeo Line(Vec3 p, Vec3 d) => new('l', p, d.Normalized(), 0, "line");
    private static AsmGeo Point(Vec3 p) => new('v', p, Vec3.Zero, 0, "point");

    private static (List<string> Problems, List<string> Checks) Run(string type, AsmGeo a, AsmGeo b, double d = 0, double ang = 0)
    {
        var checks = new List<string>();
        var problems = AsmMateIntent.Check(type, a, b, d, ang, checks);
        return (problems, checks);
    }

    [Fact]
    public void Coincident_planes_pass_and_report_gaps_and_tilts()
    {
        var (ok, checks) = Run("coincident", Plane(Vec3.Zero, Vec3.UnitY), Plane(new Vec3(0.05, 0, -0.02), -Vec3.UnitY));
        Assert.Empty(ok);
        Assert.Contains("planes coincide (0 mm apart)", checks);
        Assert.True(AsmMateIntent.Judged(checks));

        var (gap, _) = Run("coincident", Plane(Vec3.Zero, Vec3.UnitY), Plane(new Vec3(0, 0.001, 0), Vec3.UnitY));
        Assert.Contains(gap, p => p.Contains("1 mm apart"));

        var (tilt, _) = Run("coincident", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, new Vec3(0, 1, 0.01)));
        Assert.Contains(tilt, p => p.Contains("° apart"));

        var (onPlane, _) = Run("coincident", Point(new Vec3(3, 0, 4)), Plane(Vec3.Zero, Vec3.UnitY));
        Assert.Empty(onPlane);
    }

    [Fact]
    public void Concentric_needs_coinciding_axes()
    {
        var shaft = Cyl(new Vec3(0, 0.007, 0), -Vec3.UnitY, 0.005);
        var (ok, checks) = Run("concentric", Cyl(Vec3.Zero, Vec3.UnitY, 0.005), shaft);
        Assert.Empty(ok);
        Assert.Contains("axes coincide", checks);

        var (off, _) = Run("concentric", Cyl(Vec3.Zero, Vec3.UnitY, 0.005), Cyl(new Vec3(0.001, 0, 0), Vec3.UnitY, 0.005));
        Assert.Contains(off, p => p.Contains("parallel but 1 mm apart"));

        // Two planes can't be judged: reported as not checked, never as a failure.
        var (none, notChecked) = Run("concentric", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, Vec3.UnitX));
        Assert.Empty(none);
        Assert.False(AsmMateIntent.Judged(notChecked));
    }

    [Fact]
    public void Distance_between_planes_is_measured()
    {
        var a = Plane(Vec3.Zero, Vec3.UnitX);
        var (ok, checks) = Run("distance", a, Plane(new Vec3(-0.01, 0.3, 0.2), -Vec3.UnitX), d: 0.01);
        Assert.Empty(ok);
        Assert.Contains("planes 10 mm apart", checks);
        var (bad, _) = Run("distance", a, Plane(new Vec3(0.009, 0, 0), Vec3.UnitX), d: 0.01);
        Assert.Contains(bad, p => p.Contains("9 mm apart, expected 10 mm"));
        var (pts, _) = Run("distance", Point(Vec3.Zero), Point(new Vec3(0.003, 0.004, 0)), d: 0.005);
        Assert.Empty(pts);
    }

    [Fact]
    public void Parallel_perpendicular_and_angle()
    {
        Assert.Empty(Run("parallel", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.UnitX, -Vec3.UnitY)).Problems);
        Assert.NotEmpty(Run("parallel", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, Vec3.UnitX)).Problems);
        Assert.Empty(Run("perpendicular", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, Vec3.UnitX)).Problems);
        // A line is parallel to a plane when it is perpendicular to the plane's normal.
        Assert.Empty(Run("parallel", Plane(Vec3.Zero, Vec3.UnitY), Line(Vec3.Zero, Vec3.UnitZ)).Problems);
        Assert.NotEmpty(Run("parallel", Plane(Vec3.Zero, Vec3.UnitY), Line(Vec3.Zero, Vec3.UnitY)).Problems);

        var tilted = new Vec3(0, Math.Cos(Units.Rad(30)), Math.Sin(Units.Rad(30)));
        Assert.Empty(Run("angle", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, tilted), ang: Units.Rad(30)).Problems);
        Assert.Empty(Run("angle", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, -tilted), ang: Units.Rad(30)).Problems); // either sense
        Assert.Contains(Run("angle", Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, tilted), ang: Units.Rad(45)).Problems,
            p => p.Contains("expected 45°"));
    }

    [Fact]
    public void Tangent_cylinder_touches_the_plane()
    {
        var floor = Plane(Vec3.Zero, Vec3.UnitY);
        Assert.Empty(Run("tangent", floor, Cyl(new Vec3(0, 0.005, 0), Vec3.UnitX, 0.005)).Problems);
        Assert.Contains(Run("tangent", floor, Cyl(new Vec3(0, 0.006, 0), Vec3.UnitX, 0.005)).Problems, p => p.Contains("expected 5 mm"));
        Assert.Empty(Run("tangent", Cyl(Vec3.Zero, Vec3.UnitZ, 0.01), Cyl(new Vec3(0.015, 0, 0), Vec3.UnitZ, 0.005)).Problems);
    }

    [Fact]
    public void Limit_mates_hold_the_distance_or_angle_between_their_limits()
    {
        var a = Plane(new Vec3(-0.02, 0, 0), Vec3.UnitX);
        var b = Plane(new Vec3(0.0, 0.3, 0), -Vec3.UnitX); // 20 mm from a
        var checks = new List<string>();
        Assert.Equal(0.02, AsmMateIntent.Measure("distance", a, b)!.Value, 12);
        Assert.Empty(AsmMateIntent.Range("distance", a, b, 0.01, 0.04, checks));
        Assert.Contains(checks, c => c.StartsWith("distance 20 mm, within the limits 10..40 mm", StringComparison.Ordinal));
        Assert.Contains(AsmMateIntent.Range("distance", a, b, 0.025, 0.04, []), p => p.Contains("outside the limits"));
        // Angles count either sense: 150° between planes is 30° the other way.
        var tilted = Plane(Vec3.Zero, new Vec3(Math.Cos(Units.Rad(150)), Math.Sin(Units.Rad(150)), 0));
        Assert.Empty(AsmMateIntent.Range("angle", a, tilted, Units.Rad(20), Units.Rad(40), []));
        // Planes that aren't parallel have no distance: not checked, not failed.
        var skew = new List<string>();
        Assert.Empty(AsmMateIntent.Range("distance", a, tilted, 0, 1, skew));
        Assert.Contains(skew, c => c.Contains("not checked", StringComparison.Ordinal));
    }

    [Fact]
    public void Symmetric_mate_needs_mirror_images_in_the_plane()
    {
        var mirror = Plane(Vec3.Zero, Vec3.UnitX);
        var checks = new List<string>();
        Assert.Empty(AsmMateIntent.Symmetric(Plane(new Vec3(-0.02, 0, 0), Vec3.UnitX), Plane(new Vec3(0.02, 0.1, 0), -Vec3.UnitX), mirror, checks));
        Assert.Contains("the planes are mirror images in the symmetry plane", checks);
        Assert.Contains(AsmMateIntent.Symmetric(Plane(new Vec3(-0.02, 0, 0), Vec3.UnitX), Plane(new Vec3(0.03, 0, 0), Vec3.UnitX), mirror, []),
            p => p.Contains("mm off the second", StringComparison.Ordinal));
        Assert.Empty(AsmMateIntent.Symmetric(Cyl(new Vec3(-0.05, 0, 0), Vec3.UnitY, 0.005), Cyl(new Vec3(0.05, 0.2, 0), -Vec3.UnitY, 0.005), mirror, []));
        Assert.NotEmpty(AsmMateIntent.Symmetric(Point(new Vec3(-0.01, 0, 0)), Point(new Vec3(0.011, 0, 0)), mirror, []));
    }

    [Fact]
    public void Width_mate_centres_the_tab_between_the_width_faces()
    {
        var w1 = Plane(Vec3.Zero, -Vec3.UnitX);
        var w2 = Plane(new Vec3(0.04, 0, 0), Vec3.UnitX);
        var checks = new List<string>();
        Assert.Empty(AsmMateIntent.Width(w1, w2, Plane(new Vec3(0.01, 0, 0), -Vec3.UnitX), Plane(new Vec3(0.03, 1, 1), Vec3.UnitX), checks));
        Assert.Contains("the tab is centred between the width faces", checks);
        Assert.Contains(AsmMateIntent.Width(w1, w2, Plane(new Vec3(0.012, 0, 0), -Vec3.UnitX), Plane(new Vec3(0.03, 0, 0), Vec3.UnitX), []),
            p => p.Contains("mid-plane", StringComparison.Ordinal));
        Assert.NotEmpty(AsmMateIntent.Width(w1, w2, Plane(Vec3.Zero, Vec3.UnitY), Plane(Vec3.Zero, Vec3.UnitY), []));
    }
}
