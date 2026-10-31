using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("inspect")]
public sealed class ViewTools(SwSession session)
{
    private readonly ViewService _svc = new(session);

    [McpServerTool(Name = "view_screenshot", Title = "Screenshot", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Screenshot (inline PNG image) of the target document in a standard view, zoomed to fit, planes hidden. The user's view, " +
                 "display mode, visibility settings, selection and active window are restored afterwards. Use it to check shape and " +
                 "orientation after modeling steps.")]
    public async Task<CallToolResult> ViewScreenshot(
        [Description("iso | front | back | left | right | top | bottom | trimetric | dimetric | current")] string view = "iso",
        [Description("Zoom to fit the model.")] bool zoomToFit = true,
        [Description("shaded_edges | shaded | wireframe | hidden_removed | hidden_grayed | current")] string displayMode = "shaded_edges",
        [Description("Hide reference planes in the image.")] bool hidePlanes = true,
        [Description("Hide sketches in the image.")] bool hideSketches = false,
        [Description("Longest image edge in pixels (64-2048).")] int maxSizePx = 1024,
        [Description("Also save the PNG to this file (relative paths go under Documents\\solidworks-mcp).")] string? file = null,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description("Cut a 3D section for the image: a plane or planar face, e.g. {name:'front'} (removed afterwards).")] EntityRef? section = null,
        [Description("Section: offset from the plane, mm.")] double sectionOffsetMm = 0,
        [Description("Section: keep the other side.")] bool sectionReverse = false,
        [Description("Document (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default)
    {
        var r = await _svc.ScreenshotAsync(doc, view, zoomToFit, displayMode, hidePlanes, hideSketches, maxSizePx, file, overwrite,
            section, sectionOffsetMm, sectionReverse, cancellationToken);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = ResultJson.Serialize(r) },
                ImageContentBlock.FromBytes(r.Png, "image/png"),
            ],
        };
    }
}
