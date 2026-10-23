using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers used by the assembly area (AssemblyService). Every member here returns a VARIANT or IDispatch that the
/// embedded interop imports as dynamic, so the result is cast to object first. The "Asm" prefix keeps the names clear of
/// interop members (an interop member with the same name would shadow the extension and make the call dynamic).
/// </summary>
internal static partial class SwExt
{
    // ---- components

    /// <summary>IComponent2.GetChildren: the components of a subassembly (empty for parts).</summary>
    public static List<IComponent2> AsmChildren(this IComponent2 c) => ComArgs.Objects<IComponent2>((object?)c.GetChildren());

    /// <summary>IComponent2.GetModelDoc2: the component's document, or null (suppressed or lightweight).</summary>
    public static IModelDoc2? AsmModelDoc(this IComponent2 c) => (object?)c.GetModelDoc2() as IModelDoc2;

    /// <summary>IComponent2.GetBox(false, false): [xmin, ymin, zmin, xmax, ymax, zmax] in assembly space (m); empty if none.</summary>
    public static double[] AsmCompBox(this IComponent2 c) => ComArgs.Doubles((object?)c.GetBox(false, false));

    /// <summary>IComponent2.Transform2 as ArrayData (rotation 0..8, translation 9..11 m, scale 12); empty when there is none.</summary>
    public static double[] AsmXformData(this IComponent2 c) => ComArgs.Doubles((object?)c.Transform2?.ArrayData);

    /// <summary>IEntity.GetComponent: the component a face, edge or vertex of an assembly belongs to (null at the top level).</summary>
    public static IComponent2? AsmComponentOf(this IEntity e) => (object?)e.GetComponent() as IComponent2;

    // ---- interference

    /// <summary>IInterferenceDetectionMgr.GetInterferences (runs the detection).</summary>
    public static List<IInterference> AsmInterferences(this IInterferenceDetectionMgr m) =>
        ComArgs.Objects<IInterference>((object?)m.GetInterferences());

    /// <summary>IInterference.Components: the components that interfere.</summary>
    public static List<IComponent2> AsmInterferingComponents(this IInterference i) => ComArgs.Objects<IComponent2>((object?)i.Components);

    /// <summary>IInterference.GetInterferenceBody: the overlap as a temporary body, or null.</summary>
    public static IBody2? AsmInterferenceBody(this IInterference i) => (object?)i.GetInterferenceBody() as IBody2;

    // ---- math (component placement)

    /// <summary>IMathUtility.CreateVector as the coclass type IMathUtility.ComposeTransform takes.</summary>
    public static MathVector AsmVector(this IMathUtility mu, Vec3 v) =>
        (object?)mu.CreateVector(new[] { v.X, v.Y, v.Z }) as MathVector
        ?? throw new SwException(ErrorCodes.Internal, "SOLIDWORKS returned no MathVector.");

    /// <summary>IMathUtility.CreatePoint.</summary>
    public static IMathPoint AsmPoint(this IMathUtility mu, Vec3 p) =>
        (object?)mu.CreatePoint(new[] { p.X, p.Y, p.Z }) as IMathPoint
        ?? throw new SwException(ErrorCodes.Internal, "SOLIDWORKS returned no MathPoint.");

    /// <summary>IMathUtility.CreateTransform from a 16-element ArrayData; null if SOLIDWORKS refuses it.</summary>
    public static MathTransform? AsmTransform(this IMathUtility mu, double[] arrayData) =>
        (object?)mu.CreateTransform(arrayData) as MathTransform;

    /// <summary>
    /// A point mapped by a transform the way SOLIDWORKS applies it (IMathPoint.MultiplyTransform). For a component's
    /// Transform2 this takes part coordinates to assembly coordinates, whatever the ArrayData layout convention is.
    /// </summary>
    public static Vec3 AsmMapPoint(this IMathUtility mu, IMathTransform xf, Vec3 p)
    {
        var mapped = (object?)mu.AsmPoint(p).MultiplyTransform(xf) as IMathPoint
                     ?? throw new SwException(ErrorCodes.Internal, "MathPoint.MultiplyTransform returned nothing.");
        return Vec3.From((object?)mapped.ArrayData);
    }

    /// <summary>A direction mapped by a transform (IMathVector.MultiplyTransform: rotation and scale, no translation).</summary>
    public static Vec3 AsmMapVector(this IMathUtility mu, IMathTransform xf, Vec3 v)
    {
        var mapped = (object?)mu.AsmVector(v).MultiplyTransform(xf) as IMathVector
                     ?? throw new SwException(ErrorCodes.Internal, "MathVector.MultiplyTransform returned nothing.");
        return Vec3.From((object?)mapped.ArrayData);
    }
}
