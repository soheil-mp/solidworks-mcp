using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the drawing area (DrawingService): sheets, views, notes, annotations and the drawing-creation
/// members that return VARIANT/IDispatch. The "Drw" prefix keeps the names clear of the interop members (an interop
/// member with the same name would shadow an extension and make the call dynamic).
/// </summary>
internal static partial class SwExt
{
    // ---- sheets

    /// <summary>IDrawingDoc.GetSheetNames: every sheet, in tab order.</summary>
    public static string[] DrwSheetNames(this IDrawingDoc d) => ComArgs.Strings((object?)d.GetSheetNames());

    /// <summary>IDrawingDoc.GetCurrentSheet.</summary>
    public static ISheet? DrwCurrentSheet(this IDrawingDoc d) => (object?)d.GetCurrentSheet() as ISheet;

    /// <summary>IDrawingDoc.Sheet[name], or null.</summary>
    public static ISheet? DrwSheet(this IDrawingDoc d, string name)
    {
        try { return d.Sheet[name]; }
        catch (COMException) { return null; }
    }

    /// <summary>ISheet.GetProperties2: [paper size, template, scale numerator, denominator, first angle (1/0), width m, height m, …].</summary>
    public static double[] DrwProps(this ISheet s) => ComArgs.Doubles((object?)s.GetProperties2());

    /// <summary>ISheet.GetViews: the drawing views of the sheet (not the sheet itself); empty when none.</summary>
    public static List<IView> DrwViews(this ISheet s) => ComArgs.Objects<IView>((object?)s.GetViews());

    /// <summary>
    /// IDrawingDoc.GetViews: one array per sheet whose first element is the sheet's own view (type sheet), then its
    /// drawing views.
    /// </summary>
    public static List<List<IView>> DrwViewsBySheet(this IDrawingDoc d)
    {
        var all = new List<List<IView>>();
        if ((object?)d.GetViews() is not Array sheets) return all;
        foreach (var s in sheets) all.Add(ComArgs.Objects<IView>(s));
        return all;
    }

    /// <summary>IDrawingDoc.GetFirstView: the current sheet's own view.</summary>
    public static IView? DrwSheetView(this IDrawingDoc d) => (object?)d.GetFirstView() as IView;

    /// <summary>IDrawingDoc.ActiveDrawingView, or null.</summary>
    public static IView? DrwActiveView(this IDrawingDoc d) => (object?)d.ActiveDrawingView as IView;

    // ---- views

    /// <summary>IView.GetOutline: [xMin, yMin, xMax, yMax] on the sheet (m).</summary>
    public static double[] DrwOutline(this IView v) => ComArgs.Doubles((object?)v.GetOutline());

    /// <summary>IView.Position: [x, y] on the sheet (m).</summary>
    public static double[] DrwPosition(this IView v) => ComArgs.Doubles((object?)v.Position);

    /// <summary>Sets IView.Position (m).</summary>
    public static void DrwMoveTo(this IView v, double x, double y) => v.Position = new[] { x, y };

    /// <summary>IView.ModelToViewTransform.ArrayData (model → sheet, m): rotation 0..8, translation 9..11, scale 12; empty when unknown.</summary>
    public static double[] DrwModelToSheet(this IView v)
    {
        try { return ComArgs.Doubles((object?)v.ModelToViewTransform?.ArrayData); }
        catch (COMException) { return []; }
    }

    /// <summary>IView.ScaleRatio: [numerator, denominator].</summary>
    public static double[] DrwScaleRatio(this IView v) => ComArgs.Doubles((object?)v.ScaleRatio);

    /// <summary>IView.GetBaseView: the parent of a projected, section or detail view, or null.</summary>
    public static IView? DrwBaseView(this IView v)
    {
        try { return (object?)v.GetBaseView() as IView; }
        catch (COMException) { return null; }
    }

    /// <summary>IView.GetAnnotations: every annotation (dimensions, notes, tables, center marks, …) in the view.</summary>
    public static List<IAnnotation> DrwAnnotations(this IView v) => ComArgs.Objects<IAnnotation>((object?)v.GetAnnotations());

    /// <summary>IView.GetNotes.</summary>
    public static List<INote> DrwNotes(this IView v) => ComArgs.Objects<INote>((object?)v.GetNotes());

    /// <summary>IView.GetSketch: the view's own sketch (section lines and detail circles are drawn in it), or null.</summary>
    public static ISketch? DrwSketch(this IView v) => (object?)v.GetSketch() as ISketch;

    // ---- view creation (IDrawingDoc)

    /// <summary>IDrawingDoc.CreateDetailViewAt4 (12 arguments; returns the new view or null).</summary>
    public static IView? DrwDetailView(this IDrawingDoc d, double x, double y, int style, double scale1, double scale2, string label,
        int showType, bool fullOutline) =>
        (object?)d.CreateDetailViewAt4(x, y, 0, style, scale1, scale2, label, showType, fullOutline, false, false, 0) as IView;

    /// <summary>IDrawingDoc.InsertModelAnnotations3: the annotations inserted (empty when none).</summary>
    public static List<object> DrwInsertModelAnnotations(this IDrawingDoc d, int option, int types, bool allViews) =>
        ComArgs.Objects<object>((object?)d.InsertModelAnnotations3(option, types, allViews, false, false, false));

    // ---- annotations

    /// <summary>IModelDoc2.InsertNote: the new note, or null.</summary>
    public static INote? DrwInsertNote(this IModelDoc2 d, string text) => (object?)d.InsertNote(text) as INote;

    /// <summary>INote.GetAnnotation.</summary>
    public static IAnnotation? DrwAnnotation(this INote n) => (object?)n.GetAnnotation() as IAnnotation;

    /// <summary>IDisplayDimension.GetAnnotation.</summary>
    public static IAnnotation? DrwAnnotation(this IDisplayDimension d) => (object?)d.GetAnnotation() as IAnnotation;

    /// <summary>IAnnotation.GetPosition: [x, y, z] on the sheet (m).</summary>
    public static double[] DrwPosition(this IAnnotation a) => ComArgs.Doubles((object?)a.GetPosition());

    /// <summary>IModelDoc2.AddDimension2 and its horizontal/vertical/radial/diameter siblings, by kind.</summary>
    public static IDisplayDimension? DrwAddDimension(this IModelDoc2 d, string kind, double x, double y) => kind switch
    {
        "horizontal" => (object?)d.AddHorizontalDimension2(x, y, 0) as IDisplayDimension,
        "vertical" => (object?)d.AddVerticalDimension2(x, y, 0) as IDisplayDimension,
        "radial" => (object?)d.AddRadialDimension2(x, y, 0) as IDisplayDimension,
        "diameter" => (object?)d.AddDiameterDimension2(x, y, 0) as IDisplayDimension,
        _ => (object?)d.AddDimension2(x, y, 0) as IDisplayDimension,
    };

    // ---- selection

    /// <summary>ISelectionMgr.GetSelectedObject6(index, -1) (1-based), or null.</summary>
    public static object? DrwSelected(this ISelectionMgr sm, int index) => (object?)sm.GetSelectedObject6(index, -1);

    // ---- sketch points of a line (view sketches)

    public static ISketchPoint? DrwStart(this ISketchLine l) => (object?)l.GetStartPoint2() as ISketchPoint;
    public static ISketchPoint? DrwEnd(this ISketchLine l) => (object?)l.GetEndPoint2() as ISketchPoint;
}
