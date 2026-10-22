using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("automation")]
public sealed class AutomationTools(SwSession session)
{
    private readonly AutomationService _svc = new(session);

    [McpServerTool(Name = "vba_run", Title = "Run VBA (last resort)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [McpMeta("anthropic/requiresUserInteraction", true)]
    [Description("Runs SolidWorks BASIC code (VBA-like) inside SOLIDWORKS — a last resort when no tool fits. Off unless the user set " +
                 "SWMCP_VBA=on. The API inside uses METERS and RADIANS: helpers MM(x) and DEG(a) convert. Predefined: swApp, swDoc (the " +
                 "target document), Echo(text) for output. Declare variables with Dim x As Object/Double/Long/String; don't name anything " +
                 "Log, Imp, Eqv or Mod. Returns the output, errors (trapped or from the error dialog), whether it compiled, and features added.")]
    public Task<string> VbaRun(
        IProgress<ProgressNotificationValue> progress,
        [Description("Statements to run (the body of a Sub; no Sub/End Sub needed).")] string code,
        [Description("Time limit in seconds (5-600).")] int timeoutS = 50,
        [Description("Document (handle, title or path) bound to swDoc; default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.VbaRunAsync(doc, code, timeoutS, cancellationToken), progress, "vba_run");
}
