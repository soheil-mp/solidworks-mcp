using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed class SimConvergenceRun
{
    public double ElementSizeMm { get; init; }
    public int Nodes { get; init; }
    public int Elements { get; init; }
    public double MaxVonMisesMpa { get; init; }
    public double MaxDisplacementMm { get; init; }
}

public sealed class SimConvergenceResult : ToolResult
{
    /// <summary>Coarsest first: each run's element size is the previous one / 1.5.</summary>
    public required IReadOnlyList<SimConvergenceRun> Runs { get; init; }
    /// <summary>Change of the last run against the one before, %.</summary>
    public double DisplacementChangePct { get; init; }
    public double StressChangePct { get; init; }
    /// <summary>The maximum displacement changed less than 2 % on the last refinement.</summary>
    public bool Converged { get; init; }
    /// <summary>The finest run in full.</summary>
    public required SimStaticResult Final { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

public sealed partial class SimulationService
{
    private const double RefineRatio = 1.5;
    private const double ConvergedPct = 2;

    /// <summary>
    /// sim_static {convergence:true}: the same study solved on <paramref name="levels"/> meshes, each element size the previous
    /// one / 1.5 (from meshSizeMm or SOLIDWORKS' default), to show whether the maximum displacement and stress have settled.
    /// Only the finest study is kept (with keepStudy).
    /// </summary>
    public async Task<SimConvergenceResult> ConvergenceAsync(SimStaticRequest q, int levels = 3, Action<string>? phase = null, CancellationToken ct = default)
    {
        if (levels is < 2 or > 5) throw SwException.InvalidArgument($"levels must be 2–5 (got {levels}).");
        var runs = new List<SimStaticResult>();
        double? size = q.MeshSizeMm;
        for (int i = 0; i < levels; i++)
        {
            int level = i + 1;
            var r = await StaticAsync(q with { MeshSizeMm = size, KeepStudy = q.KeepStudy && i == levels - 1 },
                p => phase?.Invoke($"mesh {level}/{levels}: {p}"), ct);
            runs.Add(r);
            size = r.Mesh.ElementSizeMm / RefineRatio;
        }
        var last = runs[^1];
        var prev = runs[^2];
        double dU = Pct(last.MaxDisplacementMm, prev.MaxDisplacementMm);
        double dS = Pct(last.MaxVonMisesMpa, prev.MaxVonMisesMpa);
        var checks = new List<string>
        {
            $"{levels} meshes from {runs[0].Mesh.ElementSizeMm} mm to {last.Mesh.ElementSizeMm} mm elements ({runs[0].Mesh.Elements} → {last.Mesh.Elements} elements)",
            $"last refinement: max displacement {dU:+0.##;-0.##;0} %, max von Mises {dS:+0.##;-0.##;0} %",
        };
        if (Math.Abs(dS) > 10 && Math.Abs(dU) <= ConvergedPct)
            checks.Add("the displacement has settled but the peak stress still moves: a stress concentration (sharp corner?) that refining won't settle");
        return new SimConvergenceResult
        {
            Doc = last.Doc,
            Runs = runs.Select(r => new SimConvergenceRun
            {
                ElementSizeMm = r.Mesh.ElementSizeMm, Nodes = r.Mesh.Nodes, Elements = r.Mesh.Elements,
                MaxVonMisesMpa = r.MaxVonMisesMpa, MaxDisplacementMm = r.MaxDisplacementMm,
            }).ToList(),
            DisplacementChangePct = Math.Round(dU, 3),
            StressChangePct = Math.Round(dS, 3),
            Converged = Math.Abs(dU) < ConvergedPct,
            Final = last,
            Checks = checks,
        };
    }

    private static double Pct(double now, double before) => before == 0 ? 0 : (now - before) / Math.Abs(before) * 100;
}
