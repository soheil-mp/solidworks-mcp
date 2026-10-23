using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>
/// Drawings (DESIGN §4.10): sheet setup, standard / projected / section / detail views, model and manual dimensions, notes,
/// BOM tables, center marks and a read-only overview. Sheet positions are mm from the sheet's lower-left corner; model
/// references (drw_dimension_add) are in the view's model, in model mm.
/// </summary>
[McpServerToolType, Toolset("drawing")]
public sealed class DrawingTools(SwSession session)
{
    private const string DocDesc = "Drawing (handle, title or path); default: the target document.";
    private const string ParentDesc = "Parent view name, e.g. \"Drawing View1\" (drw_info lists views).";
    private const string LabelDesc = "View label letter; default: the next free one.";
    private const string AtDesc = "Center of the new view [x,y] sheet mm; default: free space beside the parent.";

    private readonly DrawingService _svc = new(session);

    [McpServerTool(Name = "drw_sheet_setup", Title = "Drawing sheet setup", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sheet size / paper, orientation, scale, projection (first or third angle) and sheet format (title block) of the current " +
                 "sheet; omitted settings stay. Verified by reading the sheet back (a mismatch restores the previous setup).")]
    public async Task<string> DrwSheetSetup(
        [Description("a4 | a3 | a2 | a1 | a0 | a | b | c | d | e | user")] string? size = null,
        [Description("landscape | portrait")] string? orientation = null,
        [Description("Size user: width in mm.")] double? widthMm = null,
        [Description("Size user: height in mm.")] double? heightMm = null,
        [Description("Sheet scale, e.g. \"1:2\".")] string? scale = null,
        [Description("first_angle | third_angle; default with a new format: iso first, ansi third.")] string? projection = null,
        [Description("iso | ansi | none | path of a .slddrt sheet format.")] string? format = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.SheetSetupAsync(new DrwSheetRequest
        {
            Doc = doc, Size = size, Orientation = orientation, WidthMm = widthMm, HeightMm = heightMm, Scale = scale,
            Projection = projection, Format = format,
        }, cancellationToken));

    [McpServerTool(Name = "drw_views_standard", Title = "Standard drawing views", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Standard / orthographic views (front, top, right, iso, ...) of a saved part or assembly, laid out for the sheet's " +
                 "projection without overlaps and scaled to fit unless scale is given. Verified per view: model, orientation and scale read " +
                 "back. Returns each view's name, centerMm, boundsMm (outline) and modelBoxMm (the model box on the sheet).")]
    public Task<string> DrwViewsStandard(
        IProgress<ProgressNotificationValue> progress,
        [Description("Saved .sldprt/.sldasm path, or an open saved document (handle or title).")] string model,
        [Description("front | top | right | iso | back | bottom | left; default [front, top, right, iso].")] string[]? views = null,
        [Description("View scale, e.g. \"1:2\"; default: the sheet scale if it fits, else a standard scale that does.")] string? scale = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ViewsStandardAsync(model, views, scale, doc, cancellationToken), progress, "drw_views_standard");

    [McpServerTool(Name = "drw_view_projected", Title = "Projected view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Projected view of a parent view on one side of it (the sheet's first/third-angle projection decides what it shows). " +
                 "Verified: a projected view on that side, looking perpendicular to the parent; otherwise removed.")]
    public Task<string> DrwViewProjected(
        IProgress<ProgressNotificationValue> progress,
        [Description(ParentDesc)] string parent,
        [Description("left | right | up | down: where the new view goes.")] string direction,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ProjectedAsync(parent, direction, doc, cancellationToken), progress, "drw_view_projected");

    [McpServerTool(Name = "drw_view_section", Title = "Section view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Section view (A-A) along a straight cut line across the parent view, in sheet mm; the line must cross the whole part " +
                 "(see modelBoxMm in drw_info). Verified: a section view looking along the cutting-plane normal, showing the cut extents; " +
                 "otherwise removed.")]
    public Task<string> DrwViewSection(
        IProgress<ProgressNotificationValue> progress,
        [Description(ParentDesc)] string parent,
        [Description("Cut line start [x,y] sheet mm, outside the part.")] double[] lineStartMm,
        [Description("Cut line end [x,y] sheet mm, outside the part.")] double[] lineEndMm,
        [Description(LabelDesc)] string? label = null,
        [Description(AtDesc)] double[]? atMm = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.SectionAsync(parent, lineStartMm, lineEndMm, label, atMm, doc, cancellationToken), progress, "drw_view_section");

    [McpServerTool(Name = "drw_view_detail", Title = "Detail view", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Detail view: an enlarged circular region of the parent view (center and radius in sheet mm). Verified: a detail view at " +
                 "the scale, otherwise removed; warns when it shows no edges (detail views looked empty in earlier tests: check a PDF).")]
    public Task<string> DrwViewDetail(
        IProgress<ProgressNotificationValue> progress,
        [Description(ParentDesc)] string parent,
        [Description("Circle center [x,y] sheet mm, on the part.")] double[] centerMm,
        [Description("Circle radius in sheet mm.")] double radiusMm,
        [Description("Detail scale, e.g. \"2:1\"; default: twice the parent's.")] string? scale = null,
        [Description(LabelDesc)] string? label = null,
        [Description(AtDesc)] double[]? atMm = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DetailAsync(parent, centerMm, radiusMm, scale, label, atMm, doc, cancellationToken), progress, "drw_view_detail");

