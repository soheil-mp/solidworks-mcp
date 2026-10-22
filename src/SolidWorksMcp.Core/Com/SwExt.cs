using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for SolidWorks members that return VARIANT/IDispatch. With embedded interop types the compiler
/// imports those as <c>dynamic</c>; Core forbids dynamic (see the ForbidDynamic target), so every such result is
/// cast to <c>object</c> here and converted with <see cref="ComArgs"/>. Prefer these over calling the raw members.
/// </summary>
internal static class SwExt
{
    // ---- documents
    public static ISelectionMgr Sel(this IModelDoc2 doc) => (ISelectionMgr)(object)doc.SelectionManager;
    public static IModelView? View(this IModelDoc2 doc) => (object?)doc.ActiveView as IModelView;
    public static IFeature? FirstFeat(this IModelDoc2 doc) => (object?)doc.FirstFeature() as IFeature;
    public static IFeature? LastFeat(this IModelDoc2 doc) => (object?)doc.FeatureByPositionReverse(0) as IFeature;

    // ---- parts
    public static List<IBody2> Bodies(this IPartDoc part, swBodyType_e type, bool visibleOnly = true) =>
        ComArgs.Objects<IBody2>((object?)part.GetBodies2((int)type, visibleOnly));

    /// <summary>Solid then surface bodies.</summary>
    public static List<IBody2> AllBodies(this IPartDoc part, bool visibleOnly = true)
    {
        var list = part.Bodies(swBodyType_e.swSolidBody, visibleOnly);
        list.AddRange(part.Bodies(swBodyType_e.swSheetBody, visibleOnly));
        return list;
    }

    public static double[] PartBox(this IPartDoc part) => ComArgs.Doubles((object?)part.GetPartBox(true));

    // ---- assemblies
    public static List<IComponent2> Components(this IAssemblyDoc asm, bool topLevelOnly) =>
        ComArgs.Objects<IComponent2>((object?)asm.GetComponents(topLevelOnly));

    public static List<IBody2> Bodies(this IComponent2 comp, swBodyType_e type) =>
        ComArgs.Objects<IBody2>((object?)comp.GetBodies3((int)type, out _));

    // ---- topology
    public static List<IFace2> Faces(this IBody2 body) => ComArgs.Objects<IFace2>((object?)body.GetFaces());
    public static List<IEdge> Edges(this IBody2 body) => ComArgs.Objects<IEdge>((object?)body.GetEdges());
    public static List<IVertex> Vertices(this IBody2 body) => ComArgs.Objects<IVertex>((object?)body.GetVertices());
    public static double[] Box(this IBody2 body) => ComArgs.Doubles((object?)body.GetBodyBox());
    public static List<IEdge> Edges(this IFace2 face) => ComArgs.Objects<IEdge>((object?)face.GetEdges());
    public static double[] Box(this IFace2 face) => ComArgs.Doubles((object?)face.GetBox());
    public static ISurface? Surface(this IFace2 face) => (object?)face.GetSurface() as ISurface;
    public static IFeature? Feature(this IFace2 face) => (object?)face.GetFeature() as IFeature;
    public static IBody2? Body(this IFace2 face) => (object?)face.GetBody() as IBody2;
    public static double[] NormalVec(this IFace2 face) => ComArgs.Doubles((object?)face.Normal);

    public static double[] ClosestPoint(this IFace2 face, Geometry.Vec3 p) =>
        ComArgs.Doubles((object?)face.GetClosestPointOn(p.X, p.Y, p.Z));

    public static double[] ClosestPoint(this IEdge edge, Geometry.Vec3 p) =>
        ComArgs.Doubles((object?)edge.GetClosestPointOn(p.X, p.Y, p.Z));

    public static ICurve? Curve(this IEdge edge) => (object?)edge.GetCurve() as ICurve;
    public static double[] CurveParams(this IEdge edge) => ComArgs.Doubles((object?)edge.GetCurveParams2());
    public static double[] EvalAt(this IEdge edge, double t) => ComArgs.Doubles((object?)edge.Evaluate2(t, 0));
    public static List<IFace2> AdjacentFaces(this IEdge edge) => ComArgs.Objects<IFace2>((object?)edge.GetTwoAdjacentFaces2());
    public static IVertex? StartVertex(this IEdge edge) => (object?)edge.GetStartVertex() as IVertex;
    public static IVertex? EndVertex(this IEdge edge) => (object?)edge.GetEndVertex() as IVertex;
    public static double[] Point(this IVertex v) => ComArgs.Doubles((object?)v.GetPoint());

    // ---- sketches
    public static List<ISketchSegment> Segments(this ISketch sketch) =>
        ComArgs.Objects<ISketchSegment>((object?)sketch.GetSketchSegments());

    public static List<ISketchPoint> Points(this ISketch sketch) =>
        ComArgs.Objects<ISketchPoint>((object?)sketch.GetSketchPoints2());

    // ---- features
    public static IFeature? FirstSub(this IFeature f) => (object?)f.GetFirstSubFeature() as IFeature;
    public static IFeature? NextSub(this IFeature f) => (object?)f.GetNextSubFeature() as IFeature;
    public static IFeature? Next(this IFeature f) => (object?)f.GetNextFeature() as IFeature;
    public static object? Specific(this IFeature f) => f.GetSpecificFeature2();

    // ---- app
    public static List<IModelDoc2> Documents(this ISldWorks app) => ComArgs.Objects<IModelDoc2>((object?)app.GetDocuments());
    public static IModelDoc2? Active(this ISldWorks app) => (object?)app.ActiveDoc as IModelDoc2;
    public static IMathUtility MathUtil(this ISldWorks app) => (IMathUtility)(object)app.GetMathUtility();
}
