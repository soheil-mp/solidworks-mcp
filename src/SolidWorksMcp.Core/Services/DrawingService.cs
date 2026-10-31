using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs

/// <summary>Annotation counts of a view or sheet.</summary>
public sealed class DrwCounts
{
    public int Dimensions { get; init; }
    public int Notes { get; init; }
    public int Tables { get; init; }
    public int CenterMarks { get; init; }
    public int Other { get; init; }
}

/// <summary>A drawing view (sheet coordinates in mm, origin at the sheet's lower-left corner).</summary>
public sealed record DrwView
{
    public required string Name { get; init; }
    /// <summary>named | standard | projected | section | detail | auxiliary | relative | …</summary>
    public required string Type { get; init; }
    /// <summary>front | back | top | bottom | left | right | iso when the view looks along one of them.</summary>
    public string? Orientation { get; init; }
    public required string Scale { get; init; }
    /// <summary>The view's position on the sheet (IView.Position).</summary>
    public double[]? CenterMm { get; init; }
    /// <summary>The view's outline on the sheet [xMin, yMin, xMax, yMax].</summary>
    public double[]? BoundsMm { get; init; }
    /// <summary>The model's bounding box mapped onto the sheet [xMin, yMin, xMax, yMax] (not for detail views).</summary>
    public double[]? ModelBoxMm { get; init; }
    /// <summary>The model file the view shows.</summary>
    public string? Model { get; init; }
    public string? Configuration { get; init; }
    /// <summary>The view this one was derived from (projected, section and detail views).</summary>
    public string? Parent { get; init; }
    public DrwCounts? Annotations { get; init; }
}

