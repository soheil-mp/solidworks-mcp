using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>weldment_member's names (WeldmentSpec): profile sizes and types matched loosely, corner treatments.</summary>
public class WeldmentSpecTests
{
    [Fact]
    public void Sizes_match_whatever_the_spacing_and_case()
    {
        string[] sizes = ["20 x 20 x 2", "40 x 40 x 4", "50 X 30 X 2.6"];
        Assert.Equal("40 x 40 x 4", WeldmentSpec.MatchSize(sizes, "40x40x4"));
        Assert.Equal("40 x 40 x 4", WeldmentSpec.MatchSize(sizes, " 40 X 40 x 4 "));
        Assert.Equal("50 X 30 X 2.6", WeldmentSpec.MatchSize(sizes, "50×30×2.6"));
        Assert.Null(WeldmentSpec.MatchSize(sizes, "40 x 40 x 5"));
    }

    [Fact]
    public void Names_match_without_case_spaces_or_separators()
    {
        string[] types = ["square tube", "rectangular tube", "angle iron"];
        Assert.Equal("square tube", WeldmentSpec.MatchName(types, "Square_Tube"));
        Assert.Equal("angle iron", WeldmentSpec.MatchName(types, "angle-iron"));
        Assert.Null(WeldmentSpec.MatchName(types, "tube"));
    }

    [Fact]
    public void Corners_map_to_the_end_condition_codes()
    {
        Assert.Equal((1, "miter"), WeldmentSpec.Corner(null));
        Assert.Equal((1, "miter"), WeldmentSpec.Corner("Mitre"));
        Assert.Equal((2, "butt1"), WeldmentSpec.Corner("butt"));
        Assert.Equal((3, "butt2"), WeldmentSpec.Corner("butt-2"));
        Assert.Equal((0, "none"), WeldmentSpec.Corner("none"));
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => WeldmentSpec.Corner("round")).Code);
    }

    [Fact]
    public void Few_lists_a_few_names_and_says_how_many_in_all()
    {
        Assert.Equal("a, b", WeldmentSpec.Few(["a", "b"]));
        Assert.Equal("1, 2, … (5 in all)", WeldmentSpec.Few(["1", "2", "3", "4", "5"], 2));
    }
}
