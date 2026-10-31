using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// sim_static, sim_list and sim_delete against a live SOLIDWORKS with Simulation (DESIGN §6.3). The oracle is a
/// 100 (x) × 10 (y) × 10 (z) AISI 1020 cantilever fixed at x = 0 (E = 200 GPa, I = 10·10³/12 = 833.3 mm⁴). Every test
/// leaves no study behind and the Simulation add-in as it found it (loaded or not).
/// </summary>
public class SimulationLiveTests(LiveFixture fx) : LiveTest(fx)
{
    // Beam theory, q = 0.1 MPa × 10 mm = 1 N/mm: δ = qL⁴/(8EI) = 0.075 mm, σ = qL²c/(2I) = 30 MPa. The verified VBA run
    // of the same recipe gave 0.07530 mm and 32.04 MPa (docs/solidworks-api-notes.md §14).
    private const double PressureUres = 0.0753;
    // End force F = 100 N: δ = FL³/(3EI) = 0.2 mm (+ ~1 % shear), σ = FLc/I = 60 MPa at the root.
    private const double ForceUres = 0.2;
    // Axial F = 1000 N on the 10 × 10 end: δ = FL/(EA) = 0.005 mm, σ = F/A = 10 MPa.
    private const double AxialUres = 0.005;
    private const double AisiYieldMpa = 351.571;

    private SimulationService Sim => new(S);

    private static readonly EntityRef FixedEnd = new() { At = [0, 5, 5] };
    private static readonly EntityRef TopFace = new() { At = [50, 10, 5] };
    private static readonly EntityRef FreeEnd = new() { At = [100, 5, 5] };