/// <summary>A drawing sheet.</summary>
public sealed class DrwSheet
{
    public required string Name { get; init; }
    /// <summary>a4 | a3 | a2 | a1 | a0 | a | b | c | d | e | user.</summary>
    public required string Size { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    /// <summary>landscape | portrait.</summary>
    public required string Orientation { get; init; }
    public required string Scale { get; init; }
    /// <summary>first_angle | third_angle.</summary>
    public required string Projection { get; init; }
    /// <summary>The sheet format file (title block); null = none.</summary>
    public string? Format { get; init; }
    public bool? Current { get; init; }
    public IReadOnlyList<DrwView>? Views { get; init; }
    /// <summary>Annotations placed on the sheet itself (not in a view).</summary>
    public DrwCounts? Annotations { get; init; }
}

public sealed class DrwSheetResult : ToolResult
{
    public required DrwSheet Sheet { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class DrwViewsResult : ToolResult
{
    public required string Model { get; init; }
    public required string Scale { get; init; }
    public required IReadOnlyList<DrwView> Views { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class DrwViewResult : ToolResult
{
    public required DrwView View { get; init; }
    public string? Label { get; init; }
    /// <summary>drw_view_add flat_pattern: the blank's size measured from the view's edges (mm, largest first).</summary>
    public double[]? FlatSizeMm { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>Annotation counts of one view before and after a call.</summary>
public sealed class DrwViewCount
{
    public required string View { get; init; }
    public int Before { get; init; }
    public int After { get; init; }
    public int Added { get; init; }
}

public sealed class DrwCountResult : ToolResult
{
    public int Added { get; init; }
    public required IReadOnlyList<DrwViewCount> Views { get; init; }
    public string? Hint { get; init; }
}

public sealed class DrwDimensionResult : ToolResult
{
    public required string View { get; init; }
    public required string Kind { get; init; }
    /// <summary>The new dimension, e.g. RD1@Drawing View1.</summary>
    public required string Dimension { get; init; }
    /// <summary>The value SOLIDWORKS reads (mm; deg for angular).</summary>
    public double Value { get; init; }
    /// <summary>mm | deg.</summary>
    public required string Unit { get; init; }
    /// <summary>The same quantity measured on the geometry, when it could be computed.</summary>
    public double? Expected { get; init; }
    /// <summary>True for a reference (driven) dimension, the usual kind in drawings.</summary>
    public bool? Reference { get; init; }
    /// <summary>hole_callout: the callout's text parts as SOLIDWORKS holds them.</summary>
    public string? Callout { get; init; }
    public double[]? TextAtMm { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed class DrwNoteResult : ToolResult
{
    public required string Note { get; init; }
    public required string Text { get; init; }
    public double[]? AtMm { get; init; }
    public double? HeightMm { get; init; }
    /// <summary>The view the note belongs to (null = the sheet).</summary>
    public string? View { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class DrwBomResult : ToolResult
{
    public required string View { get; init; }
    /// <summary>Data rows (headers not counted).</summary>
    public int Rows { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Items { get; init; }
    public double[]? AtMm { get; init; }
    public required string Template { get; init; }
    public string? Configuration { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed class DrwInfoResult : ToolResult
{
    public required IReadOnlyList<DrwSheet> Sheets { get; init; }
    public int ViewCount { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>drw_sheet_setup options: null leaves that setting as it is.</summary>
public sealed record DrwSheetRequest
{
    public string? Doc { get; init; }
    /// <summary>a4 | a3 | a2 | a1 | a0 | a | b | c | d | e | user.</summary>
    public string? Size { get; init; }
    /// <summary>landscape | portrait.</summary>
    public string? Orientation { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public string? Scale { get; init; }
    /// <summary>first_angle | third_angle (with a new format family: iso first, ansi third).</summary>
    public string? Projection { get; init; }
    /// <summary>iso | ansi | none | a .slddrt file.</summary>
    public string? Format { get; init; }
    /// <summary>The sheet to set up (it becomes the current one); default: the current sheet.</summary>
    public string? Sheet { get; init; }
}

// ====================================================================== service

/// <summary>
/// Drawings (DESIGN §4.10): sheet setup, standard / projected / section / detail views, model and manual dimensions,
/// notes, BOM tables, center marks and a read-only overview. Views are made from saved models with the verified
/// recipes of docs/solidworks-api-notes.md §9. Drawing objects are not features, so every tool reads back what it made
/// (view type, orientation from IView.ModelToViewTransform, scale, outline; dimension values against the geometry; note
/// text and position; BOM rows) and removes what it created when a hard check fails.
/// </summary>
public sealed partial class DrawingService(SwSession session)
{
    private const double PosTol = 1e-5;  // m on the sheet (0.01 mm)
    private const double DirTol = 1e-3;  // unit-vector comparisons (orientations read from a transform)
    private const int MaxViews = 200;    // drw_info
    private const int MaxBomRows = 50;
    private const string DocDefault = "Default";

    private static readonly int TemplateCustom = (int)swDwgTemplates_e.swDwgTemplateCustom; // 12: a sheet-format file
    private static readonly int TemplateNone = (int)swDwgTemplates_e.swDwgTemplateNone;     // 13: no sheet format

    /// <summary>InsertModelAnnotations3 types of the verified recipe: dimensions + dimensions marked for drawing (32776).</summary>
    private static readonly int ModelDimensionTypes =
        (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing | (int)swInsertAnnotation_e.swInsertDimensions;

    private static TimeSpan Longer(SwSession s, double seconds) => TimeSpan.FromSeconds(Math.Max(s.Options.DefaultTimeout.TotalSeconds, seconds));

    // ================================================================== doc_new type=drawing (DocumentService)

    /// <summary>The paper a new drawing is created on (A3 landscape): ISldWorks.NewDocument's paper size and size (m).</summary>
    internal const int NewPaper = 8;
    internal const double NewWidthM = 0.42, NewHeightM = 0.297;

    /// <summary>Keeps the sheet-format dialog (system option 353) off while a drawing is created.</summary>
    internal static PreferenceScope NewDrawingPrefs(SwContext ctx)
    {
        var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.DrawingShowSheetFormatDialog, false);
        return prefs;
    }

    /// <summary>
    /// doc_new type=drawing: the first sheet becomes A3 landscape with the ISO sheet format (when it is installed),
    /// first-angle projection, 1:1 (SetupSheet5, read back). With <paramref name="keepTemplateSheet"/> (the user named a
    /// template) the template's sheet stays and is only reported. Returns the summary for doc_new's message; a problem
    /// becomes a warning, since the drawing itself exists.
    /// </summary>
    internal static string SetupNewDrawing(SwContext ctx, IModelDoc2 doc, bool keepTemplateSheet = false)
    {
        try
        {
            var drw = ctx.Drawing(doc);
            var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "the new drawing has no sheet");
            if (keepTemplateSheet)
            {
                var kept = SheetInfo(sheet, true);
                return $"; sheet '{kept.Name}' is {Summary(kept)} as the template has it (change it with drw_sheet_setup)";
            }
            var paper = DrawingLayout.Paper("a3", "landscape", null, null);
            string? format = null;
            try
            {
                (format, string? note) = FormatPath(ctx, paper, "iso");
                if (note is not null) ctx.Warn($"Sheet format: {note}.");
            }
            catch (SwException ex) { ctx.Warn($"{ex.Message} The sheet has no format; set one with drw_sheet_setup."); }
            var info = ApplySheet(ctx, drw, sheet, paper, 1, 1, true, format, []);
            return $"; sheet '{info.Name}' is {Summary(info)} (change it with drw_sheet_setup)";
        }
        catch (Exception ex) when (ex is SwException or COMException && !ComArgs.IsDisconnect(ex))
        {
            ctx.Warn($"The sheet could not be set up ({ex.Message}); it keeps the template's sheet. Use drw_sheet_setup.");
            return "";
        }
    }

    // ================================================================== drw_sheet_setup

    public Task<DrwSheetResult> SheetSetupAsync(DrwSheetRequest q, CancellationToken ct = default)
    {
        // Validate what doesn't depend on the sheet before any COM call; the rest is resolved against the sheet as it is.
        if (q.Size is not null) DrawingLayout.Paper(q.Size, q.Orientation, q.WidthMm, q.HeightMm);
        else if (DrawingLayout.Norm(q.Orientation) is not (null or "landscape" or "portrait"))
            throw SwException.InvalidArgument($"Unknown orientation '{q.Orientation}'.", "Use landscape or portrait.");
        if (q.Scale is not null) DrawingLayout.ParseScale(q.Scale, "scale");
        if (DrawingLayout.Norm(q.Projection) is not (null or "first_angle" or "first" or "third_angle" or "third"))
            throw SwException.InvalidArgument($"Unknown projection '{q.Projection}'.", "Use first_angle (ISO) or third_angle (ANSI).");
        string? file = null;
        if (DrawingLayout.Norm(q.Format) is { } fam && fam is not ("iso" or "ansi" or "none"))
        {
            if (!q.Format!.Trim().EndsWith(".slddrt", StringComparison.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"Unknown format '{q.Format}'.", "Use iso, ansi, none or the path of a .slddrt sheet format.");
            file = session.Paths.ForRead(q.Format, "format");
        }
        return session.RunAsync("drw_sheet_setup", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            UseSheet(ctx, drw, q.Sheet);
            var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
            var plan = DrawingLayout.PlanSheet(State(sheet), q.Size, q.Orientation, q.WidthMm, q.HeightMm, q.Scale, q.Projection,
                file is null ? q.Format : null, file);
            string? format = plan.FormatFile;
            if (plan.Family != "file")
            {
                var (path, note) = FormatPath(ctx, plan.Paper, plan.Family);
                if (note is not null) ctx.Warn($"Sheet format: {note}.");
                format = path;
            }
            var checks = new List<string>();
            var info = ApplySheet(ctx, drw, sheet, plan.Paper, plan.Num, plan.Den, plan.FirstAngle, format, checks);
            d.GraphicsRedraw2();
            return new DrwSheetResult { Doc = ctx.Title(d), Sheet = info, Checks = checks };
        }, RunOptions.Write(q.Doc), ct);
    }

    /// <summary>The sheet's current setup (ISheet.GetProperties2 and its sheet-format file).</summary>
    private static SheetState State(ISheet sheet)
    {
        var p = sheet.DrwProps();
        if (p.Length < 7) throw new SwException(ErrorCodes.Internal, "The sheet properties could not be read.");
        string tpl = sheet.GetTemplateName() ?? "";
        bool none = (int)Math.Round(p[1]) == TemplateNone || tpl.Length == 0;
        return new SheetState((int)Math.Round(p[0]), p[5] * 1000, p[6] * 1000, p[2], p[3] == 0 ? 1 : p[3], p[4] != 0, none ? null : tpl);
    }

    /// <summary>The sheet-format file for a paper and family from the sheet-format folders (Tools > Options > File Locations).</summary>
    private static (string? Path, string? Note) FormatPath(SwContext ctx, PaperSpec paper, string family)
    {
        var (files, note) = DrawingLayout.FormatFiles(paper, family);
        if (files.Count == 0) return (null, note);
        var folders = Folders(ctx, swUserPreferenceStringValue_e.swFileLocationsSheetFormat);
        foreach (var f in files)
            foreach (var dir in folders)
            {
                string p = Path.Combine(dir, f);
                if (File.Exists(p)) return (p, note);
            }
        throw new SwException(ErrorCodes.IoError,
            $"The sheet format '{files[0]}' is not in the sheet-format folders ({(folders.Count == 0 ? "none set" : string.Join("; ", folders))}).",
            "Pass format 'none' or the path of a .slddrt file; the folders are set in Tools > Options > File Locations > Sheet Formats.");
    }

    /// <summary>The existing folders of a ';'-separated file-location preference.</summary>
    private static List<string> Folders(SwContext ctx, swUserPreferenceStringValue_e pref) =>
        (ctx.App.GetUserPreferenceStringValue((int)pref) ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists).ToList();

    /// <summary>
    /// IDrawingDoc.SetupSheet5 on the sheet (the verified recipe), then reads the sheet back: paper, size, scale,
    /// projection and format. A mismatch puts the previous setup back and fails.
    /// </summary>
    private static DrwSheet ApplySheet(SwContext ctx, IDrawingDoc drw, ISheet sheet, PaperSpec paper, double num, double den,
        bool firstAngle, string? formatPath, List<string> checks)
    {
        string name = sheet.GetName();
        var old = sheet.DrwProps();
        string oldFormat = sheet.GetTemplateName() ?? "";
        bool ok = drw.SetupSheet5(name, paper.Paper, formatPath is null ? TemplateNone : TemplateCustom, num, den, firstAngle,
            formatPath ?? "", Units.M(paper.WidthMm), Units.M(paper.HeightMm), DocDefault, true);
        var p = sheet.DrwProps();
        var problems = new List<string>();
        if (!ok) problems.Add("SetupSheet5 returned false");
        if (p.Length < 7) problems.Add("the sheet properties could not be read");
        else
        {
            if ((int)Math.Round(p[0]) != paper.Paper) problems.Add($"paper size reads {DrawingLayout.SizeName((int)Math.Round(p[0]))}");
            if (!SameScale(p[2] / p[3], num / den)) problems.Add($"scale reads {DrawingLayout.FormatScale(p[2], p[3])}");
            if ((p[4] != 0) != firstAngle) problems.Add($"projection reads {(p[4] != 0 ? "first" : "third")} angle");
            if (Math.Abs(p[5] * 1000 - paper.WidthMm) > 0.5 || Math.Abs(p[6] * 1000 - paper.HeightMm) > 0.5)
                problems.Add($"the sheet is {Units.Mm(p[5])} x {Units.Mm(p[6])} mm");
        }
        string nowFormat = sheet.GetTemplateName() ?? "";
        if (formatPath is not null && !string.Equals(Path.GetFileName(nowFormat), Path.GetFileName(formatPath), StringComparison.OrdinalIgnoreCase))
            problems.Add($"the sheet format reads '{(nowFormat.Length == 0 ? "none" : Path.GetFileName(nowFormat))}'");
        if (problems.Count > 0)
        {
            string restored = "";
            if (old.Length >= 7)
            {
                bool back = drw.SetupSheet5(name, (int)Math.Round(old[0]), (int)Math.Round(old[1]), old[2], old[3], old[4] != 0, oldFormat,
                    old[5], old[6], DocDefault, false);
                restored = back ? " The previous sheet setup was put back." : " The previous setup could not be put back.";
            }
            throw new SwException(ErrorCodes.VerifyFailed, $"The sheet setup did not take: {string.Join("; ", problems)}.{restored}",
                "Check the size and format combination (drw_info shows the sheet), or use format 'none'.");
        }
        checks.Add($"read back: {paper.Size} {paper.Orientation} {paper.WidthMm} x {paper.HeightMm} mm, {DrawingLayout.FormatScale(num, den)}, " +
                   $"{(firstAngle ? "first" : "third")} angle, format {(formatPath is null ? "none" : Path.GetFileNameWithoutExtension(formatPath))}");
        FormatOrientationCheck(ctx, sheet, paper);
        return SheetInfo(sheet, true);
    }

    /// <summary>Warns when the sheet format's lines span the paper turned by 90° (a portrait format on landscape paper).</summary>
    private static void FormatOrientationCheck(SwContext ctx, ISheet sheet, PaperSpec paper)
    {
        try
        {
            if (sheet.GetTemplateSketch() is not ISketch sk) return;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var seg in sk.Segments())
            {
                if (seg is not ISketchLine line || line.DrwStart() is not ISketchPoint a || line.DrwEnd() is not ISketchPoint b) continue;
                foreach (var pt in new[] { a, b })
                {
                    x0 = Math.Min(x0, pt.X); y0 = Math.Min(y0, pt.Y); x1 = Math.Max(x1, pt.X); y1 = Math.Max(y1, pt.Y);
                }
            }
            if (x0 > x1) return;
            double w = (x1 - x0) * 1000, h = (y1 - y0) * 1000;
            bool turned = Math.Abs(w - paper.HeightMm) < 15 && Math.Abs(h - paper.WidthMm) < 15 && Math.Abs(paper.WidthMm - paper.HeightMm) > 30;
            if (turned)
                ctx.Warn($"The sheet format's border is {Math.Round(w)} x {Math.Round(h)} mm, i.e. drawn for {(paper.Portrait ? "landscape" : "portrait")} paper; " +
                         $"use orientation {(paper.Portrait ? "landscape" : "portrait")} with this format.");
        }
        catch (COMException) { /* informational only */ }
    }

    // ================================================================== drw_views_standard

    public Task<DrwViewsResult> ViewsStandardAsync(string? model, IReadOnlyList<string>? views, string? scale, string? doc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw SwException.InvalidArgument("'model' is required: the path of a saved .sldprt/.sldasm, or an open document's handle or title.");
        var names = new List<string>();
        foreach (var v in views is { Count: > 0 } ? views : ["front", "top", "right", "iso"])
        {
            string n = DrawingLayout.Norm(v) switch
            {
                "isometric" => "iso",
                { } x => x,
                null => throw SwException.InvalidArgument("'views' contains an empty name."),
            };
            if (!DrawingLayout.NamedViews.ContainsKey(n))
                throw SwException.InvalidArgument($"Unknown view '{v}'.", $"Use {DrawingLayout.ViewNames}.");
            if (!names.Contains(n)) names.Add(n);
        }
        double? fixedScale = null;
        if (!string.IsNullOrWhiteSpace(scale))
        {
            var (n, dd) = DrawingLayout.ParseScale(scale, "scale");
            fixedScale = n / dd;
        }
        return session.RunAsync("drw_views_standard", ctx => ViewsStandard(ctx, model!, names, fixedScale),
            RunOptions.Write(doc) with { Timeout = Longer(session, 180) }, ct);
    }

    private static DrwViewsResult ViewsStandard(SwContext ctx, string model, List<string> names, double? fixedScale)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
        var (path, modelDoc) = ResolveModel(ctx, model);
        var sp = sheet.DrwProps();
        if (sp.Length < 7) throw new SwException(ErrorCodes.Internal, "The sheet properties could not be read.");
        double sheetW = sp[5] * 1000, sheetH = sp[6] * 1000, sheetScale = sp[2] / sp[3];
        bool firstAngle = sp[4] != 0;
        bool hasFormat = (int)Math.Round(sp[1]) != TemplateNone && !string.IsNullOrEmpty(sheet.GetTemplateName());
        var usable = DrawingLayout.Usable(sheetW, sheetH, hasFormat);
        var existing = sheet.DrwViews();
        var checks = new List<string>();
        var created = new List<(string Slot, IView View)>();
        d.ClearSelection2(true);
        try
        {
            double[]? box = modelDoc is null ? null : ModelBox(modelDoc);
            if (box is null)
            {
                // The model isn't open: the first view loads it, then the layout can use its box.
                var first = drw.CreateDrawViewFromModelView3(path, DrawingLayout.NamedViews[names[0]].Api, Units.M(usable.Cx), Units.M(usable.Cy), 0)
                            ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no {names[0]} view of '{Path.GetFileName(path)}'.",
                                "Check that the file is a part or assembly that opens in SOLIDWORKS.");
                created.Add((names[0], first));
                box = (first.ReferencedDocument is IModelDoc2 m ? ModelBox(m) : null)
                      ?? throw new SwException(ErrorCodes.VerifyFailed, $"Could not read the bounding box of '{Path.GetFileName(path)}' (no bodies?).");
            }
            double dx = (box[3] - box[0]) * 1000, dy = (box[4] - box[1]) * 1000, dz = (box[5] - box[2]) * 1000;
            var layout = DrawingLayout.LayoutStandard(names, firstAngle, dx, dy, dz, usable, sheetScale, fixedScale);
            double sc = layout.Scale;
            if (!layout.Fits)
                ctx.Warn($"At {DrawingLayout.FormatScale(sc)} the views need more room than the sheet offers (they fit at {DrawingLayout.FormatScale(layout.FitScale)} or less); some may overlap the border.");
            var occupied = existing.Select(v => SheetRect.FromMeters(v.DrwOutline())).OfType<SheetRect>().ToList();
            if (occupied.Count > 0)
            {
                var (moved, free) = DrawingLayout.PlaceGroup(layout, usable, occupied);
                layout = moved;
                if (!free)
                    ctx.Warn($"The sheet already has {occupied.Count} view(s) and no free area of {Math.Round(layout.Bounds.W)} x {Math.Round(layout.Bounds.H)} mm " +
                             "is left for these; they may overlap (pass a smaller scale, or use another drawing).");
            }
            // VERIFIED live: SOLIDWORKS rescales an empty sheet when its first view is inserted (a 1:2 set before that read
            // 1:1 again after the next rebuild; a 250 mm assembly on a 1:1 sheet came out 1:2), so an empty sheet gets the
            // planned scale after the views exist, whatever it read before. Views on a used sheet get their own scale.
            bool sheetScaleAfter = existing.Count == 0;
            bool perView = !sheetScaleAfter && !SameScale(sc, sheetScale);
            foreach (var slot in layout.Slots)
            {
                double x = Units.M(slot.Cx), y = Units.M(slot.Cy);
                var hit = created.FirstOrDefault(c => c.Slot == slot.View);
                IView v;
                if (hit.View is not null)
                {
                    v = hit.View;
                    v.DrwMoveTo(x, y);
                }
                else
                {
                    v = drw.CreateDrawViewFromModelView3(path, DrawingLayout.NamedViews[slot.View].Api, x, y, 0)
                        ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no {slot.View} view of '{Path.GetFileName(path)}'.");
                    created.Add((slot.View, v));
                }
                if (perView) SetViewScale(v, sc, x, y);
            }
            if (sheetScaleAfter)
            {
                var now = sheet.DrwProps();
                double current = now.Length >= 4 && now[3] != 0 ? now[2] / now[3] : sheetScale;
                if (!SameScale(current, sc) || !SameScale(sheetScale, sc) || created.Any(c => !SameScale(c.View.ScaleDecimal, sc)))
                {
                    SetSheetScale(drw, sheet, sc);
                    d.EditRebuild3();
                    foreach (var (_, v) in created)
                        if (!SameScale(v.ScaleDecimal, sc)) v.UseSheetScale = 1; // follow the sheet's new scale
                    checks.Add($"sheet scale set to {DrawingLayout.FormatScale(sc)}" + (fixedScale is null ? " to fit the views" : "") +
                               (!SameScale(current, sheetScale) ? $" (SOLIDWORKS had made it {DrawingLayout.FormatScale(current)})" : ""));
                }
            }
            d.GraphicsRedraw2();

            // Read every view back: model, scale, orientation and placement.
            var result = new List<DrwView>();
            foreach (var (slot, v) in created)
            {
                string vn = v.GetName2() ?? slot;
                if (!ShowsModel(v, path, out string shown))
                    throw new SwException(ErrorCodes.VerifyFailed, $"View '{vn}' shows '{shown}', not '{path}'.");
                if (!SameScale(v.ScaleDecimal, sc))
                    throw new SwException(ErrorCodes.VerifyFailed, $"View '{vn}' has scale {DrawingLayout.FormatScale(v.ScaleDecimal)}, not {DrawingLayout.FormatScale(sc)}.");
                var info = ViewInfo(ctx, v, false);
                var want = DrawingLayout.NamedViews[slot].Axes.Toward;
                var xf = v.DrwModelToSheet();
                if (xf.Length >= 13 && DrawingLayout.AxesOf(xf).Toward.DistanceTo(want) > DirTol)
                    ctx.Warn($"View '{vn}' ({slot}) reads as {info.Orientation ?? "another orientation"} from its model-to-sheet transform; check it (drw_info).");
                MappingCheck(ctx, v, info);
                result.Add(info);
            }
            checks.Add($"{created.Count} view(s) of {Path.GetFileName(path)} at {DrawingLayout.FormatScale(sc)}, orientation, model and scale read back");
            OverlapCheck(ctx, sheet, created.Select(c => c.View).ToList(), checks);
            return new DrwViewsResult { Doc = ctx.Title(d), Model = path, Scale = DrawingLayout.FormatScale(sc), Views = result, Checks = checks };
        }
        catch (Exception ex) when (created.Count > 0 && !ComArgs.IsDisconnect(ex))
        {
            throw Rolled(ex, DeleteViews(d, created.Select(c => SafeName(c.View))));
        }
    }

    private static string SafeName(IView v)
    {
        try { return v.GetName2() ?? ""; }
        catch (COMException) { return ""; }
    }

    /// <summary>
    /// True when the view shows the model file <paramref name="path"/>: by IView.GetReferencedModelName (a full path, or
    /// at least the file name) or by the path of IView.ReferencedDocument.
    /// </summary>
    private static bool ShowsModel(IView v, string path, out string shown)
    {
        shown = v.GetReferencedModelName() ?? "";
        if (shown.Length > 0)
        {
            bool same = Path.IsPathRooted(shown)
                ? string.Equals(Path.GetFullPath(shown), path, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFileNameWithoutExtension(shown), Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase);
            if (same) return true;
        }
        string docPath = v.ReferencedDocument is IModelDoc2 m ? DocumentRegistry.SafePath(m) : "";
        if (shown.Length == 0) shown = docPath;
        return docPath.Length > 0 && string.Equals(Path.GetFullPath(docPath), path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scales compared relatively (1:3 is not exact in binary).</summary>
    private static bool SameScale(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(Math.Abs(a), Math.Abs(b));

    /// <summary>The error to raise after a failed call removed what it had created: the message says what was removed.</summary>
    private static Exception Rolled(Exception ex, string rollback)
    {
        if (ex is SwException sw) return new SwException(sw.Code, $"{sw.Message} {rollback}".Trim(), sw.Hint, sw);
        if (ComArgs.IsRejected(ex) || ComArgs.IsServerFault(ex)) return ex; // SwSession turns these into BUSY / INTERNAL
        return new SwException(ErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message} {rollback}".Trim(), null, ex);
    }

    /// <summary>The model file of drw_views_standard: an open document (it must be saved) or a .sldprt/.sldasm path.</summary>
    private static (string Path, IModelDoc2? Doc) ResolveModel(SwContext ctx, string model)
    {
        string m = model.Trim();
        var open = ctx.App.Documents();
        string ext = Path.GetExtension(m).ToLowerInvariant();
        bool isPath = Path.IsPathRooted(m) || m.Contains('\\') || m.Contains('/');
        IModelDoc2? doc = null;
        if (!isPath)
        {
            // A handle or title of an open document first ("block.SLDPRT" may be a title); else a file under the output folder.
            try { doc = DocLocator.Find(ctx.Documents, open, m); }
            catch (SwException ex) when (ex.Code == ErrorCodes.InvalidArgument && ext is ".sldprt" or ".sldasm") { /* not open: a relative path */ }
        }
        if (doc is not null)
        {
            int t = doc.GetType();
            if (t is not ((int)swDocumentTypes_e.swDocPART or (int)swDocumentTypes_e.swDocASSEMBLY))
                throw new SwException(ErrorCodes.WrongDocType, $"'{ctx.Title(doc)}' is a {SwContext.TypeName(t)}; views show parts and assemblies.");
            string p = DocumentRegistry.SafePath(doc);
            if (p.Length == 0 || !File.Exists(p))
                throw SwException.InvalidArgument($"'{ctx.Title(doc)}' has not been saved, and drawing views need the model's file.",
                    $"Save it first: doc_save {{mode:'as', path:'{ctx.Title(doc)}{(t == (int)swDocumentTypes_e.swDocPART ? ".sldprt" : ".sldasm")}', doc:'{ctx.Documents.Track(doc).Handle}'}}.");
            if (doc.GetSaveFlag())
                ctx.Warn($"'{ctx.Title(doc)}' has unsaved changes: the views show it as it is now, but its file is older (save it to keep them in step).");
            return (Path.GetFullPath(p), doc);
        }
        if (ext is not (".sldprt" or ".sldasm"))
            throw SwException.InvalidArgument($"'model' must be a .sldprt or .sldasm file (got '{m}').");
        string full = ctx.Paths.ForRead(m, "model");
        var openDoc = open.FirstOrDefault(o => string.Equals(DocumentRegistry.SafePath(o), full, StringComparison.OrdinalIgnoreCase));
        if (openDoc is not null && openDoc.GetSaveFlag())
            ctx.Warn($"'{ctx.Title(openDoc)}' is open with unsaved changes: the views show it as it is now, not as the file is.");
        return (full, openDoc);
    }

    /// <summary>Changes only the sheet scale (SetupSheet5 with the sheet's other settings unchanged), read back.</summary>
    private static void SetSheetScale(IDrawingDoc drw, ISheet sheet, double scale)
    {
        var (num, den) = DrawingLayout.Ratio(scale);
        var p = sheet.DrwProps();
        bool ok = drw.SetupSheet5(sheet.GetName(), (int)Math.Round(p[0]), (int)Math.Round(p[1]), num, den, p[4] != 0, sheet.GetTemplateName() ?? "",
            p[5], p[6], DocDefault, false);
        var now = sheet.DrwProps();
        if (!ok || now.Length < 4 || !SameScale(now[2] / now[3], scale))
            throw new SwException(ErrorCodes.VerifyFailed, $"The sheet scale could not be set to {DrawingLayout.FormatScale(scale)}.",
                "Set it with drw_sheet_setup, then retry.");
    }

    /// <summary>Gives one view its own scale (not the sheet's) and puts it back at its position.</summary>
    private static void SetViewScale(IView v, double scale, double x, double y)
    {
        v.UseSheetScale = 0;
        v.ScaleDecimal = scale;
        v.DrwMoveTo(x, y);
        if (!SameScale(v.ScaleDecimal, scale))
            throw new SwException(ErrorCodes.VerifyFailed, $"View '{v.GetName2()}' kept scale {DrawingLayout.FormatScale(v.ScaleDecimal)} instead of {DrawingLayout.FormatScale(scale)}.",
                "Set the sheet scale with drw_sheet_setup instead (views use it by default).");
    }

    // ================================================================== drw_view_projected

    public Task<DrwViewResult> ProjectedAsync(string? parent, string? direction, string? doc, CancellationToken ct = default)
    {
        string side = DrawingLayout.Norm(direction) switch
        {
            "left" => "left",
            "right" => "right",
            "up" or "above" or "top" => "up",
            "down" or "below" or "bottom" => "down",
            _ => throw SwException.InvalidArgument($"Unknown direction '{direction}'.", "Use left, right, up or down (where the new view goes)."),
        };
        return session.RunAsync("drw_view_projected", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, parent, "parent");
            var sheet = SheetOf(drw, hit);
            bool firstAngle = sheet.DrwProps() is { Length: > 4 } sp && sp[4] != 0;
            var xf = hit.View.DrwModelToSheet();
            var outline = Outline(hit);
            var geo = GeometryBox(ctx, hit);
            double depth = geo.W;
            if (xf.Length >= 13 && ModelBox(ModelOf(hit)) is { } box)
                depth = DrawingLayout.Extent(DrawingLayout.AxesOf(xf).Toward, (box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000) * hit.View.ScaleDecimal;
            var (w, h) = side is "left" or "right" ? (depth, geo.H) : (geo.W, depth);
            // Aligned on that side of the parent, past any view already there (SOLIDWORKS takes the direction from the position).
            var (x, y, free) = DrawingLayout.SideSpot(outline, side, w, h, Occupied(sheet), SheetArea(sheet));
            if (!free) ctx.Warn($"No free space of {Math.Round(w)} x {Math.Round(h)} mm {SideWord(side)} '{hit.Name}'; the new view may overlap others or the border.");

            d.ClearSelection2(true);
            SelectView(d, hit.Name);
            var nv = drw.CreateUnfoldedViewAt3(Units.M(x), Units.M(y), 0, false);
            d.ClearSelection2(true);
            if (nv is null)
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no projected view of '{hit.Name}'.",
                    "The parent must be a model view (not a detail view); drw_info lists the views.");
            var checks = new List<string>();
            try
            {
                var info = ViewInfo(ctx, nv, false);
                if (nv.Type != (int)swDrawingViewTypes_e.swDrawingProjectedView)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{info.Name}' is a {info.Type} view, not a projected one.");
                var no = SheetRect.FromMeters(nv.DrwOutline());
                bool onSide = no is SheetRect r && side switch
                {
                    "right" => r.Cx > outline.X1,
                    "left" => r.Cx < outline.X0,
                    "up" => r.Cy > outline.Y1,
                    _ => r.Cy < outline.Y0,
                };
                if (!onSide)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{info.Name}' is not {SideWord(side)} '{hit.Name}' (its outline is [{Fmt(no?.ToArray())}] mm).");
                checks.Add($"projected view {SideWord(side)} '{hit.Name}'");
                var nx = nv.DrwModelToSheet();
                if (xf.Length >= 13 && nx.Length >= 13)
                {
                    var pa = DrawingLayout.AxesOf(xf);
                    var na = DrawingLayout.AxesOf(nx);
                    var want = DrawingLayout.ProjectedToward(pa, side, firstAngle);
                    if (Math.Abs(na.Toward.Dot(pa.Toward)) > DirTol)
                        ctx.Warn($"'{info.Name}' does not look perpendicular to '{hit.Name}' (its model-to-sheet transform); check it.");
                    else if (na.Toward.DistanceTo(want) < DirTol)
                        checks.Add($"shows the {(info.Orientation ?? "expected")} side ({(firstAngle ? "first" : "third")}-angle projection)");
                    else
                        ctx.Warn($"'{info.Name}' looks from [{Dir(na.Toward)}], not [{Dir(want)}] as {(firstAngle ? "first" : "third")}-angle projection predicts.");
                }
                MappingCheck(ctx, nv, info);
                OverlapCheck(ctx, sheet, [nv], checks);
                return new DrwViewResult { Doc = ctx.Title(d), View = info with { Parent = hit.Name }, Checks = checks };
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                throw Rolled(ex, DeleteViews(d, [SafeName(nv)]));
            }
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 120) }, ct);
    }

    // ================================================================== drw_view_section

    public Task<DrwViewResult> SectionAsync(string? parent, double[]? lineStartMm, double[]? lineEndMm, string? label, double[]? atMm,
        string? doc, CancellationToken ct = default)
    {
        var p0 = Sheet2(lineStartMm, "lineStartMm");
        var p1 = Sheet2(lineEndMm, "lineEndMm");
        double len = Math.Sqrt((p1.X - p0.X) * (p1.X - p0.X) + (p1.Y - p0.Y) * (p1.Y - p0.Y));
        if (len < 1) throw SwException.InvalidArgument($"The cut line is {Math.Round(len, 3)} mm long; it must cross the whole part.");
        (double X, double Y)? at = atMm is null ? null : Sheet2(atMm, "atMm");
        string? lab = Label(label);
        var options = RunOptions.Write(doc) with { Timeout = Longer(session, 120), DialogRules = [DialogRules.PartialSectionNo] };
        return session.RunAsync("drw_view_section", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, parent, "parent");
            var sheet = SheetOf(drw, hit);
            var geo = GeometryBox(ctx, hit);
            if (geo.Contains(p0.X, p0.Y) || geo.Contains(p1.X, p1.Y) || !Crosses(p0, p1, geo))
                throw SwException.InvalidArgument(
                    $"The cut line [{Fmt([p0.X, p0.Y])}] → [{Fmt([p1.X, p1.Y])}] mm must cross the whole part: in '{hit.Name}' the part spans [{Fmt(geo.ToArray())}] mm.",
                    "Start and end the line outside that box (a line ending inside it would make a partial section).");
            string lb = lab ?? DrawingLayout.NextLabel(AllViews(drw).Select(v => v.Name));
            CheckLabelFree(drw, lb);

            // Size and place the new view: it shows the cut plane (the part's depth across, its extent along the line).
            double ux = (p1.X - p0.X) / len, uy = (p1.Y - p0.Y) / len;
            var xf = hit.View.DrwModelToSheet();
            double depth = Math.Max(geo.W, geo.H);
            ViewAxes? pa = xf.Length >= 13 ? DrawingLayout.AxesOf(xf) : null;
            if (pa is not null && ModelBox(ModelOf(hit)) is { } box)
                depth = DrawingLayout.Extent(pa.Toward, (box[3] - box[0]) * 1000, (box[4] - box[1]) * 1000, (box[5] - box[2]) * 1000) * hit.View.ScaleDecimal;
            double along = Math.Abs(ux) * geo.W + Math.Abs(uy) * geo.H;
            bool vertical = Math.Abs(uy) >= Math.Abs(ux);
            var (w, h) = vertical ? (depth, along) : (along, depth);
            var (x, y) = at ?? Place(sheet, hit, w, h, vertical ? ["right", "left"] : ["down", "up"], ctx);

            var checks = new List<string>();
            ISketchSegment? seg = null;
            IView? sv;
            var sm = d.SketchManager;
            bool addToDb = sm.AddToDB;
            try
            {
                Activate(drw, hit);
                sm.AddToDB = true; // no snapping to the model's edges
                seg = DrawInView(ctx, d, hit, "cut line", map =>
                {
                    var a = map.ApplyPoint(new Vec3(Units.M(p0.X), Units.M(p0.Y), 0));
                    var b = map.ApplyPoint(new Vec3(Units.M(p1.X), Units.M(p1.Y), 0));
                    return sm.CreateLine(a.X, a.Y, 0, b.X, b.Y, 0);
                }, (Units.M((p0.X + p1.X) / 2), Units.M((p0.Y + p1.Y) / 2)), checks);
                // AddToDB back off before the line is selected and the view is made (as in the verified VBA recipe).
                sm.AddToDB = addToDb;
                d.ClearSelection2(true);
                if (!seg.Select4(false, null))
                    throw new SwException(ErrorCodes.FeatureFailed, "Could not select the cut line.");
                sv = drw.CreateSectionViewAt5(Units.M(x), Units.M(y), 0, lb, 0, ComArgs.Empty, 0);
            }
            catch (Exception ex) when (seg is not null && !ComArgs.IsDisconnect(ex))
            {
                DeleteSegment(d, seg);
                throw;
            }
            finally
            {
                sm.AddToDB = addToDb;
                d.ClearSelection2(true);
            }
            if (sv is null)
            {
                DeleteSegment(d, seg);
                bool asked = ctx.Op.HandledDialogs.Any(hd => ReferenceEquals(hd.Rule, DialogRules.PartialSectionNo));
                throw new SwException(ErrorCodes.FeatureFailed,
                    $"SOLIDWORKS created no section view of '{hit.Name}'{(asked ? " (it asked whether to make a partial section; answered No)" : "")}.",
                    "The cut line must cross the whole part in the parent view; check the line against the view's bounds (drw_info).");
            }
            try
            {
                d.EditRebuild3();
                var info = ViewInfo(ctx, sv, false);
                if (sv.Type != (int)swDrawingViewTypes_e.swDrawingSectionView)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{info.Name}' is a {info.Type} view, not a section view.");
                checks.Add($"section view '{info.Name}' (label {lb})");
                var sx = sv.DrwModelToSheet();
                if (pa is not null && sx.Length >= 13)
                {
                    // The cutting plane contains the line (in the model) and the parent's viewing direction.
                    var lineModel = (pa.SheetX * ux + pa.SheetY * uy).Normalized();
                    var n = lineModel.Cross(pa.Toward).Normalized();
                    var st = DrawingLayout.AxesOf(sx).Toward;
                    if (Math.Abs(Math.Abs(st.Dot(n)) - 1) < DirTol) checks.Add($"looks along the cutting-plane normal [{Dir(st)}]");
                    else ctx.Warn($"'{info.Name}' looks along [{Dir(st)}], not along the cutting-plane normal ±[{Dir(n)}]; check the cut position.");
                }
                // The section's hatches, counted on the view found by traversal (methods on returned view objects can fail),
                // as face or solid hatches, and once more after a rebuild when none show yet.
                string svName = sv.GetName2() ?? "";
                int Hatches()
                {
                    var counted = AllViews(drw).FirstOrDefault(h => string.Equals(h.Name, svName, StringComparison.Ordinal))?.View ?? sv;
                    return Math.Max(SafeCount(() => counted.GetFaceHatchCount()), SafeCount(() => counted.GetSolidHatchCount(out _)));
                }
                int hatches = Hatches();
                if (hatches <= 0)
                {
                    d.EditRebuild3();
                    hatches = Hatches();
                }
                // Informational: GetFaceHatchCount / GetSolidHatchCount read 0 for a hatched section of a solid block live, so a
                // zero proves nothing; the view's direction (above) and extents (modelBoxMm) are the checks.
                if (hatches > 0) checks.Add($"{hatches} hatched cut face(s)");
                OverlapCheck(ctx, sheet, [sv], checks);
                return new DrwViewResult { Doc = ctx.Title(d), View = info with { Parent = hit.Name }, Label = lb, Checks = checks };
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                string rb = DeleteViews(d, [SafeName(sv)]);
                DeleteSegment(d, seg); // the cut line, if deleting the view left it
                throw Rolled(ex, rb);
            }
        }, options, ct);
    }

    /// <summary>True when the segment passes through the rectangle's interior.</summary>
    private static bool Crosses((double X, double Y) a, (double X, double Y) b, SheetRect r)
    {
        // Liang–Barsky clipping: some part of the segment lies inside the rectangle.
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        foreach (var (p, q) in new[] { (-dx, a.X - r.X0), (dx, r.X1 - a.X), (-dy, a.Y - r.Y0), (dy, r.Y1 - a.Y) })
        {
            if (Math.Abs(p) < 1e-12)
            {
                if (q < 0) return false;
                continue;
            }
            double t = q / p;
            if (p < 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
            if (t0 > t1) return false;
        }
        return t1 - t0 > 1e-9;
    }

    // ================================================================== drw_view_detail

    public Task<DrwViewResult> DetailAsync(string? parent, double[]? centerMm, double radiusMm, string? scale, string? label, double[]? atMm,
        string? doc, CancellationToken ct = default)
    {
        var c = Sheet2(centerMm, "centerMm");
        if (!double.IsFinite(radiusMm) || radiusMm < 0.5 || radiusMm > 2000)
            throw SwException.InvalidArgument($"radiusMm must be between 0.5 and 2000 (got {radiusMm}).");
        (double Num, double Den)? fixedScale = string.IsNullOrWhiteSpace(scale) ? null : DrawingLayout.ParseScale(scale, "scale");
        (double X, double Y)? at = atMm is null ? null : Sheet2(atMm, "atMm");
        string? lab = Label(label);
        return session.RunAsync("drw_view_detail", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, parent, "parent");
            var sheet = SheetOf(drw, hit);
            double ps = hit.View.ScaleDecimal;
            var (num, den) = fixedScale ?? DrawingLayout.Ratio(2 * ps);
            double s = num / den;
            var geo = GeometryBox(ctx, hit);
            if (!geo.Inflate(radiusMm).Contains(c.X, c.Y))
                throw SwException.InvalidArgument($"The detail circle at [{Fmt([c.X, c.Y])}] mm is off the part in '{hit.Name}' (it spans [{Fmt(geo.ToArray())}] mm).",
                    "Center the circle on the region to enlarge (sheet mm).");
            string lb = lab ?? DrawingLayout.NextLabel(AllViews(drw).Select(v => v.Name));
            CheckLabelFree(drw, lb);
            double size = 2 * radiusMm * s / ps + 10;
            var (x, y) = at ?? Place(sheet, hit, size, size, ["right", "up", "down", "left"], ctx);

            var checks = new List<string>();
            ISketchSegment? seg = null;
            IView? dv;
            var sm = d.SketchManager;
            bool addToDb = sm.AddToDB;
            try
            {
                Activate(drw, hit);
                sm.AddToDB = true;
                seg = DrawInView(ctx, d, hit, "detail circle", map =>
                {
                    var cc = map.ApplyPoint(new Vec3(Units.M(c.X), Units.M(c.Y), 0));
                    var rim = map.ApplyPoint(new Vec3(Units.M(c.X + radiusMm), Units.M(c.Y), 0));
                    return sm.CreateCircleByRadius(cc.X, cc.Y, 0, cc.DistanceTo(rim));
                }, (Units.M(c.X + radiusMm), Units.M(c.Y)), checks);
                sm.AddToDB = addToDb; // as for the section view
                d.ClearSelection2(true);
                if (!seg.Select4(false, null))
                    throw new SwException(ErrorCodes.FeatureFailed, "Could not select the detail circle.");
                // Style standard, show type 0 (the drawn profile), full outline: the arguments of the earlier VBA test.
                dv = drw.DrwDetailView(Units.M(x), Units.M(y), (int)swDetViewStyle_e.swDetViewSTANDARD, num, den, lb,
                    (int)swDetCircleShowType_e.swDetCirclePROFILE, true);
            }
            catch (Exception ex) when (seg is not null && !ComArgs.IsDisconnect(ex))
            {
                DeleteSegment(d, seg);
                throw;
            }
            finally
            {
                sm.AddToDB = addToDb;
                d.ClearSelection2(true);
            }
            if (dv is null)
            {
                DeleteSegment(d, seg);
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no detail view of '{hit.Name}'.",
                    "Put the circle over the part's geometry in the parent view (sheet mm).");
            }
            try
            {
                d.EditRebuild3(); // earlier testing saw an empty detail view before a rebuild
                var info = ViewInfo(ctx, dv, false);
                if (dv.Type != (int)swDrawingViewTypes_e.swDrawingDetailView)
                    throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{info.Name}' is a {info.Type} view, not a detail view.");
                if (!SameScale(dv.ScaleDecimal, s))
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{info.Name}' has scale {info.Scale}, not {DrawingLayout.FormatScale(num, den)}.");
                checks.Add($"detail view '{info.Name}' at {DrawingLayout.FormatScale(num, den)} (label {lb})");
                int edges = SafeCount(() => dv.GetVisibleEntityCount2(null, (int)swViewEntityType_e.swViewEntityType_Edge));
                if (edges > 0) checks.Add($"{edges} visible edge(s) in the detail");
                else
                    ctx.Warn($"'{info.Name}' reports no visible edges: it may be empty (seen in earlier testing). Check it in a PDF export (model_export) " +
                             "and move the circle onto the part's edges if so.");
                OverlapCheck(ctx, sheet, [dv], checks);
                return new DrwViewResult { Doc = ctx.Title(d), View = info with { Parent = hit.Name }, Label = lb, Checks = checks };
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                string rb = DeleteViews(d, [SafeName(dv)]);
                DeleteSegment(d, seg); // the detail circle, if deleting the view left it
                throw Rolled(ex, rb);
            }
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 120) }, ct);
    }

    // ================================================================== drw_dimensions_auto

    public Task<DrwCountResult> DimensionsAutoAsync(IReadOnlyList<string>? views, string? doc, CancellationToken ct = default) =>
        session.RunAsync("drw_dimensions_auto", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            bool all = views is null || views.Count == 0;
            var targets = all ? ModelViews(drw) : views!.Select((n, i) => FindView(ctx, drw, n, $"views[{i}]")).ToList();
            if (targets.Count == 0)
                throw SwException.InvalidArgument("The drawing has no views to dimension.", "Create them first with drw_views_standard.");
            var before = targets.ToDictionary(t => t.Name, t => Count(t.View).Dimensions);
            d.ClearSelection2(true);
            if (!all)
                for (int i = 0; i < targets.Count; i++) SelectView(d, targets[i].Name, append: i > 0);
            var inserted = drw.DrwInsertModelAnnotations((int)swImportModelItemsSource_e.swImportModelItemsFromEntireModel, ModelDimensionTypes, all);
            d.ClearSelection2(true);
            d.GraphicsRedraw2();
            var counts = targets.Select(t =>
            {
                int after = Count(t.View).Dimensions;
                return new DrwViewCount { View = t.Name, Before = before[t.Name], After = after, Added = after - before[t.Name] };
            }).ToList();
            int added = counts.Sum(c => c.Added);
            if (inserted.Count != added && inserted.Count > 0)
                ctx.Warn($"SOLIDWORKS reported {inserted.Count} inserted annotation(s); the views gained {added} dimension(s).");
            return new DrwCountResult
            {
                Doc = ctx.Title(d), Added = added, Views = counts,
                Hint = added > 0 ? null :
                    "No model dimensions were inserted. The model's sketch and feature dimensions may not be marked for drawing, may already " +
                    "be shown in another view (each appears once), or may not be parallel to these views (iso views get none). " +
                    "Add sketch dimensions in the part (sketch_dimension_add), or dimension edges directly with drw_dimension_add. " +
                    "In testing, a long SOLIDWORKS session sometimes stopped inserting model annotations until SOLIDWORKS was restarted.",
            };
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 120) }, ct);

    // ================================================================== drw_dimension_add

    /// <summary>An entity of the view's model seen on the sheet (m): a point, a line, a circle or another curve.</summary>
    private sealed record OnSheet(string Kind, Vec3 P0, Vec3 P1, Vec3 Mid, Vec3? Center, double? Radius, bool FaceOn, string Label)
    {
        public Vec3 Pick => Kind == "point" ? P0 : Mid;
        public (double X, double Y) Dir2 => (P1.X - P0.X, P1.Y - P0.Y);
    }

    public Task<DrwDimensionResult> DimensionAddAsync(string? view, EntityRef? a, EntityRef? b, string? kind, double[]? textAtMm, string? doc,
        CancellationToken ct = default)
    {
        string k = DrawingLayout.Norm(kind) ?? "linear";
        if (k is "aligned" or "smart") k = "linear";
        if (k is "callout" or "hole") k = "hole_callout";
        if (k is not ("linear" or "horizontal" or "vertical" or "radial" or "diameter" or "angular" or "hole_callout"))
            throw SwException.InvalidArgument($"Unknown kind '{kind}'.", "Use linear, horizontal, vertical, radial, diameter, angular or hole_callout.");
        if (a is null) throw SwException.InvalidArgument("'a' is required: an edge or vertex of the view's model, e.g. {type:'edge', at:[x,y,z]}.");
        if (k is "radial" or "diameter" or "hole_callout" && b is not null) throw SwException.InvalidArgument($"A {k} dimension takes one circular edge ('a' only).");
        if (k == "angular" && b is null) throw SwException.InvalidArgument("An angular dimension needs two straight edges ('a' and 'b').");
        (double X, double Y)? text = textAtMm is null ? null : Sheet2(textAtMm, "textAtMm");
        var options = RunOptions.Write(doc) with { DialogRules = [DialogRules.ModifyDimension] };
        return session.RunAsync("drw_dimension_add", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, view, "view");
            var model = ModelOf(hit);
            var xf = hit.View.DrwModelToSheet();
            if (xf.Length < 13)
                throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform, so model references can't be placed in it.");
            var t = new Xform(xf);
            double s = SheetScale(xf, hit.View.ScaleDecimal);
            bool part = model.GetType() == (int)swDocumentTypes_e.swDocPART;
            var ra = EntityResolver.Resolve(ctx, model, a, "a", EntityKind.Edge, EntityKind.Vertex);
            var rb = b is null ? null : EntityResolver.Resolve(ctx, model, b, "b", EntityKind.Edge, EntityKind.Vertex);
            var ga = Seen(ra, t, part, "a");
            var gb = rb is null ? null : Seen(rb, t, part, "b");
            if (gb is not null && ga.Pick.DistanceTo(gb.Pick) < PosTol && ga.Kind == gb.Kind)
                throw SwException.InvalidArgument("'a' and 'b' are drawn at the same place in this view.", "Pick two different edges or vertices.");
            if (k is "radial" or "diameter" or "hole_callout" && !(ga.Kind == "circle" && ga.FaceOn))
                throw SwException.InvalidArgument($"A {k} dimension needs a circular edge seen face-on; {ga.Label} is a {ga.Kind}{(ga.Kind == "circle" ? " seen edge-on" : "")} in '{hit.Name}'.");
            if (k == "angular" && !(ga.Kind == "line" && gb!.Kind == "line"))
                throw SwException.InvalidArgument("An angular dimension needs two straight edges.");
            double? expected = Expected(k == "hole_callout" ? "diameter" : k, ga, gb, s);
            if (k == "angular" && expected is null)
                throw SwException.InvalidArgument("The two edges are parallel in this view, so there is no angle between them.");
            var outline = Outline(hit);
            var picks = new List<(double, double)> { (ga.Pick.X * 1000, ga.Pick.Y * 1000) };
            if (gb is not null) picks.Add((gb.Pick.X * 1000, gb.Pick.Y * 1000));
            var tp = text ?? DrawingLayout.DimensionText(k, picks, outline);

            var checks = new List<string>();
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.InputDimValOnCreate, false); // no modal Modify box
            Activate(drw, hit);
            int before = Count(hit.View).Dimensions;
            d.ClearSelection2(true);
            SelectInView(ctx, d, hit, ra, ga, t, part, append: false, "a", checks);
            if (rb is not null) SelectInView(ctx, d, hit, rb, gb!, t, part, append: true, "b", checks);
            var dd = d.DrwAddDimension(k, Units.M(tp.X), Units.M(tp.Y));
            d.ClearSelection2(true);
            if (dd is null)
                throw new SwException(ErrorCodes.FeatureFailed,
                    $"SOLIDWORKS created no {k} dimension on {ra.Label}{(rb is null ? "" : $" and {rb.Label}")} in '{hit.Name}'.",
                    "Check that the entities can carry this kind of dimension (two parallel lines or two points for a distance, a circle for radial/diameter).");
            var dim = dd.GetDimension2(0);
            if (dim is null)
            {
                DeleteDimension(d, dd);
                throw new SwException(ErrorCodes.FeatureFailed, "The new display dimension has no dimension value; it was removed.");
            }
            string? callout = null;
            if (k == "hole_callout")
            {
                if (!dd.IsHoleCallout())
                {
                    bool gone = DeleteDimension(d, dd);
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"SOLIDWORKS made a plain dimension, not a hole callout, on {ra.Label}; {(gone ? "it was removed" : "it could not be removed")}.",
                        "Hole callouts need a hole made by feature_hole (Hole Wizard) or a cut, seen face-on; use kind diameter otherwise.");
                }
                callout = CalloutText(dd);
                checks.Add($"a hole callout{(callout is null ? "" : $": {callout}")}");
            }
            bool angular = k == "angular" || dim.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
            double v = dim.SystemValue;
            if (expected is double e)
            {
                bool ok = angular
                    ? Math.Abs(v - e) < 1e-5 || Math.Abs(v - (Math.PI - e)) < 1e-5
                    : Math.Abs(v - e) <= Math.Max(1e-6, 1e-6 * Math.Abs(e));
                if (!ok)
                {
                    bool gone = DeleteDimension(d, dd);
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"SOLIDWORKS dimensioned {Show(v, angular)} instead of the {k} {Show(e, angular)} of {ra.Label}{(rb is null ? "" : $" and {rb.Label}")}; " +
                        (gone ? "the dimension was removed." : "the dimension could not be removed."),
                        "Pass textAtMm next to the quantity you want (beside a length, inside an angle), or use kind horizontal / vertical.");
                }
                checks.Add($"value {Show(v, angular)} matches the geometry");
            }
            int after = Count(hit.View).Dimensions;
            if (after == before + 1) checks.Add($"'{hit.Name}' has {after} dimension(s)");
            else ctx.Warn($"'{hit.Name}' went from {before} to {after} dimension(s).");
            var resolved = new List<ResolvedInfo> { ra.Info("a") };
            if (rb is not null) resolved.Add(rb.Info("b"));
            return new DrwDimensionResult
            {
                Doc = ctx.Title(d),
                View = hit.Name,
                Kind = k,
                Dimension = FeatureVerifier.ShortName(dim.FullName),
                Value = angular ? Units.Deg(v) : Units.Mm(v),
                Unit = angular ? "deg" : "mm",
                Expected = expected is double ex ? (angular ? Units.Deg(ex) : Units.Mm(ex)) : null,
                Reference = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven ? true : null,
                Callout = callout,
                TextAtMm = [Math.Round(tp.X, 3), Math.Round(tp.Y, 3)],
                Checks = checks,
                Resolved = resolved,
            };
        }, options, ct);
    }

