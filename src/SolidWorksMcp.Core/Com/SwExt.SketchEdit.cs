using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the sketch-edit area: sketch-segment end/center points, spline fit points, relations and the
/// annotation of a display dimension. All of these return DISPATCH/VARIANT (imported as dynamic). The "Sk" prefix
/// keeps the names clear of the interop members (an interop member with the same name would shadow an extension).
/// </summary>
internal static partial class SwExt
{
    // ---- lines and arcs
    public static ISketchPoint? SkStart(this ISketchLine l) => (object?)l.GetStartPoint2() as ISketchPoint;
    public static ISketchPoint? SkEnd(this ISketchLine l) => (object?)l.GetEndPoint2() as ISketchPoint;
    public static ISketchPoint? SkStart(this ISketchArc a) => (object?)a.GetStartPoint2() as ISketchPoint;
    public static ISketchPoint? SkEnd(this ISketchArc a) => (object?)a.GetEndPoint2() as ISketchPoint;
    public static ISketchPoint? SkCenter(this ISketchArc a) => (object?)a.GetCenterPoint2() as ISketchPoint;

    // ---- ellipses
    public static ISketchPoint? SkStart(this ISketchEllipse e) => (object?)e.GetStartPoint2() as ISketchPoint;
    public static ISketchPoint? SkEnd(this ISketchEllipse e) => (object?)e.GetEndPoint2() as ISketchPoint;
    public static ISketchPoint? SkCenter(this ISketchEllipse e) => (object?)e.GetCenterPoint2() as ISketchPoint;
    public static ISketchPoint? SkMajor(this ISketchEllipse e) => (object?)e.GetMajorPoint2() as ISketchPoint;
    public static ISketchPoint? SkMinor(this ISketchEllipse e) => (object?)e.GetMinorPoint2() as ISketchPoint;

    // ---- splines
    public static List<ISketchPoint> SkFitPoints(this ISketchSpline s) => ComArgs.Objects<ISketchPoint>((object?)s.GetPoints2());

    // ---- relations and dimensions
    public static List<ISketchRelation> SkRelations(this ISketchRelationManager m, int filter) =>
        ComArgs.Objects<ISketchRelation>((object?)m.GetRelations(filter));

    public static IAnnotation? SkAnnotation(this IDisplayDimension d) => (object?)d.GetAnnotation() as IAnnotation;
}
