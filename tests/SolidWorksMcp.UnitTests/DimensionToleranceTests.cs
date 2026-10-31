using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>How dimension_tolerance_set validates its entries and compares what SOLIDWORKS reads back.</summary>
public class DimensionToleranceTests
{
    private static TolerancePlan Plan(string type, double? upper = null, double? lower = null, string? hole = null, string? shaft = null) =>
        DimensionTolerance.Plan(new ToleranceRequest { Name = "D1@Sketch1", Type = type, Upper = upper, Lower = lower, Hole = hole, Shaft = shaft }, 0);

    private static void Refused(Func<object> f) => Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(f).Code);

    [Fact]
    public void Bilateral_and_limit_take_an_upper_above_the_lower()
    {
        var p = Plan("bilateral", 0.1, -0.05);
        Assert.Equal((DimensionTolerance.Bilateral, 0.1, -0.05), (p.Code, p.Upper!.Value, p.Lower!.Value));
        Assert.Equal(DimensionTolerance.Limit, Plan("Limit", 0.2, 0).Code);
        Assert.Equal(DimensionTolerance.Bilateral, Plan("plus-minus", 0.1, 0.05).Code); // both positive is legal (a shifted zone)
        Refused(() => Plan("bilateral", 0.05, 0.1));
        Refused(() => Plan("bilateral", 0.1));
        Refused(() => Plan("bilateral", 0.1, -0.1, hole: "H7"));
        Refused(() => Plan("limit", double.NaN, -0.1));
        Refused(() => Plan("bilateral", 250, -1)); // not a tolerance
    }

    [Fact]
    public void Symmetric_is_plus_minus_upper()
    {
        var p = Plan("symmetric", 0.07);
        Assert.Equal(-0.07, p.Lower);
        Assert.Equal(-0.07, Plan("symmetric", 0.07, -0.07).Lower);
        Refused(() => Plan("symmetric", 0.07, -0.02));
        Refused(() => Plan("symmetric", -0.07));
    }

    [Fact]
    public void Fits_take_ISO_classes_hole_upper_case_shaft_lower_case()
    {
        var f = Plan("fit", hole: "H7", shaft: "g6");
        Assert.Equal(("H7", "g6"), (f.Hole, f.Shaft));
        Assert.Equal("JS9", Plan("fit_with_tolerance", hole: " JS9 ").Hole);
        Assert.Equal(DimensionTolerance.FitToleranceOnly, Plan("fit_tolerance_only", shaft: "js6").Code);
        Refused(() => Plan("fit"));
        Refused(() => Plan("fit", hole: "h7"));
        Refused(() => Plan("fit", shaft: "G6"));
        Refused(() => Plan("fit", hole: "H7", upper: 0.1));
    }

    [Fact]
    public void Types_without_values_take_none()
    {
        Assert.Equal(DimensionTolerance.None, Plan("none").Code);
        Assert.Equal(DimensionTolerance.Basic, Plan("basic").Code);
        Assert.Equal(DimensionTolerance.Max, Plan("max").Code);
        Refused(() => Plan("basic", 0.1));
        Refused(() => Plan("min", hole: "H7"));
        Refused(() => Plan("wobbly"));
    }

    [Fact]
    public void A_batch_names_each_dimension_once()
    {
        Refused(() => DimensionTolerance.PlanAll([]));
        Refused(() => DimensionTolerance.PlanAll([
            new ToleranceRequest { Name = "D1@Sketch1", Type = "basic" }, new ToleranceRequest { Name = "d1@sketch1", Type = "none" }]));
        Assert.Equal(2, DimensionTolerance.PlanAll([
            new ToleranceRequest { Name = "D1@Sketch1", Type = "basic" }, new ToleranceRequest { Name = "D2@Sketch1", Type = "none" }]).Count);
    }

    [Fact]
    public void Read_back_must_match_the_type_the_set_deviations_and_the_classes()
    {
        var p = Plan("bilateral", 0.1, -0.05);
        Assert.Null(DimensionTolerance.Mismatch(p, new ToleranceInfo { Type = "bilateral", Upper = 0.1, Lower = -0.05 }));
        Assert.Contains("type", DimensionTolerance.Mismatch(p, new ToleranceInfo { Type = "none" }));
        Assert.Contains("lower", DimensionTolerance.Mismatch(p, new ToleranceInfo { Type = "bilateral", Upper = 0.1, Lower = 0 }));
        // Symmetric compares the ± value only.
        Assert.Null(DimensionTolerance.Mismatch(Plan("symmetric", 0.07), new ToleranceInfo { Type = "symmetric", Upper = 0.07 }));
        // Fits: the classes exactly; the deviations SOLIDWORKS computes are not compared.
        var fit = Plan("fit_with_tolerance", hole: "H7");
        Assert.Null(DimensionTolerance.Mismatch(fit, new ToleranceInfo { Type = "fit_with_tolerance", Upper = 0.025, Lower = 0, Hole = "H7" }));
        Assert.Contains("hole class", DimensionTolerance.Mismatch(fit, new ToleranceInfo { Type = "fit_with_tolerance", Hole = "H8" }));
    }
}
