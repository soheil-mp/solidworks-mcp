using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;
using static SolidWorksMcp.Core.Services.AsmPatternMath;

namespace SolidWorksMcp.UnitTests;

/// <summary>Where asm_pattern expects its instances (AsmPatternMath): linear grids, rotations, reflections, matching.</summary>
public class AssemblyPatternMathTests
{
    private const double E = 1e-12;
    private static readonly Placement Seed = new(new Vec3(0.05, 0.03, 0), AsmMath.Identity);

    private static void Near(Vec3 want, Vec3 got, double tol = E) => Assert.True(want.DistanceTo(got) <= tol, $"expected {want}, got {got}");

    [Fact]
    public void Linear_grid_skips_the_seed_and_moves_by_i_and_j_spacings()
    {
        var e = Linear([Seed], Vec3.UnitX, 0.03, 3, -Vec3.UnitZ, 0.025, 2);
        Assert.Equal(5, e.Count);
        Assert.DoesNotContain(e, x => x.I == 0 && x.J == 0);
        Near(new Vec3(0.11, 0.03, -0.025), e.Single(x => x.I == 2 && x.J == 1).P.Origin);
        Assert.All(e, x => Assert.True(AsmMath.IsIdentity(x.P.R)));
        // Two seeds give two full grids, tagged by seed.
        Assert.Equal(4, Linear([Seed, Seed with { Origin = Vec3.Zero }], Vec3.UnitY, 0.01, 3, null, 0, 1).Count);
    }

    [Fact]
    public void Circular_turns_origin_and_axes_about_the_axis_line()
    {
        // A component at (50, 30, 0) mm turned about the y axis through the origin: 90° right-hand takes x to -z.
        var e = Circular([Seed], Vec3.Zero, Vec3.UnitY, Math.PI / 2, 4);
        Assert.Equal(3, e.Count);
        Near(new Vec3(0, 0.03, -0.05), e[0].P.Origin);
        Near(new Vec3(-0.05, 0.03, 0), e[1].P.Origin);
        Near(new Vec3(0, 0.03, 0.05), e[2].P.Origin);
        Near(-Vec3.UnitZ, AsmMath.Axes(e[0].P.R).X);
        // An axis line off the origin: the turn is about that line.
        var off = Circular([new Placement(new Vec3(0.02, 0, 0), AsmMath.Identity)], new Vec3(0.01, 0, 0), Vec3.UnitZ, Math.PI, 2);
        Near(Vec3.Zero, off[0].P.Origin);
    }

    [Fact]
    public void Step_shares_a_full_circle_and_spans_a_partial_one()
    {
        Assert.Equal(Math.PI / 2, Step(2 * Math.PI, 4, true), 12);
        Assert.Equal(Math.PI / 4, Step(Math.PI / 2, 3, true), 12);
        Assert.Equal(Math.PI / 6, Step(Math.PI / 6, 5, false), 12);
    }

    [Fact]
    public void Reflect_mirrors_points_through_an_offset_plane()
    {
        Near(new Vec3(-0.03, 0.01, 0.02), Reflect(new Vec3(0.03, 0.01, 0.02), Vec3.Zero, Vec3.UnitX));
        Near(new Vec3(0.01, 0, 0), Reflect(new Vec3(0.03, 0, 0), new Vec3(0.02, 5, 5), Vec3.UnitX));
        Near(-Vec3.UnitY, ReflectDir(Vec3.UnitY, Vec3.UnitY));
        Near(Vec3.UnitX, ReflectDir(Vec3.UnitX, Vec3.UnitY));
    }

    [Fact]
    public void Match_pairs_one_to_one_within_tolerances()
    {
        var expected = new List<Placement> { new(new Vec3(1, 0, 0), AsmMath.Identity), new(new Vec3(2, 0, 0), AsmMath.Identity) };
        var actual = new List<Placement>
        {
            new(new Vec3(2, 0, 1e-9), AsmMath.Identity),
            new(new Vec3(1, 0, 0), AsmMath.Identity),
            new(new Vec3(1, 0, 0), AsmMath.Identity), // a duplicate gets no partner
            new(new Vec3(2, 0, 0), AsmMath.Rotation(Vec3.UnitZ, 0.1)), // right place, wrong orientation
        };
        Assert.Equal([1, 0, -1, -1], Match(actual, expected, 1e-6, 1e-7));
        Assert.Equal([-1, -1, -1, -1], Match(actual, [], 1e-6, 1e-7));
    }

    [Fact]
    public void Mirror_signs_name_the_flipped_axes_of_a_rigid_copy()
    {
        // About x = 0: a copy turned 180° about y has x → -x (mirrored), y → y (mirrored), z → -z (flipped).
        var turned = AsmMath.Rotation(Vec3.UnitY, Math.PI);
        Assert.Equal([1, 1, -1], MirrorSigns(AsmMath.Identity, turned, Vec3.UnitX, 1e-9)!);
        // Turned 180° about z instead: x → -x (mirrored), y → -y (flipped), z → z (mirrored).
        Assert.Equal([1, -1, 1], MirrorSigns(AsmMath.Identity, AsmMath.Rotation(Vec3.UnitZ, Math.PI), Vec3.UnitX, 1e-9)!);
        // Not a mirror image at all.
        Assert.Null(MirrorSigns(AsmMath.Identity, AsmMath.Rotation(Vec3.UnitY, Math.PI / 2), Vec3.UnitX, 1e-9));
    }
}
