using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Weldments: structural members along sketch lines, and their trims, end caps, gussets and weld beads.</summary>
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

    [McpServerTool(Name = "weldment_finish", Title = "Weldment trim, end cap, gusset, weld bead", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Finishes a weldment's members. trim: trims or extends members (bodies) to a boundary (bodies or faces), or joins " +
                 "two at a corner (miter/butt1/butt2); trimmed bodies are renamed after the trim. end_cap: plates on open member ends. " +
                 "gusset: a triangular plate in the corner of two flat faces. bead: a weld bead (cosmetic, as weld tables count them) " +
                 "where two face sets meet. Verified against the geometry (plate thickness and side, the triangle, the weld's edges and " +
                 "length); else rolled back.")]
    public Task<string> WeldmentFinish(
        IProgress<ProgressNotificationValue> progress,
        [Description("trim | end_cap | gusset | bead")] string kind,
        [Description("trim: members to trim or extend: {name:'Structural Member1[2]'} (body_list) or {at:[x,y,z]} on a face.")] EntityRef[]? bodies = null,
        [Description("trim: where they end: bodies, or faces.")] EntityRef[]? boundary = null,
        [Description("trim: trim (default) | miter | butt1 | butt2")] string? corner = null,
        [Description("trim: weld gap (mm).")] double gapMm = 0,
        [Description("trim: cut to the boundary's shape (coped) instead of its plane.")] bool coped = false,
        [Description("end_cap: open end faces; gusset: the two faces it joins; bead: faces welded from.")] EntityRef[]? faces = null,
        [Description("bead: faces welded to.")] EntityRef[]? faces2 = null,
        [Description("end_cap, gusset: plate thickness.")] double? thicknessMm = null,
        [Description("end_cap: cap outline inset from the member's outer edges.")] double offsetMm = 0,
        [Description("end_cap: outward (default, beyond the end) | inward (member gets shorter) | internal (in the bore)")] string? direction = null,
        [Description("end_cap internal: depth inside the end.")] double insetMm = 0,
        [Description("gusset: legs [along faces[0], along faces[1]].")] double[]? legsMm = null,
        [Description("gusset: start | center (default) | end of the joint.")] string? location = null,
        [Description("gusset: both (default) | inner | outer; bead: selection (default) | both | all_around")] string? side = null,
        [Description("bead: weld size.")] double? sizeMm = null,
        [Description("bead: follow tangent edges.")] bool tangent = false,
        [Description("bead: intermittent, beads this long...")] double? lengthMm = null,
        [Description("...one every pitchMm.")] double? pitchMm = null,
        [Description("Keep a failed result (for diagnosis).")] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FinishAsync(new WeldmentFinishRequest
        {
            Doc = doc, Kind = kind, Bodies = bodies, Boundary = boundary, Corner = corner, GapMm = gapMm, Coped = coped, Faces = faces,
            Faces2 = faces2, ThicknessMm = thicknessMm, OffsetMm = offsetMm, Direction = direction, InsetMm = insetMm, LegsMm = legsMm, Location = location, Side = side,
            SizeMm = sizeMm, Tangent = tangent, LengthMm = lengthMm, PitchMm = pitchMm,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "weldment_finish");
}
