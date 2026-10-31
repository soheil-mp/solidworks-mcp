using System.Text.RegularExpressions;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

// Pure planning for dimension_tolerance_set (no SOLIDWORKS types; linked into the offline unit tests).

/// <summary>One dimension_tolerance_set entry.</summary>
[System.ComponentModel.Description("{name:'D1@Sketch1', type:'bilateral', upper:0.1, lower:-0.05}")]
public sealed class ToleranceRequest
{
    [System.ComponentModel.Description("Dimension name: from dimension_list (part, assembly) or as drw_dimension_add returned it (drawing), e.g. D1@Sketch1.")]
    public required string Name { get; set; }

    [System.ComponentModel.Description("none | basic | bilateral | limit | symmetric | min | max | fit | fit_with_tolerance | fit_tolerance_only")]
    public required string Type { get; set; }

    [System.ComponentModel.Description("bilateral, limit: the upper deviation; symmetric: the ± value. In the dimension's unit (mm, deg for angles).")]
    public double? Upper { get; set; }

    [System.ComponentModel.Description("bilateral, limit: the lower deviation, usually negative, e.g. -0.05.")]
    public double? Lower { get; set; }

    [System.ComponentModel.Description("Fit types: the ISO hole class, e.g. H7.")]
    public string? Hole { get; set; }

    [System.ComponentModel.Description("Fit types: the ISO shaft class, e.g. g6.")]
    public string? Shaft { get; set; }
}

/// <summary>A validated tolerance: the swTolType_e code and the values in the dimension's unit (mm or deg).</summary>
internal sealed record TolerancePlan(string Name, int Code, double? Upper, double? Lower, string? Hole, string? Shaft)
{
    public string TypeName => DimensionTolerance.TypeName(Code);
}

internal static class DimensionTolerance
{
    // swTolType_e
    public const int None = 0, Basic = 1, Bilateral = 2, Limit = 3, Symmetric = 4, Min = 5, Max = 6, Fit = 7, FitWithTolerance = 8,
        FitToleranceOnly = 9;

    private static readonly Regex HoleClass = new(@"^[A-Z]{1,2}[0-9]{1,2}$");
    private static readonly Regex ShaftClass = new(@"^[a-z]{1,2}[0-9]{1,2}$");

    public static string TypeName(int code) => code switch
    {
        None => "none",
        Basic => "basic",
        Bilateral => "bilateral",
        Limit => "limit",
        Symmetric => "symmetric",
        Min => "min",
        Max => "max",
        Fit => "fit",
        FitWithTolerance => "fit_with_tolerance",
        FitToleranceOnly => "fit_tolerance_only",
        10 => "block",
        11 => "general",
        _ => $"type {code}",
    };

    /// <summary>Types whose deviations the caller sets (SetValues).</summary>
    public static bool TakesValues(int code) => code is Bilateral or Limit or Symmetric;

    /// <summary>Types with ISO fit classes (SetFitValues).</summary>
    public static bool IsFit(int code) => code is Fit or FitWithTolerance or FitToleranceOnly;

    /// <summary>Types that show deviations (set, or computed by SOLIDWORKS from the fit).</summary>
    public static bool ShowsDeviations(int code) => code is Bilateral or Limit or Symmetric or FitWithTolerance or FitToleranceOnly;

