using System.Globalization;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>Typed wrappers used by the feature-management area (FeatureManagementService).</summary>
internal static partial class SwExt
{
    /// <summary>The features this one depends on (IFeature.GetParents), e.g. the profile sketch of an extrude.</summary>
    public static List<IFeature> ParentFeatures(this IFeature f) => ComArgs.Objects<IFeature>((object?)f.GetParents());

    /// <summary>The features that depend on this one (IFeature.GetChildren), e.g. a fillet on an extrude's edges.</summary>
    public static List<IFeature> ChildFeatures(this IFeature f) => ComArgs.Objects<IFeature>((object?)f.GetChildren());

    /// <summary>
    /// The feature-data object (IExtrudeFeatureData2, ISimpleFilletFeatureData2, …) for reading only: without
    /// AccessSelections, which would roll the model back to the feature until ReleaseSelectionAccess.
    /// </summary>
    public static object? Definition(this IFeature f) => (object?)f.GetDefinition();

    /// <summary>IFeature.IsSuppressed2 with VT_EMPTY names: one flag per configuration (1 = this one, 2 = all).</summary>
    public static bool[] SuppressionStates(this IFeature f, int configOpt) =>
        Bools((object?)f.IsSuppressed2(configOpt, ComArgs.Empty));

    /// <summary>IModelDoc2.Parameter: the dimension named like "D1@Boss-Extrude1", or null (also when the lookup throws).</summary>
    public static IDimension? DimensionNamed(this IModelDoc2 doc, string name)
    {
        try { return (object?)doc.Parameter(name) as IDimension; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>The What's Wrong list: features with a rebuild error (swFeatureError_e) or warning, in SOLIDWORKS' order.</summary>
    public static List<(IFeature Feature, int Code, bool Warning)> WhatsWrong(this IModelDocExtension ext)
    {
        var list = new List<(IFeature, int, bool)>();
        if (ext.GetWhatsWrongCount() <= 0) return list;
        ext.GetWhatsWrong(out object features, out object codes, out object warnings);
        if ((object?)features is not Array feats) return list;
        var c = ComArgs.Ints((object?)codes);
        var w = Bools((object?)warnings);
        for (int i = 0; i < feats.Length; i++)
            if (feats.GetValue(i) is IFeature f)
                list.Add((f, i < c.Length ? c[i] : 0, i < w.Length && w[i]));
        return list;
    }

    /// <summary>The document's equations (text as written, e.g. "\"D1@Boss-Extrude1\" = 20mm") and their suppression.</summary>
    public static List<(string Text, bool Suppressed)> EquationList(this IModelDoc2 doc)
    {
        var list = new List<(string, bool)>();
        var em = doc.GetEquationMgr();
        if (em is null) return list;
        int n = em.GetCount();
        for (int i = 0; i < n; i++)
        {
            string text = em.Equation[i] ?? "";
            bool suppressed = false;
            try { suppressed = em.Suppression[i]; }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* older documents: treat as active */ }
            list.Add((text, suppressed));
        }
        return list;
    }

    /// <summary>A VARIANT of booleans → bool[] (empty when null).</summary>
    private static bool[] Bools(object? v) => v switch
    {
        null => [],
        bool b => [b],
        bool[] a => a,
        Array a => a.Cast<object?>().Select(x => x is not null && Convert.ToBoolean(x, CultureInfo.InvariantCulture)).ToArray(),
        _ => throw new SwException(ErrorCodes.Internal, $"Expected a boolean array, got {v.GetType().Name}."),
    };
}
