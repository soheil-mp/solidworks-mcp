using System.Text;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>material_add's library (MaterialXml): SOLIDWORKS' .sldmat layout in SI, marked as this server's, checked inputs.</summary>
public class MaterialXmlTests
{
    private static MaterialDef Steel(string name = "Test Steel") =>
        MaterialXml.Check(name, "Steel", 7850, 210, 0.3, null, 355, 510, 50, 460, 1.2e-5);

    [Fact]
    public void A_material_is_written_in_si_and_reads_back_with_the_shear_modulus_from_e_and_nu()
    {
        var m = Steel();
        Assert.Equal(210e9 / (2 * 1.3), m.ShearModulusPa, 1e-3);
        var doc = MaterialXml.NewLibrary();
        Assert.True(MaterialXml.IsOurs(doc));
        Assert.False(MaterialXml.Upsert(doc, m, overwrite: false));
        // Through bytes and back, as the file will be.
        var again = MaterialXml.Parse(Encoding.Unicode.GetString(MaterialXml.Bytes(doc)).TrimStart('﻿'));
        var props = MaterialXml.Read(again, "Test Steel")!;
        Assert.Equal(MaterialXml.Expected(m), props);
        Assert.Equal(210e9, props["EX"]);
        Assert.Equal(7850, props["DENS"]);
        Assert.Equal(355e6, props["SIGYLD"]);
        Assert.Contains("<classification name=\"Steel\">", again.ToString());
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>", Encoding.Unicode.GetString(MaterialXml.Bytes(doc)).TrimStart('﻿'));
    }

    [Fact]
    public void A_name_is_replaced_only_with_overwrite_and_an_emptied_classification_goes()
    {
        var doc = MaterialXml.NewLibrary();
        MaterialXml.Upsert(doc, Steel(), false);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => MaterialXml.Upsert(doc, Steel(), false)).Code);
        var softer = MaterialXml.Check("Test Steel", "Plastics", 1200, 2.5, 0.38, null, null, null, null, null, null);
        Assert.True(MaterialXml.Upsert(doc, softer, overwrite: true));
        Assert.Equal(2.5e9, MaterialXml.Read(doc, "Test Steel")!["EX"]);
        Assert.Single(doc.Root!.Elements("classification")); // the empty Steel classification was removed
        Assert.False(MaterialXml.Read(doc, "Test Steel")!.ContainsKey("SIGYLD"));
    }

    [Theory]
    [InlineData(null, 7850.0, 210.0, 0.3)]      // no name
    [InlineData("A<b", 7850.0, 210.0, 0.3)]     // XML markup in the name
    [InlineData("Steel", 0.0, 210.0, 0.3)]      // no density
    [InlineData("Steel", 7850.0, -1.0, 0.3)]    // negative modulus
    [InlineData("Steel", 7850.0, 210.0, 0.5)]   // incompressible
    public void Bad_materials_are_refused(string? name, double density, double e, double nu) =>
        Assert.Equal(ErrorCodes.InvalidArgument,
            Assert.Throws<SwException>(() => MaterialXml.Check(name, null, density, e, nu, null, null, null, null, null, null)).Code);

    [Fact]
    public void Yield_above_tensile_and_libraries_without_the_marker_are_told_apart()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => MaterialXml.Check("x", null, 1, 1, 0.3, null, 500, 400, null, null, null)).Code);
        Assert.False(MaterialXml.IsOurs(MaterialXml.Parse("<?xml version=\"1.0\"?><materials><classification name=\"Steel\"/></materials>")));
    }
}
