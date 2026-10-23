using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the bodies, surfaces and sheet-metal area (BodyService, SurfaceService, SheetMetalService). The
/// "Bd" prefix keeps the names clear of interop members (a member with the same name would shadow an extension).
/// </summary>
internal static partial class SwExt
{
    /// <summary>Every feature of the document, sub-features included (IFeatureManager.GetFeatures(false)).</summary>
    public static List<IFeature> BdAllFeatures(this IModelDoc2 doc) =>
        ComArgs.Objects<IFeature>((object?)doc.FeatureManager.GetFeatures(false));

    /// <summary>IFeatureManager.PreSplitBody2: the bodies the selected trim tools would cut the part into (empty when none).</summary>
    public static List<IBody2> BdPreSplit(this IFeatureManager fm) => ComArgs.Objects<IBody2>((object?)fm.PreSplitBody2());

    /// <summary>IFeatureManager.PostSplitBody2: creates the split feature for the bodies of a PreSplitBody2 call (bodies kept, no files saved).</summary>
    public static IFeature? BdPostSplit(this IFeatureManager fm, object bodies, object? origins, object? savePaths) =>
        (object?)fm.PostSplitBody2(bodies, false, origins, savePaths, "") as IFeature;

    /// <summary>IFeatureManager.PreSplitBody (the older pair): the bodies the selected trim tools would make.</summary>
    public static List<IBody2> BdPreSplitLegacy(this IFeatureManager fm) => ComArgs.Objects<IBody2>((object?)fm.PreSplitBody());

    /// <summary>IFeatureManager.PostSplitBody (the older pair; bodies kept, no files saved).</summary>
    public static IFeature? BdPostSplitLegacy(this IFeatureManager fm, object bodies, object? origins, object? savePaths) =>
        fm.PostSplitBody(bodies, false, origins, savePaths);

    /// <summary>The ICombineBodiesFeatureData of a Combine feature, or null.</summary>
    public static ICombineBodiesFeatureData? BdCombineData(this IFeature f) => f.Definition() as ICombineBodiesFeatureData;

    /// <summary>ICombineBodiesFeatureData.BodiesToCombine as a list (empty when none).</summary>
    public static List<IBody2> BdBodiesToCombine(this ICombineBodiesFeatureData data) => ComArgs.Objects<IBody2>((object?)data.BodiesToCombine);

    /// <summary>The ISheetMetalFeatureData of a SheetMetal feature (read-only use: no AccessSelections), or null.</summary>
    public static ISheetMetalFeatureData? BdSheetMetalData(this IFeature f) => f.Definition() as ISheetMetalFeatureData;

    /// <summary>
    /// IModelDoc2.InsertSketchForEdgeFlange: the profile sketch SOLIDWORKS makes for an edge flange on <paramref name="edge"/>
    /// at <paramref name="angle"/> (radians), as its feature (the API returns a DISPATCH: a feature or a sketch); null if none.
    /// </summary>
    public static IFeature? BdSketchForEdgeFlange(this IModelDoc2 doc, IEdge edge, double angle, bool flip)
    {
        object? r = doc.InsertSketchForEdgeFlange(edge, angle, flip);
        return r as IFeature ?? (r as ISketch) as IFeature;
    }

    /// <summary>The selected object at a 1-based index (any mark), or null.</summary>
    public static object? BdSelected(this ISelectionMgr sm, int index) => (object?)sm.GetSelectedObject6(index, -1);

    /// <summary>The faces a feature owns (empty when none).</summary>
    public static List<IFace2> BdFaces(this IFeature f) => ComArgs.Objects<IFace2>((object?)f.GetFaces());

    /// <summary>The dimensions shown with a feature (its display dimensions' IDimension objects).</summary>
    public static List<IDimension> BdDimensions(this IFeature f)
    {
        var list = new List<IDimension>();
        object? dd = f.GetFirstDisplayDimension();
        int guard = 0;
        while (dd is IDisplayDimension disp && guard++ < 200)
        {
            if (disp.GetDimension2(0) is IDimension dim) list.Add(dim);
            dd = f.GetNextDisplayDimension(disp);
        }
        return list;
    }
}
