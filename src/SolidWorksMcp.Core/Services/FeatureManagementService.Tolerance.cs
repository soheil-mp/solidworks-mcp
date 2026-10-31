using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>One dimension's tolerance before and after dimension_tolerance_set.</summary>
public sealed class ToleranceChange
{
    public required string Name { get; init; }
    /// <summary>mm | deg.</summary>
    public required string Unit { get; init; }
    /// <summary>The nominal value.</summary>
    public double Value { get; init; }
    public required ToleranceInfo Previous { get; init; }
    public required ToleranceInfo Tolerance { get; init; }
    /// <summary>[smallest, largest] size allowed (nominal plus the deviations), when the tolerance has deviations.</summary>
    public double[]? Limits { get; init; }
}

public sealed class DimensionToleranceResult : ToolResult
{
    public required IReadOnlyList<ToleranceChange> Changes { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    /// <summary>How the deviations were written: SetValues2, or SetValues where SetValues2 refused them.</summary>
    public string? Method { get; init; }
}

public sealed partial class FeatureManagementService
{
    private static readonly ToleranceInfo NoTolerance = new() { Type = "none" };

    private sealed record ToleranceTarget(TolerancePlan Plan, IDimension Dim, int Type, string Name);

    public Task<DimensionToleranceResult> DimensionToleranceSetAsync(string? doc, IReadOnlyList<ToleranceRequest>? tolerances, CancellationToken ct = default)
    {
        var plans = DimensionTolerance.PlanAll(tolerances);
        return session.RunAsync("dimension_tolerance_set", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY, swDocumentTypes_e.swDocDRAWING);
            bool drawing = d.GetType() == (int)swDocumentTypes_e.swDocDRAWING;
            var targets = new List<ToleranceTarget>();
            foreach (var p in plans)
            {
                var dim = d.DimensionNamed(p.Name)
                          ?? throw new SwException(ErrorCodes.EntityNotFound, $"No dimension named '{p.Name}' in '{ctx.Title(d)}'.",
                              drawing
                                  ? "In a drawing, name a dimension of the drawing (drw_dimension_add returns it, e.g. RD1@Drawing View1); set a model dimension's tolerance in its part or assembly (it shows in the drawing)."
                                  : "dimension_list shows the names, e.g. D1@Sketch1 or D1@Boss-Extrude1.");
                int type = dim.GetType();
                if (type == (int)swDimensionParamType_e.swDimensionParamTypeInteger)
                    throw SwException.InvalidArgument($"'{p.Name}' is a count (e.g. a pattern's instances); counts take no tolerance.");
                targets.Add(new ToleranceTarget(p, dim, type, FeatureVerifier.ShortName(dim.FullName)));
            }

            var previous = targets.Select(t => ReadTolerance(t.Dim, t.Type) ?? NoTolerance).ToList();
            var methods = new SortedSet<string>(StringComparer.Ordinal);
            int touched = 0;
            string? problem = null;
            foreach (var t in targets)
            {
                touched++;
                problem = ApplyTolerance(t.Dim, t.Plan, t.Type, methods);
                if (problem is not null)
                {
                    problem = $"'{t.Name}': {problem}";
                    break;
                }
            }
            var read = targets.Select(t => ReadTolerance(t.Dim, t.Type) ?? NoTolerance).ToList();
            if (problem is null)
                for (int i = 0; i < targets.Count && problem is null; i++)
                    if (DimensionTolerance.Mismatch(targets[i].Plan, read[i]) is { } m) problem = $"'{targets[i].Name}': {m}";
            if (problem is not null)
            {
                string restored = RestoreTolerances(targets.Take(touched).ToList(), previous);
                throw new SwException(ErrorCodes.VerifyFailed, $"dimension_tolerance_set: {problem}. {restored}",
                    "Fit classes must exist in SOLIDWORKS' ISO tables for the size (e.g. H7, g6); deviations are in the dimension's unit.");
            }
            d.GraphicsRedraw2();

            var checks = new List<string> { $"{targets.Count} tolerance(s) read back: type, deviations and fit classes" };
            var changes = new List<ToleranceChange>();
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                double nominal = FeatureManagementLogic.FromApi(t.Type, t.Dim.SystemValue);
                var tol = read[i];
                double[]? limits = DimensionTolerance.ShowsDeviations(t.Plan.Code) && tol.Upper is double up
                    ? [Math.Round(nominal + (tol.Lower ?? -up), 6), Math.Round(nominal + up, 6)]
                    : null;
                if (DimensionTolerance.IsFit(t.Plan.Code) && tol.Upper is not null)
                    checks.Add($"'{t.Name}' {t.Plan.Hole}{t.Plan.Shaft}: SOLIDWORKS gives {tol.Lower} / +{tol.Upper} {FeatureManagementLogic.UnitOf(t.Type)}");
                changes.Add(new ToleranceChange
                {
                    Name = t.Name,
                    Unit = FeatureManagementLogic.UnitOf(t.Type),
                    Value = nominal,
                    Previous = previous[i],
                    Tolerance = tol,
                    Limits = limits,
                });
            }
            return new DimensionToleranceResult
            {
                Doc = ctx.Title(d),
                Changes = changes,
                Checks = checks,
                Method = methods.Count == 0 ? null : string.Join(", ", methods),
            };
        }, RunOptions.Write(doc), ct);
    }

    /// <summary>
    /// Sets the type, then the deviations (SetValues2; from VBA it refused every form, so SetValues, which took them,
    /// VERIFIED 2026-10-31) and the fit classes. Returns a problem, or null; the caller checks by reading back.
    /// </summary>
    private static string? ApplyTolerance(IDimension dim, TolerancePlan p, int dimType, ISet<string> methods)
    {
        var t = dim.Tolerance;
        if (t is null) return "it has no tolerance object";
        t.Type = p.Code;
        if (DimensionTolerance.TakesValues(p.Code))
        {
            double lo = ToleranceToApi(dimType, p.Lower!.Value), up = ToleranceToApi(dimType, p.Upper!.Value);
            bool ok = false;
            try
            {
                ok = t.SetValues2(lo, up, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
            }
            if (ok) methods.Add("SetValues2");
            else if (t.SetValues(lo, up)) methods.Add("SetValues");
            else return "SOLIDWORKS refused the deviations";
        }
        if (DimensionTolerance.IsFit(p.Code) && !t.SetFitValues(p.Hole ?? "", p.Shaft ?? ""))
            return $"SOLIDWORKS refused the fit {p.Hole}{(p.Hole is not null && p.Shaft is not null ? "/" : "")}{p.Shaft}";
        return null;
    }

    /// <summary>Puts the previous tolerances back (best effort) and says whether that worked.</summary>
    private static string RestoreTolerances(IReadOnlyList<ToleranceTarget> touched, IReadOnlyList<ToleranceInfo> previous)
    {
        var bad = new List<string>();
        for (int i = 0; i < touched.Count; i++)
        {
            var target = touched[i];
            var was = previous[i];
            try
            {
                var t = target.Dim.Tolerance;
                int code = Code(was.Type);
                t.Type = code;
                if (was.Upper is double up) t.SetValues(ToleranceToApi(target.Type, was.Lower ?? -up), ToleranceToApi(target.Type, up));
                if (DimensionTolerance.IsFit(code)) t.SetFitValues(was.Hole ?? "", was.Shaft ?? "");
                if (ReadTolerance(target.Dim, target.Type)?.Type != was.Type) bad.Add(target.Name);
            }
            catch (Exception e) when (!ComArgs.IsDisconnect(e))
            {
                bad.Add(target.Name);
            }
        }
        return bad.Count == 0 ? "The previous tolerances were restored." : $"Restore incomplete for {string.Join(", ", bad)}.";
    }

    private static int Code(string type)
    {
        for (int c = 0; c <= 11; c++)
            if (DimensionTolerance.TypeName(c) == type) return c;
        return DimensionTolerance.None;
    }

    /// <summary>A deviation in the tool's unit (mm, deg) → the API's (m, rad). Deviations may be negative.</summary>
    private static double ToleranceToApi(int dimType, double v) =>
        dimType == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular ? Units.Rad(v) : Units.M(v);

    /// <summary>
    /// A dimension's tolerance in the tool's units, or null for a count or when it can't be read. Deviations come only
    /// where SOLIDWORKS says they are valid for the type (swDimensionTolerance_ValidForType).
    /// </summary>
    internal static ToleranceInfo? ReadTolerance(IDimension dim, int dimType)
    {
        if (dimType == (int)swDimensionParamType_e.swDimensionParamTypeInteger) return null;
        try
        {
            var t = dim.Tolerance;
            if (t is null) return null;
            int code = t.Type;
            bool angular = dimType == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
            double? Dev(int status, double v) =>
                status == (int)swDimensionToleranceWarning_e.swDimensionTolerance_ValidForType ? Math.Round(angular ? Units.Deg(v) : Units.Mm(v), 9) : null;
            double? up = null, lo = null;
            if (DimensionTolerance.ShowsDeviations(code))
            {
                up = Dev(t.GetMaxValue2(out double max), max);
                lo = code == DimensionTolerance.Symmetric ? (up is double u ? -u : null) : Dev(t.GetMinValue2(out double min), min);
            }
            string? hole = null, shaft = null;
            if (DimensionTolerance.IsFit(code))
            {
                hole = t.GetHoleFitValue() is { Length: > 0 } h ? h : null;
                shaft = t.GetShaftFitValue() is { Length: > 0 } s ? s : null;
            }
            return new ToleranceInfo { Type = DimensionTolerance.TypeName(code), Upper = up, Lower = lo, Hole = hole, Shaft = shaft };
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            return null;
        }
    }
}
