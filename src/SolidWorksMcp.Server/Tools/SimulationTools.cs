using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>SOLIDWORKS Simulation: linear static studies, listing and deleting studies.</summary>
[McpServerToolType, Toolset("simulation")]
public sealed class SimulationTools(SwSession session)
{
    private const string DocArg = "Document (handle, title or path); default: the target document.";
    private readonly SimulationService _svc = new(session);

    [McpServerTool(Name = "sim_static", Title = "Static stress analysis (FEA)", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("FEA / linear static stress analysis of a part with SOLIDWORKS Simulation: fixed faces, pressure or force loads, high-quality " +
                 "mesh, solve. Returns max von Mises stress (MPa) and where, max displacement (mm) and where, displacement ranges per axis, " +
                 "factor of safety (yield / max von Mises), fixture reactions against the loads, mesh counts and timings. Verified: fixture " +
                 "and loads read back, mesh current, solver status 0. The study is deleted afterwards unless keepStudy; the add-in is " +
                 "loaded if needed and unloaded again. Needs a material (the part's, or 'material'). Blocks SOLIDWORKS while it solves " +
                 "(progress is reported).")]
    public Task<string> SimStatic(
        IProgress<ProgressNotificationValue> progress,
        [Description("Faces held fixed (all degrees of freedom), e.g. [{at:[0,5,5]}].")] EntityRef[] fixedFaces,
        [Description("Loads, e.g. [{kind:'pressure',faces:[{at:[50,10,5]}],valueMpa:0.1}] or [{kind:'force',faces:[...],valueN:100,direction:[0,-1,0]}].")]
        SimLoad[] loads,
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
            Doc = doc, FixedFaces = fixedFaces, Loads = loads, Material = material, MeshSizeMm = meshSizeMm, KeepStudy = keepStudy,
            StudyName = studyName,
        }, heartbeat is null ? null : heartbeat.Set, cancellationToken);
        return Output.Json(task, heartbeat, "sim_static");
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
