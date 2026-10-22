using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("session")]
public sealed class HelpTools(SwSession session)
{
    [McpServerTool(Name = "sw_help", Title = "Help and guides", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Help, guides and recipes for this server: conventions (units, axes, points, documents), entityref (referencing faces, " +
                 "edges and planes), workflow_plate, workflow_enclosure, editing (existing parts, STEP files), recovery (every error code " +
                 "and what to do), tools (all tools with their parameters). Without a topic: the index. Makes no SOLIDWORKS call.")]
    public string SwHelp(
        [Description("conventions | entityref | workflow_plate | workflow_enclosure | editing | recovery | tools; default: the index.")] string? topic = null) =>
        ResultJson.Serialize(Guide.Get(topic, session.IsAttached ? session.Revision : null));
}