    [McpServerTool(Name = "drw_dimensions_auto", Title = "Insert model dimensions", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Model items / auto dimensions: inserts the model's sketch and feature dimensions (marked for drawing) into the views. " +
                 "Returns the dimensions added per view, counted before and after; none comes with a hint why.")]
    public Task<string> DrwDimensionsAuto(
        IProgress<ProgressNotificationValue> progress,
        [Description("View names; default: every view.")] string[]? views = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DimensionsAutoAsync(views, doc, cancellationToken), progress, "drw_dimensions_auto");

    [McpServerTool(Name = "drw_dimension_add", Title = "Drawing dimension", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Dimension edges or vertices of the view's model in a drawing view (a reference dimension): linear, horizontal, vertical, " +
                 "radial, diameter or angular. References are in model mm, e.g. a:{type:'edge',at:[0,30,20]}. Verified: the value SOLIDWORKS " +
                 "reads equals the geometry, otherwise removed.")]
    public async Task<string> DrwDimensionAdd(
        [Description("View name (drw_info lists views).")] string view,
        [Description("Edge or vertex of the view's model.")] EntityRef a,
        [Description("Second edge or vertex (distance or angle).")] EntityRef? b = null,
        [Description("linear | horizontal | vertical | radial | diameter | angular")] string kind = "linear",
        [Description("Dimension text [x,y] sheet mm; default: beside the view.")] double[]? textAtMm = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DimensionAddAsync(view, a, b, kind, textAtMm, doc, cancellationToken));

    [McpServerTool(Name = "drw_note_add", Title = "Drawing note", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Note / text on the sheet, or in a view (it then moves with the view), at [x,y] sheet mm; \\n starts a new line. " +
                 "Verified: text and position read back.")]
    public async Task<string> DrwNoteAdd(
        [Description("Note text.")] string text,
        [Description("Position [x,y] sheet mm, from the lower-left corner.")] double[] atMm,
        [Description("Text height in mm.")] double? heightMm = null,
        [Description("View to attach the note to; default: the sheet.")] string? view = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.NoteAddAsync(text, atMm, heightMm, view, doc, cancellationToken));

    [McpServerTool(Name = "drw_annotation_add", Title = "Drawing annotation", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("GD&T and symbols in a view, on an edge of the view's model (model mm): datum feature (label), surface finish " +
                 "(roughness Ra), geometric tolerance frame (characteristic, tolerance, Ø, MMC/LMC, datums) or a BOM balloon. " +
                 "Verified: label, roughness, frame or balloon text and the attachment read back; otherwise removed.")]
    public async Task<string> DrwAnnotationAdd(
        [Description("datum | surface_finish | gtol | balloon")] string kind,
        [Description("View name (drw_info lists views).")] string view,
        [Description("Edge or vertex it points to (model mm), e.g. {type:'edge',at:[0,20,10]}; gtol: optional.")] EntityRef? at = null,
        [Description("Symbol position [x,y] sheet mm; default: beside the edge, outside the view.")] double[]? atMm = null,
        [Description("datum: the letter, e.g. A (default).")] string? label = null,
        [Description("surface_finish: roughness Ra in µm, e.g. \"1.6\".")] string? roughness = null,
        [Description("surface_finish: basic (default) | machining_required | no_machining")] string? finish = null,
        [Description("gtol: flatness, straightness, circularity, cylindricity, line_profile, surface_profile, parallelism, perpendicularity, angularity, position, concentricity, symmetry, circular_runout, total_runout")] string? characteristic = null,
        [Description("gtol: tolerance in mm, e.g. \"0.05\".")] string? tolerance = null,
        [Description("gtol: a diameter (Ø) tolerance zone.")] bool diameter = false,
        [Description("gtol: none (default) | mmc | lmc")] string? material = null,
        [Description("gtol: datum letters, e.g. [\"A\",\"B\"].")] string[]? datums = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.AnnotationAddAsync(new DrwAnnotationRequest
        {
            Doc = doc, Kind = kind, View = view, At = at, AtMm = atMm, Label = label, Roughness = roughness, Finish = finish,
            Characteristic = characteristic, Tolerance = tolerance, Diameter = diameter, Material = material, Datums = datums,
        }, cancellationToken));

    [McpServerTool(Name = "drw_bom_add", Title = "Bill of materials", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("BOM / bill of materials table (top level) for the model shown in a view. Returns the rows and columns read back from the table.")]
    public Task<string> DrwBomAdd(
        IProgress<ProgressNotificationValue> progress,
        [Description("View whose model is listed.")] string view,
        [Description("Table top-right corner [x,y] sheet mm; default: the sheet's top-right corner.")] double[]? atMm = null,
        [Description("BOM template (.sldbomtbt); default: bom-standard.")] string? template = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.BomAddAsync(view, atMm, template, doc, cancellationToken), progress, "drw_bom_add");

    [McpServerTool(Name = "drw_center_marks", Title = "Center marks", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Center marks on the holes seen face-on in drawing views. Returns the center marks added per view, counted before and " +
                 "after; none comes with a hint why.")]
    public Task<string> DrwCenterMarks(
        IProgress<ProgressNotificationValue> progress,
        [Description("View names; default: the views of the current sheet.")] string[]? views = null,
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.CenterMarksAsync(views, doc, cancellationToken), progress, "drw_center_marks");

    [McpServerTool(Name = "drw_info", Title = "Drawing info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Drawing overview: sheets (size, scale, projection, format) and their views (name, type, orientation, scale, centerMm, " +
                 "boundsMm, modelBoxMm, model, configuration, parent) with annotation counts (dimensions, notes, tables, center marks). " +
                 "Positions are sheet mm from the lower-left corner.")]
    public async Task<string> DrwInfo(
        [Description(DocDesc)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.InfoAsync(doc, cancellationToken));
}
