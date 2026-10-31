using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Core.Win32;
using Xunit;

namespace SolidWorksMcp.UnitTests;

/// <summary>sim_static's input validation, mesh guard, force bookkeeping and result interpretation (SimulationMath).</summary>
public class SimulationMathTests
{
    private static readonly EntityRef[] Face = [new EntityRef { At = [50, 10, 5] }];

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Code(Action a) => Assert.Throws<SwException>(a).Code;

    private static void Near(double[] expected, double[] actual, double tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], tol);
    }

    // ------------------------------------------------------------------ inputs

    [Fact]
    public void Pressure_is_converted_to_pascal_and_acts_normal()
    {
        var p = SimulationMath.ParseLoad(new SimLoad { Kind = "pressure", Faces = Face, ValueMpa = 0.1 }, 0);
        Assert.Equal(SimLoadKind.Pressure, p.Kind);
        Assert.Equal(1e5, p.Value, 9);
        Assert.Null(p.Direction);
        Assert.Equal("loads[0]", p.Arg);

        var normal = SimulationMath.ParseLoad(new SimLoad { Faces = Face, ValueMpa = 2, Direction = Json("\"normal\"") }, 3);
        Assert.Equal(SimLoadKind.Pressure, normal.Kind); // kind inferred from valueMpa
        Assert.Equal(2e6, normal.Value, 6);
        Assert.Equal("loads[3]", normal.Arg);
    }

    [Fact]
    public void Force_takes_newtons_and_a_normal_or_model_direction()
    {
        var n = SimulationMath.ParseLoad(new SimLoad { Faces = Face, ValueN = 100 }, 0);
        Assert.Equal(SimLoadKind.Force, n.Kind); // kind inferred from valueN
        Assert.Equal(100, n.Value);
        Assert.Null(n.Direction);

        var v = SimulationMath.ParseLoad(new SimLoad { Kind = "Force", Faces = Face, ValueN = 5, Direction = Json("[0,-2,0]") }, 0);
        Assert.Equal(new Vec3(0, -1, 0), v.Direction);
        var axis = SimulationMath.ParseLoad(new SimLoad { Kind = "force", Faces = Face, ValueN = 5, Direction = Json("\"-y\"") }, 0);
        Assert.Equal(new Vec3(0, -1, 0), axis.Direction);
        var diag = SimulationMath.ParseLoad(new SimLoad { Kind = "force", Faces = Face, ValueN = 5, Direction = Json("\"1,1,0\"") }, 0);
        Assert.Equal(Math.Sqrt(0.5), diag.Direction!.Value.X, 12);
        Assert.Equal(Math.Sqrt(0.5), diag.Direction!.Value.Y, 12);
    }

    [Theory]
    [InlineData("{\"kind\":\"pressure\",\"valueMpa\":0.1}")]                            // no faces
    [InlineData("{\"kind\":\"pressure\",\"faces\":[],\"valueMpa\":0.1}")]               // empty faces
    [InlineData("{\"faces\":[{\"at\":[0,0,0]}]}")]                                     // no value, no kind
    [InlineData("{\"faces\":[{\"at\":[0,0,0]}],\"valueMpa\":1,\"valueN\":1}")]          // both values, no kind
    [InlineData("{\"kind\":\"pressure\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":10}")] // pressure with newtons
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueMpa\":1}")]   // force with MPa
    [InlineData("{\"kind\":\"pressure\",\"faces\":[{\"at\":[0,0,0]}],\"valueMpa\":0}")]
    [InlineData("{\"kind\":\"pressure\",\"faces\":[{\"at\":[0,0,0]}],\"valueMpa\":-1}")]
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}]}")]
    [InlineData("{\"kind\":\"moment\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1}")]
    [InlineData("{\"kind\":\"pressure\",\"faces\":[{\"at\":[0,0,0]}],\"valueMpa\":1,\"direction\":[0,1,0]}")] // pressure is always normal
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1,\"direction\":[0,0,0]}")]
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1,\"direction\":[1,2]}")]
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1,\"direction\":[\"a\",\"b\",\"c\"]}")]
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1,\"direction\":true}")]
    [InlineData("{\"kind\":\"force\",\"faces\":[{\"at\":[0,0,0]}],\"valueN\":1,\"direction\":\"up\"}")]
    public void Bad_loads_are_invalid_arguments(string json)
    {
        var load = JsonSerializer.Deserialize<SimLoad>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.ParseLoad(load, 0)));
    }

    [Fact]
    public void A_study_needs_one_to_twenty_loads()
    {
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.ParseLoads(null)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.ParseLoads([])));
        var many = Enumerable.Range(0, SimulationMath.MaxLoads + 1).Select(_ => new SimLoad { Faces = Face, ValueN = 1 }).ToList();
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.ParseLoads(many)));
        Assert.Equal(2, SimulationMath.ParseLoads([new SimLoad { Faces = Face, ValueN = 1 }, new SimLoad { Faces = Face, ValueMpa = 1 }]).Count);
    }

    [Fact]
    public void Study_names_are_checked_or_generated()
    {
        string a = SimulationMath.StudyName(null), b = SimulationMath.StudyName("  ");
        Assert.Matches("^MCP Static [0-9a-f]{4}$", a);
        Assert.Matches("^MCP Static [0-9a-f]{4}$", b);
        Assert.Equal("Bracket load 1", SimulationMath.StudyName("  Bracket load 1 "));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.StudyName(new string('s', 65))));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.StudyName("a/b")));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.StudyName("a\"b")));
    }

    [Fact]
    public void The_fixture_bypass_is_off_by_default() => Assert.False(new SimStaticRequest().AllowNoFixtures);

    // ------------------------------------------------------------------ mesh

    [Fact]
    public void Mesh_guard_refuses_absurd_element_sizes_and_suggests_one()
    {
        const double beam = 1e-5; // 100 x 10 x 10 mm = 10 000 mm³
        Assert.Equal(6 * Math.Sqrt(2) * 10000 / 125, SimulationMath.EstimatedElements(beam, 5), 6); // ≈ 679
        SimulationMath.CheckMeshSize(null, beam);
        SimulationMath.CheckMeshSize(1, beam); // ≈ 85 000 elements: fine
        var ex = Assert.Throws<SwException>(() => SimulationMath.CheckMeshSize(0.1, beam)); // ≈ 85 million
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("million elements", ex.Message);
        Assert.Contains("mm", ex.Hint);
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.CheckMeshSize(-1, beam)));
        Assert.Equal(ErrorCodes.InvalidArgument, Code(() => SimulationMath.CheckMeshSize(double.NaN, beam)));
        // The suggested size is the inverse of the estimate.
        double s = SimulationMath.SizeFor(beam, SimulationMath.SuggestedElements);
        Assert.Equal(SimulationMath.SuggestedElements, SimulationMath.EstimatedElements(beam, s), 3);
    }

    [Fact]
    public void Tolerance_follows_the_default_ratio()
    {
        Assert.Equal(0.1, SimulationMath.Tolerance(2, 5, 0.25), 12);  // 5 % like the default pair
        Assert.Equal(0.2, SimulationMath.Tolerance(2, 5, 0.5), 12);   // 10 %
        Assert.Equal(0.1, SimulationMath.Tolerance(2, 0, 0), 12);     // unknown defaults: 5 %
        Assert.Equal(0.1, SimulationMath.Tolerance(2, 5, 7), 12);     // nonsense defaults: 5 %
    }

    // ------------------------------------------------------------------ forces

    [Fact]
    public void Force_components_are_taken_along_the_reference_axes()
    {
        var f = new Vec3(0, -100, 0);
        Assert.Equal([0, -100, 0], SimulationMath.Components(f, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ));
        // Top Plane axes: x = +X, y = -Z, normal = +Y.
        Assert.Equal([0, 0, -100], SimulationMath.Components(f, Vec3.UnitX, new Vec3(0, 0, -1), Vec3.UnitY));
    }

    [Fact]
    public void AddForce3_components_use_only_the_non_zero_axes_and_keep_every_element_non_zero()
    {
        var (use, ucode, comps) = SimulationMath.ForceComps([0, -100, 0], 100);
        Assert.Equal([false, true, false], use);
        Assert.Equal(2, ucode); // Fy only
        Assert.Equal([1, -100, 1], comps);

        (use, ucode, comps) = SimulationMath.ForceComps([30, 1e-12, -40], 50);
        Assert.Equal([true, false, true], use); // float noise is not a component
        Assert.Equal(1 | 4, ucode);
        Assert.Equal([30, 1, -40], comps);
    }

    [Fact]
    public void Load_resultants_follow_the_face_normals()
    {
        // The cantilever's top face: 100 x 10 mm, outward normal +y; 0.1 MPa pushes it down with 100 N.
        var top = new SimFace(Vec3.UnitY, 1e-3);
        Near([0, -100, 0], SimulationMath.PressureResultant(1e5, [top]).ToArray(), 1e-9);
        // Its free end (10 x 10 mm, normal +x): a 1000 N normal force pushes toward -x.
        var end = new SimFace(Vec3.UnitX, 1e-4);
        Near([-1000, 0, 0], SimulationMath.NormalForceResultant(1000, [end]).ToArray(), 1e-9);
        // A normal force over two faces is spread by area: 300 N on 1 + 2 units of area along -y and -x.
        var a = new SimFace(Vec3.UnitY, 1e-4);
        var b = new SimFace(Vec3.UnitX * 3, 2e-4); // normals are normalized
        Near([-200, -100, 0], SimulationMath.NormalForceResultant(300, [a, b]).ToArray(), 1e-9);
        // Opposite faces cancel.
        Near([0, 0, 0], SimulationMath.PressureResultant(1e5, [top, new SimFace(-Vec3.UnitY, 1e-3)]).ToArray(), 1e-9);
        Assert.Equal(Vec3.Zero, SimulationMath.NormalForceResultant(10, []));
    }

    [Fact]
    public void Reactions_come_from_the_summary_and_must_cancel_the_loads()
    {
        double[] summary = [1, 2, 3, 4, 0, 0, 0, 0, 0, 100.2, 0, 100.2, 0, 0, 0, 0];
        Assert.Equal(new Vec3(0, 100.2, 0), SimulationMath.ReactionTotal(summary));
        double[] onlySelection = [0, 99, 0, 99, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(new Vec3(0, 99, 0), SimulationMath.ReactionTotal(onlySelection));
        Assert.Equal(new Vec3(5, 6, 7), SimulationMath.ReactionTotal([5, 6, 7, 10]));
        Assert.Null(SimulationMath.ReactionTotal(null));
        Assert.Null(SimulationMath.ReactionTotal([1, 2]));
        Assert.Null(SimulationMath.ReactionTotal([double.NaN, 0, 0, 0]));

        var load = new Vec3(0, -100, 0);
        var opposite = SimulationMath.Balance(new Vec3(0.1, 100.5, 0), load);
        Assert.True(opposite.Balanced);
        Assert.False(opposite.LoadSign);
        var same = SimulationMath.Balance(new Vec3(0, -99.5, 0), load); // reported as the force on the fixture
        Assert.True(same.Balanced);
        Assert.True(same.LoadSign);
        Assert.False(SimulationMath.Balance(new Vec3(0, 200, 0), load).Balanced);   // e.g. a force applied to each of 2 faces
        Assert.False(SimulationMath.Balance(new Vec3(100, 0, 0), load).Balanced);   // wrong axis
        Assert.False(SimulationMath.Balance(new Vec3(0, 1e5, 0), load).Balanced);   // MPa taken as Pa, or the reverse
    }

    // ------------------------------------------------------------------ results

    [Fact]
    public void Ranges_and_node_locations_are_parsed_defensively()
    {
        var r = SimulationMath.Range([12, 0.5, 34, 32.04]);
        Assert.Equal(new SimRange(12, 0.5, 34, 32.04), r);
        Assert.Null(SimulationMath.Range(null));
        Assert.Null(SimulationMath.Range([1, 2, 3]));
        Assert.Null(SimulationMath.Range([1, double.NaN, 3, 4]));

        double[] box = [0, 0, 0, 0.1, 0.01, 0.01]; // the 100 x 10 x 10 beam, meters
        Assert.Equal([100, 5, 5], SimulationMath.LocationMm(0.1, 0.005, 0.005, box)!); // meters
        Assert.Equal([100, 5, 5], SimulationMath.LocationMm(100, 5, 5, box)!);         // already mm
        Assert.Null(SimulationMath.LocationMm(200, 5, 5, box));                      // outside in either unit
        Assert.Null(SimulationMath.LocationMm(-0.5, 0.005, 0.005, box));
        Assert.Null(SimulationMath.LocationMm(0.05, 0.005, 0.005, null));
        Assert.Null(SimulationMath.LocationMm(double.NaN, 0, 0, box));
    }

    [Fact]
    public void Factor_of_safety_is_yield_over_max_von_mises()
    {
        Assert.Equal(10.97, SimulationMath.FactorOfSafety(351.571, 32.04));
        Assert.Null(SimulationMath.FactorOfSafety(null, 32));
        Assert.Null(SimulationMath.FactorOfSafety(351.571, 0));
        Assert.Null(SimulationMath.FactorOfSafety(0, 32));
    }

    [Fact]
    public void Significant_digit_rounding()
    {
        Assert.Equal(0.0753012, SimulationMath.Sig(0.0753012345));
        Assert.Equal(32.0412, SimulationMath.Sig(32.0412345));
        Assert.Equal(1234570, SimulationMath.Sig(1234567));
        Assert.Equal(-0.2016, SimulationMath.Sig(-0.20160004, 4));
        Assert.Equal(0, SimulationMath.Sig(0));
        Assert.Equal(1.23457, SimulationMath.Sig(1.234567e-12) * 1e12, 9);
        Assert.Equal([0, 100.123, 0], SimulationMath.Sig(new Vec3(1e-11, 100.12345, -3e-12)));
        Assert.Equal("[0, -100, 0] N", SimulationMath.Newtons(new Vec3(0, -100.00001, 0)));
    }

    [Fact]
    public void Names_and_hints()
    {
        Assert.Equal("static", SimulationMath.StudyType(0));
        Assert.Equal("frequency", SimulationMath.StudyType(1));
        Assert.Equal("type_42", SimulationMath.StudyType(42));
        Assert.Equal("current", SimulationMath.MeshState(1));
        Assert.Equal("outdated", SimulationMath.MeshState(2));
        Assert.Contains("fixedFaces", SimulationMath.RunHint(30));
        Assert.Contains("license", SimulationMath.RunHint(22));
        Assert.Equal("0,-1,0", SimulationMath.Vector(new Vec3(0, -1, 0)));
    }

    [Fact]
    public void Natural_frequencies_come_in_groups_of_mode_rad_per_s_hertz_and_period()
    {
        double f1 = 203.29, f2 = 1259.34;
        Assert.Equal([f1, f2], SimulationMath.Frequencies([1, 2 * Math.PI * f1, f1, 1 / f1, 2, 2 * Math.PI * f2, f2, 1 / f2]));
        Assert.Equal([0.0], SimulationMath.Frequencies([1, 0, 0, 0])); // a rigid-body mode
        // A misread array: not groups of four, modes out of order, or rad/s that isn't 2π·Hz.
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.Frequencies([1, 2, 3])).Code);
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.Frequencies([2, 2 * Math.PI, 1, 1])).Code);
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.Frequencies([1, 100, 1, 1])).Code);
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.Frequencies(null)).Code);
    }

    [Fact]
    public void Buckling_load_factors_come_plain_or_in_mode_pairs()
    {
        Assert.Equal([10.2848], SimulationMath.LoadFactors([10.2848], 1));
        Assert.Equal([10.28, 92.5], SimulationMath.LoadFactors([1, 10.28, 2, 92.5], 2));
        Assert.Equal([3.0, 4.0], SimulationMath.LoadFactors([3, 4], 2)); // two plain factors aren't a pair
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.LoadFactors([], 1)).Code);
        Assert.Equal(ErrorCodes.VerifyFailed, Assert.Throws<SwException>(() => SimulationMath.LoadFactors([double.NaN], 1)).Code);
    }

    [Fact]
    public void Gravity_takes_a_direction_and_g_and_no_faces()
    {
        var g = SimulationMath.ParseLoad(new SimLoad { Kind = "gravity", Direction = JsonDocument.Parse("\"-y\"").RootElement.Clone() }, 0);
        Assert.Equal(SimLoadKind.Gravity, g.Kind);
        Assert.Equal(9.80665, g.Value, 1e-12);
        Assert.Equal(new Vec3(0, -1, 0), g.Direction);
        var ten = SimulationMath.ParseLoad(new SimLoad { ValueG = 10, Direction = JsonDocument.Parse("[0,0,-1]").RootElement.Clone() }, 0);
        Assert.Equal(SimLoadKind.Gravity, ten.Kind); // the kind follows from valueG
        Assert.Equal(98.0665, ten.Value, 1e-9);
        foreach (var bad in new[]
                 {
                     new SimLoad { Kind = "gravity" }, // no direction
                     new SimLoad { Kind = "gravity", Direction = JsonDocument.Parse("\"-y\"").RootElement.Clone(), Faces = [new EntityRef { At = [0, 0, 0] }] },
                     new SimLoad { Kind = "gravity", Direction = JsonDocument.Parse("\"-y\"").RootElement.Clone(), ValueN = 5 },
                     new SimLoad { Kind = "force", ValueN = 5, ValueG = 1, Faces = [new EntityRef { At = [0, 0, 0] }] },
                 })
            Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => SimulationMath.ParseLoad(bad, 0)).Code);
    }

    [Fact]
    public void Fixtures_map_to_restraint_types_and_the_faces_they_need()
    {
        EntityRef[] face = [new EntityRef { At = [100, 5, 5] }];
        var f = SimulationMath.ParseFixtures(face, [
            new SimFixture { Kind = "roller", Faces = face }, new SimFixture { Kind = "symmetry", Faces = face }, new SimFixture { Kind = "hinge", Faces = face },
        ]);
        Assert.Equal(["fixed", "roller", "symmetry", "hinge"], f.Select(x => x.Kind));
        Assert.Equal([0, 3, 2, 4], f.Select(x => x.Code)); // swsRestraintType_e
        Assert.Equal([null, "plane", "plane", "cylinder"], f.Select(x => x.Surface));
        Assert.Equal("fixtures[1]", f[2].Arg);
        Assert.Empty(SimulationMath.ParseFixtures(null, null));
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => SimulationMath.ParseFixtures(null, [new SimFixture { Kind = "glue", Faces = face }])).Code);
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => SimulationMath.ParseFixtures(null, [new SimFixture { Kind = "roller" }])).Code);
    }

    [Fact]
    public void Modal_study_types_and_their_default_names()
    {
        Assert.Equal("frequency", SimulationMath.ModalType("Modal"));
        Assert.Equal("buckling", SimulationMath.ModalType(" buckling "));
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => SimulationMath.ModalType("static")).Code);
        Assert.StartsWith("MCP Frequency ", SimulationMath.StudyName(null, "MCP Frequency"));
        Assert.Empty(SimulationMath.ParseLoads(null, allowNone: true));
        Assert.Equal(ErrorCodes.InvalidArgument, Assert.Throws<SwException>(() => SimulationMath.ParseLoads([])).Code);
    }
}

