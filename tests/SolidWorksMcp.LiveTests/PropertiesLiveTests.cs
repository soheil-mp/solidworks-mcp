using System.Text;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>The properties toolset against a live SOLIDWORKS: materials, custom properties, equations, configurations, appearance.</summary>
public class PropertiesLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private const double BlockVolumeMm3 = 100 * 20 * 60; // 120 000 mm³ = 1.2e-4 m³

    private PropertiesService P => new(S);

    /// <summary>A 100 (x) × 20 (y) × 60 (z) block on the top plane; returns the document and its depth dimension (D1@Boss-Extrude1).</summary>
    private async Task<(string Doc, string Depth)> BlockAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 100, HeightMm = 60 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 20 });
        Assert.Equal(BlockVolumeMm3, r.Verified.VolumeDeltaMm3, 1e-3);
        // The depth is the 20 mm dimension of the extrude (D1@Boss-Extrude1).
        var depth = r.Dimensions!.Where(x => x.Unit == "mm" && Math.Abs(x.Value - 20) < 1e-9).OrderBy(x => x.Name, StringComparer.Ordinal).First();
        return (doc, depth.Name);
    }

    private static async Task<string> Code(Func<Task> call) => (await Assert.ThrowsAsync<SwException>(call)).Code;

    // ------------------------------------------------------------------ materials

    [Fact]
    public async Task Material_list_has_AISI_1020_at_7900_kg_per_m3()
    {
        var r = await P.MaterialListAsync("aisi 1020", 100, 0);
        var m = Assert.Single(r.Materials, x => x.Name == "AISI 1020" && x.Database == "SOLIDWORKS Materials");
        Assert.Equal(7900, m.DensityKgM3!.Value, 1e-6);
        Assert.Equal("Steel", m.Classification);
        Assert.Equal("SOLIDWORKS Materials", r.Databases[0].Name); // the standard database is listed first
        Assert.True(r.Databases[0].Materials > 100);

        var page = await P.MaterialListAsync(null, 5, 0);
        Assert.Equal(5, page.Materials.Count);
        Assert.True(page.Truncated);
        Assert.True(page.Total > 100, $"total {page.Total}");
        var next = await P.MaterialListAsync(null, 5, 5);
        Assert.Empty(next.Materials.Select(m => m.Name).Intersect(page.Materials.Select(m => m.Name)));

        var none = await P.MaterialListAsync("PLA-does-not-exist", 10, 0);
        Assert.Empty(none.Materials);
        Assert.Equal(0, none.Total);
        Assert.Contains(none.Warnings!, w => w.Contains("No material matches"));
    }

    [Fact]
    public async Task Material_set_AISI_1020_gives_the_analytic_mass()
    {
        var (doc, _) = await BlockAsync("material");
        var r = await P.MaterialSetAsync(doc, "AISI 1020");
        Assert.Equal("AISI 1020", r.Material);
        Assert.Null(r.Previous);
        Assert.Equal(7900, r.DatabaseDensityKgM3!.Value, 1e-6);
        Assert.Equal(7900, r.DensityKgM3!.Value, 0.5);
        Assert.Equal(7900 * 1.2e-4, r.MassKg!.Value, 1e-6); // 0.948 kg
        Assert.Contains(r.Checks, c => c.StartsWith("density"));
        Assert.Equal("AISI 1020", (await new InspectService(S).ModelInfoAsync(doc)).Material);

        // A case-insensitive name is accepted and reported in its real spelling; the previous material comes back.
        var alu = await P.MaterialSetAsync(doc, "6061 alloy");
        Assert.Equal("6061 Alloy", alu.Material);
        Assert.Equal("AISI 1020", alu.Previous);
        Assert.Equal(2700 * 1.2e-4, alu.MassKg!.Value, 1e-6); // 0.324 kg
    }

    [Fact]
    public async Task Material_set_rejects_unknown_names_with_suggestions()
    {
        var (doc, _) = await BlockAsync("material_bad");
        var pla = await Assert.ThrowsAsync<SwException>(() => P.MaterialSetAsync(doc, "PLA"));
        Assert.Equal(ErrorCodes.EntityNotFound, pla.Code);
        Assert.Contains("Closest:", pla.Message);
        var alu = await Assert.ThrowsAsync<SwException>(() => P.MaterialSetAsync(doc, "Aluminum 6061-T6"));
        Assert.Contains("'6061-T6 (SS)'", alu.Message);
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.MaterialSetAsync(doc, "AISI 1020", "No Such Database")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.MaterialSetAsync(doc, "  ")));
        Assert.Null((await new InspectService(S).ModelInfoAsync(doc)).Material); // nothing was applied
    }

    // ------------------------------------------------------------------ custom properties

    [Fact]
    public async Task Custom_properties_round_trip()
    {
        string doc = await NewPartAsync("props");
        var a = await P.PropSetAsync(doc, "SwmcpPartNo", "ACME-001");
        Assert.Null(a.Previous);
        Assert.Equal("ACME-001", a.Property.Value);
        Assert.Equal("text", a.Property.Type);
        var b = await P.PropSetAsync(doc, "SwmcpPartNo", "ACME-002");
        Assert.Equal("ACME-001", b.Previous!.Value);
        var n = await P.PropSetAsync(doc, "SwmcpCount", "12", null, "number");
        Assert.Contains(n.Property.Type, new[] { "number", "double" });
        var y = await P.PropSetAsync(doc, "SwmcpBought", "yes", null, "yesno");
        Assert.Equal("Yes", y.Property.Value);

        var list = await P.PropListAsync(doc);
        Assert.Equal("ACME-002", Assert.Single(list.Properties, p => p.Name == "SwmcpPartNo").Value);
        Assert.Contains(list.Properties, p => p.Name == "SwmcpCount");

        var del = await P.PropDeleteAsync(doc, "SwmcpPartNo");
        Assert.Equal("ACME-002", del.Deleted.Value);
        Assert.DoesNotContain((await P.PropListAsync(doc)).Properties, p => p.Name == "SwmcpPartNo");
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.PropDeleteAsync(doc, "SwmcpPartNo")));
    }

    [Fact]
    public async Task Decimal_number_property_reads_back()
    {
        // Probes the locale: the value is passed with a dot whatever the Windows decimal separator is.
        string doc = await NewPartAsync("props_decimal");
        var r = await P.PropSetAsync(doc, "SwmcpMass", "12.5", null, "number");
        Assert.True(PropertiesService.SameValue(3, r.Property.Value, "12.5"), r.Property.Value);
    }

    [Fact]
    public async Task Configuration_properties_are_separate_and_links_resolve()
    {
        var (doc, depth) = await BlockAsync("props_cfg");
        string cfg = (await P.ConfigListAsync(doc)).Active;
        var c = await P.PropSetAsync(doc, "SwmcpFinish", "Anodized", cfg);
        Assert.Equal(cfg, c.Configuration);
        Assert.Contains((await P.PropListAsync(doc, cfg)).Properties, p => p.Name == "SwmcpFinish");
        Assert.DoesNotContain((await P.PropListAsync(doc)).Properties, p => p.Name == "SwmcpFinish");

        var link = await P.PropSetAsync(doc, "SwmcpThickness", $"\"{depth}\"");
        Assert.StartsWith($"\"{depth}", link.Property.Value); // possibly qualified with the document name
        Assert.StartsWith("20", link.Property.Resolved); // "20.00" or "20,00" (Windows locale)
    }

    [Fact]
    public async Task Custom_property_arguments_are_validated()
    {
        string doc = await NewPartAsync("props_bad");
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.PropSetAsync(doc, "SwmcpX", "1", null, "color")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.PropSetAsync(doc, "SwmcpX", "abc", null, "number")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.PropSetAsync(doc, "SwmcpX", "maybe", null, "yesno")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.PropSetAsync(doc, " ", "1")));
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.PropSetAsync(doc, "SwmcpX", "1", "NoSuchConfiguration")));
        Assert.DoesNotContain((await P.PropListAsync(doc)).Properties, p => p.Name == "SwmcpX");
    }

    // ------------------------------------------------------------------ equations

    [Fact]
    public async Task A_global_variable_drives_the_extrude_depth()
    {
        var (doc, depth) = await BlockAsync("equations");
        var w = await P.EquationSetAsync(doc, "W", "60mm");
        Assert.True(w.Created);
        Assert.True(w.Equation.Global);
        Assert.Equal(60, w.Equation.Value!.Value, 1e-6); // document units (MMGS)
        Assert.Null(w.Warnings);

        var w2 = await P.EquationSetAsync(doc, "W", "80mm");
        Assert.False(w2.Created);
        Assert.Contains("60mm", w2.Previous!.Text);
        Assert.Equal(80, w2.Equation.Value!.Value, 1e-6);

        var d = await P.EquationSetAsync(doc, depth, "\"W\" / 2");
        Assert.Equal(40, d.Dimension!.Value, 1e-6);
        Assert.Equal(100 * 40 * 60, (await new InspectService(S).ModelInfoAsync(doc)).VolumeMm3!.Value, 1e-3);
        Assert.Equal(2, (await P.EquationListAsync(doc)).Equations.Count);

        // W is used by the depth equation, so it can't go first.
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.EquationDeleteAsync(doc, "W")));
        Assert.Equal(1, (await P.EquationDeleteAsync(doc, depth)).Remaining);
        Assert.Equal(0, (await P.EquationDeleteAsync(doc, "W")).Remaining);
        Assert.Empty((await P.EquationListAsync(doc)).Equations);
        // The dimension keeps its last value once its equation is gone.
        Assert.Equal(100 * 40 * 60, (await new InspectService(S).ModelInfoAsync(doc)).VolumeMm3!.Value, 1e-3);
    }

    [Fact]
    public async Task Equation_errors_are_typed_and_bare_numbers_warn()
    {
        var (doc, _) = await BlockAsync("equations_bad");
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.EquationSetAsync(doc, "D9@NoSuchSketch", "5mm")));
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.EquationDeleteAsync(doc, "Nope")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.EquationSetAsync(doc, "W", "\"W\" = 5mm")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.EquationSetAsync(doc, "", "5mm")));

        var h = await P.EquationSetAsync(doc, "H", "25");
        Assert.Contains(h.Warnings!, x => x.Contains("Bare number"));
        Assert.Equal(25, h.Equation.Value!.Value, 1e-6);

        // An unknown name is refused before anything changes (SOLIDWORKS could keep an equation that reads 0).
        var unknown = await Assert.ThrowsAsync<SwException>(() => P.EquationSetAsync(doc, "Q", "\"NoSuchVariable\" * 2"));
        Assert.Equal(ErrorCodes.EntityNotFound, unknown.Code);
        Assert.Contains("Global variables: H", unknown.Hint!);

        // Circular references, directly and through another equation.
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.EquationSetAsync(doc, "H", "\"H\" + 1mm")));
        var g = await P.EquationSetAsync(doc, "G", "\"H\" * 2");
        Assert.Equal(50, g.Equation.Value!.Value, 1e-6);
        var loop = await Assert.ThrowsAsync<SwException>(() => P.EquationSetAsync(doc, "H", "\"G\" / 2"));
        Assert.Equal(ErrorCodes.InvalidArgument, loop.Code);
        Assert.Contains("circular", loop.Message);

        var list = await P.EquationListAsync(doc);
        Assert.DoesNotContain(list.Equations, e => e.Lhs == "Q");
        Assert.Equal(25, Assert.Single(list.Equations, e => e.Lhs == "H").Value!.Value, 1e-6); // unchanged
    }

    // ------------------------------------------------------------------ configurations

    [Fact]
    public async Task Configurations_add_activate_and_delete()
    {
        string doc = await NewPartAsync("configs");
        var start = await P.ConfigListAsync(doc);
        string first = start.Active;
        int count = start.Configurations.Count;

        var a = await P.ConfigAddAsync(doc, "SwmcpLong");
        Assert.Equal("SwmcpLong", a.Active);
        Assert.Equal(first, a.Previous);
        var b = await P.ConfigAddAsync(doc, "SwmcpShort", activate: false);
        Assert.Equal("SwmcpLong", b.Active);
        Assert.Equal(count + 2, b.Configurations.Count);

        var c = await P.ConfigActivateAsync(doc, first);
        Assert.Equal(first, c.Active);
        Assert.Equal("SwmcpLong", c.Previous);
        Assert.Equal(first, (await P.ConfigListAsync(doc)).Active);

        var d = await P.ConfigDeleteAsync(doc, "swmcpshort"); // names match without case
        Assert.Equal("SwmcpShort", d.Configuration);
        Assert.DoesNotContain("SwmcpShort", d.Configurations);
        Assert.Equal(count + 1, d.Configurations.Count);
    }

    [Fact]
    public async Task Configuration_errors_are_typed()
    {
        string doc = await NewPartAsync("configs_bad");
        string active = (await P.ConfigListAsync(doc)).Active;
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.ConfigDeleteAsync(doc, active)));
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.ConfigActivateAsync(doc, "NoSuchConfiguration")));
        Assert.Equal(ErrorCodes.EntityNotFound, await Code(() => P.ConfigDeleteAsync(doc, "NoSuchConfiguration")));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.ConfigAddAsync(doc, active.ToUpperInvariant())));
        var again = await P.ConfigActivateAsync(doc, active); // already active: a no-op
        Assert.Equal(active, again.Active);
    }

    // ------------------------------------------------------------------ appearance

    [Fact]
    public async Task Appearance_colors_the_part_a_face_and_a_body()
    {
        var (doc, _) = await BlockAsync("appearance");
        var red = await P.AppearanceSetAsync(doc, [255, 0, 0]);
        Assert.Equal("part", red.Target);
        Assert.Equal([255, 0, 0], red.Appearance.ColorRgb);

        var glass = await P.AppearanceSetAsync(doc, [0, 0, 255], 0.5);
        Assert.Equal([255, 0, 0], glass.Previous!.ColorRgb);
        Assert.Equal(0.5, glass.Appearance.Transparency, 1e-3);

        var face = await P.AppearanceSetAsync(doc, [0, 255, 0], null, new EntityRef { At = [0, 20, 0] }); // the top face (y = 20)
        Assert.StartsWith("face", face.Target);
        Assert.Equal([0, 255, 0], face.Appearance.ColorRgb);

        var body = (await new InspectService(S).ModelInfoAsync(doc)).Bodies![0];
        var byId = await P.AppearanceSetAsync(doc, [255, 255, 0], null, new EntityRef { Id = body.Id });
        Assert.StartsWith("body", byId.Target);
        Assert.Equal([255, 255, 0], byId.Appearance.ColorRgb);
        var byName = await P.AppearanceSetAsync(doc, [0, 255, 255], null, new EntityRef { Name = body.Name }); // a body, not the feature
        Assert.StartsWith("body", byName.Target);
        Assert.Equal([255, 255, 0], byName.Previous!.ColorRgb);
    }

    [Fact]
    public async Task Appearance_arguments_are_validated()
    {
        var (doc, _) = await BlockAsync("appearance_bad");
        foreach (var bad in new[] { new[] { 300, 0, 0 }, new[] { 1, 2 }, new[] { -1, 0, 0 } })
            Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.AppearanceSetAsync(doc, bad)));
        Assert.Equal(ErrorCodes.InvalidArgument, await Code(() => P.AppearanceSetAsync(doc, [0, 0, 0], 1.5)));
        Assert.Equal(ErrorCodes.InvalidArgument,
            await Code(() => P.AppearanceSetAsync(doc, [0, 0, 0], null, new EntityRef { Type = "edge", At = [0, 20, 30] })));
        Assert.Equal(ErrorCodes.EntityNotFound,
            await Code(() => P.AppearanceSetAsync(doc, [0, 0, 0], null, new EntityRef { Name = "NoSuchBody" })));

        // 0-1 values are a likely mistake: applied as given (0-255), with a warning.
        var dark = await P.AppearanceSetAsync(doc, [1, 0, 0]);
        Assert.Equal([1, 0, 0], dark.Appearance.ColorRgb);
        Assert.Contains(dark.Warnings!, w => w.Contains("0-255"));
    }

    // ------------------------------------------------------------------ ownership

    [Fact]
    public async Task Foreign_documents_can_be_read_but_not_changed()
    {
        string title = await TestHooks.NewForeignPartAsync(S);
        try
        {
            Assert.Equal(ErrorCodes.NotOwned, await Code(() => P.PropSetAsync(title, "SwmcpX", "1")));
            Assert.Equal(ErrorCodes.NotOwned, await Code(() => P.MaterialSetAsync(title, "AISI 1020")));
            Assert.Equal(ErrorCodes.NotOwned, await Code(() => P.ConfigAddAsync(title, "SwmcpX")));
            Assert.Equal(ErrorCodes.NotOwned, await Code(() => P.AppearanceSetAsync(title, [255, 0, 0])));
            await P.PropListAsync(title);
            Assert.NotEmpty((await P.ConfigListAsync(title)).Configurations);
            Assert.Empty((await P.EquationListAsync(title)).Equations);
        }
        finally
        {
            await TestHooks.CloseRawAsync(S, title);
        }
    }
}