    /// <summary>The callout's text parts (prefix, value, suffix, above, below) joined, or null when SOLIDWORKS gives none.</summary>
    private static string? CalloutText(IDisplayDimension dd)
    {
        var parts = new List<string>();
        foreach (var which in new[] { swDimensionTextParts_e.swDimensionTextAll, swDimensionTextParts_e.swDimensionTextPrefix,
                     swDimensionTextParts_e.swDimensionTextSuffix, swDimensionTextParts_e.swDimensionTextCalloutAbove,
                     swDimensionTextParts_e.swDimensionTextCalloutBelow })
        {
            try
            {
                if (dd.GetText((int)which) is { Length: > 0 } t && !parts.Contains(t)) parts.Add(t);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
            }
        }
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    /// <summary>How a resolved model entity appears on the sheet.</summary>
    private static OnSheet Seen(Resolved r, Xform t, bool part, string arg)
    {
        if (!part)
        {
            // Assembly bodies report part-space geometry; only the world pick point of an 'at' or 'where' reference is known.
            if (r.Pick is not Vec3 wp)
                throw SwException.InvalidArgument($"'{arg}': in an assembly view, reference the edge by 'at' (assembly mm) or a query, not by id.");
            var p = t.ApplyPoint(wp);
            return new OnSheet(r.Obj is IVertex ? "point" : "curve", p, p, p, null, null, false, r.Label);
        }
        switch (r.Obj)
        {
            case IVertex vx:
            {
                var p = t.ApplyPoint(Vec3.From(vx.Point()));
                return new OnSheet("point", p, p, p, null, null, false, r.Label);
            }
            case IEdge e:
            {
                var g = Topology.Describe(e);
                var p0 = t.ApplyPoint(g.Start);
                var p1 = t.ApplyPoint(g.End);
                var m = t.ApplyPoint(g.Mid);
                if (g.Curve == "line")
                {
                    if (Flat(p0).DistanceTo(Flat(p1)) < PosTol)
                        throw SwException.InvalidArgument($"'{arg}' ({r.Label}) is seen end-on in this view (it points at the viewer).",
                            "Dimension it in a view where it lies flat, or pick another edge.");
                    return new OnSheet("line", p0, p1, m, null, null, false, r.Label);
                }
                if (g.Curve == "circle" && g.Center is Vec3 c && g.Axis is Vec3 ax)
                {
                    bool faceOn = Math.Abs(t.ApplyVector(ax).Normalized().Z) > 1 - 1e-6;
                    return new OnSheet("circle", p0, p1, m, t.ApplyPoint(c), g.Radius, faceOn, r.Label);
                }
                return new OnSheet("curve", p0, p1, m, null, null, false, r.Label);
            }
        }
        throw SwException.InvalidArgument($"'{arg}' resolved to {r.Label}; drawing dimensions take edges and vertices.");
    }

    private static Vec3 Flat(Vec3 p) => new(p.X, p.Y, 0);

    /// <summary>Sheet length per model length of a model-to-sheet transform (rotation row length × scale), else the view scale.</summary>
    private static double SheetScale(double[] xf, double viewScale)
    {
        double s = new Vec3(xf[0], xf[1], xf[2]).Length * (xf[12] == 0 ? 1 : xf[12]);
        return s > 0 && double.IsFinite(s) ? s : viewScale;
    }

    /// <summary>The quantity a dimension of this kind should read, from the geometry on the sheet (m or rad), when it is clear.</summary>
    private static double? Expected(string kind, OnSheet a, OnSheet? b, double scale)
    {
        if (scale <= 0) return null;
        static double Len((double X, double Y) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
        static bool IsVertical(OnSheet g) => g.Kind == "line" && Math.Abs(g.Dir2.X) < 1e-9 * Math.Max(1, Len(g.Dir2)) + 1e-9;
        static bool IsHorizontal(OnSheet g) => g.Kind == "line" && Math.Abs(g.Dir2.Y) < 1e-9 * Math.Max(1, Len(g.Dir2)) + 1e-9;
        switch (kind)
        {
            case "radial": return a.Radius;
            case "diameter": return a.Radius * 2;
            case "angular":
            {
                var (ax, ay) = a.Dir2;
                var (bx, by) = b!.Dir2;
                double cos = Math.Abs(ax * bx + ay * by) / (Len(a.Dir2) * Len(b.Dir2));
                double ang = Math.Acos(Math.Clamp(cos, -1, 1));
                return ang < 1e-6 ? null : ang;
            }
        }
        if (b is null)
        {
            if (a.Kind != "line") return null;
            return kind switch
            {
                "horizontal" => Math.Abs(a.P1.X - a.P0.X) / scale,
                "vertical" => Math.Abs(a.P1.Y - a.P0.Y) / scale,
                _ => Flat(a.P0).DistanceTo(Flat(a.P1)) / scale,
            };
        }
        // Two entities: points, and lines that are parallel (distance) or a point and a line.
        bool aLine = a.Kind == "line", bLine = b.Kind == "line";
        bool aPt = a.Kind == "point", bPt = b.Kind == "point";
        if (aPt && bPt)
            return kind switch
            {
                "horizontal" => Math.Abs(a.P0.X - b.P0.X) / scale,
                "vertical" => Math.Abs(a.P0.Y - b.P0.Y) / scale,
                _ => Flat(a.P0).DistanceTo(Flat(b.P0)) / scale,
            };
        if (aLine && bLine)
        {
            double cross = a.Dir2.X * b.Dir2.Y - a.Dir2.Y * b.Dir2.X;
            if (Math.Abs(cross) > 1e-9 * Len(a.Dir2) * Len(b.Dir2) + 1e-15) return null; // not parallel: SOLIDWORKS would make an angle
            return kind switch
            {
                "horizontal" when IsVertical(a) && IsVertical(b) => Math.Abs(a.P0.X - b.P0.X) / scale,
                "vertical" when IsHorizontal(a) && IsHorizontal(b) => Math.Abs(a.P0.Y - b.P0.Y) / scale,
                "linear" => LineDistance(a, b.P0) / scale,
                _ => null,
            };
        }
        if (aLine && bPt || aPt && bLine)
        {
            var line = aLine ? a : b;
            var pt = aLine ? b.P0 : a.P0;
            return kind switch
            {
                "horizontal" when IsVertical(line) => Math.Abs(pt.X - line.P0.X) / scale,
                "vertical" when IsHorizontal(line) => Math.Abs(pt.Y - line.P0.Y) / scale,
                "linear" => LineDistance(line, pt) / scale,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>Distance on the sheet from a point to the infinite line through a segment.</summary>
    private static double LineDistance(OnSheet line, Vec3 p)
    {
        double dx = line.P1.X - line.P0.X, dy = line.P1.Y - line.P0.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        return Math.Abs((p.X - line.P0.X) * dy - (p.Y - line.P0.Y) * dx) / len;
    }

    /// <summary>
    /// Selects a model entity in the active view: SelectByID2 at its sheet position (the verified recipe), checking that
    /// what got selected is drawn where the requested entity is; IView.SelectEntity is the fallback.
    /// </summary>
    private static void SelectInView(SwContext ctx, IModelDoc2 d, ViewHit hit, Resolved r, OnSheet g, Xform t, bool part, bool append,
        string arg, List<string> checks, bool pickFirst = false, int mark = 0)
    {
        // The exact entity first (IView.SelectEntity, then IEntity.Select4 with the view in the SelectData), the sheet pick
        // last. Live: the pick by coordinates found no edge in a view, and a failed pick with append also dropped the
        // selection made before it, so the second of two edges was refused. Annotations (one entity, pickFirst) try the
        // pick first: a leader attaches where the entity was picked, not at its end point.
        var sm = d.Sel();
        int before = sm.GetSelectedObjectCount2(-1);
        if (pickFirst && !append && before == 0)
        {
            // A pick in the active view selects the view itself (type 12) as well as the entity under the point (VERIFIED):
            // keep the entity, drop the rest. Its pick point is where a leader attaches.
            var p = g.Pick;
            if (d.Extension.SelectByID2("", g.Kind == "point" ? "VERTEX" : "EDGE", p.X, p.Y, p.Z, false, mark, null, 0))
            {
                int n = sm.GetSelectedObjectCount2(-1), keep = -1;
                for (int i = 1; i <= n && keep < 0; i++)
                    if (sm.DrwSelected(i) is IEdge or IVertex && (!part || DrawnAlike(sm.DrwSelected(i), g, t))) keep = i;
                if (keep > 0)
                {
                    for (int i = n; i >= 1; i--)
                        if (i != keep) sm.DeSelect2(i, -1);
                    if (sm.GetSelectedObjectCount2(-1) == 1 && sm.DrwSelected(1) is IEdge or IVertex)
                    {
                        checks.Add($"'{arg}' picked at its sheet position");
                        return;
                    }
                }
            }
            d.ClearSelection2(true);
        }
        if (mark == 0 && hit.View.SelectEntity(r.Obj, append) && sm.GetSelectedObjectCount2(-1) == before + 1) // no mark there
        {
            checks.Add($"'{arg}' selected through the view (IView.SelectEntity)");
            return;
        }
        while (sm.GetSelectedObjectCount2(-1) > before) sm.DeSelect2(sm.GetSelectedObjectCount2(-1), -1);
        var data = sm.CreateSelectData();
        data.View = (View)(object)hit.View;
        data.Mark = mark;
        if (r.Obj is IEntity ent && ent.Select4(append, data) && sm.GetSelectedObjectCount2(-1) == before + 1)
        {
            checks.Add($"'{arg}' selected in the view (IEntity.Select4)");
            return;
        }
        while (sm.GetSelectedObjectCount2(-1) > before) sm.DeSelect2(sm.GetSelectedObjectCount2(-1), -1);
        string type = g.Kind == "point" ? "VERTEX" : "EDGE";
        var at = g.Pick;
        string other = "";
        if (sm.GetSelectedObjectCount2(-1) == before &&
            d.Extension.SelectByID2("", type, at.X, at.Y, 0, append, mark, null, 0) && sm.GetSelectedObjectCount2(-1) == before + 1)
        {
            var sel = sm.DrwSelected(before + 1);
            if (!part || DrawnAlike(sel, g, t))
            {
                checks.Add($"'{arg}' picked at its sheet position");
                return;
            }
            other = sel switch
            {
                IEdge e => $" (the pick found another {Topology.Describe(e).Curve} edge there)",
                IVertex => " (the pick found another vertex there)",
                _ => $" (the pick found a {sel?.GetType().Name ?? "nothing"})",
            };
            sm.DeSelect2(before + 1, -1);
        }
        throw new SwException(ErrorCodes.EntityNotFound,
            $"'{arg}' ({r.Label}) could not be selected in '{hit.Name}' at sheet [{Fmt([at.X * 1000, at.Y * 1000])}] mm{other}.",
            "The edge may be hidden in this view (hidden lines can't be picked) or lie behind another edge; pick one that is visible in the view.");
    }

    /// <summary>True when a selected entity is drawn where the requested one is (same sheet points), e.g. a coincident edge behind it.</summary>
    private static bool DrawnAlike(object? sel, OnSheet g, Xform t)
    {
        switch (sel)
        {
            case IVertex v when g.Kind == "point":
                return Flat(t.ApplyPoint(Vec3.From(v.Point()))).DistanceTo(Flat(g.P0)) < PosTol;
            case IEdge e when g.Kind != "point":
            {
                var s = Topology.Describe(e);
                var a = Flat(t.ApplyPoint(s.Start));
                var b = Flat(t.ApplyPoint(s.End));
                var m = Flat(t.ApplyPoint(s.Mid));
                bool ends = (a.DistanceTo(Flat(g.P0)) < PosTol && b.DistanceTo(Flat(g.P1)) < PosTol) ||
                            (a.DistanceTo(Flat(g.P1)) < PosTol && b.DistanceTo(Flat(g.P0)) < PosTol);
                return ends && m.DistanceTo(Flat(g.Mid)) < PosTol;
            }
        }
        return false;
    }

    private static bool DeleteDimension(IModelDoc2 d, IDisplayDimension dd)
    {
        try
        {
            d.ClearSelection2(true);
            var an = dd.DrwAnnotation();
            bool ok = an is not null && an.Select2(false, 0) && d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
            return ok;
        }
        catch (COMException) { return false; }
    }

    private static string Show(double v, bool angular) => angular ? $"{Units.Deg(v)}°" : $"{Units.Mm(v)} mm";

    // ================================================================== drw_note_add

    public Task<DrwNoteResult> NoteAddAsync(string? text, double[]? atMm, double? heightMm, string? view, string? doc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw SwException.InvalidArgument("'text' is empty.");
        if (text.Length > 4000) throw SwException.InvalidArgument($"'text' is {text.Length} characters long (at most 4000).");
        var at = Sheet2(atMm, "atMm");
        if (heightMm is double hm && (!double.IsFinite(hm) || hm < 0.5 || hm > 500))
            throw SwException.InvalidArgument($"heightMm must be between 0.5 and 500 (got {hm}).");
        string apiText = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return session.RunAsync("drw_note_add", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            ViewHit? hit = view is null ? null : FindView(ctx, drw, view, "view");
            var sheet = hit is null ? (drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.")) : SheetOf(drw, hit);
            if (hit is not null) Activate(drw, hit);
            else OnSheetOnly(ctx, drw, sheet);
            d.ClearSelection2(true); // a selected entity would get the note as a leader
            var note = d.DrwInsertNote(apiText)
                       ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS created no note.", "Check that the drawing is not in sheet-format edit mode.");
            var an = note.DrwAnnotation() ?? throw new SwException(ErrorCodes.Internal, "The new note has no annotation.");
            var checks = new List<string>();
            bool placed = an.SetPosition2(Units.M(at.X), Units.M(at.Y), 0);
            double? height = null;
            if (heightMm is double h) height = SetNoteHeight(ctx, note, an, Units.M(h));
            d.ClearSelection2(true);

            string got = (note.GetText() ?? "").Replace("\r\n", "\n");
            string want = text.Replace("\r\n", "\n");
            if (got != want)
            {
                // Property links ($PRP:...) and formatting tags read back evaluated; plain text must match exactly.
                if (want.Contains('$') || want.Contains('<'))
                    ctx.Warn($"The note reads '{Clip(got, 80)}' (links and formatting are evaluated).");
                else
                {
                    bool gone = an.Select2(false, 0) && d.Extension.DeleteSelection2(0);
                    d.ClearSelection2(true);
                    throw new SwException(ErrorCodes.VerifyFailed, $"The note was created but its text reads '{Clip(got, 80)}'; it was {(gone ? "" : "not ")}removed.");
                }
            }
            else checks.Add("text read back");
            var pos = an.DrwPosition();
            if (placed && pos.Length >= 2 && Math.Abs(pos[0] - Units.M(at.X)) <= PosTol && Math.Abs(pos[1] - Units.M(at.Y)) <= PosTol)
                checks.Add("position read back");
            else
                ctx.Warn($"The note sits at [{(pos.Length < 2 ? "?" : Fmt([pos[0] * 1000, pos[1] * 1000]))}] mm{(placed ? "" : " (SetPosition2 returned false)")}, " +
                         $"not at [{Fmt([at.X, at.Y])}] mm.");
            string noteName = note.GetName() ?? "";
            string? owner = OwnerOf(drw, sheet, noteName);
            if (hit is not null && owner is not null && !string.Equals(owner, hit.Name, StringComparison.OrdinalIgnoreCase))
                ctx.Warn($"The note belongs to '{owner}', not '{hit.Name}'.");
            if (hit is null && owner is not null && !string.Equals(owner, sheet.GetName(), StringComparison.OrdinalIgnoreCase))
                ctx.Warn($"The note belongs to view '{owner}' (it moves with that view), not to the sheet.");
            return new DrwNoteResult
            {
                Doc = ctx.Title(d),
                Note = noteName,
                Text = text,
                AtMm = pos.Length >= 2 ? [Math.Round(pos[0] * 1000, 3), Math.Round(pos[1] * 1000, 3)] : null,
                HeightMm = height is double hh ? Units.Mm(hh) : Units.Mm(note.GetHeight()),
                View = hit?.Name,
                Checks = checks,
            };
        }, RunOptions.Write(doc), ct);
    }

    /// <summary>INote.SetHeight, then the text format's character height as the fallback; returns the height read back (m).</summary>
    private static double SetNoteHeight(SwContext ctx, INote note, IAnnotation an, double h)
    {
        note.SetHeight(h);
        double got = note.GetHeight();
        if (Math.Abs(got - h) < 1e-7) return got;
        try
        {
            if ((object?)an.GetTextFormat(0) is ITextFormat tf)
            {
                tf.CharHeight = h;
                an.SetTextFormat(0, false, tf);
                got = note.GetHeight();
            }
        }
        catch (COMException) { /* keep what SetHeight gave */ }
        if (Math.Abs(got - h) >= 1e-7) ctx.Warn($"The note's text height reads {Units.Mm(got)} mm, not {Units.Mm(h)} mm.");
        return got;
    }

    /// <summary>
    /// Makes sure no model view is active, so a note lands on the sheet itself: the sheet is a view of its own (named like
    /// the sheet), so activating it deactivates the others; ActivateSheet is the fallback.
    /// </summary>
    private static void OnSheetOnly(SwContext ctx, IDrawingDoc drw, ISheet sheet)
    {
        static bool ModelViewActive(IDrawingDoc drw) =>
            drw.DrwActiveView() is IView v && v.Type != (int)swDrawingViewTypes_e.swDrawingSheet;
        if (!ModelViewActive(drw)) return;
        string name = sheet.GetName();
        if (!drw.ActivateView(name) || ModelViewActive(drw)) drw.ActivateSheet(name);
        if (drw.DrwActiveView() is IView still && still.Type != (int)swDrawingViewTypes_e.swDrawingSheet)
            ctx.Warn($"View '{still.GetName2()}' stays active; the note may belong to it.");
    }

    /// <summary>The view (or the sheet, by its name) whose notes include <paramref name="noteName"/>.</summary>
    private static string? OwnerOf(IDrawingDoc drw, ISheet sheet, string noteName)
    {
        if (noteName.Length == 0) return null;
        try
        {
            if (drw.DrwSheetView() is IView sv && sv.DrwNotes().Any(n => n.GetName() == noteName)) return sheet.GetName();
            foreach (var v in sheet.DrwViews())
                if (v.DrwNotes().Any(n => n.GetName() == noteName)) return v.GetName2();
        }
        catch (COMException) { /* informational only */ }
        return null;
    }

    // ================================================================== drw_bom_add

    public Task<DrwBomResult> BomAddAsync(string? view, double[]? atMm, string? template, string? doc, CancellationToken ct = default)
    {
        (double X, double Y)? at = atMm is null ? null : Sheet2(atMm, "atMm");
        string? tpl = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            if (!template.Trim().EndsWith(".sldbomtbt", StringComparison.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"'template' must be a .sldbomtbt BOM template (got '{template}').");
            tpl = session.Paths.ForRead(template, "template");
        }
        return session.RunAsync("drw_bom_add", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var hit = FindView(ctx, drw, view, "view");
            var model = ModelOf(hit);
            var sheet = SheetOf(drw, hit);
            string path = tpl ?? DefaultBomTemplate(ctx);
            var sp = sheet.DrwProps();
            var (x, y) = at ?? (sp.Length >= 7 ? (sp[5] * 1000 - 10, sp[6] * 1000 - 10) : (400, 280));
            string config = hit.View.ReferencedConfiguration is { Length: > 0 } c ? c : DocDefault;
            d.ClearSelection2(true);
            // VERIFIED live: a part's view gets no BOM with the top-level type (InsertBomTable4 returns nothing); parts-only
            // makes it. Assemblies keep top-level.
            int bomType = model.GetType() == (int)swDocumentTypes_e.swDocPART
                ? (int)swBomType_e.swBomType_PartsOnly : (int)swBomType_e.swBomType_TopLevelOnly;
            var bom = hit.View.InsertBomTable4(false, Units.M(x), Units.M(y), (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
                bomType, config, path, false, 0, false);
            d.ClearSelection2(true);
            if (bom is null)
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no BOM for '{hit.Name}'.",
                    "Check the template and that the view shows a part or assembly.");
            var table = (object)bom as ITableAnnotation
                        ?? throw new SwException(ErrorCodes.Internal, "The BOM is not a table annotation.");
            int rows = table.RowCount, cols = table.ColumnCount, headers = Math.Max(0, table.GetHeaderCount());
            bool headerAtBottom = table.GetHeaderStyle() == (int)swTableHeaderPosition_e.swTableHeader_Bottom;
            int headerRow = headerAtBottom ? rows - 1 : 0;
            var columns = Enumerable.Range(0, cols)
                .Select(i => table.GetColumnTitle(i) is { Length: > 0 } title ? title : headers > 0 && rows > 0 ? table.DisplayedText[headerRow, i] ?? "" : "")
                .ToList();
            var dataRows = headerAtBottom ? Enumerable.Range(0, Math.Max(0, rows - headers)) : Enumerable.Range(headers, Math.Max(0, rows - headers));
            var items = dataRows.Take(MaxBomRows)
                .Select(r => (IReadOnlyList<string>)Enumerable.Range(0, cols).Select(ci => table.DisplayedText[r, ci] ?? "").ToList()).ToList();
            int dataCount = Math.Max(0, rows - headers);
            var checks = new List<string> { $"{dataCount} row(s) and {cols} column(s) read back" };
            if (dataCount == 0)
                ctx.Warn($"The BOM lists no rows: '{Path.GetFileName(model.GetPathName() ?? "")}' has no components or parts to list.");
            return new DrwBomResult
            {
                Doc = ctx.Title(d),
                View = hit.Name,
                Rows = dataCount,
                Columns = columns,
                Items = items,
                AtMm = [Math.Round(x, 3), Math.Round(y, 3)],
                Template = path,
                Configuration = config,
                Checks = checks,
            };
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 90) }, ct);
    }

    /// <summary>bom-standard.sldbomtbt from the BOM template folders (Tools > Options > File Locations).</summary>
    private static string DefaultBomTemplate(SwContext ctx)
    {
        const string file = "bom-standard.sldbomtbt";
        var folders = Folders(ctx, swUserPreferenceStringValue_e.swFileLocationsBOMTemplates);
        foreach (var dir in folders)
        {
            string p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        throw new SwException(ErrorCodes.IoError, $"'{file}' is not in the BOM template folders ({(folders.Count == 0 ? "none set" : string.Join("; ", folders))}).",
            "Pass 'template' with the path of a .sldbomtbt file.");
    }

    // ================================================================== drw_center_marks

    public Task<DrwCountResult> CenterMarksAsync(IReadOnlyList<string>? views, string? doc, CancellationToken ct = default) =>
        session.RunAsync("drw_center_marks", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var targets = views is null || views.Count == 0
                ? ModelViews(drw, currentSheetOnly: true)
                : views.Select((n, i) => FindView(ctx, drw, n, $"views[{i}]")).ToList();
            if (targets.Count == 0) throw SwException.InvalidArgument("The sheet has no views.", "Create them first with drw_views_standard.");
            var counts = new List<DrwViewCount>();
            foreach (var t in targets)
            {
                int before = Count(t.View).CenterMarks;
                Activate(drw, t);
                bool ok = t.View.AutoInsertCenterMarks2((int)swAutoInsertCenterMarkTypes_e.swAutoInsertCenterMarkType_Hole, 0, true, true, true,
                    0.0025, 0.0005, true, false, 0);
                int after = Count(t.View).CenterMarks;
                if (!ok && after == before) ctx.Warn($"AutoInsertCenterMarks2 returned false for '{t.Name}'.");
                counts.Add(new DrwViewCount { View = t.Name, Before = before, After = after, Added = after - before });
            }
            d.ClearSelection2(true);
            d.GraphicsRedraw2();
            int added = counts.Sum(c => c.Added);
            return new DrwCountResult
            {
                Doc = ctx.Title(d), Added = added, Views = counts,
                Hint = added > 0 ? null :
                    "No center marks were added: the views show no holes face-on (center marks go on circular edges seen as circles), " +
                    "or the holes already have them. In testing, a long SOLIDWORKS session sometimes stopped adding center marks until " +
                    "SOLIDWORKS was restarted.",
            };
        }, RunOptions.Write(doc) with { Timeout = Longer(session, 90) }, ct);

    // ================================================================== drw_info

    public Task<DrwInfoResult> InfoAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("drw_info", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            string? current = drw.DrwCurrentSheet()?.GetName();
            List<List<IView>> bySheet;
            try { bySheet = drw.DrwViewsBySheet(); }
            catch (COMException) { bySheet = []; }
            var sheets = new List<DrwSheet>();
            int total = 0;
            bool truncated = false;
            foreach (var name in drw.DrwSheetNames())
            {
                var sh = drw.DrwSheet(name);
                if (sh is null) continue;
                var views = new List<DrwView>();
                foreach (var v in sh.DrwViews())
                {
                    if (total >= MaxViews) { truncated = true; break; }
                    views.Add(ViewInfo(ctx, v, true));
                    total++;
                }
                var sheetView = bySheet.FirstOrDefault(a => a.Count > 0 && string.Equals(a[0].GetName2(), name, StringComparison.OrdinalIgnoreCase))?[0];
                sheets.Add(SheetInfo(sh, string.Equals(name, current, StringComparison.OrdinalIgnoreCase), views, sheetView is null ? null : Count(sheetView)));
            }
            return new DrwInfoResult { Doc = ctx.Title(d), Sheets = sheets, ViewCount = total, Truncated = truncated ? true : null };
        }, RunOptions.Read(doc), ct);

    // ================================================================== shared helpers

    private sealed record ViewHit(IView View, string Name, string Sheet);

    private static List<ViewHit> AllViews(IDrawingDoc drw)
    {
        // The current sheet's views through GetFirstView / GetNextView: methods on the IView objects ISheet.GetViews returns
        // (GetAnnotations, AutoInsertCenterMarks2, InsertBomTable4) did nothing live, while the same calls on traversal
        // views work. Other sheets fall back to ISheet.GetViews.
        var list = new List<ViewHit>();
        string? cur = drw.DrwCurrentSheet()?.GetName();
        foreach (var sn in drw.DrwSheetNames())
        {
            if (string.Equals(sn, cur, StringComparison.OrdinalIgnoreCase))
            {
                for (var v = drw.DrwSheetView(); v is not null; v = (object?)v.GetNextView() as IView)
                    list.Add(new ViewHit(v, v.GetName2() ?? "", sn));
                continue;
            }
            if (drw.DrwSheet(sn) is ISheet sh)
                foreach (var v in sh.DrwViews())
                    list.Add(new ViewHit(v, v.GetName2() ?? "", sn));
        }
        return list;
    }

    /// <summary>The model views (not the sheets) of the drawing, or of its current sheet.</summary>
    private static List<ViewHit> ModelViews(IDrawingDoc drw, bool currentSheetOnly = false)
    {
        string? cur = drw.DrwCurrentSheet()?.GetName();
        return AllViews(drw).Where(h => h.View.Type != (int)swDrawingViewTypes_e.swDrawingSheet &&
                                        (!currentSheetOnly || string.Equals(h.Sheet, cur, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>A view by name (case-insensitive); its sheet is activated when it isn't the current one.</summary>
    private static ViewHit FindView(SwContext ctx, IDrawingDoc drw, string? name, string arg)
    {
        if (string.IsNullOrWhiteSpace(name)) throw SwException.InvalidArgument($"'{arg}' must name a drawing view, e.g. \"Drawing View1\" (drw_info lists them).");
        var all = AllViews(drw);
        var hit = all.FirstOrDefault(h => string.Equals(h.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw new SwException(ErrorCodes.EntityNotFound, $"No drawing view named '{name}'.",
                      all.Count == 0 ? "The drawing has no views yet; create them with drw_views_standard."
                          : $"Views: {string.Join(", ", all.Take(12).Select(h => h.Name))}{(all.Count > 12 ? ", …" : "")}.");
        var cur = drw.DrwCurrentSheet();
        if (cur is not null && !string.Equals(cur.GetName(), hit.Sheet, StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Access != DocAccess.Write || !drw.ActivateSheet(hit.Sheet))
                throw new SwException(ErrorCodes.InvalidArgument, $"View '{hit.Name}' is on sheet '{hit.Sheet}', not on the current sheet '{cur.GetName()}'.",
                    "Work on the current sheet, or switch sheets in SOLIDWORKS.");
            ctx.Warn($"Switched to sheet '{hit.Sheet}' (it holds '{hit.Name}').");
        }
        return hit;
    }

    private static ISheet SheetOf(IDrawingDoc drw, ViewHit hit) =>
        drw.DrwSheet(hit.Sheet) ?? drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, $"Sheet '{hit.Sheet}' could not be read.");

    private static void Activate(IDrawingDoc drw, ViewHit hit)
    {
        if (!drw.ActivateView(hit.Name))
            throw new SwException(ErrorCodes.FeatureFailed, $"Could not activate view '{hit.Name}'.", "Check that the view is visible (not hidden).");
    }

    /// <summary>Selects a drawing view by name (the DRAWINGVIEW selection of the verified projected-view recipe).</summary>
    private static void SelectView(IModelDoc2 d, string name, bool append = false)
    {
        if (!d.Extension.SelectByID2(name, "DRAWINGVIEW", 0, 0, 0, append, 0, null, 0))
            throw new SwException(ErrorCodes.FeatureFailed, $"Could not select view '{name}'.");
    }

    private static IModelDoc2 ModelOf(ViewHit hit) =>
        hit.View.ReferencedDocument
        ?? throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' has no model loaded (its file may be missing, or the view is lightweight).",
            "Open the model (doc_open), then retry.");

    /// <summary>The model's bounding box [xmin, ymin, zmin, xmax, ymax, zmax] (m), or null.</summary>
    private static double[]? ModelBox(IModelDoc2 m)
    {
        var b = m.GetType() switch
        {
            (int)swDocumentTypes_e.swDocPART => ((IPartDoc)m).PartBox(),
            (int)swDocumentTypes_e.swDocASSEMBLY => ((IAssemblyDoc)m).AsmBox(),
            _ => [],
        };
        return b.Length == 6 && b.All(double.IsFinite) && b[3] - b[0] + b[4] - b[1] + b[5] - b[2] > 1e-9 ? b : null;
    }

    /// <summary>The model box mapped onto the sheet (mm), from the view's model-to-sheet transform.</summary>
    private static SheetRect? Project(double[] xf, double[]? box)
    {
        if (xf.Length < 13 || box is not { Length: 6 }) return null;
        var t = new Xform(xf);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (double x in new[] { box[0], box[3] })
            foreach (double y in new[] { box[1], box[4] })
                foreach (double z in new[] { box[2], box[5] })
                {
                    var p = t.ApplyPoint(new Vec3(x, y, z));
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                }
        return new SheetRect(x0 * 1000, y0 * 1000, x1 * 1000, y1 * 1000);
    }

    private static SheetRect Outline(ViewHit hit) =>
        SheetRect.FromMeters(hit.View.DrwOutline())
        ?? throw new SwException(ErrorCodes.Internal, $"The outline of view '{hit.Name}' could not be read.");

    /// <summary>
    /// Where the part is drawn in a view (mm): the model box mapped through the view transform when that agrees with the
    /// view's outline, else the outline (detail views: always the outline).
    /// </summary>
    private static SheetRect GeometryBox(SwContext ctx, ViewHit hit)
    {
        var outline = Outline(hit);
        if (hit.View.Type == (int)swDrawingViewTypes_e.swDrawingDetailView) return outline;
        var proj = hit.View.ReferencedDocument is IModelDoc2 m ? Project(hit.View.DrwModelToSheet(), ModelBox(m)) : null;
        if (proj is SheetRect p && outline.Inflate(2).Contains(p.Cx, p.Cy)) return p;
        if (proj is not null)
            ctx.Warn($"The model-to-sheet transform of '{hit.Name}' puts the part at [{Fmt(proj.Value.ToArray())}] mm, outside its outline " +
                     $"[{Fmt(outline.ToArray())}] mm; the outline was used.");
        return outline;
    }

    /// <summary>Warns when a view's model box (via its transform) doesn't sit inside its outline: a sign the sheet mapping is off.</summary>
    private static void MappingCheck(SwContext ctx, IView v, DrwView info)
    {
        if (info.ModelBoxMm is not { Length: 4 } b || info.BoundsMm is not { Length: 4 } o) return;
        var box = new SheetRect(b[0], b[1], b[2], b[3]);
        if (!box.Inside(new SheetRect(o[0], o[1], o[2], o[3]).Inflate(2)))
            ctx.Warn($"View '{info.Name}': its model box maps to [{Fmt(b)}] mm, which is not inside its outline [{Fmt(o)}] mm (check the sheet mapping).");
    }

    /// <summary>
    /// A free spot for a new view next to its parent: aligned with it on the preferred sides first (as close as the other
    /// views allow), then the free place nearest to those; warns when nothing is free.
    /// </summary>
    private static (double X, double Y) Place(ISheet sheet, ViewHit parent, double w, double h, string[] sides, SwContext ctx)
    {
        var area = SheetArea(sheet);
        var occupied = Occupied(sheet);
        var outline = Outline(parent);
        foreach (var side in sides)
        {
            var (sx, sy, ok) = DrawingLayout.SideSpot(outline, side, w, h, occupied, area);
            if (ok) return (sx, sy);
        }
        var (x, y, free) = DrawingLayout.FindSpot(area, occupied, w, h, DrawingLayout.SideCandidates(outline, w, h, sides));
        if (!free) ctx.Warn($"No free space of {Math.Round(w)} x {Math.Round(h)} mm next to '{parent.Name}'; the new view may overlap others (pass atMm).");
        return (x, y);
    }

    /// <summary>The sheet inside a 5 mm edge (mm).</summary>
    private static SheetRect SheetArea(ISheet sheet)
    {
        var sp = sheet.DrwProps();
        return sp.Length >= 7 ? new SheetRect(5, 5, sp[5] * 1000 - 5, sp[6] * 1000 - 5) : new SheetRect(-1e5, -1e5, 1e5, 1e5);
    }

    /// <summary>The outlines of the views on the sheet (mm).</summary>
    private static List<SheetRect> Occupied(ISheet sheet) =>
        sheet.DrwViews().Select(v => SheetRect.FromMeters(v.DrwOutline())).OfType<SheetRect>().ToList();

    /// <summary>Warns about views (the new ones against all on the sheet) whose outlines overlap.</summary>
    private static void OverlapCheck(SwContext ctx, ISheet sheet, IReadOnlyList<IView> newViews, List<string> checks)
    {
        try
        {
            var all = sheet.DrwViews().Select(v => (Name: v.GetName2() ?? "", R: SheetRect.FromMeters(v.DrwOutline()))).Where(x => x.R is not null).ToList();
            var sp = sheet.DrwProps();
            var paper = sp.Length >= 7 ? new SheetRect(0, 0, sp[5] * 1000, sp[6] * 1000) : (SheetRect?)null;
            var names = newViews.Select(v => v.GetName2() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var clashes = new List<string>();
            foreach (var n in all.Where(x => names.Contains(x.Name)))
            {
                foreach (var o in all.Where(x => x.Name != n.Name && (!names.Contains(x.Name) || string.CompareOrdinal(x.Name, n.Name) > 0)))
                    if (n.R!.Value.Overlaps(o.R!.Value)) clashes.Add($"'{n.Name}' overlaps '{o.Name}'");
                if (paper is SheetRect p && !n.R!.Value.Inside(p, 0.5)) clashes.Add($"'{n.Name}' extends past the sheet");
            }
            if (clashes.Count == 0) checks.Add("no overlaps with other views, inside the sheet");
            else ctx.Warn(string.Join("; ", clashes.Take(6)) + ".");
        }
        catch (COMException) { /* informational only */ }
    }

    private static string DeleteViews(IModelDoc2 d, IEnumerable<string> names)
    {
        var list = names.Where(n => n.Length > 0).ToList();
        var left = new List<string>();
        foreach (var n in list)
        {
            try
            {
                d.ClearSelection2(true);
                if (!d.Extension.SelectByID2(n, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0) || !d.Extension.DeleteSelection2(0)) left.Add(n);
            }
            catch (COMException) { left.Add(n); }
        }
        d.ClearSelection2(true);
        if (list.Count == 0) return "";
        return left.Count == 0 ? $"Removed the view(s) this call created ({string.Join(", ", list)})." : $"Could not remove {string.Join(", ", left)} (delete them in SOLIDWORKS).";
    }

    /// <summary>
    /// Best effort: deletes a sketch segment this call drew. It may already be gone with its view, and then its proxy can
    /// fail in any way (even RPC_E_DISCONNECTED); that must not replace the error being reported, so nothing escapes.
    /// </summary>
    private static void DeleteSegment(IModelDoc2 d, ISketchSegment? seg)
    {
        if (seg is null) return;
        try
        {
            d.ClearSelection2(true);
            if (seg.Select4(false, d.Sel().CreateSelectData())) d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
        }
        catch (Exception) { /* already deleted, or SOLIDWORKS went away: the next call reports that */ }
    }

    /// <summary>
    /// Draws a sketch entity in the active view at sheet coordinates. The verified recipe passes sheet coordinates; when
    /// the view's sketch has its own transform, a selection at the sheet point tells which interpretation SOLIDWORKS used,
    /// and the entity is redrawn through the transform if needed.
    /// </summary>
    private static ISketchSegment DrawInView(SwContext ctx, IModelDoc2 d, ViewHit hit, string what, Func<Xform, ISketchSegment?> draw,
        (double X, double Y) probe, List<string> checks)
    {
        // With the view active and AddToDB on, sketch entities take sheet coordinates (m). A selection pick at the sheet
        // point to confirm the segment (SelectByID2) deactivated the view live, so the next entities and the section or
        // detail command lost it: the segment's owner sketch is compared with the view's instead.
        var seg = draw(Xform.Identity)
                  ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not draw the {what} in '{hit.Name}'.",
                      "Check that the view can be activated (drw_info) and is not locked.");
        string? owner = SketchName(seg.GetSketch()), mine = SketchName(hit.View.DrwSketch());
        if (owner is not null && mine is not null && !string.Equals(owner, mine, StringComparison.Ordinal))
            ctx.Warn($"The {what} went into sketch '{owner}', not the sketch of '{hit.Name}' ('{mine}').");
        else
            checks.Add($"{what} drawn in '{hit.Name}'");
        return seg;
    }

    private static string? SketchName(ISketch? sk)
    {
        try { return ((object?)sk as IFeature)?.Name; }
        catch (COMException) { return null; }
    }

    /// <summary>True when a selection at the sheet point picks this sketch segment.</summary>
    private static bool Probe(IModelDoc2 d, ISketchSegment seg, (double X, double Y) p)
    {
        try
        {
            d.ClearSelection2(true);
            bool ok = d.Extension.SelectByID2("", "SKETCHSEGMENT", p.X, p.Y, 0, false, 0, null, 0);
            var sel = ok ? d.Sel().DrwSelected(1) as ISketchSegment : null;
            return sel is not null && string.Equals(sel.GetName(), seg.GetName(), StringComparison.Ordinal);
        }
        catch (COMException) { return false; }
        finally { d.ClearSelection2(true); }
    }

    private static void CheckLabelFree(IDrawingDoc drw, string label)
    {
        string l = Regex.Escape(label);
        if (AllViews(drw).Any(v => Regex.IsMatch(v.Name, $@"\b{l}\s*(-\s*{l}\b|\(\s*\d)", RegexOptions.IgnoreCase)))
            throw SwException.InvalidArgument($"Label '{label}' is already used by another section or detail view.", "Leave 'label' out for the next free letter.");
    }

    private static string? Label(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        string l = label.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(l, "^[A-Z]{1,2}$"))
            throw SwException.InvalidArgument($"label '{label}' must be one or two letters (e.g. \"A\").");
        return l;
    }

    // ------------------------------------------------------------------ reading views and sheets

    private static DrwSheet SheetInfo(ISheet sh, bool current, IReadOnlyList<DrwView>? views = null, DrwCounts? counts = null)
    {
        var p = sh.DrwProps();
        int paper = p.Length > 0 ? (int)Math.Round(p[0]) : DrawingLayout.UserPaper;
        bool none = p.Length > 1 && (int)Math.Round(p[1]) == TemplateNone;
        double s1 = p.Length > 3 ? p[2] : 1, s2 = p.Length > 3 && p[3] != 0 ? p[3] : 1;
        double w = p.Length > 6 ? Units.Mm(p[5]) : 0, h = p.Length > 6 ? Units.Mm(p[6]) : 0;
        string tpl = sh.GetTemplateName() ?? "";
        return new DrwSheet
        {
            Name = sh.GetName(),
            Size = DrawingLayout.SizeName(paper),
            WidthMm = w,
            HeightMm = h,
            Orientation = h > w ? "portrait" : "landscape",
            Scale = DrawingLayout.FormatScale(s1, s2),
            Projection = p.Length > 4 && p[4] != 0 ? "first_angle" : "third_angle",
            Format = none || tpl.Length == 0 ? null : Path.GetFileName(tpl),
            Current = current ? true : null,
            Views = views,
            Annotations = counts,
        };
    }

    private static string Summary(DrwSheet s) =>
        $"{s.Size.ToUpperInvariant()} {s.Orientation} {s.WidthMm} x {s.HeightMm} mm, {s.Scale}, {s.Projection.Replace('_', ' ')}, format {s.Format ?? "none"}";

    private static DrwView ViewInfo(SwContext ctx, IView v, bool counts)
    {
        int type = v.Type;
        var xf = v.DrwModelToSheet();
        var ratio = v.DrwScaleRatio();
        string scale = ratio.Length >= 2 && ratio[0] > 0 && ratio[1] > 0 ? DrawingLayout.FormatScale(ratio[0], ratio[1]) : DrawingLayout.FormatScale(v.ScaleDecimal);
        var pos = v.DrwPosition();
        var outline = SheetRect.FromMeters(v.DrwOutline());
        SheetRect? box = null;
        if (type != (int)swDrawingViewTypes_e.swDrawingDetailView && type != (int)swDrawingViewTypes_e.swDrawingSheet)
        {
            try { box = v.ReferencedDocument is IModelDoc2 m ? Project(xf, ModelBox(m)) : null; }
            catch (COMException) { /* no model loaded */ }
        }
        string? model = null, config = null;
        try
        {
            model = v.GetReferencedModelName() is { Length: > 0 } mn ? mn : null;
            config = v.ReferencedConfiguration is { Length: > 0 } cf ? cf : null;
        }
        catch (COMException) { /* sheet views have no model */ }
        return new DrwView
        {
            Name = v.GetName2() ?? "",
            Type = TypeName(type),
            Orientation = xf.Length >= 13 && type != (int)swDrawingViewTypes_e.swDrawingSheet ? DrawingLayout.Classify(DrawingLayout.AxesOf(xf)) : null,
            Scale = scale,
            CenterMm = pos.Length >= 2 ? [Math.Round(pos[0] * 1000, 3), Math.Round(pos[1] * 1000, 3)] : null,
            BoundsMm = outline?.ToArray(),
            ModelBoxMm = box?.ToArray(),
            Model = model,
            Configuration = config,
            Parent = v.DrwBaseView()?.GetName2(),
            Annotations = counts ? Count(v) : null,
        };
    }

    private static string TypeName(int t) => t switch
    {
        (int)swDrawingViewTypes_e.swDrawingSheet => "sheet",
        (int)swDrawingViewTypes_e.swDrawingSectionView => "section",
        (int)swDrawingViewTypes_e.swDrawingDetailView => "detail",
        (int)swDrawingViewTypes_e.swDrawingProjectedView => "projected",
        (int)swDrawingViewTypes_e.swDrawingAuxiliaryView => "auxiliary",
        (int)swDrawingViewTypes_e.swDrawingStandardView => "standard",
        (int)swDrawingViewTypes_e.swDrawingNamedView => "named",
        (int)swDrawingViewTypes_e.swDrawingRelativeView => "relative",
        (int)swDrawingViewTypes_e.swDrawingDetachedView => "detached",
        (int)swDrawingViewTypes_e.swDrawingAlternatePositionView => "alternate_position",
        _ => $"type {t}",
    };

    /// <summary>The annotations of a view by kind (IView.GetAnnotations, IAnnotation.GetType).</summary>
    private static DrwCounts Count(IView v)
    {
        int dims = 0, notes = 0, tables = 0, marks = 0, other = 0;
        foreach (var a in v.DrwAnnotations())
        {
            switch (a.GetType())
            {
                case (int)swAnnotationType_e.swDisplayDimension: dims++; break;
                case (int)swAnnotationType_e.swNote: notes++; break;
                case (int)swAnnotationType_e.swTableAnnotation: tables++; break;
                case (int)swAnnotationType_e.swCenterMarkSym: marks++; break;
                default: other++; break;
            }
        }
        // GetAnnotations may leave some kinds out in some views: the dedicated counts win when they are higher.
        return new DrwCounts
        {
            Dimensions = Math.Max(dims, SafeCount(v.GetDisplayDimensionCount)),
            Notes = Math.Max(notes, SafeCount(v.GetNoteCount)),
            Tables = Math.Max(tables, SafeCount(v.GetTableAnnotationCount)),
            CenterMarks = Math.Max(marks, SafeCount(v.GetCenterMarkCount)),
            Other = other,
        };
    }

    /// <summary>A count from a member that may be missing in some views (-1 when it throws).</summary>
    private static int SafeCount(Func<int> f)
    {
        try { return f(); }
        catch (COMException) { return -1; }
    }

    /// <summary>[x, y] in sheet mm, validated.</summary>
    private static (double X, double Y) Sheet2(double[]? mm, string arg)
    {
        if (mm is null || mm.Length != 2 || mm.Any(v => !double.IsFinite(v) || Math.Abs(v) > 10000))
            throw SwException.InvalidArgument($"'{arg}' must be [x, y] in sheet mm (from the sheet's lower-left corner).");
        return (mm[0], mm[1]);
    }

    private static string SideWord(string side) => side switch { "right" => "right of", "left" => "left of", "up" => "above", _ => "below" };

    private static string Fmt(double[]? a) => a is null ? "?" : TopoQuery.Fmt(a.Select(v => Math.Round(v, 3)).ToArray());

    private static string Dir(Vec3 v) => TopoQuery.Fmt(Units.Unit(v.Normalized()).Select(x => Math.Round(x, 4)).ToArray());

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
