using System.Globalization;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using Xunit;

namespace SolidWorksMcp.UnitTests;

public class UnitsTests
{
    [Fact]
    public void Converts_mm_m_and_rounds()
    {
        Assert.Equal(0.025, Units.M(25));
        Assert.Equal(25, Units.Mm(0.025));
        Assert.Equal(120000, Units.Mm3(1.2e-4));
        Assert.Equal(6000, Units.Mm2(0.006));
        Assert.Equal(90, Units.Deg(Math.PI / 2));
        Assert.Equal(Math.PI, Units.Rad(180), 12);
        Assert.Equal(1.0, Units.Mm(0.0010000000004)); // float noise rounded away
    }

    [Fact]
    public void Point_validation_names_the_argument()
    {
        var ex = Assert.Throws<SwException>(() => Units.PointM([1, 2], "at"));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("'at'", ex.Message);
        Assert.Throws<SwException>(() => Units.PointM([1, double.NaN, 3], "at"));
        Assert.Throws<SwException>(() => Units.PositiveM(0, "depthMm"));
        Assert.Equal(new Vec3(0.001, 0.002, 0.003), Units.PointM([1, 2, 3], "at"));
    }

    [Fact]
    public void Formatting_is_culture_invariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = Core.Model.ResultJson.Serialize(new Core.Model.ModelState { VolumeMm3 = 1.5, Bodies = 1 });
            Assert.Contains("1.5", json);
            Assert.DoesNotContain("1,5", json);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}

public class Vec3Tests
{
    [Fact]
    public void Vector_algebra()
    {
        var a = new Vec3(1, 0, 0);
        var b = new Vec3(0, 1, 0);
        Assert.Equal(Vec3.UnitZ, a.Cross(b));
        Assert.Equal(0, a.Dot(b));
        Assert.True(new Vec3(0, 0, 2).IsParallelTo(-Vec3.UnitZ));
        Assert.False(new Vec3(0, 1, 2).IsParallelTo(Vec3.UnitZ, 0.01));
        Assert.Equal(5, new Vec3(3, 4, 0).Length, 12);
    }
}

public class XformTests
{
    // SOLIDWORKS ArrayData: rotation 0..8 row-major, translation 9..11, scale 12; row vectors: p' = p·R + t.
    private static Xform RotZ90(double tx = 0, double ty = 0, double tz = 0) =>
        new([0, 1, 0, -1, 0, 0, 0, 0, 1, tx, ty, tz, 1, 0, 0, 0]);

    [Fact]
    public void Applies_row_vector_convention()
    {
        var x = RotZ90();
        var p = x.ApplyPoint(new Vec3(1, 0, 0));
        Assert.Equal(0, p.X, 12);
        Assert.Equal(1, p.Y, 12);
        Assert.Equal(new Vec3(-1, 0, 0), Round(x.ApplyVector(Vec3.UnitY)));
    }

    [Fact]
    public void Inverse_round_trips()
    {
        var x = RotZ90(0.01, -0.02, 0.03);
        var inv = x.Inverse();
        var p = new Vec3(0.1, 0.2, 0.3);
        var back = inv.ApplyPoint(x.ApplyPoint(p));
        Assert.True(back.DistanceTo(p) < 1e-12);
        Assert.True(Xform.Identity.IsIdentity());
    }

    [Fact]
    public void Top_plane_sketch_maps_x_y_to_x_0_minus_y()
    {
        // Model→sketch for Top Plane: sketch x = model x, sketch y = −model z. Its inverse maps (x, y) to (x, 0, −y).
        var sketchToModel = new Xform([1, 0, 0, 0, 0, -1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0]);
        var m = sketchToModel.ApplyPoint(new Vec3(0.02, 0.03, 0));
        Assert.Equal(new Vec3(0.02, 0, -0.03), Round(m));
    }

    private static Vec3 Round(Vec3 v) => new(Math.Round(v.X, 12), Math.Round(v.Y, 12), Math.Round(v.Z, 12));
}

public class AxisSpecTests
{
    [Theory]
    [InlineData("+y", 0, 1, 0)]
    [InlineData("y", 0, 1, 0)]
    [InlineData("-x", -1, 0, 0)]
    [InlineData("z-", 0, 0, -1)]
    [InlineData("0,0,2", 0, 0, 1)]
    [InlineData("[1, 1, 0]", 0.7071067811865475, 0.7071067811865475, 0)]
    public void Parses_axes(string spec, double x, double y, double z)
    {
        var v = AxisSpec.Parse(spec, "a");
        Assert.Equal(x, v.X, 9);
        Assert.Equal(y, v.Y, 9);
        Assert.Equal(z, v.Z, 9);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("0,0,0")]
    [InlineData("1,2")]
    public void Rejects_bad_axes(string spec) =>
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => AxisSpec.Parse(spec, "a")).Code);

    [Fact]
    public void Extreme_gives_axis_and_sign()
    {
        var (axis, sign) = AxisSpec.Extreme("-z");
        Assert.Equal(Vec3.UnitZ, axis);
        Assert.Equal(-1, sign);
    }
}
