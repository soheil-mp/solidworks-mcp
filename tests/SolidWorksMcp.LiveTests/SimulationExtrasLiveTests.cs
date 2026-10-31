using System.Text.Json;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using Xunit;

namespace SolidWorksMcp.LiveTests;

/// <summary>
/// Simulation extras against a live SOLIDWORKS with Simulation (skipped without SWMCP_LIVE_TESTS=1): mesh convergence of a
/// cantilever against beam theory, and a no-penetration contact between two stacked bars against bonded ones.
/// </summary>
public class SimulationExtrasLiveTests(LiveFixture fx) : LiveTest(fx)
{
    private SimulationService Sim => new(S);
    private SketchService Sk => new(S);
    private FeatureService Fs => new(S);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>A bar along x from 0 to 200 with a 20 (z) × <paramref name="h"/> (y) section from y = <paramref name="y0"/>.</summary>
    private async Task BarAsync(string doc, double y0, double h, bool merge)
    {
        await Sk.StartAsync(doc, new EntityRef { Name = "right" }, false);
        // Right-plane sketch: x is -z, y is y.
        await Sk.AddAsync(doc, [new SketchEntity { Kind = "rectangle", Corners = [[-10, y0], [10, y0 + h]] }]);
        await Fs.ExtrudeAsync(new ExtrudeRequest { Doc = doc, DepthMm = 200, Direction = "+x", Merge = merge });
    }

    [Fact]
    public async Task Mesh_convergence_of_a_cantilever_settles_near_beam_theory()
    {
        string doc = await NewPartAsync("sim_conv");
        await BarAsync(doc, 0, 20, true);
        var r = await Sim.ConvergenceAsync(new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = [new EntityRef { At = [0, 10, 0] }],
            Loads = [new SimLoad { Kind = "force", Faces = [new EntityRef { At = [200, 10, 0] }], ValueN = 100, Direction = Json("[0,-1,0]") }],
            MeshSizeMm = 10,
        });
        Assert.Equal(3, r.Runs.Count);
        Assert.True(r.Runs[2].ElementSizeMm < r.Runs[0].ElementSizeMm);
        // δ = FL³/(3EI) = 100·200³ / (3·200 000·20⁴/12) = 0.1 mm, plus about 1 % shear deflection.
        Assert.InRange(r.Runs[2].MaxDisplacementMm, 0.097, 0.106);
        Assert.True(r.Converged, $"displacement change {r.DisplacementChangePct} %");
    }

    [Fact]
    public async Task A_nonlinear_study_of_a_small_load_matches_the_linear_one()
    {
        string doc = await NewPartAsync("sim_nonlin");
        await BarAsync(doc, 0, 20, true);
        var q = new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = [new EntityRef { At = [0, 10, 0] }],
            Loads = [new SimLoad { Kind = "force", Faces = [new EntityRef { At = [200, 10, 0] }], ValueN = 100, Direction = Json("[0,-1,0]") }],
            MeshSizeMm = 6,
        };
        string report = Path.Combine(Path.GetTempPath(), $"swmcp_report_{Guid.NewGuid():N}", "cantilever");
        var linear = await Sim.StaticAsync(q with { ReportPath = report });
        Assert.NotNull(linear.Report);
        Assert.True(File.Exists(linear.Report) && linear.ReportBytes > 1000, linear.Report);
        Directory.Delete(Path.GetDirectoryName(linear.Report)!, recursive: true);
        var nonlinear = await Sim.StaticAsync(q with { Nonlinear = true });
        Assert.Contains(nonlinear.Checks, c => c.Contains("nonlinear solver", StringComparison.Ordinal));
        // 0.1 mm on a 200 mm beam is a small displacement: both solvers agree.
        Assert.Equal(linear.MaxDisplacementMm, nonlinear.MaxDisplacementMm, 0.02 * linear.MaxDisplacementMm);
    }

    [Fact]
    public async Task Two_stacked_bars_with_no_penetration_bend_about_four_times_more_than_bonded()
    {
        string doc = await NewPartAsync("sim_contact");
        await BarAsync(doc, 0, 10, true);
        await BarAsync(doc, 10, 10, false);
        var fixedEnds = new[] { new EntityRef { At = [0, 5, 0] }, new EntityRef { At = [0, 15, 0] } };
        var load = new SimLoad { Kind = "force", Faces = [new EntityRef { At = [200, 15, 0] }], ValueN = 100, Direction = Json("[0,-1,0]") };
        var bonded = await Sim.StaticAsync(new SimStaticRequest { Doc = doc, Material = "AISI 1020", FixedFaces = fixedEnds, Loads = [load], MeshSizeMm = 4 });
        var sliding = await Sim.StaticAsync(new SimStaticRequest
        {
            Doc = doc, Material = "AISI 1020", FixedFaces = fixedEnds, Loads = [load], MeshSizeMm = 4,
            Contacts =
            [
                new SimContact
                {
                    Kind = "no_penetration",
                    Source = [new EntityRef { Where = Json("{\"type\":\"face\",\"body\":\"Boss-Extrude2\",\"near\":[100,10,0],\"withinMm\":0.01}"), Expect = 1 }],
                    Target = [new EntityRef { Where = Json("{\"type\":\"face\",\"body\":\"Boss-Extrude1\",\"near\":[100,10,0],\"withinMm\":0.01}"), Expect = 1 }],
                },
            ],
        });
        Assert.Contains(sliding.Checks, c => c.StartsWith("no_penetration contact set", StringComparison.Ordinal));
        double ratio = sliding.MaxDisplacementMm / bonded.MaxDisplacementMm;
        // One 20 mm beam (I ∝ 20³) against two 10 mm beams side by side (2 · 10³): four times softer.
        Assert.InRange(ratio, 3, 5);
    }
}
