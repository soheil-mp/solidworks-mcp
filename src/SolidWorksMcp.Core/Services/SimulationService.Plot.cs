using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using SolidWorks.Interop.cosworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of sim_plot: a result plot of a kept study, as an image.</summary>
public sealed record SimPlotRequest
{
    public string? Doc { get; init; }
    /// <summary>The study (default: the one study this server kept in the document, or its only study).</summary>
    public string? Study { get; init; }
    /// <summary>von_mises | displacement | mode | temperature | heat_flux (default: by the study's type).</summary>
    public string? Result { get; init; }
    /// <summary>frequency / buckling: which mode (default 1).</summary>
    public int? Mode { get; init; }
    /// <summary>Show the deformed shape (default: yes, except for thermal plots).</summary>
    public bool? Deformed { get; init; }
    public string? View { get; init; }
    public int MaxSizePx { get; init; } = 1024;
    public string? File { get; init; }
    public bool Overwrite { get; init; }
}

public sealed class SimPlotResult : ToolResult
{
    public required string Study { get; init; }
    /// <summary>static | frequency | buckling | thermal.</summary>
    public required string StudyType { get; init; }
    /// <summary>von_mises | displacement | mode | temperature | heat_flux.</summary>
    public required string Result { get; init; }
    public required string Plot { get; init; }
    public int? Mode { get; init; }
    /// <summary>[min, max] the plot shows, in <see cref="Unit"/>.</summary>
    public double[]? Range { get; init; }
    public required string Unit { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public string? SavedTo { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    [JsonIgnore] public byte[] Png { get; init; } = [];
}

/// <summary>
/// sim_plot: ICWResults.CreatePlot on a kept study (the result type, component and unit per result), SetModeShape for
/// frequency and buckling modes, ShowDeformedPlot, ActivatePlot, then the viewport image through view_screenshot's capture.
/// Tier B: the range the plot shows (GetMinMaxResultValues) equals the study's own results.
/// </summary>
public sealed partial class SimulationService
{
    private sealed record PlotSpec(string Result, int Type, int Component, int Units, string Unit, bool DeformedByDefault);

    private static PlotSpec PlotOf(string? result, int studyType) => (result?.Trim().ToLowerInvariant().Replace(' ', '_'), studyType) switch
    {
        (null or "von_mises" or "stress", 0) => new("von_mises", (int)swsPlotResultTypes_e.swsResultStress,
            (int)swsStaticResultNodalStressComponentTypes_e.swsStaticNodalStress_VON, NPerMm2, "MPa", true),
        ("displacement", 0) => new("displacement", (int)swsPlotResultTypes_e.swsResultDisplacementOrAmplitude,
            (int)swsDisplacementComponent_e.swsDisplacementComponentURES, Mm, "mm", true),
        (null or "mode" or "mode_shape" or "displacement", 1 or 2) => new("mode", (int)swsPlotResultTypes_e.swsResultDisplacementOrAmplitude,
            (int)swsFrequencyBucklingResultDisplacementComponentTypes_e.swsFrequencyBucklingDisplacement_URES, Mm, "relative amplitude", true),
        (null or "temperature", 3) => new("temperature", (int)swsPlotResultTypes_e.swsResultThermal,
            (int)swsThermalComponent_e.swsThermalComponentTEMP, Celsius, "°C", false),
        ("heat_flux", 3) => new("heat_flux", (int)swsPlotResultTypes_e.swsResultThermal,
            (int)swsThermalComponent_e.swsThermalComponentHFLUXN, 0, "W/m²", false),
        _ => throw SwException.InvalidArgument($"No '{result}' plot for a {SimulationMath.StudyType(studyType)} study.",
            studyType switch
            {
                0 => "Static studies plot von_mises (default) or displacement (sim_static reports the factor of safety).",
                1 or 2 => "Frequency and buckling studies plot mode (the mode shape; pass mode).",
                3 => "Thermal studies plot temperature (default) or heat_flux.",
                _ => "sim_plot plots static, frequency, buckling and thermal studies.",
            }),
    };

    public Task<SimPlotResult> PlotAsync(SimPlotRequest q, CancellationToken ct = default)
    {
        if (q.Mode is int m && (m < 1 || m > SimulationMath.MaxModes))
            throw SwException.InvalidArgument($"mode must be between 1 and {SimulationMath.MaxModes} (got {m}).");
        return session.RunAsync("sim_plot", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            string title = ctx.Title(d);
            var cb = ctx.App.SimulationAddin()
                     ?? throw new SwException(ErrorCodes.AddinUnavailable, "The Simulation add-in is not loaded, so there are no results to plot.",
                         "Run the study with keepStudy:true (sim_static or sim_run), then sim_plot.");
            var tracked = Tracked(ctx.DocEntry);
            return OnActive(ctx, d, () =>
            {
                var cw = cb.CosmosWorks ?? throw new SwException(ErrorCodes.AddinUnavailable, "The Simulation add-in returned no API object.");
                var manager = WaitForDoc(cw)?.StudyManager
                              ?? throw new SwException(ErrorCodes.AddinUnavailable, $"Simulation has no data for '{title}'.");
                var studies = Studies(manager).ToList();
                var pick = q.Study is { Length: > 0 } want
                    ? studies.Where(x => string.Equals(x.Study.Name, want.Trim(), StringComparison.OrdinalIgnoreCase)).ToList()
                    : studies.Where(x => tracked.Contains(x.Study.Name ?? "")).ToList() is { Count: 1 } mine ? mine : studies;
                if (pick.Count != 1)
                    throw new SwException(pick.Count == 0 ? ErrorCodes.EntityNotFound : ErrorCodes.EntityAmbiguous,
                        q.Study is { Length: > 0 } ? $"'{title}' has no study named '{q.Study}'." : $"'{title}' has {studies.Count} studies; say which.",
                        studies.Count == 0 ? "Run one with keepStudy:true first." : $"Studies: {Quote(studies.Select(x => x.Study.Name ?? ""))}.");
                var (index, s) = pick[0];
                string name = s.Name ?? "";
                manager.ActiveStudy = index;
                int type = s.AnalysisType;
                var spec = PlotOf(q.Result, type);
                var res = s.Results ?? throw new SwException(ErrorCodes.InvalidArgument, $"The study '{name}' has no results.",
                    "Run it again with keepStudy:true (results are deleted with the study otherwise).");
                var checks = new List<string>();
                var plot = res.CreatePlot(spec.Type, spec.Component, spec.Units, false, out int err);
                if (plot is null || err != 0)
                    throw new SwException(ErrorCodes.FeatureFailed, $"Simulation could not make the {spec.Result} plot ({Name<swsResultPlotErrorCode_e>(err)}).");
                // VERIFIED: CreatePlot ignores its unit (a displacement plot came in ångström); setting it afterwards works.
                if (spec.Result != "mode")
                {
                    int ue = plot.SetComponentUnitAndValueByElem(spec.Component, spec.Units, false);
                    if (ue != 0) throw new SwException(ErrorCodes.FeatureFailed, $"The {spec.Result} plot refused the unit {spec.Unit} ({Name<swsResultPlotErrorCode_e>(ue)}).");
                }
                int? mode = null;
                if (spec.Result == "mode")
                {
                    mode = q.Mode ?? 1;
                    int e = plot.SetModeShape(mode.Value);
                    if (e != 0) throw new SwException(ErrorCodes.InvalidArgument, $"The study has no mode {mode} ({Name<swsResultPlotErrorCode_e>(e)}).");
                }
                bool deformed = q.Deformed ?? spec.DeformedByDefault;
                plot.ShowDeformedPlot(deformed, 0, 0, true);
                int act = plot.ActivatePlot();
                if (act != 0) throw new SwException(ErrorCodes.FeatureFailed, $"The plot could not be shown ({Name<swsResultPlotErrorCode_e>(act)}).");
                plot.GetPlotName(out string plotName);
                checks.Add($"{spec.Result} plot '{plotName}' of '{name}' made and shown{(mode is int md ? $" (mode {md})" : "")}{(deformed ? ", deformed" : "")}");
                // The range it shows, against the study's own results where they can be read.
                var shown = SimulationMath.Range(ComArgs.Doubles((object?)plot.GetMinMaxResultValues(out int re)));
                double[]? range = shown is null || re != 0 ? null : [SimulationMath.Sig(shown.Min), SimulationMath.Sig(shown.Max)];
                var expect = spec.Result switch
                {
                    "von_mises" => SimulationMath.Range(res.StressRange(spec.Component, NPerMm2, out _)),
                    "displacement" => SimulationMath.Range(res.DisplacementRange(spec.Component, Mm, out _)),
                    "temperature" or "heat_flux" => SimulationMath.Range(res.ThermalRange(spec.Component, spec.Units, out _)),
                    _ => null,
                };
                if (shown is not null && expect is not null)
                {
                    double scale = Math.Max(Math.Abs(expect.Max), 1e-12);
                    if (Math.Abs(shown.Max - expect.Max) > 1e-6 * scale || Math.Abs(shown.Min - expect.Min) > 1e-6 * scale + 1e-12)
                        throw new SwException(ErrorCodes.VerifyFailed,
                            $"The plot shows {SimulationMath.F(shown.Min)}..{SimulationMath.F(shown.Max)} {spec.Unit} but the results are {SimulationMath.F(expect.Min)}..{SimulationMath.F(expect.Max)}.");
                    checks.Add($"the plot shows the results' range {SimulationMath.F(SimulationMath.Sig(expect.Min, 5))}..{SimulationMath.F(SimulationMath.Sig(expect.Max, 5))} {spec.Unit}");
                }
                else if (range is null) ctx.Warn("The plot's range could not be read.");
                // The image: view_screenshot's capture (inline on this thread), keeping the plot's colours (display mode as is).
                var shot = new ViewService(session).ScreenshotAsync(ctx.DocEntry.Handle, q.View ?? "iso", true, "current", true, true,
                    q.MaxSizePx, q.File, q.Overwrite).GetAwaiter().GetResult();
                checks.Add($"image {shot.WidthPx} x {shot.HeightPx} px");
                return new SimPlotResult
                {
                    Doc = title, Study = name, StudyType = SimulationMath.StudyType(type), Result = spec.Result, Plot = plotName, Mode = mode,
                    Range = range, Unit = spec.Unit, WidthPx = shot.WidthPx, HeightPx = shot.HeightPx, SavedTo = shot.SavedTo, Checks = checks, Png = shot.Png,
                };
            });
        }, RunOptions.Read(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(300, session.Options.DefaultTimeout.TotalSeconds)) }, ct);
    }
}
