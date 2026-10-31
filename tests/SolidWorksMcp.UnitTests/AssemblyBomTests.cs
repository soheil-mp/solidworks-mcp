using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>How asm_bom groups component instances into BOM lines and writes them as CSV.</summary>
public class AssemblyBomTests
{
    private static BomNode Part(string file, string config = "Default") => new(@"C:\p\" + file, config, false, []);

    private static BomNode Sub(string file, params BomNode[] children) => new(@"C:\p\" + file, "Default", true, children);

    // A frame: 2 bolts at the top, a hinge subassembly twice (a leaf and 2 pins each), a plate, and 2 more bolts.
    private static readonly BomNode[] Top =
    [
        Part("bolt.sldprt"), Part("bolt.sldprt"),
        Sub("hinge.sldasm", Part("leaf.sldprt"), Part("pin.sldprt"), Part("pin.sldprt")),
        Part("plate.sldprt"),
        Sub("hinge.sldasm", Part("leaf.sldprt"), Part("pin.sldprt"), Part("pin.sldprt")),
        Part("bolt.sldprt"), Part("bolt.sldprt"),
    ];

    [Fact]
    public void Top_level_groups_by_file_and_configuration_in_first_seen_order()
    {
        var lines = AssemblyBom.Top(Top);
        Assert.Equal(["bolt", "hinge", "plate"], lines.Select(l => Path.GetFileNameWithoutExtension(l.Path)));
        Assert.Equal([4, 2, 1], lines.Select(l => l.Quantity));
        Assert.Equal(["1", "2", "3"], lines.Select(l => l.Item));
        Assert.True(lines[1].IsAssembly);
        Assert.Equal(7, AssemblyBom.Instances(Top, "top"));
        // Another configuration is another line.
        var two = AssemblyBom.Top([Part("bolt.sldprt", "M6"), Part("bolt.sldprt", "M8"), Part("BOLT.SLDPRT", "M6")]);
        Assert.Equal([2, 1], two.Select(l => l.Quantity));
    }

    [Fact]
    public void Parts_only_multiplies_through_subassemblies()
    {
        var lines = AssemblyBom.Parts(Top);
        Assert.Equal(["bolt", "leaf", "pin", "plate"], lines.Select(l => Path.GetFileNameWithoutExtension(l.Path)));
        Assert.Equal([4, 2, 4, 1], lines.Select(l => l.Quantity));
        Assert.Equal(11, AssemblyBom.Instances(Top, "parts"));
        Assert.Equal(lines.Sum(l => l.Quantity), AssemblyBom.Instances(Top, "parts"));
    }

    [Fact]
    public void Indented_lists_a_subassemblys_contents_once_under_it()
    {
        var lines = AssemblyBom.Indented(Top);
        Assert.Equal(["1", "2", "2.1", "2.2", "3"], lines.Select(l => l.Item));
        Assert.Equal([1, 1, 2, 2, 1], lines.Select(l => l.Level));
        Assert.Equal([4, 2, 1, 2, 1], lines.Select(l => l.Quantity)); // per parent
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_line_breaks_and_counts_its_rows()
    {
        BomRow Row(string item, string desc) => new() { Item = item, PartNumber = "P-1", Description = desc, Quantity = 2, MassKg = 0.125, Kind = "part", File = @"C:\p\a.sldprt" };
        string csv = AssemblyBom.Csv([Row("1", "Plate, 6 mm"), Row("2", "Say \"hi\""), Row("3", "two\nlines")]);
        Assert.StartsWith("Item,Level,Part number,Description,Material,Quantity,Mass (kg),Kind,File,Configuration\r\n", csv);
        Assert.Contains("\"Plate, 6 mm\"", csv);
        Assert.Contains("\"Say \"\"hi\"\"\"", csv);
        Assert.Contains(",0.125,part,a.sldprt,", csv);
        Assert.Equal(3, AssemblyBom.CsvRows(csv));
    }
}