    private static SimLoad TopPressure(double mpa = 0.1) => new() { Kind = "pressure", Faces = [TopFace], ValueMpa = mpa };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The cantilever: a 100 × 10 rectangle on the Front Plane (x 0..100, y 0..10) extruded 10 mm toward +z.</summary>
    private async Task<string> CantileverAsync(string name, string? material = "AISI 1020")
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "front" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[0, 0], [100, 10]] }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 10 });
        Assert.Equal(10000, r.Verified.VolumeDeltaMm3, 1e-3);
        if (material is not null) Assert.Equal(material, (await new PropertiesService(S).MaterialSetAsync(doc, material)).Material);
        return doc;
    }

    private async Task<bool> AddinLoadedAsync(string doc) => (await Sim.ListAsync(doc)).AddinLoaded;

    /// <summary>No study is left and the add-in is in the state it was in before the test.</summary>
    private async Task AssertCleanAsync(string doc, bool addinBefore)
    {
        var list = await Sim.ListAsync(doc);
        Assert.Equal(addinBefore, list.AddinLoaded);
        Assert.Empty(list.Studies);
    }

    /// <summary>
    /// sim_static with a safety net for the live run: if SOLIDWORKS put up a dialog no rule covers (DIALOG_BLOCKING) or
    /// the call timed out, the dialog is dismissed (No, Cancel, OK or Close), the abandoned call is waited for, and the
    /// test fails with what was seen, so the following tests still find SOLIDWORKS usable.
    /// </summary>
    private async Task<SimStaticResult> StaticAsync(SimStaticRequest q)
    {
        try
        {
            return await Sim.StaticAsync(q);
        }
        catch (SwException ex) when (ex.Code is ErrorCodes.DialogBlocking or ErrorCodes.Timeout)
        {
            string seen = await DismissDialogsAsync();
            Assert.Fail($"sim_static ended with {ex.ToAgentText()} Dialogs dismissed by the test: {seen}");
            throw;
        }
    }

    /// <summary>sim_run frequency / buckling with the same safety net as <see cref="StaticAsync"/>.</summary>
    private async Task<SimModesResult> ModesAsync(SimStaticRequest q, string type)
    {
        try
        {
            return await Sim.ModesAsync(q, type);
        }
        catch (SwException ex) when (ex.Code is ErrorCodes.DialogBlocking or ErrorCodes.Timeout)
        {
            string seen = await DismissDialogsAsync();
            Assert.Fail($"sim_run {type} ended with {ex.ToAgentText()} Dialogs dismissed by the test: {seen}");
            throw;
        }
    }

    /// <summary>A 10 × 10 × 200 mm AISI 1020 bar standing on the top plane (y 0..200): the column of the modal tests.</summary>
    private async Task<string> ColumnAsync(string name)
    {
        string doc = await NewPartAsync(name);
        var sk = new SketchService(S);
        await sk.StartAsync(doc, new EntityRef { Name = "top" }, false);
        await sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Center = [0, 0], WidthMm = 10, HeightMm = 10 }]);
        var r = await new FeatureService(S).ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 200 });
        Assert.Equal(20000, r.Verified.VolumeDeltaMm3, 1e-3);
        Assert.Equal("AISI 1020", (await new PropertiesService(S).MaterialSetAsync(doc, "AISI 1020")).Material);
        return doc;
    }

    // The column as a beam: E = 200 GPa, ρ = 7900 kg/m³, I = 10⁴/12 mm⁴, A = 100 mm², L = 200 mm.
    private static readonly double ColumnRoot = Math.Sqrt(200e9 * (1e-8 / 12) / (7900 * 1e-4 * Math.Pow(0.2, 4)));

    private static readonly EntityRef Foot = new() { At = [0, 0, 0] };
    private static readonly EntityRef Head = new() { At = [0, 200, 0] };

    private async Task<string> DismissDialogsAsync()
    {
        var seen = new List<string>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var dialogs = S.ListDialogs();
            foreach (var d in dialogs)
            {
                string? button = new[] { "No", "Cancel", "OK", "Close" }
                    .FirstOrDefault(b => d.Buttons.Any(x => x.Enabled && x.Caption.Trim().Equals(b, StringComparison.OrdinalIgnoreCase)));
                seen.Add($"{d.Describe()} -> {button ?? "nothing pressed"}");
                if (button is null) continue;
                try { await new SessionService(S).RespondAsync(d.HandleHex, button, confirm: true); }
                catch (SwException) { /* it closed meanwhile */ }
            }
            if (dialogs.Count == 0 && S.Dispatcher.Current is null) break;
            await Task.Delay(1000);
        }
        return seen.Count == 0 ? "none" : string.Join("; ", seen.Distinct());
    }

    // ------------------------------------------------------------------ gravity and fixtures

    [Fact]
    public async Task Gravity_bends_the_cantilever_by_its_own_weight_and_the_fixture_carries_that_weight()
    {
        string doc = await CantileverAsync("sim_gravity");
        bool addinBefore = await AddinLoadedAsync(doc);
        // 10 g on 7900 kg/m³ × 10 cm³: w = ρ·10g·A = 77.47 N/m, δ = wL⁴/(8EI) = 0.00581 mm; the fixture holds 10 × 0.775 N.
        double w = 7900 * 10 * 9.80665 * 1e-4, delta = w * Math.Pow(0.1, 4) / (8 * 200e9 * (1e-8 / 12)) * 1000;
        var r = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Loads = [new SimLoad { Kind = "gravity", ValueG = 10, Direction = Json("\"-y\"") }],
        });
        Assert.Equal(delta, r.MaxDisplacementMm, delta * 0.05);
        Assert.Equal("gravity", Assert.Single(r.Loads).Kind);
        Assert.Equal(98.0665, r.Loads[0].AccelerationMs2!.Value, 1e-6);
        Assert.True(r.Reactions?.Balanced, "the fixture reactions must carry the weight");
        Assert.Equal(-w * 0.1, r.Reactions!.LoadsN![1], 1e-3);
        Assert.Contains(r.Checks, c => c.StartsWith("weight [0, -7.747, 0] N", StringComparison.Ordinal));
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task Symmetry_or_a_roller_on_the_free_end_guides_it_so_it_bends_a_quarter_as_much()
    {
        // Fixed at x = 0, the end x = 100 kept from moving along x: a guided cantilever, δ = PL³/(12EI) = 0.25 mm for 500 N
        // (+ about 3 % from shear), a quarter of the free cantilever's PL³/(3EI) = 1 mm.
        string doc = await CantileverAsync("sim_guided");
        bool addinBefore = await AddinLoadedAsync(doc);
        var down = new SimLoad { Kind = "force", Faces = [FreeEnd], ValueN = 500, Direction = Json("[0,-1,0]") };
        var sym = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Fixtures = [new SimFixture { Kind = "symmetry", Faces = [FreeEnd] }], Loads = [down],
        });
        Assert.InRange(sym.MaxDisplacementMm, 0.25, 0.27);
        Assert.Contains(sym.Checks, c => c.StartsWith("symmetry fixture on 1 face(s) accepted", StringComparison.Ordinal));
        Assert.Equal(2, sym.FixedFaces);
        var roller = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Fixtures = [new SimFixture { Kind = "roller", Faces = [FreeEnd] }], Loads = [down],
        });
        Assert.Equal(sym.MaxDisplacementMm, roller.MaxDisplacementMm, sym.MaxDisplacementMm * 0.01); // on a solid's face both hold the normal
        Assert.True(roller.Reactions?.Balanced);
        // A hinge turns about a cylinder's axis: a flat face is refused before any study.
        var hinge = await Assert.ThrowsAsync<SwException>(() => Sim.StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Fixtures = [new SimFixture { Kind = "hinge", Faces = [FreeEnd] }], Loads = [down],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, hinge.Code);
        await AssertCleanAsync(doc, addinBefore);
    }

    // ------------------------------------------------------------------ sim_run: frequency and buckling

    [Fact]
    public async Task Frequency_of_the_cantilever_column_matches_beam_theory_and_a_free_bar_has_six_rigid_body_modes()
    {
        string doc = await ColumnAsync("sim_freq");
        bool addinBefore = await AddinLoadedAsync(doc);
        // Cantilever: f = (βL)²/(2π) · √(EI/(ρAL⁴)), βL = 1.8751 for the first bending mode (twice: the section is square).
        double f1 = 1.8751 * 1.8751 / (2 * Math.PI) * ColumnRoot; // 203.2 Hz
        var r = await ModesAsync(new SimStaticRequest { Doc = doc, FixedFaces = [Foot], Modes = 4, MeshSizeMm = 2.5 }, "frequency");
        Assert.Equal("frequency", r.Type);
        Assert.Equal(4, r.FrequenciesHz!.Count);
        Assert.Equal(f1, r.FrequenciesHz[0], f1 * 0.015);
        Assert.Equal(f1, r.FrequenciesHz[1], f1 * 0.015);
        // The second bending pair: Euler-Bernoulli gives 6.27 f1; shear and rotary inertia bring a 20:1 bar a little lower.
        Assert.InRange(r.FrequenciesHz[2] / r.FrequenciesHz[0], 6.0, 6.3);
        Assert.Contains(r.Checks, c => c.StartsWith("4 natural frequencies asked for (read back)", StringComparison.Ordinal));
        await AssertCleanAsync(doc, addinBefore);

        // Free-free: six rigid-body modes at 0 Hz, then the first free bending mode, βL = 4.7300.
        var free = await ModesAsync(new SimStaticRequest { Doc = doc, Modes = 8 }, "frequency");
        Assert.Equal(6, free.FrequenciesHz!.Count(f => f < 1));
        Assert.Equal(4.73 * 4.73 / (2 * Math.PI) * ColumnRoot, free.FrequenciesHz[6], 1292.9 * 0.02);
        Assert.Contains(free.Checks, c => c.StartsWith("six rigid-body modes below 1 Hz", StringComparison.Ordinal));
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task Buckling_of_the_fixed_free_column_matches_euler()
    {
        string doc = await ColumnAsync("sim_buckle");
        bool addinBefore = await AddinLoadedAsync(doc);
        // Euler, fixed at the foot and free at the head: P = π²EI/(2L)² = 10.28 kN, so 1 kN gives a load factor of 10.28.
        double factor = Math.PI * Math.PI * 200e9 * (1e-8 / 12) / Math.Pow(2 * 0.2, 2) / 1000;
        var r = await ModesAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [Foot], MeshSizeMm = 2.5,
            Loads = [new SimLoad { Kind = "force", Faces = [Head], ValueN = 1000, Direction = Json("[0,-1,0]") }],
        }, "buckling");
        Assert.Equal("buckling", r.Type);
        Assert.Equal(factor, Assert.Single(r.LoadFactors!), factor * 0.02);
        Assert.Contains(r.Checks, c => c.Contains("it buckles at", StringComparison.Ordinal));
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task Sim_run_refuses_loads_on_a_frequency_study_and_a_buckling_study_without_loads_before_any_study()
    {
        string doc = await ColumnAsync("sim_run_bad");
        bool addinBefore = await AddinLoadedAsync(doc);
        var loads = new[] { new SimLoad { Kind = "force", Faces = [Head], ValueN = 1000, Direction = Json("[0,-1,0]") } };
        foreach (var (q, type) in new[]
                 {
                     (new SimStaticRequest { Doc = doc, FixedFaces = [Foot], Loads = loads }, "frequency"),
                     (new SimStaticRequest { Doc = doc, FixedFaces = [Foot] }, "buckling"),
                     (new SimStaticRequest { Doc = doc, Loads = loads }, "buckling"),
                     (new SimStaticRequest { Doc = doc, FixedFaces = [Foot], Modes = 0 }, "frequency"),
                     (new SimStaticRequest { Doc = doc, FixedFaces = [Foot] }, "thermal"),
                 })
            Assert.Equal(ErrorCodes.InvalidArgument, (await Assert.ThrowsAsync<SwException>(() => Sim.ModesAsync(q, type))).Code);
        await AssertCleanAsync(doc, addinBefore);
    }

    // ------------------------------------------------------------------ sim_static: oracles

    [Fact]
    public async Task Static_cantilever_under_pressure_matches_beam_theory()
    {
        string doc = await CantileverAsync("sim_pressure", material: null); // sim_static assigns the material itself
        bool addinBefore = await AddinLoadedAsync(doc);

        var r = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = [FixedEnd], Loads = [TopPressure()],
        });

        Assert.InRange(r.MaxDisplacementMm, PressureUres * 0.97, PressureUres * 1.03);
        Assert.InRange(r.MaxVonMisesMpa, 28, 40);
        // The pressure pushes into the top face: the beam bends down (-y).
        Assert.InRange(r.DisplacementMm!.Uy![0], -PressureUres * 1.03, -PressureUres * 0.97);

        Assert.Equal("AISI 1020", r.Material);
        Assert.True(r.MaterialAssigned);
        Assert.Null(r.PreviousMaterial);
        Assert.False(r.StudyKept);
        Assert.StartsWith("MCP Static ", r.Study);
        Assert.Equal(1, r.FixedFaces);
        var load = Assert.Single(r.Loads);
        Assert.Equal("pressure", load.Kind);
        Assert.Equal(0.1, load.ValueMpa!.Value, 1e-12);
        Assert.Equal(1000, load.AreaMm2!.Value, 1e-3); // 100 × 10
        Assert.Equal(100, load.ForceN!.Value, 1e-6);   // 0.1 N/mm² × 1000 mm²
        Assert.True(r.Mesh.DefaultSize);
        Assert.Equal("high", r.Mesh.Quality);
        Assert.True(r.Mesh.Nodes > 100 && r.Mesh.Elements > 10, $"mesh {r.Mesh.Nodes} nodes / {r.Mesh.Elements} elements");
        Assert.True(r.Time.SolveS > 0 && r.Time.TotalS >= r.Time.SolveS);
        Assert.Contains(r.Checks, c => c.Contains("fixed fixture on 1 face"));
        Assert.Contains(r.Checks, c => c.Contains("pressure 0.1 MPa"));
        Assert.Contains(r.Checks, c => c.Contains("solver status 0"));
        Assert.Contains(r.Checks, c => c.Contains("deleted again"));
        Assert.Equal(!addinBefore, r.Addin.LoadedByCall);
        Assert.Equal(!addinBefore, r.Addin.Unloaded);
        await AssertCleanAsync(doc, addinBefore);

        // Best-effort readings whose recipes are not verified live yet (checked last, so the oracle above is reported first):
        // the fixture reactions balance the 100 N load, where the extremes are, and the factor of safety.
        Assert.NotNull(r.Reactions);
        Assert.Equal([0, -100, 0], r.Reactions!.LoadsN!);
        Assert.True(r.Reactions.Balanced, $"reactions {string.Join(", ", r.Reactions.TotalN)} N");
        Assert.Equal(100, Math.Abs(r.Reactions.TotalN[1]), 2.0);
        Assert.NotNull(r.MaxDisplacementAtMm);
        Assert.InRange(r.MaxDisplacementAtMm![0], 99, 100.01);  // the free end
        Assert.NotNull(r.MaxVonMisesAtMm);
        Assert.InRange(r.MaxVonMisesAtMm![0], -0.01, 10);       // at the root
        Assert.NotNull(r.YieldStrengthMpa);
        Assert.Equal(AisiYieldMpa, r.YieldStrengthMpa!.Value, 0.5);
        Assert.Equal(r.YieldStrengthMpa.Value / r.MaxVonMisesMpa, r.MinFactorOfSafety!.Value, 0.01);
    }

    [Fact]
    public async Task Static_force_along_minus_y_on_the_free_end_bends_the_beam_down()
    {
        string doc = await CantileverAsync("sim_force");
        bool addinBefore = await AddinLoadedAsync(doc);

        var r = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd],
            Loads = [new SimLoad { Kind = "force", Faces = [FreeEnd], ValueN = 100, Direction = Json("[0,-1,0]") }],
        });

        Assert.InRange(r.MaxDisplacementMm, ForceUres * 0.97, ForceUres * 1.06);
        Assert.InRange(r.DisplacementMm!.Uy![0], -ForceUres * 1.06, -ForceUres * 0.97); // down, along the force
        Assert.InRange(r.MaxVonMisesMpa, 50, 90);
        var load = Assert.Single(r.Loads);
        Assert.Equal("force", load.Kind);
        Assert.Equal("0,-1,0", load.Direction);
        Assert.Equal(100, load.ValueN!.Value, 1e-12);
        Assert.Contains(r.Checks, c => c.Contains("along the force (-y)"));
        Assert.Null(r.MaterialAssigned); // the part's own material
        Assert.Equal("AISI 1020", r.Material);
        await AssertCleanAsync(doc, addinBefore);

        Assert.NotNull(r.Reactions);
        Assert.True(r.Reactions!.Balanced, $"reactions {string.Join(", ", r.Reactions.TotalN)} N");
        Assert.Equal(100, Math.Abs(r.Reactions.TotalN[1]), 2.0);
    }

    [Fact]
    public async Task Static_normal_force_on_the_free_end_compresses_the_beam()
    {
        string doc = await CantileverAsync("sim_axial");
        bool addinBefore = await AddinLoadedAsync(doc);

        var r = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Loads = [new SimLoad { Kind = "force", Faces = [FreeEnd], ValueN = 1000 }],
        });

        Assert.InRange(r.MaxDisplacementMm, AxialUres * 0.97, AxialUres * 1.05);
        // A normal force pushes into its face (like a pressure): the free end moves toward the root (-x).
        Assert.InRange(r.DisplacementMm!.Ux![0], -AxialUres * 1.05, -AxialUres * 0.97);
        Assert.InRange(r.MaxVonMisesMpa, 9, 15); // F/A = 10 MPa (more at the clamped root: Poisson is restrained there)
        Assert.Equal("normal", Assert.Single(r.Loads).Direction);
        await AssertCleanAsync(doc, addinBefore);

        Assert.NotNull(r.Reactions);
        Assert.Equal([-1000, 0, 0], r.Reactions!.LoadsN!);
        Assert.True(r.Reactions.Balanced, $"reactions {string.Join(", ", r.Reactions.TotalN)} N");
        Assert.Equal(1000, Math.Abs(r.Reactions.TotalN[0]), 20.0);
    }

    [Fact]
    public async Task Static_with_a_finer_mesh_kept_study_then_sim_list_and_sim_delete()
    {
        string doc = await CantileverAsync("sim_keep");
        bool addinBefore = await AddinLoadedAsync(doc);

        var r = await StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Loads = [TopPressure()], MeshSizeMm = 2.5, KeepStudy = true, StudyName = "MCP keep test",
        });
        Assert.True(r.StudyKept);
        Assert.Equal("MCP keep test", r.Study);
        Assert.False(r.Mesh.DefaultSize);
        Assert.Equal(2.5, r.Mesh.ElementSizeMm, 1e-9);
        Assert.InRange(r.MaxDisplacementMm, PressureUres * 0.97, PressureUres * 1.03);
        Assert.False(r.Addin.Unloaded); // a study is left
        if (r.Addin.LoadedByCall) Assert.NotNull(r.Addin.LeftLoaded);

        // sim_list shows the kept study with its mesh and results.
        var list = await Sim.ListAsync(doc);
        Assert.True(list.AddinLoaded);
        var row = Assert.Single(list.Studies);
        Assert.Equal("MCP keep test", row.Name);
        Assert.Equal("static", row.Type);
        Assert.Equal("current", row.Mesh);
        Assert.Equal(r.Mesh.Nodes, row.Nodes);
        Assert.Equal(r.Mesh.Elements, row.Elements);
        Assert.True(row.Results);
        Assert.Equal(r.MaxDisplacementMm, row.MaxDisplacementMm!.Value, 1e-6);
        Assert.True(row.ServerCreated);

        // The same name again is refused before anything is created.
        var dup = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Loads = [TopPressure()], StudyName = "mcp KEEP test",
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, dup.Code);
        Assert.Contains("already has a study", dup.Message);
        Assert.Single((await Sim.ListAsync(doc)).Studies);

        // A name that doesn't exist: ENTITY_NOT_FOUND, nothing deleted; an empty name is refused up front.
        var missing = await Assert.ThrowsAsync<SwException>(() => Sim.DeleteAsync(doc, ["no such study"]));
        Assert.Equal(ErrorCodes.EntityNotFound, missing.Code);
        Assert.Contains("MCP keep test", missing.Hint);
        var blank = await Assert.ThrowsAsync<SwException>(() => Sim.DeleteAsync(doc, [" "]));
        Assert.Equal(ErrorCodes.InvalidArgument, blank.Code);
        Assert.Single((await Sim.ListAsync(doc)).Studies);

        // Default: the studies this server created here.
        var del = await Sim.DeleteAsync(doc, null);
        Assert.Equal(["MCP keep test"], del.Deleted);
        Assert.Empty(del.Remaining!);
        Assert.Contains(del.Checks, c => c.Contains("gone from the study list"));
        await AssertCleanAsync(doc, addinBefore); // unloaded again if the server had loaded it

        var again = await Sim.DeleteAsync(doc, null);
        Assert.Empty(again.Deleted);
        Assert.NotNull(again.Message);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Static_without_fixed_faces_is_refused_and_leaves_no_study()
    {
        string doc = await CantileverAsync("sim_nofix");
        bool addinBefore = await AddinLoadedAsync(doc);
        var ex = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [], Loads = [TopPressure()],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, ex.Code);
        Assert.Contains("fixedFaces", ex.Message);
        Assert.NotNull(ex.Hint);
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task A_study_without_a_fixture_is_refused_by_the_solver_and_deleted()
    {
        // The tool refuses an empty fixedFaces up front; the test bypass sends the study to the solver anyway, to check the
        // solver's refusal (RunAnalysis 30 = swsRunAnalysisErrorInvalidLBC in the VBA run) and the cleanup after a failed run.
        string doc = await CantileverAsync("sim_unfixed");
        bool addinBefore = await AddinLoadedAsync(doc);
        var ex = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [], Loads = [TopPressure()], AllowNoFixtures = true, StudyName = "MCP unfixed",
        }));
        Assert.Equal(ErrorCodes.FeatureFailed, ex.Code);
        Assert.Contains("RunAnalysis returned", ex.Message);
        Assert.Contains("The study 'MCP unfixed' was deleted.", ex.Message);
        Assert.NotNull(ex.Hint);
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task Static_refuses_a_part_without_material_bad_names_and_absurd_meshes_before_any_study()
    {
        string doc = await CantileverAsync("sim_refusals", material: null);
        bool addinBefore = await AddinLoadedAsync(doc);

        var noMaterial = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd], Loads = [TopPressure()],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, noMaterial.Code);
        Assert.Contains("no material", noMaterial.Message);
        Assert.Contains("material", noMaterial.Hint);

        var badMaterial = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, Material = "Unobtainium 42", FixedFaces = [FixedEnd], Loads = [TopPressure()],
        }));
        Assert.Equal(ErrorCodes.EntityNotFound, badMaterial.Code);
        Assert.True(string.IsNullOrEmpty((await new InspectService(S).ModelInfoAsync(doc)).Material));

        var tiny = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = [FixedEnd], Loads = [TopPressure()], MeshSizeMm = 0.05,
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, tiny.Code);
        Assert.Contains("million elements", tiny.Message);

        var noFace = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = [new EntityRef { At = [500, 500, 500] }], Loads = [TopPressure()],
        }));
        Assert.Equal(ErrorCodes.EntityNotFound, noFace.Code);

        var pressureWithDirection = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = [FixedEnd],
            Loads = [new SimLoad { Kind = "pressure", Faces = [TopFace], ValueMpa = 0.1, Direction = Json("[0,-1,0]") }],
        }));
        Assert.Equal(ErrorCodes.InvalidArgument, pressureWithDirection.Code);

        // None of these touched the material or loaded the add-in.
        Assert.True(string.IsNullOrEmpty((await new InspectService(S).ModelInfoAsync(doc)).Material));
        await AssertCleanAsync(doc, addinBefore);
    }

    [Fact]
    public async Task Sim_list_and_sim_delete_refuse_an_unknown_document()
    {
        var list = await Assert.ThrowsAsync<SwException>(() => Sim.ListAsync("no such document 42"));
        Assert.Equal(ErrorCodes.InvalidArgument, list.Code);
        Assert.Contains("No open document matches", list.Message);
        var del = await Assert.ThrowsAsync<SwException>(() => Sim.DeleteAsync("no such document 42", ["MCP Static 0000"]));
        Assert.Equal(ErrorCodes.InvalidArgument, del.Code);
    }

    [Fact]
    public async Task Simulation_refuses_to_change_the_users_documents()
    {
        string title = await TestHooks.NewForeignPartAsync(S);
        try
        {
            var run = await Assert.ThrowsAsync<SwException>(() => StaticAsync(new SimStaticRequest
            {
                Doc = title, Material = "AISI 1020", FixedFaces = [FixedEnd], Loads = [TopPressure()],
            }));
            Assert.Equal(ErrorCodes.NotOwned, run.Code);
            var del = await Assert.ThrowsAsync<SwException>(() => Sim.DeleteAsync(title, ["Static 1"]));
            Assert.Equal(ErrorCodes.NotOwned, del.Code);
            var list = await Sim.ListAsync(title); // reading is fine
            Assert.Empty(list.Studies);

            // Even when the user lets the server edit the part, only studies the server created may be deleted.
            await new DocumentService(S).ActivateAsync(title, allowEdit: true);
            var adopted = await Assert.ThrowsAsync<SwException>(() => Sim.DeleteAsync(title, ["Static 1"]));
            Assert.Equal(ErrorCodes.NotOwned, adopted.Code);
            Assert.Contains("did not create", adopted.Message);
            var none = await Sim.DeleteAsync(title, null); // nothing of the server's: a no-op
            Assert.Empty(none.Deleted);
        }
        finally
        {
            await TestHooks.CloseRawAsync(S, title);
        }
    }
}
