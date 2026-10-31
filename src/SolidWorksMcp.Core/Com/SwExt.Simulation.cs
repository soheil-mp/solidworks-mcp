using System.Runtime.InteropServices;
using SolidWorks.Interop.cosworks;
using SolidWorks.Interop.sldworks;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Typed wrappers for the SOLIDWORKS Simulation (cosworks) API (SimulationService): VARIANT and IDispatch results are
/// cast to object first. Names are prefixed Sim… or name the result, so they can't shadow an interop member.
/// </summary>
internal static partial class SwExt
{
    /// <summary>ProgID of the Simulation add-in for <c>ISldWorks.GetAddInObject</c>.</summary>
    public const string SimulationProgId = "SldWorks.Simulation";

    /// <summary><c>ISldWorks.GetAddInObject("SldWorks.Simulation")</c>: the Simulation add-in when it is loaded, else null.</summary>
    public static CwAddincallback? SimulationAddin(this ISldWorks app)
    {
        try { return (object?)app.GetAddInObject(SimulationProgId) as CwAddincallback; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>
    /// <c>ICWResults.GetMinMaxStress(component, 0, step 1, no plane, units)</c>: [nodeMin, min, nodeMax, max] (empty when
    /// there is no answer). VERIFIED recipe (docs/solidworks-api-notes.md §14).
    /// </summary>
    public static double[] StressRange(this CWResults results, int component, int units, out int error) =>
        ComArgs.Doubles((object?)results.GetMinMaxStress(component, 0, 1, null, units, out error));

    /// <summary><c>ICWResults.GetMinMaxDisplacement(component, step 1, no plane, units)</c>: [nodeMin, min, nodeMax, max].</summary>
    public static double[] DisplacementRange(this CWResults results, int component, int units, out int error) =>
        ComArgs.Doubles((object?)results.GetMinMaxDisplacement(component, 1, null, units, out error));

    /// <summary><c>ICWResults.GetMinMaxThermal(component, step 1, no plane, units)</c>: [nodeMin, min, nodeMax, max].</summary>
    public static double[] ThermalRange(this CWResults results, int component, int units, out int error) =>
        ComArgs.Doubles((object?)results.GetMinMaxThermal(component, 1, null, units, out error));

    /// <summary>
    /// <c>ICWResults.GetReactionForcesAndMomentsWithSelections(step 1, no plane, units, selection, …)</c>: the 16-value
    /// summary (sums of forces and moments over the selection, then over the entire model). The per-node return value is
    /// discarded.
    /// </summary>
    public static double[] SimReactionSummary(this CWResults results, object selection, int forceUnits, out int error)
    {
        _ = (object?)results.GetReactionForcesAndMomentsWithSelections(1, null, forceUnits, selection, out object summary, out object _,
            out error);
        return ComArgs.Doubles(summary);
    }

    /// <summary>The by-value array flavours the Simulation API may take for a list of entities.</summary>
    public enum SimArrayFlavor { Variant, Dispatch }

    /// <summary>
    /// The entity array for loads, fixtures and result selections: a VARIANT array of VT_DISPATCH elements (what VBA's
    /// <c>(Array(face))</c> passes, VERIFIED for AddRestraint/AddPressure from VBA), or a VT_DISPATCH SAFEARRAY.
    /// </summary>
    public static object SimEntityArray(IReadOnlyList<object> entities, SimArrayFlavor flavor) => flavor == SimArrayFlavor.Variant
        ? ComArgs.VariantArray(entities.Select(e => (object)new DispatchWrapper(e)).ToArray())
        : ComArgs.DispatchArray(entities);

    /// <summary>Both flavours, <paramref name="preferred"/> first (the one that worked last).</summary>
    public static IEnumerable<SimArrayFlavor> SimFlavors(SimArrayFlavor preferred) =>
        preferred == SimArrayFlavor.Variant ? [SimArrayFlavor.Variant, SimArrayFlavor.Dispatch] : [SimArrayFlavor.Dispatch, SimArrayFlavor.Variant];
}
