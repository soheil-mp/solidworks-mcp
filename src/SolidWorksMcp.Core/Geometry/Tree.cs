using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;

namespace SolidWorksMcp.Core.Geometry;

/// <summary>Feature-tree traversal helpers.</summary>
internal static class Tree
{
    /// <summary>Top-level features in tree order.</summary>
    public static IEnumerable<IFeature> Walk(IModelDoc2 doc)
    {
        var f = doc.FirstFeat();
        int guard = 0;
        while (f is not null && guard++ < 100_000)
        {
            yield return f;
            f = f.Next();
        }
    }

    /// <summary>Every feature including sub-features (absorbed sketches, hole profiles, mates), depth first.</summary>
    public static IEnumerable<IFeature> WalkAll(IModelDoc2 doc)
    {
        foreach (var f in Walk(doc))
        {
            yield return f;
            foreach (var c in Descendants(f)) yield return c;
        }
    }

    private static IEnumerable<IFeature> Descendants(IFeature f)
    {
        foreach (var c in Children(f))
        {
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }

    /// <summary>Sub-features (absorbed sketches, mate members, ...) of a feature.</summary>
    public static IEnumerable<IFeature> Children(IFeature parent)
    {
        var f = parent.FirstSub();
        int guard = 0;
        while (f is not null && guard++ < 10_000)
        {
            yield return f;
            f = f.NextSub();
        }
    }

    /// <summary>Feature types that are folders or system nodes rather than modeling features.</summary>
    public static readonly HashSet<string> SystemTypes = new(StringComparer.Ordinal)
    {
        "HistoryFolder", "SelectionSetFolder", "SensorFolder", "DocsFolder", "DetailCabinet", "CommentsFolder",
        "FavoriteFolder", "SurfaceBodyFolder", "SolidBodyFolder", "EnvFolder", "InkMarkupFolder", "EqnFolder",
        "MaterialFolder", "CutListFolder", "SubWeldFolder", "SubAtomFolder", "BlockFolder", "DesignTableFolder",
        "ProfileFeatureFolder", "RefPlaneFolder", "MateReferenceGroupFolder", "LiveSectionFolder", "MBD3DViewFolder",
        "ExplodeFolder", "TableFolder", "GridFeature", "AmbientLight", "DirectionLight", "AttributeFeature",
        "CameraFolder", "DisplayStateFolder", "RevisionTableFolder", "BomTemplate", "FtrFolder",
    };

    /// <summary>Counts user features (not system folders).</summary>
    public static int CountUserFeatures(IModelDoc2 doc) => Walk(doc).Count(f => !SystemTypes.Contains(f.GetTypeName2()));

    /// <summary>The last feature in the tree (newest).</summary>
    public static IFeature? Last(IModelDoc2 doc) => doc.LastFeat();
}