/// <summary>The dialog rules of sim_static and sim_delete: progress windows are tolerated, prompts and errors are not.</summary>
public class SimulationDialogRuleTests
{
    private static DialogInfo D(string title, string text, params string[] buttons) =>
        new(IntPtr.Zero, title, "#32770", text, buttons.Select((b, i) => new DialogButton(b, i + 1, true, IntPtr.Zero)).ToList());

    private static readonly IReadOnlyList<DialogRule> Run = DialogRules.SimulationRun("MCP Static 1a2b");

    private static DialogRule? First(IReadOnlyList<DialogRule> rules, DialogInfo d) => rules.FirstOrDefault(r => r.Matches(d));

    [Theory]
    [InlineData("MCP Static 1a2b (-Default-)", "Solver: FFEPlus Iterative / Memory usage: 12 MB")]
    [InlineData("MCP Static 1a2b (-Default-)", "")]
    [InlineData("Mesh Progress", "Creating mesh / 35 %")]
    [InlineData("Meshing", "")]
    [InlineData("Static 1 (-Default-)", "Elapsed time: 00:00:03")]
    [InlineData("SOLIDWORKS Simulation", "Solver: Intel Direct Sparse / Status: Solving")]
    [InlineData("Solver", "Solving equations / 40 %")]
    public void Progress_windows_of_the_running_study_are_tolerated(string title, string text)
    {
        var rule = First(Run, D(title, text, "Pause", "Cancel", "More >>"));
        Assert.NotNull(rule);
        Assert.Equal("", rule.Button); // tolerate only: the watchdog never presses anything
    }

