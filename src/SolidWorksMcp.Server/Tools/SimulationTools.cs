using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>SOLIDWORKS Simulation: static, frequency, buckling and thermal studies, result plots, listing and deleting studies.</summary>
[McpServerToolType, Toolset("simulation")]
public sealed class SimulationTools(SwSession session)
{
    private const string DocArg = "Document (handle, title or path); default: the target document.";
    private readonly SimulationService _svc = new(session);

    [McpServerTool(Name = "sim_static", Title = "Static stress analysis (FEA)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("FEA / linear static stress analysis of a part or assembly with SOLIDWORKS Simulation: fixed faces (also roller, symmetry, hinge " +
                 "fixtures), pressure, force and gravity loads, high-quality mesh, solve. Returns max von Mises stress (MPa) and where, max " +
                 "displacement (mm) and where, displacement ranges per axis, factor of safety (yield / max von Mises), fixture reactions " +
                 "against the loads (gravity: the weight), mesh counts and timings. Verified: fixtures and loads read back, mesh current, " +
                 "solver status 0. The study is deleted afterwards unless keepStudy; the add-in is loaded if needed and unloaded again. " +
                 "Needs a material (the part's, or 'material'; an assembly's parts their own). Blocks SOLIDWORKS while it solves.")]
    public Task<string> SimStatic(
        IProgress<ProgressNotificationValue> progress,
        [Description("Loads, e.g. [{kind:'pressure',faces:[{at:[50,10,5]}],valueMpa:0.1}], [{kind:'force',faces:[...],valueN:100,direction:[0,-1,0]}] " +
                     "or [{kind:'gravity',direction:'-y'}] (valueG, default 1).")]
        SimLoad[] loads,
        [Description("Faces held fixed (all degrees of freedom), e.g. [{at:[0,5,5]}].")] EntityRef[]? fixedFaces = null,
        [Description("More fixtures: [{kind:'roller'|'symmetry' (flat faces) |'hinge' (cylindrical faces)|'fixed', faces:[…]}].")] SimFixture[]? fixtures = null,
        [Description("Material to assign first, as material_list names it (e.g. \"AISI 1020\"); default: the part's own.")] string? material = null,
        [Description("Global element size in mm (default: SOLIDWORKS' default for the part).")] double? meshSizeMm = null,
        [Description("Keep the study and its results in the document (sim_delete removes it later).")] bool keepStudy = false,
        [Description("Study name (default \"MCP Static xxxx\").")] string? studyName = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var heartbeat = progress is null ? null : new PhaseProgress(progress);
        var task = _svc.StaticAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = fixedFaces, Fixtures = fixtures, Loads = loads, Material = material, MeshSizeMm = meshSizeMm, KeepStudy = keepStudy,
            StudyName = studyName,
        }, heartbeat is null ? null : heartbeat.Set, cancellationToken);
        return Output.Json(task, heartbeat, "sim_static");
    }

    [McpServerTool(Name = "sim_run", Title = "Frequency, buckling or thermal analysis (FEA)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("FEA beyond static stress (sim_static). frequency: natural frequencies (Hz), lowest first (no fixtures: six rigid-body " +
                 "modes at 0 Hz). buckling: load factors (the loads times the first one buckle the part). thermal: steady-state " +
                 "temperatures (°C) and heat flux. Verified: inputs read back, mesh current, solver status 0, results consistent.")]
    public Task<string> SimRun(
        IProgress<ProgressNotificationValue> progress,
        [Description("frequency | buckling | thermal")] string type,
        [Description("Faces held fixed (frequency: optional).")] EntityRef[]? fixedFaces = null,
        [Description("More fixtures, as for sim_static.")] SimFixture[]? fixtures = null,
        [Description("buckling: the loads, as for sim_static.")] SimLoad[]? loads = null,
        [Description("Modes (default 5 frequencies, 1 buckling mode).")] int? modes = null,
        [Description("thermal: boundary conditions on faces.")] SimThermalLoad[]? thermal = null,
        [Description("Material to assign first (material_list).")] string? material = null,
        [Description("Element size in mm (default: SOLIDWORKS').")] double? meshSizeMm = null,
        [Description("Keep the study (sim_delete removes it).")] bool keepStudy = false,
        [Description("Study name.")] string? studyName = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var heartbeat = progress is null ? null : new PhaseProgress(progress);
        var q = new SimStaticRequest
        {
            Doc = doc, FixedFaces = fixedFaces, Fixtures = fixtures, Loads = loads, Material = material, MeshSizeMm = meshSizeMm, KeepStudy = keepStudy,
            StudyName = studyName, Modes = modes, Thermal = thermal,
        };
        if (string.Equals(type?.Trim(), "thermal", StringComparison.OrdinalIgnoreCase))
            return Output.Json(_svc.ThermalAsync(q, heartbeat is null ? null : heartbeat.Set, cancellationToken), heartbeat, "sim_run");
        return Output.Json(_svc.ModesAsync(q, type, heartbeat is null ? null : heartbeat.Set, cancellationToken), heartbeat, "sim_run");
    }

    [McpServerTool(Name = "sim_plot", Title = "Simulation result plot (image)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Image of a result plot of a study kept with keepStudy (sim_static / sim_run): von_mises or displacement (static), " +
                 "mode (frequency, buckling: pass mode), temperature or heat_flux (thermal); deformed shape by default. Verified: the range " +
                 "the plot shows equals the study's results. Returns the plot's range and the PNG.")]
    public async Task<CallToolResult> SimPlot(
        [Description("Study name (default: the one this server kept).")] string? study = null,
        [Description("von_mises | displacement | mode | temperature | heat_flux (default by the study's type).")] string? result = null,
        [Description("frequency / buckling: the mode (default 1).")] int? mode = null,
        [Description("Show the deformed shape.")] bool? deformed = null,
        [Description("iso | front | top | right | … | current")] string? view = null,
        [Description("Longest image edge in pixels (64-2048).")] int maxSizePx = 1024,
        [Description("Also save the PNG here (relative paths go under Documents\\solidworks-mcp).")] string? file = null,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var r = await _svc.PlotAsync(new SimPlotRequest
        {
            Doc = doc, Study = study, Result = result, Mode = mode, Deformed = deformed, View = view, MaxSizePx = maxSizePx, File = file, Overwrite = overwrite,
        }, cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = ResultJson.Serialize(r) }, ImageContentBlock.FromBytes(r.Png, "image/png")],
        };
    }

    [McpServerTool(Name = "sim_list", Title = "List simulation studies", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Simulation studies of a part or assembly: name, type, configuration, mesh state and counts, whether results exist (static: " +
                 "max displacement), active, and whether this server created it. Doesn't load the Simulation add-in: when it isn't loaded, " +
                 "says so.")]
    public async Task<string> SimList(
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ListAsync(doc, cancellationToken));

    [McpServerTool(Name = "sim_delete", Title = "Delete simulation studies", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes Simulation studies: by default the ones this server created in the document (sim_static keepStudy); other studies " +
                 "only by name, and only in documents this server owns. Verified: gone from the study list. Unloads the add-in again if " +
                 "this server loaded it and no study is left.")]
    public Task<string> SimDelete(
        IProgress<ProgressNotificationValue> progress,
        [Description("Study names (default: the studies this server created in the document).")] string[]? studies = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DeleteAsync(doc, studies, cancellationToken), progress, "sim_delete");

    /// <summary>Adds the running phase (meshing, solving, …) to the heartbeat messages of <see cref="Output.Json"/>.</summary>
    private sealed class PhaseProgress(IProgress<ProgressNotificationValue> inner) : IProgress<ProgressNotificationValue>
    {
        private volatile string? _phase;

        public void Set(string phase) => _phase = phase;

        public void Report(ProgressNotificationValue value) =>
            inner.Report(_phase is { } p
                ? new ProgressNotificationValue { Progress = value.Progress, Total = value.Total, Message = $"{value.Message} ({p})" }
                : value);
    }
}
