using System.Globalization;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>body_move's rotation readings (Euler orders, sign, translation order) and rigid-motion algebra.</summary>
public class BodyMotionTests
{
    private static readonly Vec3[] Points = [new(0.01, 0.02, -0.03), new(-0.05, 0.004, 0.06), new(0.1, -0.2, 0.3), Vec3.Zero];

    private static void AssertSame(Rot3 a, Rot3 b, double tol = 1e-12) => Assert.True(a.MaxDiff(b) < tol, $"{a} != {b}");

    private static void AssertSame(Motion a, Motion b, double tol = 1e-12)
    {
        foreach (var p in Points) Assert.True(a.Apply(p).DistanceTo(b.Apply(p)) < tol, $"{a.Apply(p)} != {b.Apply(p)}");
    }

    [Fact]
    public void Axis_angle_follows_the_right_hand_rule()
    {
        var r = Rot3.AxisAngle(Vec3.UnitZ, Math.PI / 2);
        Assert.True(r.Apply(Vec3.UnitX).DistanceTo(Vec3.UnitY) < 1e-12);
        AssertSame(Rot3.RotZ(Math.PI / 2), r);
        AssertSame(Rot3.RotX(0.7), Rot3.AxisAngle(Vec3.UnitX, 0.7));
        AssertSame(Rot3.RotY(-1.1), Rot3.AxisAngle(new Vec3(0, 2, 0), -1.1)); // the axis is normalized
        AssertSame(Rot3.Identity, r * r.Transpose());
    }

    [Theory]
    [InlineData(0.3, -0.4, 1.2)]
    [InlineData(-2.5, 0.1, 3.0)]
    [InlineData(0, 0, Math.PI / 2)]
    [InlineData(0.2, Math.PI / 2, 0.5)]  // gimbal lock of the x-y-z order
    [InlineData(0.2, -Math.PI / 2, -0.5)]
    public void Euler_angles_round_trip_in_both_orders(double ax, double ay, double az)
    {
        foreach (var order in new[] { EulerOrder.Xyz, EulerOrder.Zyx })
        {
            var r = MoveMath.Euler(order, new Vec3(ax, ay, az));
            var back = MoveMath.Euler(order, MoveMath.Decompose(r, order));
            AssertSame(r, back, 1e-9);
        }
    }

    [Fact]
    public void An_arbitrary_axis_decomposes_into_angles_that_rebuild_it()
    {
        var r = Rot3.AxisAngle(new Vec3(1, 2, 3), 1.234);
        foreach (var order in new[] { EulerOrder.Xyz, EulerOrder.Zyx })
            AssertSame(r, MoveMath.Euler(order, MoveMath.Decompose(r, order)), 1e-9);
    }

    [Fact]
    public void A_principal_axis_rotation_needs_only_its_own_angle()
    {
        var want = new Motion(Rot3.AxisAngle(Vec3.UnitZ, Math.PI / 2), new Vec3(0.01, 0, 0), Vec3.Zero);
        var p = MoveMath.ParamsFor(want, MoveMath.Assumed);
        Assert.Equal(0, p.Angles.X, 12);
        Assert.Equal(0, p.Angles.Y, 12);
        Assert.Equal(Math.PI / 2, p.Angles.Z, 12);
        Assert.Equal(new Vec3(0.01, 0, 0), p.Point);
    }

    [Fact]
    public void Arguments_for_each_reading_reproduce_the_wanted_motion()
    {
        var want = new Motion(Rot3.AxisAngle(new Vec3(-1, 0.5, 2), 0.8), new Vec3(0.01, -0.02, 0.005), new Vec3(0.03, 0.0, -0.04));
        Assert.Equal(8, MoveMath.All.Count);
        Assert.Equal(MoveMath.Assumed, MoveMath.All[0]);
        foreach (var c in MoveMath.All)
            AssertSame(want, MoveMath.Applied(MoveMath.ParamsFor(want, c), c), 1e-9);
    }

    [Fact]
    public void Readings_differ_so_a_result_tells_which_one_SOLIDWORKS_used()
    {
        var want = new Motion(Rot3.AxisAngle(new Vec3(0, 0, 1), 0.5), Vec3.Zero, new Vec3(0.02, 0, 0));
        var p = MoveMath.ParamsFor(want, MoveMath.Assumed);
        var negated = MoveMath.Applied(p, MoveMath.Assumed with { Negated = true });
        var translateFirst = MoveMath.Applied(p, MoveMath.Assumed with { TranslateFirst = true });
        var probe = new Vec3(0.01, 0.01, 0);
        Assert.True(want.Apply(probe).DistanceTo(negated.Apply(probe)) > 1e-4);
        Assert.True(want.Apply(probe).DistanceTo(translateFirst.Apply(probe)) > 1e-4);
    }