    [Theory]
    [InlineData("SOLIDWORKS Simulation", "Excessive displacements were calculated in this model. Do you want to save the results?")]
    [InlineData("MCP Static 1a2b", "Analysis failed. The model is unstable.")]
    [InlineData("MCP Static 1a2b (-Default-)", "Elapsed time: 00:00:03 / Error: the solver stopped")]
    [InlineData("Mesh Failed", "")]
    [InlineData("Mesh Progress", "Mesh failed for 1 component")]
    [InlineData("Mesh Quality Warning", "Elements with high aspect ratio")]
    [InlineData("SOLIDWORKS", "The model is insufficiently constrained")]
    [InlineData("Static 1 (-Default-)", "Do you want to continue? / Elapsed time: 00:00:03")]
    [InlineData("SOLIDWORKS Simulation", "The analysis has completed.")] // an information box needs an answer: never tolerated
    public void Prompts_and_error_boxes_stay_unexpected(string title, string text) =>
        Assert.Null(First(Run, D(title, text, "Yes", "No", "OK")));

    [Fact]
    public void The_Simulation_data_save_prompt_is_answered_No_and_nothing_else_is()
    {
        var save = D("Simulation", "Do you want to save Simulation data?", "Yes", "No");
        Assert.Equal("No", First(Run, save)?.Button);
        Assert.Equal("No", First(DialogRules.SimulationCleanup, save)?.Button);
        Assert.Null(First(DialogRules.SimulationCleanup, D("SOLIDWORKS Simulation", "Do you want to save the results of this solution?", "Yes", "No")));
        Assert.Null(First(DialogRules.SimulationCleanup, D("Mesh Progress", "Creating mesh", "Pause")));
    }

    [Fact]
    public void The_study_name_is_matched_literally()
    {
        Assert.Null(First(DialogRules.SimulationRun("a.b"), D("axb", "", "Cancel")));
        Assert.NotNull(First(DialogRules.SimulationRun("a.b"), D("a.b (-Default-)", "", "Cancel")));
    }
}
