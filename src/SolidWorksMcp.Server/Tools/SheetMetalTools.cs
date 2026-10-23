using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Sheet metal: base flange, edge flange, flatten / fold and the flat-pattern DXF/DWG.</summary>
[McpServerToolType, Toolset("sheetmetal")]
public sealed class SheetMetalTools(SwSession session)
{
    private const string DocDesc = "Document (handle, title or path); default: the target document.";
    private const string KeepDesc = "Keep a failed result (for diagnosis).";

    private readonly SheetMetalService _svc = new(session);

    [McpServerTool(Name = "sheetmetal_base_flange", Title = "Sheet-metal base flange", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sheet-metal base flange / tab: a flat plate from a closed sketch; makes the part sheet metal (Sheet-Metal, " +
                 "Base-Flange, Flat-Pattern). Thickened against the sketch normal (reverse: along it). Verified: the three features, " +
                 "volume = area x thickness, the plate's side, and thickness, bend radius and K-factor read back; else rolled back.")]
    public Task<string> SheetMetalBaseFlange(
        IProgress<ProgressNotificationValue> progress,
        [Description("Sheet thickness in mm.")] double thicknessMm,
        [Description("Default bend radius in mm (inside radius of later bends).")] double bendRadiusMm,
        [Description("K-factor of the bend allowance (0 to 1).")] double kFactor = 0.5,
        [Description("Thicken along the sketch normal.")] bool reverse = false,
        [Description("Closed profile sketch (default: the open sketch, else the last finished one).")] string? sketch = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.BaseFlangeAsync(new BaseFlangeRequest
        {
            Doc = doc, Sketch = sketch, ThicknessMm = thicknessMm, BendRadiusMm = bendRadiusMm, KFactor = kFactor, Reverse = reverse,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "sheetmetal_base_flange");

    [McpServerTool(Name = "sheetmetal_edge_flange", Title = "Sheet-metal edge flange", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sheet-metal edge flange: bends a wall up from straight edges of the sheet's face (experimental: several API " +
                 "variants, 'method' names the one that worked). Verified: volume about length x edge x thickness and the part " +
                 "grew toward the flange; else rolled back.")]
    public Task<string> SheetMetalEdgeFlange(
        IProgress<ProgressNotificationValue> progress,
        [Description("Straight edges of the sheet's top/bottom face, e.g. [{type:'edge',at:[25,0,0]}].")] EntityRef[] edges,
        [Description("Flange length in mm.")] double lengthMm,
        [Description("Bend angle in degrees (0 to 180).")] double angleDeg = 90,
        [Description("material_inside | material_outside | bend_outside")] string position = "material_inside",
        [Description("Bend radius in mm (default: the sheet's).")] double? bendRadiusMm = null,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.EdgeFlangeAsync(new EdgeFlangeRequest
        {
            Doc = doc, Edges = edges, LengthMm = lengthMm, AngleDeg = angleDeg, Position = position, BendRadiusMm = bendRadiusMm,
            KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "sheetmetal_edge_flange");

    [McpServerTool(Name = "sheetmetal_hem", Title = "Sheet-metal hem", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Sheet-metal hem: folds the sheet back on itself along straight edges of its face, closed (flat on the sheet), open " +
                 "(with a gap), teardrop or rolled. Verified: the hem type read back and the added volume about edge x thickness x " +
                 "(length + bend); else rolled back.")]
    public Task<string> SheetMetalHem(
        IProgress<ProgressNotificationValue> progress,
        [Description("Straight edges of the sheet's face, e.g. [{type:'edge',at:[25,0,0]}].")] EntityRef[] edges,
        [Description("closed (default) | open | teardrop | rolled")] string? type = null,
        [Description("closed/open: hem length in mm.")] double? lengthMm = null,
        [Description("open: gap to the sheet in mm.")] double? gapMm = null,
        [Description("teardrop/rolled: bend radius in mm.")] double? radiusMm = null,
        [Description("teardrop/rolled: angle in degrees (default 200 / 180).")] double? angleDeg = null,
        [Description("inside (default) | outside: the hem's material inside or outside the sheet's edge.")] string? position = null,
        [Description("Fold to the other side of the sheet.")] bool reverse = false,
        [Description(KeepDesc)] bool keepOnFailure = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.HemAsync(new HemRequest
        {
            Doc = doc, Edges = edges, Type = type, LengthMm = lengthMm, GapMm = gapMm, RadiusMm = radiusMm, AngleDeg = angleDeg,
            Position = position, Reverse = reverse, KeepOnFailure = keepOnFailure,
        }, cancellationToken), progress, "sheetmetal_hem");

    [McpServerTool(Name = "sheetmetal_flatten", Title = "Flatten / fold sheet metal", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Flatten (flat:true) or fold back (flat:false) a sheet-metal part through its Flat-Pattern feature. Verified: the " +
                 "state read back; when flat, reports the blank's size (two largest box extents, mm) and thickness.")]
    public Task<string> SheetMetalFlatten(
        IProgress<ProgressNotificationValue> progress,
        [Description("true = flat pattern, false = folded.")] bool flat,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.FlattenAsync(doc, flat, cancellationToken), progress, "sheetmetal_flatten");

    [McpServerTool(Name = "sheetmetal_export_flat", Title = "Export flat pattern", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Export the sheet-metal flat pattern to DXF/DWG (optional bend lines); save the part first (doc_save). Relative " +
                 "paths go under Documents\\solidworks-mcp. Verified from the file: written by this call, header, and the DXF " +
                 "extents against the flat blank.")]
    public Task<string> SheetMetalExportFlat(
        IProgress<ProgressNotificationValue> progress,
        [Description("Output file, e.g. bracket_flat.dxf (.dxf or .dwg).")] string path,
        [Description("Include bend lines.")] bool bendLines = true,
        [Description("Replace an existing file.")] bool overwrite = false,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ExportFlatAsync(doc, path, bendLines, overwrite, cancellationToken), progress, "sheetmetal_export_flat");
}
