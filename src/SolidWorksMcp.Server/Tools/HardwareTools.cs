using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Standard hardware: ISO screws, nuts, washers, pins and bearings as library parts, and screws fastened into holes.</summary>
[McpServerToolType, Toolset("assembly")]
public sealed class HardwareTools(SwSession session)
{
    private const string FolderDesc = "Library folder for the parts (default: 'hardware' in the output folder).";

    private readonly HardwareService _svc = new(session);

    [McpServerTool(Name = "hardware_part", Title = "Standard part", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Makes (or reuses) a standard part file from the standard's table, without Toolbox: ISO 4762 socket head and ISO 4017 " +
                 "hex head screws, ISO 4032 nuts, ISO 7089 washers, ISO 8734 dowel pins, ISO 15 ball bearings (simplified envelope). " +
                 "Cosmetic thread, Alloy Steel, PartNo and Description for BOMs. Verified: the volume is the table's geometry.")]
    public Task<string> HardwarePart(
        IProgress<ProgressNotificationValue> progress,
        [Description("ISO 4762 | ISO 4017 | ISO 4032 | ISO 7089 | ISO 8734 | ISO 15")] string standard,
        [Description("M5 (screws, nuts, washers), 6 (pins), 6204 (bearings).")] string size,
        [Description("Screws and pins: a preferred length (the error lists them).")] double? lengthMm = null,
        [Description(FolderDesc)] string? folder = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.PartAsync(new HardwarePartRequest { Standard = standard, Size = size, LengthMm = lengthMm, Folder = folder }, cancellationToken),
            progress, "hardware_part");

    [McpServerTool(Name = "asm_fastener", Title = "Fasten screws", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Screws into holes: per hole (its centre on the face the head sits on) inserts an ISO screw, washers and a nut on the " +
                 "far side if asked, each mated concentric with the hole and seated. Without lengthMm a nut gets the shortest screw " +
                 "standing two threads past it. Verified: each part on the hole's axis at its seat; reports grip and protrusion.")]
    public Task<string> AsmFastener(
        IProgress<ProgressNotificationValue> progress,
        [Description("Screw size, e.g. M5.")] string size,
        [Description("Hole centres on the head's face, assembly mm, e.g. [[20,10,15],[-20,10,15]].")] double[][] holes,
        [Description("Screw length, mm (optional with a nut).")] double? lengthMm = null,
        [Description("ISO 4762 (socket head) | ISO 4017 (hex head).")] string standard = "ISO 4762",
        [Description("ISO 7089 washers under the head and the nut.")] bool washer = false,
        [Description("An ISO 4032 nut on the far side (through holes).")] bool nut = false,
        [Description(FolderDesc)] string? folder = null,
        [Description("Assembly (handle, title or path); default: the target document.")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FastenAsync(new AsmFastenerRequest
        {
            Doc = doc, Standard = standard, Size = size, LengthMm = lengthMm, Holes = holes, Washer = washer, Nut = nut, Folder = folder,
        }, cancellationToken), progress, "asm_fastener");
}