    [Fact]
    public void Power_applies_the_motion_repeatedly()
    {
        var m = new Motion(Rot3.AxisAngle(Vec3.UnitY, 0.3), new Vec3(0.01, 0, 0.02), new Vec3(0, 0.005, 0));
        var p = new Vec3(0.04, 0.01, -0.02);
        Assert.True(m.Power(0).Apply(p).DistanceTo(p) < 1e-15);
        Assert.True(m.Power(3).Apply(p).DistanceTo(m.Apply(m.Apply(m.Apply(p)))) < 1e-12);
        var t = Motion.Translate(new Vec3(0.01, 0, 0));
        Assert.True(t.Power(4).Apply(Vec3.Zero).DistanceTo(new Vec3(0.04, 0, 0)) < 1e-15);
        Assert.False(t.Rotates());
        Assert.True(m.Rotates());
    }

    [Fact]
    public void Sampling_is_deterministic_and_bounded()
    {
        var pts = Enumerable.Range(0, 100).Select(i => new Vec3(i % 7, i, -i)).ToList();
        var a = MoveMath.Sample(pts, 10);
        var b = MoveMath.Sample(pts.AsEnumerable().Reverse().ToList(), 10);
        Assert.Equal(10, a.Count);
        Assert.Equal(a, b);
        Assert.Equal(3, MoveMath.Sample(pts.Take(3).ToList(), 10).Count);
        Assert.True(MoveMath.AllNear(a, pts, 1e-9));
        Assert.False(MoveMath.AllNear([new Vec3(0.5, 0.5, 0.5)], pts, 1e-3));
    }
}

/// <summary>The DXF summary sheetmetal_export_flat checks the exported flat pattern against.</summary>
public class DxfReaderTests
{
    private static string Dxf(params string[] entities) =>
        "  0\nSECTION\n  2\nHEADER\n  9\n$ACADVER\n  1\nAC1015\n  9\n$INSUNITS\n 70\n     4\n  0\nENDSEC\n" +
        "  0\nSECTION\n  2\nENTITIES\n" + string.Concat(entities) + "  0\nENDSEC\n  0\nEOF\n";

    private static string Line(double x1, double y1, double x2, double y2, string layer = "0") => string.Create(CultureInfo.InvariantCulture,
        $"  0\nLINE\n  8\n{layer}\n 10\n{x1}\n 20\n{y1}\n 30\n0.0\n 11\n{x2}\n 21\n{y2}\n 31\n0.0\n");

    [Fact]
    public void A_rectangle_of_lines_gives_its_extents_units_and_counts()
    {
        var s = DxfReader.Parse(Dxf(Line(0, 0, 50, 0), Line(50, 0, 50, 30), Line(50, 30, 0, 30), Line(0, 30, 0, 0)));
        Assert.Equal("AC1015", s.Version);
        Assert.Equal(4, s.InsUnits);
        Assert.Equal(4, s.Entities["LINE"]);
        Assert.Equal(["0"], s.Layers);
        Assert.Equal([0, 0, 50, 30], s.Extents!);
        Assert.Equal([50, 30], s.Size!);
        Assert.Equal(0, s.BendLayerEntities);
    }

    [Fact]
    public void Arcs_count_only_the_quadrants_they_pass()
    {
        Assert.Equal([0, 90], DxfReader.ArcExtremeAngles(0, 90));
        Assert.Contains(360.0, DxfReader.ArcExtremeAngles(350, 10)); // passes 0°
        string arc = "  0\nARC\n  8\n0\n 10\n0\n 20\n0\n 40\n10\n 50\n0\n 51\n90\n";
        var s = DxfReader.Parse(Dxf(arc));
        Assert.Equal(0, s.Extents![0], 9);
        Assert.Equal(0, s.Extents[1], 9);
        Assert.Equal(10, s.Extents[2], 9);
        Assert.Equal(10, s.Extents[3], 9);
        string wrap = "  0\nARC\n  8\n0\n 10\n0\n 20\n0\n 40\n10\n 50\n350\n 51\n10\n";
        Assert.Equal(10, DxfReader.Parse(Dxf(wrap)).Extents![2], 9);
    }