    /// <summary>Validates one entry before any SOLIDWORKS call.</summary>
    public static TolerancePlan Plan(ToleranceRequest q, int index)
    {
        string where = $"tolerances[{index}]";
        if (string.IsNullOrWhiteSpace(q.Name)) throw SwException.InvalidArgument($"{where}: 'name' is required, e.g. D1@Sketch1 (dimension_list).");
        string name = q.Name.Trim();
        int code = (q.Type ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_') switch
        {
            "none" => None,
            "basic" => Basic,
            "bilateral" or "plus_minus" => Bilateral,
            "limit" or "limits" => Limit,
            "symmetric" => Symmetric,
            "min" => Min,
            "max" => Max,
            "fit" => Fit,
            "fit_with_tolerance" => FitWithTolerance,
            "fit_tolerance_only" => FitToleranceOnly,
            _ => throw SwException.InvalidArgument($"{where}: unknown tolerance type '{q.Type}'.",
                "Use none, basic, bilateral, limit, symmetric, min, max, fit, fit_with_tolerance or fit_tolerance_only."),
        };
        string type = TypeName(code);
        void NotFor(string what, bool given)
        {
            if (given) throw SwException.InvalidArgument($"{where}: '{what}' does not apply to a {type} tolerance.");
        }
        double Finite(double? v, string what)
        {
            if (v is not double x || !double.IsFinite(x)) throw SwException.InvalidArgument($"{where}: a {type} tolerance needs a finite '{what}'.");
            if (Math.Abs(x) > 100) throw SwException.InvalidArgument($"{where}: '{what}' = {x} is not a tolerance (more than 100 mm or degrees).");
            return x;
        }
        string? Class(string? s, Regex rx, string what, string example)
        {
            if (s is null) return null;
            string c = s.Trim();
            if (!rx.IsMatch(c))
                throw SwException.InvalidArgument($"{where}: '{s}' is not an ISO {what} class.", $"{char.ToUpperInvariant(what[0])}{what[1..]} classes look like {example} (letters, then the grade).");
            return c;
        }
        switch (code)
        {
            case Bilateral or Limit:
            {
                NotFor("hole", q.Hole is not null);
                NotFor("shaft", q.Shaft is not null);
                double up = Finite(q.Upper, "upper"), lo = Finite(q.Lower, "lower");
                if (up <= lo) throw SwException.InvalidArgument($"{where}: upper ({up}) must be greater than lower ({lo}).");
                return new TolerancePlan(name, code, up, lo, null, null);
            }
            case Symmetric:
            {
                NotFor("hole", q.Hole is not null);
                NotFor("shaft", q.Shaft is not null);
                double up = Finite(q.Upper, "upper");
                if (up <= 0) throw SwException.InvalidArgument($"{where}: a symmetric tolerance needs a positive 'upper' (the ± value).");
                if (q.Lower is double lo && Math.Abs(lo + up) > 1e-12)
                    throw SwException.InvalidArgument($"{where}: a symmetric tolerance is ±upper; leave 'lower' out (or pass -{up}).");
                return new TolerancePlan(name, code, up, -up, null, null);
            }
            case Fit or FitWithTolerance or FitToleranceOnly:
            {
                NotFor("upper", q.Upper is not null);
                NotFor("lower", q.Lower is not null);
                var hole = Class(q.Hole, HoleClass, "hole", "H7 or JS9");
                var shaft = Class(q.Shaft, ShaftClass, "shaft", "g6 or js6");
                if (hole is null && shaft is null)
                    throw SwException.InvalidArgument($"{where}: a {type} tolerance needs 'hole' (e.g. H7) and/or 'shaft' (e.g. g6).");
                return new TolerancePlan(name, code, null, null, hole, shaft);
            }
            default: // none, basic, min, max
                NotFor("upper", q.Upper is not null);
                NotFor("lower", q.Lower is not null);
                NotFor("hole", q.Hole is not null);
                NotFor("shaft", q.Shaft is not null);
                return new TolerancePlan(name, code, null, null, null, null);
        }
    }

    /// <summary>All entries validated; a dimension may appear once.</summary>
    public static List<TolerancePlan> PlanAll(IReadOnlyList<ToleranceRequest>? items)
    {
        if (items is not { Count: > 0 }) throw SwException.InvalidArgument("Pass 'tolerances', e.g. [{name:'D1@Sketch1', type:'bilateral', upper:0.1, lower:-0.05}].");
        if (items.Count > 200) throw SwException.InvalidArgument($"At most 200 tolerances per call (got {items.Count}).");
        var plans = items.Select(Plan).ToList();
        var twice = plans.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null) throw SwException.InvalidArgument($"'{twice.Key}' is listed twice.");
        return plans;
    }

    /// <summary>
    /// Whether the read-back tolerance is the planned one: the type; set deviations to 1e-9 of the unit (they may come back
    /// as computed for fits, which are not compared); fit classes exactly.
    /// </summary>
    public static string? Mismatch(TolerancePlan p, ToleranceInfo got)
    {
        if (got.Type != p.TypeName) return $"the type reads {got.Type}, not {p.TypeName}";
        static bool Near(double? a, double? b) => a is double x && b is double y && Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
        if (TakesValues(p.Code))
        {
            if (!Near(got.Upper, p.Upper)) return $"the upper deviation reads {got.Upper?.ToString() ?? "nothing"}, not {p.Upper}";
            if (p.Code != Symmetric && !Near(got.Lower, p.Lower)) return $"the lower deviation reads {got.Lower?.ToString() ?? "nothing"}, not {p.Lower}";
        }
        if (IsFit(p.Code))
        {
            if (p.Hole is not null && got.Hole != p.Hole) return $"the hole class reads '{got.Hole}', not '{p.Hole}'";
            if (p.Shaft is not null && got.Shaft != p.Shaft) return $"the shaft class reads '{got.Shaft}', not '{p.Shaft}'";
        }
        return null;
    }
}
