using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the reference-geometry and measurement members that return VARIANTs (see <see cref="SwExt"/>).
/// Names are prefixed by area (Ref…, Mass…, Fault…) so they can't collide with other areas' wrappers.
/// </summary>
internal static partial class SwExt
{
    // ---- reference geometry (ReferenceService)

    /// <summary>IRefPlane.Transform as ArrayData: rotation rows 0..8 (row 3 = the normal), translation 9..11 (m); empty if none.</summary>
    public static double[] RefPlaneXform(this IRefPlane plane) => ComArgs.Doubles((object?)plane.Transform?.ArrayData);

    /// <summary>
    /// IRefPlane.CornerPoints (m): the corners of the displayed plane. The in-process twin returns MathPoints, so both
    /// an array of MathPoint objects and a flat array of doubles are accepted.
    /// </summary>
    public static List<Vec3> RefPlaneCorners(this IRefPlane plane)
    {
        object? v = plane.CornerPoints;
        var points = ComArgs.Objects<IMathPoint>(v);
        if (points.Count > 0) return points.Select(p => Vec3.From((object?)p.ArrayData)).ToList();
        var d = ComArgs.Doubles(v);
        var list = new List<Vec3>();
        for (int i = 0; i + 2 < d.Length; i += 3) list.Add(new Vec3(d[i], d[i + 1], d[i + 2]));
        return list;
    }

    /// <summary>IRefAxis.GetRefAxisParams: [start xyz, end xyz] (m).</summary>
    public static double[] RefAxisEnds(this IRefAxis axis) => ComArgs.Doubles((object?)axis.GetRefAxisParams());

    /// <summary>IRefPoint.GetRefPoint as [x, y, z] (m).</summary>
    public static double[] RefPointXyz(this IRefPoint point) => ComArgs.Doubles((object?)point.GetRefPoint()?.ArrayData);

    /// <summary>IModelDocExtension.GetCoordinateSystemTransformByName as ArrayData (empty if the name is unknown).</summary>
    public static double[] RefCoordSysXform(this IModelDoc2 doc, string name) =>
        ComArgs.Doubles((object?)doc.Extension.GetCoordinateSystemTransformByName(name)?.ArrayData);

    // ---- mass properties (MeasureService)

    /// <summary>IMassProperty2.CenterOfMass (m).</summary>
    public static double[] MassCenter(this IMassProperty2 mp) => ComArgs.Doubles((object?)mp.CenterOfMass);

    /// <summary>IMassProperty2.PrincipalMomentsOfInertia (kg·m², at the center of mass).</summary>
    public static double[] MassPrincipalMoments(this IMassProperty2 mp) => ComArgs.Doubles((object?)mp.PrincipalMomentsOfInertia);

    /// <summary>IMassProperty2.PrincipalAxesOfInertia(i), i = 0..2 (unit vector).</summary>
    public static double[] MassPrincipalAxis(this IMassProperty2 mp, int i) => ComArgs.Doubles((object?)mp.get_PrincipalAxesOfInertia(i));

    /// <summary>IMassProperty2.GetMomentOfInertia(whereTaken): the 3×3 inertia tensor, row-major (kg·m²).</summary>
    public static double[] MassTensor(this IMassProperty2 mp, int whereTaken) => ComArgs.Doubles((object?)mp.GetMomentOfInertia(whereTaken));

    /// <summary>IMassProperty.CenterOfMass (m).</summary>
    public static double[] MassCenter(this IMassProperty mp) => ComArgs.Doubles((object?)mp.CenterOfMass);

    /// <summary>IMassProperty.PrincipleMomentsOfInertia (sic; kg·m²).</summary>
    public static double[] MassPrincipalMoments(this IMassProperty mp) => ComArgs.Doubles((object?)mp.PrincipleMomentsOfInertia);

    /// <summary>IMassProperty.PrincipleAxesOfInertia(i) (sic), i = 0..2.</summary>
    public static double[] MassPrincipalAxis(this IMassProperty mp, int i) => ComArgs.Doubles((object?)mp.get_PrincipleAxesOfInertia(i));

    /// <summary>IMassProperty.GetMomentOfInertia(whereTaken): the 3×3 inertia tensor, row-major (kg·m²).</summary>
    public static double[] MassTensor(this IMassProperty mp, int whereTaken) => ComArgs.Doubles((object?)mp.GetMomentOfInertia(whereTaken));

    /// <summary>IBody2.GetMassProperties(density): [cx, cy, cz, volume, area, mass, …] (m, m³, m², kg).</summary>
    public static double[] MassOfBody(this IBody2 body, double density = 1.0) => ComArgs.Doubles((object?)body.GetMassProperties(density));

    // ---- body check (MeasureService)

    /// <summary>The faults of IBody2.Check3 as (error code, entity) pairs; empty when the body is valid.</summary>
    public static List<(int Code, object? Entity)> FaultList(this IBody2 body)
    {
        var list = new List<(int, object?)>();
        IFaultEntity? faults = body.Check3;
        if (faults is null) return list;
        int n = faults.Count;
        for (int i = 0; i < n; i++)
        {
            object? entity = null;
            try { entity = faults.get_Entity(i); }
            catch (System.Runtime.InteropServices.COMException e) when (!ComArgs.IsDisconnect(e)) { /* a fault without an entity */ }
            list.Add((faults.get_ErrorCode(i), entity));
        }
        return list;
    }
}
