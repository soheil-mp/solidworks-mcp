using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>How sheetmetal_miter_flange orders its edges into a chain and finds where the profile goes.</summary>
public class EdgeChainTests
{
    private const double Tol = 1e-6;

    private static (Vec3, Vec3) E(double x1, double y1, double z1, double x2, double y2, double z2) => (new(x1, y1, z1), new(x2, y2, z2));

    [Fact]
    public void One_edge_starts_at_its_start()
    {
        var c = EdgeChain.Order([E(-25, 0, 15, 25, 0, 15)], Tol)!;
        Assert.Equal([0], c.Order);
        Assert.Equal([false], c.Reversed);
        Assert.False(c.Closed);
        Assert.Equal(0, c.Corners);
        Assert.Equal(new Vec3(-25, 0, 15), c.Start);
    }

    [Fact]
    public void A_corner_chain_starts_at_the_first_edges_free_end_whatever_the_edge_directions()
    {
        // The first edge's free end is its start.
        var a = EdgeChain.Order([E(25, 0, -15, 25, 0, 15), E(25, 0, 15, -25, 0, 15)], Tol)!;
        Assert.Equal([0, 1], a.Order);
        Assert.Equal([false, false], a.Reversed);
        Assert.Equal(1, a.Corners);
        Assert.Equal(new Vec3(25, 0, -15), a.Start);
        // The first edge's free end is its end: both edges run backwards along the chain.
        var b = EdgeChain.Order([E(25, 0, 15, -25, 0, 15), E(25, 0, -15, 25, 0, 15)], Tol)!;
        Assert.Equal([0, 1], b.Order);
        Assert.Equal([true, true], b.Reversed);
        Assert.Equal(new Vec3(-25, 0, 15), b.Start);
        // The first edge is in the middle: the chain starts at the lowest-numbered edge with a free end.
        var mid = EdgeChain.Order([E(-25, 0, 15, 25, 0, 15), E(25, 0, 15, 25, 0, -15), E(-25, 0, -15, -25, 0, 15)], Tol)!;
        Assert.Equal([1, 0, 2], mid.Order);
        Assert.Equal([true, true, true], mid.Reversed);
        Assert.Equal(new Vec3(25, 0, -15), mid.Start);
        Assert.Equal(2, mid.Corners);
        Assert.False(mid.Closed);
    }

    [Fact]
    public void A_closed_loop_starts_at_the_first_edge_and_counts_every_corner()
    {
        var c = EdgeChain.Order([E(-25, 0, 15, 25, 0, 15), E(25, 0, -15, 25, 0, 15), E(25, 0, -15, -25, 0, -15), E(-25, 0, -15, -25, 0, 15)], Tol)!;
        Assert.True(c.Closed);
        Assert.Equal(4, c.Corners);
        Assert.Equal([0, 1, 2, 3], c.Order);
        Assert.Equal([false, true, false, false], c.Reversed);
        Assert.Equal(new Vec3(-25, 0, 15), c.Start);
    }

    [Fact]
    public void Collinear_pieces_make_no_corner()
    {
        var c = EdgeChain.Order([E(0, 0, 0, 10, 0, 0), E(20, 0, 0, 10, 0, 0)], Tol)!;
        Assert.Equal(0, c.Corners);
        Assert.Equal([false, true], c.Reversed);
    }

    [Fact]
    public void Gaps_branches_repeats_and_points_are_not_chains()
    {
        Assert.Null(EdgeChain.Order([E(0, 0, 0, 10, 0, 0), E(11, 0, 0, 20, 0, 0)], Tol)); // a gap
        Assert.Null(EdgeChain.Order([E(0, 0, 0, 10, 0, 0), E(10, 0, 0, 10, 0, 10), E(10, 0, 0, 20, 0, 0)], Tol)); // a branch
        Assert.Null(EdgeChain.Order([E(0, 0, 0, 10, 0, 0), E(10, 0, 0, 0, 0, 0)], Tol)); // the same edge twice
        Assert.Null(EdgeChain.Order([E(1, 1, 1, 1, 1, 1)], Tol)); // no length
        Assert.Null(EdgeChain.Order([], Tol));
    }
}
