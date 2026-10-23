using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Weldments: structural members along sketch lines.</summary>
[McpServerToolType, Toolset("weldments")]
public sealed class WeldmentTools(SwSession session)
{
    private const string DocDesc = "Document (handle, title or path); default: the target document.";

    private readonly WeldmentService _svc = new(session);

    [McpServerTool(Name = "weldment_member", Title = "Weldment structural member", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Weldment frame: structural members (tube, pipe, channel, angle, beam profiles) swept along the lines and arcs of a " +
                 "2D or 3D sketch, with mitered or butt corners; makes the part a weldment. A wrong type or size lists the available ones. " +
                 "Verified: one new solid body along each segment, its length there; else rolled back. Returns each member's body and length.")]
    public Task<string> WeldmentMember(
        IProgress<ProgressNotificationValue> progress,
        [Description("Profile type, e.g. \"square tube\", \"rectangular tube\", \"pipe\", \"c channel\", \"angle iron\", \"sb beam\" (iso).")] string type,
        [Description("Profile size, e.g. \"40 x 40 x 4\".")] string size,
        [Description("Profile standard folder: iso (default), ansi, din, jis, …")] string? standard = null,
        [Description("Path sketch (2D or 3D); default: the open sketch, else the last finished one.")] string? sketch = null,
        [Description("Only these segments, e.g. [\"Line1\",\"Line3\"] (sketch_info); default: every line and arc.")] string[]? segments = null,
        [Description("miter (default) | butt1 | butt2 | none")] string? corner = null,
        [Description("Profile rotation about the path in degrees.")] double rotateDeg = 0,
        [Description("Keep a failed result (for diagnosis).")] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MemberAsync(new WeldmentMemberRequest
        {
            Doc = doc, Sketch = sketch, Segments = segments, Standard = standard, Type = type, Size = size, Corner = corner,
            RotateDeg = rotateDeg, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "weldment_member");
}