    [Fact]
    public void Circles_polylines_and_bend_layers()
    {
        string circle = "  0\nCIRCLE\n  8\nGEOM\n 10\n5\n 20\n5\n 40\n2\n";
        string poly = "  0\nLWPOLYLINE\n  8\nGEOM\n 90\n3\n 10\n-1\n 20\n0\n 10\n4\n 20\n7\n 10\n2\n 20\n-3\n";
        var s = DxfReader.Parse(Dxf(circle, poly, Line(0, 1, 9, 1, "BENDLINES")));
        Assert.Equal(1, s.Entities["CIRCLE"]);
        Assert.Equal(1, s.Entities["LWPOLYLINE"]);
        Assert.Equal(["GEOM", "BENDLINES"], s.Layers);
        Assert.Equal(1, s.BendLayerEntities);
        Assert.Equal([-1, -3, 9, 7], s.Extents!);
    }

    [Fact]
    public void Text_that_is_not_a_DXF_has_no_geometry()
    {
        var s = DxfReader.Parse("hello\nworld\n");
        Assert.Empty(s.Entities);
        Assert.Null(s.Extents);
        Assert.Null(s.Size);
        Assert.Null(s.Version);
    }

    /// <summary>A DXF whose geometry is a block (base point <paramref name="baseX"/>, 0) inserted once.</summary>
    private static string WithBlock(string insert, double baseX = 0) =>
        "  0\nSECTION\n  2\nHEADER\n  9\n$ACADVER\n  1\nAC1015\n  0\nENDSEC\n" +
        "  0\nSECTION\n  2\nBLOCKS\n" +
        string.Create(CultureInfo.InvariantCulture, $"  0\nBLOCK\n  8\n0\n  2\nFLAT\n 70\n0\n 10\n{baseX}\n 20\n0.0\n 30\n0.0\n  3\nFLAT\n") +
        Line(0, 0, 50, 0) + Line(50, 0, 50, 30) + Line(50, 30, 0, 30) + Line(0, 30, 0, 0) + Line(0, 10, 50, 10, "BEND_LINES") +
        "  0\nENDBLK\n  8\n0\n  0\nENDSEC\n" +
        "  0\nSECTION\n  2\nENTITIES\n" + insert + "  0\nENDSEC\n  0\nEOF\n";

    [Fact]
    public void An_inserted_block_counts_with_its_insertion_point()
    {
        var s = DxfReader.Parse(WithBlock("  0\nINSERT\n  8\n0\n  2\nFLAT\n 10\n100\n 20\n5\n 30\n0\n"));
        Assert.Equal(1, s.Entities["INSERT"]);
        Assert.False(s.Entities.ContainsKey("LINE")); // the lines live in the block
        Assert.Equal(5, s.InsertedEntities);
        Assert.Equal(1, s.BendLayerEntities);
        Assert.Contains("BEND_LINES", s.Layers);
        Assert.Equal([100, 5, 150, 35], s.Extents!);
        Assert.Equal([50, 30], s.Size!);
    }

    [Fact]
    public void An_inserted_block_is_rotated_scaled_and_moved_by_its_base_point()
    {
        var rotated = DxfReader.Parse(WithBlock("  0\nINSERT\n  8\n0\n  2\nFLAT\n 10\n0\n 20\n0\n 50\n90\n"));
        Assert.Equal(30, rotated.Size![0], 9); // width and height swap
        Assert.Equal(50, rotated.Size[1], 9);
        var scaled = DxfReader.Parse(WithBlock("  0\nINSERT\n  8\n0\n  2\nFLAT\n 10\n0\n 20\n0\n 41\n2\n 42\n2\n"));
        Assert.Equal([100, 60], scaled.Size!);
        var based = DxfReader.Parse(WithBlock("  0\nINSERT\n  8\n0\n  2\nFLAT\n 10\n0\n 20\n0\n", baseX: 25));
        Assert.Equal([-25, 0, 25, 30], based.Extents!);
        var unknown = DxfReader.Parse(WithBlock("  0\nINSERT\n  8\n0\n  2\nOTHER\n 10\n0\n 20\n0\n"));
        Assert.Null(unknown.Extents);
        Assert.Equal(0, unknown.InsertedEntities);
    }
}
