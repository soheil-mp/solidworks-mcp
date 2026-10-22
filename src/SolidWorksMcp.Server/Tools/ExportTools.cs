using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("export")]
public sealed class ExportTools(SwSession session)
{
    private readonly ExportService _svc = new(session);

    [McpServerTool(Name = "model_export", Title = "Export (STEP, STL, PDF, ...)", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Export, save as STEP/STL/IGES/Parasolid/3MF/PNG/JPG/eDrawings/PDF, and DWG/DXF from drawings; the file extension picks the " +
                 "format. Relative paths go under Documents\\solidworks-mcp; an existing file needs overwrite=true (backed up first unless this " +
                 "server wrote it). The export options are set only for this call and restored. Verified from the file itself: written by this " +
                 "call, not empty, header and details (STEP schema = AP, STL triangle count and size formula, image size). Returns the path " +
                 "actually written (e.g. .STL in capitals).")]
    public Task<string> ModelExport(
        IProgress<ProgressNotificationValue> progress,
        [Description("Output file, e.g. bracket.step, part.stl, view.png (the extension is added from 'format' when missing).")] string path,
        [Description("step | iges | parasolid | stl | 3mf | png | jpg | eprt | pdf | dwg | dxf; default: from the extension.")] string? format = null,
        [Description("STEP application protocol: 203, 214 (AUTOMOTIVE_DESIGN) or 242.")] int stepAp = 214,
        [Description("STL: binary (true) or ASCII (false).")] bool stlBinary = true,
        [Description("STL resolution: coarse | fine | custom.")] string stlQuality = "fine",
        [Description("STL custom: maximum chordal deviation in mm.")] double? stlDeviationMm = null,
        [Description("STL custom: angle tolerance in degrees (0-90).")] double? stlAngleDeg = null,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ExportAsync(new ExportRequest
        {
            Path = path, Format = format, StepAp = stepAp, StlBinary = stlBinary, StlQuality = stlQuality,
            StlDeviationMm = stlDeviationMm, StlAngleDeg = stlAngleDeg, Overwrite = overwrite, Doc = doc,
        }, cancellationToken), progress, "model_export");
}