/// <summary>The interop-free helpers of the properties toolset (no SOLIDWORKS needed).</summary>
public class PropertiesOfflineTests
{
    [Theory]
    [InlineData("\"W\" = 60mm", "W")]
    [InlineData("\"D1@Sketch1\" = \"W\" / 2", "D1@Sketch1")]
    [InlineData("  \"D1@Boss-Extrude1\"= 20", "D1@Boss-Extrude1")]
    public void The_left_hand_side_is_the_first_quoted_name(string text, string lhs) => Assert.Equal(lhs, EquationText.Lhs(text));

    [Theory]
    [InlineData("60", new[] { "60" })]
    [InlineData("60mm", new string[0])]
    [InlineData("\"W\" / 2", new string[0])]
    [InlineData("2 * \"W\" + 5", new[] { "5" })]
    [InlineData("\"D1@Sketch1\" + 2.5 in", new string[0])]
    [InlineData("sin(30) * \"W\"", new[] { "30" })]
    [InlineData("1e-3m + .5mm - 30deg", new string[0])]
    [InlineData("iif(\"W\" > 50mm, 3, 2)", new[] { "3", "2" })]
    public void Bare_numbers_are_found_outside_names(string expression, string[] bare) =>
        Assert.Equal(bare, EquationText.BareNumbers(expression));

