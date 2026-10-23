using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the advanced-feature area (AdvancedFeatureService): the edges of reference curves (a helix, a
/// composite curve). The "Adv" prefix keeps the names clear of interop members (which would shadow an extension) and of
/// other areas' wrappers.
/// </summary>
internal static partial class SwExt
{
    /// <summary>IReferenceCurve.GetSegments: the curve's edges in SOLIDWORKS' order (empty when it reports none).</summary>
    public static List<IEdge> AdvSegments(this IReferenceCurve curve) => ComArgs.Objects<IEdge>((object?)curve.GetSegments());

    /// <summary>IReferenceCurve.GetFirstSegment: the first edge (a helix starts at its start vertex; verified).</summary>
    public static IEdge? AdvFirstSegment(this IReferenceCurve curve) => (object?)curve.GetFirstSegment() as IEdge;

    /// <summary>IReferenceCurve.GetNextSegment after <see cref="AdvFirstSegment"/>, or null after the last one.</summary>
    public static IEdge? AdvNextSegment(this IReferenceCurve curve) => (object?)curve.GetNextSegment() as IEdge;

    /// <summary>
    /// The edges of a reference curve: GetSegments, or the GetFirstSegment / GetNextSegment walk when that returns
    /// nothing (capped, so a curve that never ends the walk can't hang the call).
    /// </summary>
    public static List<IEdge> AdvCurveEdges(this IReferenceCurve curve)
    {
        var list = curve.AdvSegments();
        if (list.Count > 0) return list;
        var e = curve.AdvFirstSegment();
        int guard = 0;
        while (e is not null && guard++ < 10_000)
        {
            list.Add(e);
            e = curve.AdvNextSegment();
        }
        return list;
    }
}
