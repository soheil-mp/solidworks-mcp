using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("documents")]
public sealed class OpenTools(SwSession session)
{
    private readonly OpenService _svc = new(session);

    [McpServerTool(Name = "doc_open", Title = "Open or import a file", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Open a file: .sldprt/.sldasm/.slddrw opens silently without a rebuild prompt; STEP/IGES/Parasolid/SAT/STL is imported as a " +
                 "new part (or assembly) with MMGS units and its bodies verified. The document becomes the target and this server may edit it. A file that " +
                 "is already open is not reopened: it becomes the target with alreadyOpen=true and keeps its access (the user's documents stay " +
                 "read-only). Reports load warnings (missing references, read-only, newer version) and, for imports, bodies and volume.")]
    public Task<string> DocOpen(
        IProgress<ProgressNotificationValue> progress,
        [Description("File to open or import; relative paths are under Documents\\solidworks-mcp.")] string path,
        [Description("Open a SOLIDWORKS file read-only (changes need doc_save mode copy/as).")] bool readOnly = false,
        [Description("Part/assembly configuration to open; default: the one last saved.")] string? configuration = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.OpenAsync(path, readOnly, configuration, cancellationToken), progress, "doc_open");
}