    [Fact]
    public void Left_hand_sides_are_normalized_and_checked()
    {
        Assert.Equal("W", EquationText.NormalizeLhs("\"W\""));
        Assert.Equal("D1@Sketch1", EquationText.NormalizeLhs("D1@Sketch1@Part1.Part"));
        Assert.True(EquationText.SameLhs("D1@Sketch1@Part1.Part", "d1@sketch1"));
        Assert.False(EquationText.SameLhs("W", "W2"));
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => EquationText.NormalizeLhs("W = 5")).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => EquationText.Expression("\"W\" = 5mm")).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => EquationText.Expression("\"W / 2")).Code);
        Assert.Equal("\"W\" = 60mm", EquationText.Build("W", "60mm"));
        Assert.True(EquationText.SameText("\"W\"=60MM", "\"W\" = 60mm"));
    }

    [Fact]
    public void Literals_and_references_parse()
    {
        Assert.Equal((60.0, "mm"), EquationText.Literal("60mm"));
        Assert.Equal((2.5, "in"), EquationText.Literal(" 2.5 in "));
        Assert.Equal((12.0, ""), EquationText.Literal("12"));
        Assert.Equal((-30.0, "deg"), EquationText.Literal("-30deg"));
        Assert.Null(EquationText.Literal("\"W\" / 2"));
        Assert.Equal(["W", "D2@Sketch1"], EquationText.References("\"D1@Sketch1\" = \"W\" / 2 + \"D2@Sketch1\""));
        Assert.Equal((0.0254, false), EquationText.UnitFactor("in"));
        Assert.Null(EquationText.UnitFactor("furlong"));
    }

    [Fact]
    public void Circular_references_are_found_through_other_equations()
    {
        (string, string)[] eqs = [("H", "\"H\" = \"W\" * 2"), ("D1@Sketch1", "\"D1@Sketch1\" = \"H\" + 5mm"), ("X", "\"X\" = 3mm")];
        Assert.Equal(["W", "W"], EquationText.Cycle("W", ["W"], eqs));
        Assert.Equal(["W", "H", "W"], EquationText.Cycle("W", ["h"], eqs));
        Assert.Equal(["W", "D1@Sketch1", "H", "W"], EquationText.Cycle("W", ["X", "D1@Sketch1"], eqs));
        Assert.Null(EquationText.Cycle("W", ["X"], eqs));
        Assert.Null(EquationText.Cycle("Y", ["H", "D1@Sketch1"], eqs)); // H uses W, which has no equation
        Assert.Null(EquationText.Cycle("W", [], eqs));
    }

    [Fact]
    public void Expression_names_skip_keywords_and_other_documents()
    {
        Assert.Equal(["W", "D2@Sketch1"],
            EquationText.ExpressionReferences("iif(\"W\" > 5mm, \"suppressed\", \"Unsuppressed\") + \"D2@Sketch1\""));
        Assert.Empty(EquationText.ExpressionReferences("60mm"));
        Assert.True(EquationText.IsExternal("D1@Sketch1@bracket-1.Part"));
        Assert.True(EquationText.IsExternal("W@Part1<1>.Part"));
        Assert.True(EquationText.IsExternal("SW-Mass@Part1.SLDPRT"));
        Assert.False(EquationText.IsExternal("D1@Sketch1"));
        Assert.False(EquationText.IsExternal("W"));
    }

    [Fact]
    public void Property_values_are_validated_per_type()
    {
        Assert.Equal((30, "x"), PropertiesService.PropValue("text", "x"));
        Assert.Equal((3, "12.5"), PropertiesService.PropValue("number", " 12.5 "));
        Assert.Equal((11, "Yes"), PropertiesService.PropValue("yesno", "true"));
        Assert.Equal((64, "2026-10-22"), PropertiesService.PropValue("date", "2026-10-22"));
        Assert.Throws<SwException>(() => PropertiesService.PropValue("number", "12,5"));
        Assert.Throws<SwException>(() => PropertiesService.PropValue("color", "red"));
        Assert.True(PropertiesService.SameValue(3, "12.50", "12.5"));
        Assert.True(PropertiesService.SameValue(11, "YES", "Yes"));
        Assert.False(PropertiesService.SameValue(30, "a", "b"));
        Assert.True(PropertiesService.SameValue(30, "\"D1@Boss-Extrude1@Part1.SLDPRT\"", "\"D1@Boss-Extrude1\""));
        Assert.False(PropertiesService.SameValue(30, "\"D1@Boss-Extrude2@Part1.SLDPRT\"", "\"D1@Boss-Extrude1\""));
        Assert.Equal("yesno", PropertiesService.TypeName(11));
    }

    [Fact]
    public void Databases_load_from_folders_with_the_standard_one_first()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"swmcp_mat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            static string Db(string cls, string name, string dens) =>
                "<?xml version=\"1.0\" encoding=\"utf-16\"?><mstns:materials xmlns:mstns=\"http://www.solidworks.com/sldmaterials\">" +
                $"<classification name=\"{cls}\"><material name=\"{name}\"><physicalproperties><DENS value=\"{dens}\" />" +
                "</physicalproperties></material></classification></mstns:materials>";
            File.WriteAllText(Path.Combine(dir, "a custom.sldmat"), Db("Custom", "My Alloy", "2800"), Encoding.Unicode);
            File.WriteAllText(Path.Combine(dir, "solidworks materials.sldmat"), Db("Steel", "Test Steel", "7850"), Encoding.Unicode);
            File.WriteAllText(Path.Combine(dir, "broken.sldmat"), "<not xml", Encoding.Unicode);
            var cat = MaterialCatalog.Load($"{dir};{dir}\\", allowNetwork: false); // the same folder twice is read once
            Assert.Equal(["SOLIDWORKS Materials", "a custom"], cat.Databases.Select(d => d.Name));
            Assert.Equal(["Test Steel", "My Alloy"], cat.Materials.Select(m => m.Name));
            Assert.Contains(cat.Warnings, w => w.Contains("broken.sldmat"));
            Assert.Equal(2800, cat.Materials[1].DensityKgM3);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Material_databases_parse_classification_name_and_density()
    {
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?>" +
            "<mstns:materials xmlns:mstns=\"http://www.solidworks.com/sldmaterials\" version=\"2008.03\">" +
            "<curves id=\"curve0\"><point x=\"1.0\" y=\"1.0\" /></curves>" +
            "<classification name=\"Metals\"><material name=\"Test Steel\" matid=\"1\"><shaders /><physicalproperties>" +
            "<EX displayname=\"Elastic modulus\" value=\"2.1E+11\" /><DENS displayname=\"Mass density\" value=\"0.79E+04\" />" +
            "</physicalproperties><custom><DENS value=\"1\" /></custom></material>" +
            "<material name=\"No Density\" /></classification>" +
            "<classification name=\"Plastics\"><material name=\"Test Plastic\"><physicalproperties><DENS value=\"1020\" />" +
            "</physicalproperties></material></classification></mstns:materials>";
        using var ms = new MemoryStream([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(xml)]);
        var rows = MaterialCatalog.Parse(ms, "Test DB");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new MaterialCatalog.Entry("Test Steel", "Metals", "Test DB", 7900), rows[0]);
        Assert.Equal(new MaterialCatalog.Entry("No Density", "Metals", "Test DB", null), rows[1]);
        Assert.Equal(new MaterialCatalog.Entry("Test Plastic", "Plastics", "Test DB", 1020), rows[2]);
    }

    [Fact]
    public void Missing_or_network_folders_are_reported_not_thrown()
    {
        var cat = MaterialCatalog.Load(@"\\server\share\materials;C:\swmcp-no-such-folder", allowNetwork: false);
        Assert.Empty(cat.Materials);
        Assert.Contains(cat.Warnings, w => w.Contains("network folder"));
        Assert.Contains(cat.Warnings, w => w.Contains("No material database"));
        Assert.Equal("SOLIDWORKS Materials", MaterialCatalog.ApiName("solidworks materials"));
        Assert.True(MaterialCatalog.SameDatabase(@"C:\x\solidworks materials.sldmat", "SOLIDWORKS Materials"));
    }

    [Fact]
    public void Suggestions_rank_shared_words_first()
    {
        string[] names = ["6061 Alloy", "6061-T6 (SS)", "7075-T6, Plate (SS)", "AISI 1020", "AISI 1020 Steel, Cold Rolled", "ABS", "ABS PC",
            "Plain Carbon Steel"];
        Assert.Equal("6061-T6 (SS)", MaterialCatalog.Suggest(names, "Aluminum 6061-T6")[0]);
        Assert.Equal("AISI 1020", MaterialCatalog.Suggest(names, "AISI1020")[0]);
        Assert.Equal("ABS", MaterialCatalog.Suggest(names, "abs plastic")[0]);
        Assert.InRange(MaterialCatalog.Suggest(names, "PLA").Count, 1, 5);
        Assert.Empty(MaterialCatalog.Suggest([], "PLA"));
    }

    [Fact]
    public void The_installed_SOLIDWORKS_database_parses()
    {
        string? dir = InstalledMaterialFolder();
        Assert.SkipWhen(dir is null, "No SOLIDWORKS material folder (lang\\english\\sldmaterials) on this machine.");
        var cat = MaterialCatalog.Load(dir!, allowNetwork: false);
        var aisi = Assert.Single(cat.Materials, m => m.Name == "AISI 1020" && m.Database == MaterialCatalog.DefaultDatabase);
        Assert.Equal(7900, aisi.DensityKgM3);
        Assert.Equal("Steel", aisi.Classification);
        Assert.DoesNotContain(cat.Materials, m => m.Name == "PLA");
        Assert.Contains("6061-T6 (SS)", MaterialCatalog.Suggest(cat.Materials.Select(m => m.Name), "Aluminum 6061-T6"));
        Assert.All(cat.Materials.Where(m => m.Database == MaterialCatalog.DefaultDatabase), m => Assert.NotNull(m.DensityKgM3));
    }

    private static string? InstalledMaterialFolder()
    {
        foreach (var version in new[] { "2026", "2025", "2024", "2023" })
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SOFTWARE\SolidWorks\SOLIDWORKS {version}\Setup");
            if (key?.GetValue("SolidWorks Folder") is string root && Path.Combine(root, "lang", "english", "sldmaterials") is var dir &&
                Directory.Exists(dir))
                return dir;
        }
        return null;
    }
}
