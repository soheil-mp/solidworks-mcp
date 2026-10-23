using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Services;

// Pure chain ordering for sheetmetal_miter_flange (no SOLIDWORKS types; linked into the offline unit tests).

/// <summary>
/// Straight edges ordered end to end: <see cref="Order"/> lists the input indices along the chain, <see cref="Reversed"/>
/// whether each of them runs end-to-start along it, <see cref="Corners"/> counts the joints where the direction changes.
/// </summary>
internal sealed record ChainPlan(int[] Order, bool[] Reversed, bool Closed, int Corners, Vec3 Start);

internal static class EdgeChain
{
    /// <summary>
    /// Orders straight edges (given by their end points) into one chain. An open chain starts at a free end (an end no
    /// other edge shares): the first edge's when it has one, else the free end of the lowest-numbered edge that has one; a
    /// closed loop starts at the first edge's start. Null when the edges are not one chain: a gap, a branch (three edges
    /// at a point), or an edge given twice.
    /// </summary>
    public static ChainPlan? Order(IReadOnlyList<(Vec3 S, Vec3 E)> edges, double tol)
    {
        int n = edges.Count;
        if (n == 0) return null;
        Vec3 End(int i, bool atEnd) => atEnd ? edges[i].E : edges[i].S;
        int Degree(Vec3 p) => Enumerable.Range(0, n).Count(j => edges[j].S.DistanceTo(p) <= tol || edges[j].E.DistanceTo(p) <= tol);
        for (int i = 0; i < n; i++)
        {
            if (edges[i].S.DistanceTo(edges[i].E) <= tol) return null; // degenerate
            for (int j = 0; j < i; j++)
                if (Same(edges[i], edges[j], tol)) return null; // given twice
            if (Degree(edges[i].S) > 2 || Degree(edges[i].E) > 2) return null; // a branch
        }
        var free = new List<(int Edge, bool AtEnd)>();
        for (int i = 0; i < n; i++)
        {
            if (Degree(edges[i].S) == 1) free.Add((i, false));
            if (Degree(edges[i].E) == 1) free.Add((i, true));
        }
        if (free.Count is not (0 or 2)) return null;
        bool closed = free.Count == 0;
        // Start: the first edge's free end if it has one (its start when both ends are free), else the other free end.
        var (first, startAtEnd) = closed ? (0, false) : free.OrderBy(f => f.Edge).ThenBy(f => f.AtEnd).First();
        var order = new List<int> { first };
        var reversed = new List<bool> { startAtEnd };
        var used = new bool[n];
        used[first] = true;
        Vec3 tail = End(first, !startAtEnd);
        while (order.Count < n)
        {
            int next = -1;
            bool rev = false;
            for (int j = 0; j < n && next < 0; j++)
            {
                if (used[j]) continue;
                if (edges[j].S.DistanceTo(tail) <= tol) (next, rev) = (j, false);
                else if (edges[j].E.DistanceTo(tail) <= tol) (next, rev) = (j, true);
            }
            if (next < 0) return null; // a gap: the rest is not connected
            used[next] = true;
            order.Add(next);
            reversed.Add(rev);
            tail = End(next, !rev);
        }
        Vec3 start = End(first, startAtEnd);
        if (closed && tail.DistanceTo(start) > tol) return null;
        int corners = 0;
        int joints = closed ? n : n - 1;
        for (int k = 0; k < joints; k++)
        {
            Vec3 Dir(int at) => (End(order[at], !reversed[at]) - End(order[at], reversed[at])).Normalized();
            if (!Dir(k).IsParallelTo(Dir((k + 1) % n), 1e-6)) corners++;
        }
        return new ChainPlan([.. order], [.. reversed], closed, corners, start);
    }

    private static bool Same((Vec3 S, Vec3 E) a, (Vec3 S, Vec3 E) b, double tol) =>
        (a.S.DistanceTo(b.S) <= tol && a.E.DistanceTo(b.E) <= tol) || (a.S.DistanceTo(b.E) <= tol && a.E.DistanceTo(b.S) <= tol);
}
