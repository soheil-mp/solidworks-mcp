using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>drw_annotation_add's names and codes (DrwAnnotationSpec) and its default placement (DrawingLayout.AnnotationSpot).</summary>
public class DrawingAnnotationSpecTests
{
    [Theory]
    [InlineData("position", "<IGTOL-POSI>", false)]
    [InlineData("true position", "<IGTOL-POSI>", false)]
    [InlineData("Flatness", "<IGTOL-FLAT>", false)]
    [InlineData("perpendicular", "<IGTOL-PERP>", true)]
    [InlineData("total-runout", "<IGTOL-TRUN>", true)]
    [InlineData("profile", "<IGTOL-SPROF>", false)]
    public void Characteristics_map_to_the_iso_gtol_symbols(string name, string symbol, bool needsDatum)
    {
        var c = DrwAnnotationSpec.Gtol(name);
        Assert.Equal(symbol, DrwAnnotationSpec.Symbol(c));
        Assert.Equal(needsDatum, c.NeedsDatum);
    }

    [Fact]
    public void Weld_types_map_to_the_ISO_symbols_of_gtol_sym()
    {
        Assert.Equal("weld", DrwAnnotationSpec.Kind("weld symbol"));
        Assert.Equal(("FILL", "fillet"), DrwAnnotationSpec.Weld(null));
        Assert.Equal("BUSV", DrwAnnotationSpec.Weld("V").Code);
        Assert.Equal("BUSQ", DrwAnnotationSpec.Weld("square-butt").Code);
        Assert.Equal("BUTT", DrwAnnotationSpec.Weld("flanged").Code);
        Assert.Equal("PLUG", DrwAnnotationSpec.Weld("slot").Code);
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Weld("glue"));
        Assert.Equal("arrow", DrwAnnotationSpec.WeldSide(null));
        Assert.Equal("both", DrwAnnotationSpec.WeldSide("Both sides"));
        Assert.Throws<SwException>(() => DrwAnnotationSpec.WeldSide("left"));
    }

    [Fact]
    public void Bad_names_values_and_labels_are_refused()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => DrwAnnotationSpec.Gtol("wobble")).Code);
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Gtol(null));
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Kind("glue"));
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Material("max"));
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Tolerance("-0.1", "tolerance"));
        Assert.Throws<SwException>(() => DrwAnnotationSpec.Tolerance("abc", "tolerance"));
        foreach (var bad in new[] { "I", "O", "Q", "ABC", "1", "" })
            Assert.Throws<SwException>(() => DrwAnnotationSpec.Label(bad, "label"));
    }

    [Fact]
    public void Values_come_back_the_way_solidworks_shows_them()
    {
        Assert.Equal("0.05", DrwAnnotationSpec.Tolerance(" 0.050 ", "t"));
        Assert.Equal("AB", DrwAnnotationSpec.Label("ab", "l"));
        Assert.Equal("<MOD-MMC>", DrwAnnotationSpec.Material("MMC"));
        Assert.Equal("", DrwAnnotationSpec.Material(null));
        Assert.Equal((1, "machining_required"), DrwAnnotationSpec.Finish("machined"));
        Assert.Equal("gtol", DrwAnnotationSpec.Kind("feature control frame"));
        var pos = DrwAnnotationSpec.Gtol("position");
        Assert.Equal("position Ø0.1 (MMC) | A | B", DrwAnnotationSpec.FrameText(pos, "0.1", true, "<MOD-MMC>", ["A", "B"]));
        Assert.Equal("flatness 0.05", DrwAnnotationSpec.FrameText(DrwAnnotationSpec.Gtol("flatness"), "0.05", false, "", []));
    }

    [Fact]
    public void Table_cells_columns_and_revision_letters()
    {
        Assert.Equal(50, DrwAnnotationSpec.Number("50"));
        Assert.Equal(-12.5, DrwAnnotationSpec.Number(" -12.50 "));
        Assert.Equal(20, DrwAnnotationSpec.Number("Ø20 THRU"));
        Assert.Equal(6.6, DrwAnnotationSpec.Number("⌀6,6"));
        Assert.Null(DrwAnnotationSpec.Number("THRU ALL"));
        string[] titles = ["TAG", "X  LOC", "Y LOC", "SIZE"];
        Assert.Equal(1, DrwAnnotationSpec.Column(titles, "X LOC"));
        Assert.Equal(3, DrwAnnotationSpec.Column(titles, "DIA", "SIZE"));
        Assert.Equal(-1, DrwAnnotationSpec.Column(titles, "DESCRIPTION"));
        Assert.Equal("A", DrwAnnotationSpec.RevisionLetter(0));
        Assert.Equal("H", DrwAnnotationSpec.RevisionLetter(7));
        Assert.Equal("J", DrwAnnotationSpec.RevisionLetter(8)); // I is skipped
        Assert.Equal("Z", DrwAnnotationSpec.RevisionLetter(22));
        Assert.Equal("AA", DrwAnnotationSpec.RevisionLetter(23));
        Assert.Equal("AB", DrwAnnotationSpec.RevisionLetter(24));
    }

    [Fact]
    public void Default_spot_lies_outside_the_view_away_from_its_centre_and_clear_of_others()
    {
        var view = new SheetRect(165, 165.7, 265, 225.7); // a 100 x 60 front view
        // The right edge's midpoint: straight out to the right, 8 mm clear of the view.
        var right = DrawingLayout.AnnotationSpot(view, (265, 195.7));
        Assert.True(right.X >= 273 && Math.Abs(right.Y - 195.7) < 1e-9, $"{right}");
        // The top edge: above the view.
        var top = DrawingLayout.AnnotationSpot(view, (215, 225.7));
        Assert.True(top.Y >= 233.7 && Math.Abs(top.X - 215) < 1e-9, $"{top}");
        // A spot already taken moves down 12 mm.
        var moved = DrawingLayout.AnnotationSpot(view, (265, 195.7), [right]);
        Assert.Equal(right.X, moved.X, 9);
        Assert.Equal(right.Y - 12, moved.Y, 9);
        // Without an attachment: above the view's top-left corner.
        Assert.Equal((165, 237.7), DrawingLayout.AnnotationSpot(view, null));
    }
}
