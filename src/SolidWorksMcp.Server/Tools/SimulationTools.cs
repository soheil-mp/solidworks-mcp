using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>SOLIDWORKS Simulation: linear static, frequency and buckling studies; listing and deleting studies.</summary>
[McpServerToolType, Toolset("simulation")]
public sealed class SimulationTools(SwSession session)
{
    private const string DocArg = "Document (handle, title or path); default: the target document.";
    private readonly SimulationService _svc = new(session);

    [McpServerTool(Name = "sim_static", Title = "Static stress analysis (FEA)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("FEA / linear static stress analysis of a part with SOLIDWORKS Simulation: fixed faces (also roller, symmetry, hinge " +
                 "fixtures), pressure, force and gravity loads, high-quality mesh, solve. Returns max von Mises stress (MPa) and where, max " +
                 "displacement (mm) and where, displacement ranges per axis, factor of safety (yield / max von Mises), fixture reactions " +
                 "against the loads (gravity: the weight), mesh counts and timings. Verified: fixtures and loads read back, mesh current, " +
                 "solver status 0. The study is deleted afterwards unless keepStudy; the add-in is loaded if needed and unloaded again. " +
                 "Needs a material (the part's, or 'material'). Blocks SOLIDWORKS while it solves (progress is reported).")]
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

    [McpServerTool(Name = "sim_run", Title = "Frequency or buckling analysis (FEA)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("FEA beyond static stress with SOLIDWORKS Simulation. frequency: the part's natural frequencies (Hz), lowest first, with " +
                 "fixed faces (none = free-free: six rigid-body modes near 0 Hz). buckling: the load factors of the given compressive loads " +
                 "(the loads times the first factor buckle the part; below 1 they already do). Same fixtures, loads, material and mesh as " +
                 "sim_static (which does static stress); verified: inputs read back, mesh current, solver status 0, results consistent. " +
                 "The study is deleted afterwards unless keepStudy. Blocks SOLIDWORKS while it solves (progress is reported).")]
    public Task<string> SimRun(
        IProgress<ProgressNotificationValue> progress,
        [Description("frequency | buckling")] string type,
        [Description("Faces held fixed, e.g. [{at:[0,5,5]}] (frequency: may be left out).")] EntityRef[]? fixedFaces = null,
        [Description("More fixtures, as for sim_static: roller, symmetry, hinge.")] SimFixture[]? fixtures = null,
        [Description("buckling: the loads, e.g. [{kind:'force',faces:[{at:…}],valueN:1000,direction:[0,-1,0]}].")] SimLoad[]? loads = null,
        [Description("How many modes (default: 5 frequencies, 1 buckling mode).")] int? modes = null,
        [Description("Material to assign first, as material_list names it; default: the part's own.")] string? material = null,
        [Description("Global element size in mm (default: SOLIDWORKS' default).")] double? meshSizeMm = null,
        [Description("Keep the study in the document (sim_delete removes it later).")] bool keepStudy = false,
        [Description("Study name (default \"MCP Frequency xxxx\" / \"MCP Buckling xxxx\").")] string? studyName = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var heartbeat = progress is null ? null : new PhaseProgress(progress);
        var task = _svc.ModesAsync(new SimStaticRequest
        {
            Doc = doc, FixedFaces = fixedFaces, Fixtures = fixtures, Loads = loads, Material = material, MeshSizeMm = meshSizeMm, KeepStudy = keepStudy,
            StudyName = studyName, Modes = modes,
        }, type, heartbeat is null ? null : heartbeat.Set, cancellationToken);
        return Output.Json(task, heartbeat, "sim_run");
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
