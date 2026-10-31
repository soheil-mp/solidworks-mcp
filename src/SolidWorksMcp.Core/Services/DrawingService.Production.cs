using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of drw_sheet_add: a new sheet (settings default to the current sheet's).</summary>
public sealed record DrwSheetAddRequest
{
    public string? Doc { get; init; }
    public string? Name { get; init; }
    public string? Size { get; init; }
    public string? Orientation { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public string? Scale { get; init; }
    public string? Projection { get; init; }
    public string? Format { get; init; }
}

public sealed class DrwSheetAddResult : ToolResult
{
    public required DrwSheet Sheet { get; init; }
    public required IReadOnlyList<string> Sheets { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

/// <summary>Input of drw_view_add: a flat-pattern view of a sheet-metal part, or an auxiliary view of a parent view.</summary>
public sealed record DrwViewAddRequest
{
    public string? Doc { get; init; }
    /// <summary>flat_pattern | auxiliary.</summary>
    public string? Kind { get; init; }
    /// <summary>flat_pattern: the saved sheet-metal part.</summary>
    public string? Model { get; init; }
    /// <summary>auxiliary: the view the new one is projected from.</summary>
    public string? Parent { get; init; }
    /// <summary>auxiliary: the straight edge (model mm) the new view looks perpendicular to.</summary>
    public EntityRef? Edge { get; init; }
    /// <summary>The new view's centre, [x, y] sheet mm; default: a free place (beside the parent for auxiliary).</summary>
    public double[]? AtMm { get; init; }
    /// <summary>flat_pattern: the view's own scale ("1:2"); default: the sheet's.</summary>
    public string? Scale { get; init; }
    /// <summary>flat_pattern: show the bend lines (default true).</summary>
    public bool BendLines { get; init; } = true;
    public string? Label { get; init; }
}

/// <summary>Input of drw_view_edit: break a view (a long part shortened), or cut a broken-out section into it.</summary>
public sealed record DrwViewEditRequest
{
    public string? Doc { get; init; }
    /// <summary>break | broken_out.</summary>
    public string? Kind { get; init; }
    public string? View { get; init; }
    /// <summary>break: vertical (lines across a horizontal part; positions are sheet x) | horizontal (positions are sheet y).</summary>
    public string? Lines { get; init; }
    /// <summary>break: the two break-line positions, sheet mm (x for vertical lines, y for horizontal ones).</summary>
    public double? FromMm { get; init; }
    public double? ToMm { get; init; }
    /// <summary>break: straight | zigzag | small_zigzag | curve | jagged (default zigzag).</summary>
    public string? Style { get; init; }
    /// <summary>broken_out: the circular cut's centre [x, y] sheet mm and radius (sheet mm).</summary>
    public double[]? CenterMm { get; init; }
    public double? RadiusMm { get; init; }
    /// <summary>broken_out: how deep the cut goes into the model, model mm from its front.</summary>
    public double? DepthMm { get; init; }
}

public sealed class DrwViewEditResult : ToolResult
{
    public required string Kind { get; init; }
    public required DrwView View { get; init; }
    /// <summary>The view's outline before the edit [xMin, yMin, xMax, yMax] (mm).</summary>
    public double[]? BoundsBeforeMm { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_sheet_add

    public Task<DrwSheetAddResult> SheetAddAsync(DrwSheetAddRequest q, CancellationToken ct = default)
    {
        if (q.Size is not null) DrawingLayout.Paper(q.Size, q.Orientation, q.WidthMm, q.HeightMm);
        if (q.Scale is not null) DrawingLayout.ParseScale(q.Scale, "scale");
        if (DrawingLayout.Norm(q.Projection) is not (null or "first_angle" or "first" or "third_angle" or "third"))
            throw SwException.InvalidArgument($"Unknown projection '{q.Projection}'.", "Use first_angle (ISO) or third_angle (ANSI).");
        string? name = q.Name?.Trim();
        if (name is { Length: 0 } || name is { Length: > 64 } || name?.IndexOfAny(['@', '/', '\\', '"']) >= 0)
            throw SwException.InvalidArgument($"'name' must be 1 to 64 characters without @ / \\ or quotes (got \"{q.Name}\").");
        string? file = null;
        if (DrawingLayout.Norm(q.Format) is { } fam && fam is not ("iso" or "ansi" or "none"))
        {
            if (!q.Format!.Trim().EndsWith(".slddrt", StringComparison.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"Unknown format '{q.Format}'.", "Use iso, ansi, none or the path of a .slddrt sheet format.");
            file = session.Paths.ForRead(q.Format, "format");
        }
        return session.RunAsync("drw_sheet_add", ctx =>
        {
            var d = ctx.Doc;
            var drw = ctx.Drawing();
            var current = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
            var before = drw.DrwSheetNames();
            string sheetName = name ?? NextSheetName(before);
            if (before.Any(n => string.Equals(n, sheetName, StringComparison.OrdinalIgnoreCase)))
                throw SwException.InvalidArgument($"The drawing already has a sheet named '{sheetName}'.", $"Sheets: {string.Join(", ", before)}.");
            // The new sheet starts as the current one is; the request's settings then apply as in drw_sheet_setup.
            var plan = DrawingLayout.PlanSheet(State(current), q.Size, q.Orientation, q.WidthMm, q.HeightMm, q.Scale, q.Projection,
                file is null ? q.Format : null, file);
            string? format = plan.FormatFile;
            if (plan.Family != "file")
            {
                var (path, note) = FormatPath(ctx, plan.Paper, plan.Family);
                if (note is not null) ctx.Warn($"Sheet format: {note}.");
                format = path;
            }
            bool ok = drw.NewSheet4(sheetName, plan.Paper.Paper, format is null ? TemplateNone : TemplateCustom, plan.Num, plan.Den, plan.FirstAngle,
                format ?? "", Units.M(plan.Paper.WidthMm), Units.M(plan.Paper.HeightMm), DocDefault, 0, 0, 0, 0, 0, 0);
            var sheet = drw.DrwSheet(sheetName);
            if (!ok || sheet is null)
            {
                if (sheet is not null) DeleteSheet(d, drw, sheetName, current.GetName());
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS added no sheet '{sheetName}'.", "Check the size and format (drw_info shows the current sheet).");
            }
            var checks = new List<string>();
            try
            {
                if (!string.Equals(drw.DrwCurrentSheet()?.GetName(), sheetName, StringComparison.OrdinalIgnoreCase) && !drw.ActivateSheet(sheetName))
                    throw new SwException(ErrorCodes.VerifyFailed, $"Sheet '{sheetName}' was added but could not be made current.");
                var info = ApplySheet(ctx, drw, sheet, plan.Paper, plan.Num, plan.Den, plan.FirstAngle, format, checks);
                var after = drw.DrwSheetNames();
                if (after.Length != before.Length + 1) throw new SwException(ErrorCodes.VerifyFailed, $"The drawing has {after.Length} sheets, not {before.Length + 1}.");
                checks.Add($"sheet '{sheetName}' added ({after.Length} sheets) and current");
                d.GraphicsRedraw2();
                return new DrwSheetAddResult { Doc = ctx.Title(d), Sheet = info, Sheets = after, Checks = checks };
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
            {
                throw Rolled(ex, DeleteSheet(d, drw, sheetName, current.GetName()));
            }
        }, RunOptions.Write(q.Doc), ct);
    }

    private static string NextSheetName(IReadOnlyCollection<string> names)
    {
        for (int i = names.Count + 1; ; i++)
            if (!names.Any(n => string.Equals(n, $"Sheet{i}", StringComparison.OrdinalIgnoreCase))) return $"Sheet{i}";
    }

    /// <summary>Deletes a sheet this call added (selected by name) after making another one current.</summary>
    private static string DeleteSheet(IModelDoc2 d, IDrawingDoc drw, string name, string back)
    {
        try
        {
            drw.ActivateSheet(back);
            d.ClearSelection2(true);
            bool gone = d.Extension.SelectByID2(name, "SHEET", 0, 0, 0, false, 0, null, 0) && d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
            return gone && drw.DrwSheet(name) is null ? $"Removed the sheet '{name}'." : $"Could not remove sheet '{name}' (delete it in SOLIDWORKS).";
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            return $"Could not remove sheet '{name}' ({e.Message}).";
        }
    }

    /// <summary>drw_sheet_setup {sheet}: makes that sheet current first.</summary>
    internal static void UseSheet(SwContext ctx, IDrawingDoc drw, string? sheet)
    {
        if (string.IsNullOrWhiteSpace(sheet)) return;
        var names = drw.DrwSheetNames();
        string? hit = names.FirstOrDefault(n => string.Equals(n, sheet.Trim(), StringComparison.OrdinalIgnoreCase))
                      ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sheet named '{sheet}'.", $"Sheets: {string.Join(", ", names)}.");
        if (string.Equals(drw.DrwCurrentSheet()?.GetName(), hit, StringComparison.OrdinalIgnoreCase)) return;
        if (!drw.ActivateSheet(hit) || !string.Equals(drw.DrwCurrentSheet()?.GetName(), hit, StringComparison.OrdinalIgnoreCase))
            throw new SwException(ErrorCodes.FeatureFailed, $"Sheet '{hit}' could not be made current.");
    }

    // ================================================================== drw_view_add

    public Task<DrwViewResult> ViewAddAsync(DrwViewAddRequest q, CancellationToken ct = default)
    {
        string kind = DrawingLayout.Norm(q.Kind) switch
        {
            "flat_pattern" or "flat" or "flatpattern" => "flat_pattern",
            "auxiliary" or "aux" => "auxiliary",
            null => throw SwException.InvalidArgument("'kind' is required: flat_pattern or auxiliary."),
            _ => throw SwException.InvalidArgument($"Unknown kind '{q.Kind}'.", "Use flat_pattern (a sheet-metal blank) or auxiliary (looking square onto an angled face)."),
        };
        (double X, double Y)? at = q.AtMm is null ? null : Sheet2(q.AtMm, "atMm");
        double? scale = null;
        if (kind == "flat_pattern")
        {
            if (string.IsNullOrWhiteSpace(q.Model)) throw SwException.InvalidArgument("kind 'flat_pattern' needs 'model': the saved sheet-metal part.");
            if (q.Parent is not null || q.Edge is not null) throw SwException.InvalidArgument("'parent' and 'edge' only apply to kind 'auxiliary'.");
            if (q.Scale is not null)
            {
                var (n, dd) = DrawingLayout.ParseScale(q.Scale, "scale");
                scale = n / dd;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(q.Parent)) throw SwException.InvalidArgument("kind 'auxiliary' needs 'parent': the view to project from (drw_info).");
            if (q.Edge is null) throw SwException.InvalidArgument("kind 'auxiliary' needs 'edge': the straight edge to look square at, e.g. {type:'edge', at:[x,y,z]}.");
            if (q.Model is not null || q.Scale is not null) throw SwException.InvalidArgument("'model' and 'scale' only apply to kind 'flat_pattern'.");
        }
        string? label = q.Label?.Trim();
        if (label is { Length: 0 or > 3 }) throw SwException.InvalidArgument("'label' is 1 to 3 characters, e.g. \"B\".");
        return session.RunAsync("drw_view_add", ctx => kind == "flat_pattern" ? FlatPatternView(ctx, q, at, scale) : AuxiliaryView(ctx, q, at, label),
            RunOptions.Write(q.Doc) with { Timeout = Longer(session, 120) }, ct);
    }

    private static DrwViewResult FlatPatternView(SwContext ctx, DrwViewAddRequest q, (double X, double Y)? at, double? scale)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var sheet = drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.");
        var (path, modelDoc) = ResolveModel(ctx, q.Model!);
        if (!path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase))
            throw SwException.InvalidArgument($"'{Path.GetFileName(path)}' is not a part; flat-pattern views show sheet-metal parts.");
        if (modelDoc is not null && !Tree.Walk(modelDoc).Any(f => f.GetTypeName2() == "FlatPattern"))
            throw SwException.InvalidArgument($"'{Path.GetFileName(path)}' has no flat pattern (it is not sheet metal).", "Make it with sheetmetal_base_flange.");
        var sp = sheet.DrwProps();
        bool hasFormat = sp.Length >= 2 && (int)Math.Round(sp[1]) != TemplateNone && !string.IsNullOrEmpty(sheet.GetTemplateName());
        var usable = sp.Length >= 7 ? DrawingLayout.Usable(sp[5] * 1000, sp[6] * 1000, hasFormat) : SheetArea(sheet);
        var (x0, y0) = at ?? (usable.Cx, usable.Cy);
        d.ClearSelection2(true);
        var v = (object?)drw.CreateFlatPatternViewFromModelView3(path, "", Units.M(x0), Units.M(y0), 0, !q.BendLines, false) as IView
                ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no flat-pattern view of '{Path.GetFileName(path)}'.",
                    "The part must be sheet metal (sheetmetal_base_flange) and saved; fold it first if it is flattened.");
        var checks = new List<string>();
        try
        {
            if (scale is double s) SetViewScale(v, s, Units.M(x0), Units.M(y0));
            if (at is null)
            {
                // Move it to free space: its size is known only now.
                var o = SheetRect.FromMeters(v.DrwOutline());
                if (o is SheetRect r)
                {
                    var others = Occupied(sheet).Where(x => !(Math.Abs(x.Cx - r.Cx) < 1e-6 && Math.Abs(x.Cy - r.Cy) < 1e-6)).ToList();
                    var (fx, fy, free) = DrawingLayout.FindSpot(usable, others, r.W, r.H, [(usable.Cx, usable.Cy)]);
                    if (!free) ctx.Warn($"No free space of {Math.Round(r.W)} x {Math.Round(r.H)} mm on the sheet; the flat pattern may overlap other views.");
                    var pos = v.DrwPosition();
                    if (pos.Length >= 2) v.DrwMoveTo(pos[0] + Units.M(fx - r.Cx), pos[1] + Units.M(fy - r.Cy));
                }
            }
            d.GraphicsRedraw2();
            if (!v.IsFlatPatternView()) throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{v.GetName2()}' is not a flat-pattern view.");
            if (!ShowsModel(v, path, out string shown)) throw new SwException(ErrorCodes.VerifyFailed, $"The new view shows '{shown}', not '{path}'.");
            checks.Add("a flat-pattern view of the part");
            int bendLines = Safe(() => v.GetBendLineCount(), -1);
            if (q.BendLines && bendLines >= 0) checks.Add($"{bendLines} bend line(s) shown");
            CloseOwnPropertyManager(ctx, checks);
            var info = ViewInfo(ctx, v, false);
            var blank = VisibleExtentMm(v);
            if (blank is null) ctx.Warn("The blank's size could not be read from the view's edges.");
            else checks.Add($"blank {blank[0]} x {blank[1]} mm (its visible edges, at {info.Scale})");
            OverlapCheck(ctx, sheet, [v], checks);
            return new DrwViewResult { Doc = ctx.Title(d), View = info, FlatSizeMm = blank, Checks = checks };
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
        {
            throw Rolled(ex, DeleteViews(d, [SafeName(v)]));
        }
    }

    private static DrwViewResult AuxiliaryView(SwContext ctx, DrwViewAddRequest q, (double X, double Y)? at, string? label)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.Parent, "parent");
        var sheet = SheetOf(drw, hit);
        var model = ModelOf(hit);
        var xf = hit.View.DrwModelToSheet();
        if (xf.Length < 13) throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform.");
        var t = new Xform(xf);
        bool part = model.GetType() == (int)swDocumentTypes_e.swDocPART;
        var r = EntityResolver.Resolve(ctx, model, q.Edge, "edge", EntityKind.Edge);
        var g = Seen(r, t, part, "edge");
        if (g.Kind != "line") throw SwException.InvalidArgument($"'edge' ({r.Label}) must be a straight edge seen as a line in '{hit.Name}' (it is a {g.Kind}).");
        // The new view looks along the sheet direction square to the edge, so it goes beside the parent that way.
        var (ex, ey) = g.Dir2;
        double len = Math.Sqrt(ex * ex + ey * ey);
        (double nx, double ny) = (-ey / len, ex / len);
        var outline = Outline(hit);
        double reach = Math.Max(outline.W, outline.H) / 2 + 30;
        var (x, y) = at ?? (outline.Cx + nx * reach, outline.Cy + ny * reach);
        var checks = new List<string>();
        Activate(drw, hit);
        d.ClearSelection2(true);
        SelectInView(ctx, d, hit, r, g, t, part, append: false, "edge", checks);
        var nv = (object?)drw.CreateAuxiliaryViewAt2(Units.M(x), Units.M(y), 0, false, label ?? DrawingLayout.NextLabel(AllViews(drw).Select(h => h.Name)), true, false) as IView;
        CloseOwnPropertyManager(ctx, checks);
        d.ClearSelection2(true);
        if (nv is null)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no auxiliary view from {r.Label} in '{hit.Name}'.",
                "Pick a straight edge that is not parallel to the sheet's x or y axis in that view (an angled face's edge).");
        try
        {
            var info = ViewInfo(ctx, nv, false);
            if (nv.Type != (int)swDrawingViewTypes_e.swDrawingAuxiliaryView)
                throw new SwException(ErrorCodes.VerifyFailed, $"The new view '{info.Name}' is a {info.Type} view, not an auxiliary one.");
            // It looks square to the edge: its viewing direction is perpendicular to the edge's model direction and to the parent's.
            var edgeDir = (Topology.Describe((IEdge)r.Obj).Direction ?? Vec3.UnitX).Normalized();
            var nx2 = nv.DrwModelToSheet();
            if (nx2.Length >= 13)
            {
                var toward = DrawingLayout.AxesOf(nx2).Toward;
                var parentToward = DrawingLayout.AxesOf(xf).Toward;
                if (part && Math.Abs(toward.Dot(edgeDir)) > 1e-3)
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{info.Name}' looks along [{Dir(toward)}], not square to {r.Label} [{Dir(edgeDir)}].");
                if (Math.Abs(toward.Dot(parentToward)) > 1e-3)
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{info.Name}' is not perpendicular to '{hit.Name}'.");
                checks.Add($"looks along [{Dir(toward)}]: square to {r.Label} and to '{hit.Name}'");
            }
            OverlapCheck(ctx, sheet, [nv], checks);
            return new DrwViewResult { Doc = ctx.Title(d), View = info with { Parent = hit.Name }, Label = label, Checks = checks };
        }
        catch (Exception e) when (!ComArgs.IsDisconnect(e))
        {
            throw Rolled(e, DeleteViews(d, [SafeName(nv)]));
        }
    }

    /// <summary>
    /// The model-space size (mm, largest first) of what a view draws: its visible edges' ends and midpoints mapped to the
    /// sheet, divided by the view's scale. Null when the view gives no edges.
    /// </summary>
    private static double[]? VisibleExtentMm(IView v)
    {
        var xf = v.DrwModelToSheet();
        double scale = v.ScaleDecimal;
        if (xf.Length < 13 || scale <= 0) return null;
        var t = new Xform(xf);
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        int n = 0;
        foreach (var e in ComArgs.Objects<IEdge>((object?)v.GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Edge)))
        {
            var g = Topology.Describe(e);
            foreach (var p in new[] { g.Start, g.End, g.Mid })
            {
                var s = t.ApplyPoint(p);
                x0 = Math.Min(x0, s.X); y0 = Math.Min(y0, s.Y); x1 = Math.Max(x1, s.X); y1 = Math.Max(y1, s.Y);
            }
            n++;
        }
        if (n == 0) return null;
        return new[] { Math.Round((x1 - x0) * 1000 / scale, 3), Math.Round((y1 - y0) * 1000 / scale, 3) }.OrderByDescending(x => x).ToArray();
    }

    /// <summary>
    /// Presses OK on a PropertyManager that the call itself opened (some view calls leave the new view's page open). The
    /// call started without one (writes check that first), so any page now is ours.
    /// </summary>
    private static void CloseOwnPropertyManager(SwContext ctx, List<string> checks)
    {
        ctx.App.GetRunningCommandInfo(out _, out string title, out _);
        if (string.IsNullOrWhiteSpace(title)) return;
        ctx.App.RunCommand(-2 /* swCommands_e.swCommands_PmOK */, "");
        ctx.App.GetRunningCommandInfo(out _, out string still, out _);
        if (!string.IsNullOrWhiteSpace(still)) ctx.Warn($"The '{still}' page SOLIDWORKS opened for the new view is still open.");
        else checks.Add($"closed the '{title}' page SOLIDWORKS opened");
    }

    // ================================================================== drw_view_edit

    public Task<DrwViewEditResult> ViewEditAsync(DrwViewEditRequest q, CancellationToken ct = default)
    {
        string kind = DrawingLayout.Norm(q.Kind) switch
        {
            "break" or "broken" => "break",
            "broken_out" or "breakout" or "break_out" => "broken_out",
            null => throw SwException.InvalidArgument("'kind' is required: break or broken_out."),
            _ => throw SwException.InvalidArgument($"Unknown kind '{q.Kind}'.", "Use break (shorten a long view) or broken_out (cut away to show inside)."),
        };
        if (string.IsNullOrWhiteSpace(q.View)) throw SwException.InvalidArgument("'view' is required (drw_info lists the views).");
        if (kind == "break")
        {
            if (q.CenterMm is not null || q.RadiusMm is not null || q.DepthMm is not null)
                throw SwException.InvalidArgument("'centerMm', 'radiusMm' and 'depthMm' only apply to kind 'broken_out'.");
            string lines = DrawingLayout.Norm(q.Lines) ?? throw SwException.InvalidArgument("kind 'break' needs 'lines': vertical (positions along sheet x) or horizontal (along y).");
            if (lines is not ("vertical" or "horizontal")) throw SwException.InvalidArgument($"Unknown lines '{q.Lines}'.", "Use vertical or horizontal.");
            if (q.FromMm is not double a || q.ToMm is not double b || !double.IsFinite(a) || !double.IsFinite(b))
                throw SwException.InvalidArgument("kind 'break' needs 'fromMm' and 'toMm': the two break-line positions in sheet mm.");
            if (Math.Abs(b - a) < 1) throw SwException.InvalidArgument("The break lines must be at least 1 mm apart.");
            _ = BreakStyle(q.Style);
        }
        else
        {
            if (q.Lines is not null || q.FromMm is not null || q.ToMm is not null || q.Style is not null)
                throw SwException.InvalidArgument("'lines', 'fromMm', 'toMm' and 'style' only apply to kind 'break'.");
            Sheet2(q.CenterMm, "centerMm");
            if (q.RadiusMm is not double rr || !double.IsFinite(rr) || rr <= 0) throw SwException.InvalidArgument("kind 'broken_out' needs a positive 'radiusMm'.");
            if (q.DepthMm is not double dp || !double.IsFinite(dp) || dp <= 0) throw SwException.InvalidArgument("kind 'broken_out' needs a positive 'depthMm' (model mm).");
        }
        return session.RunAsync("drw_view_edit", ctx => kind == "break" ? BreakView(ctx, q) : BrokenOut(ctx, q),
            RunOptions.Write(q.Doc) with { Timeout = Longer(session, 90) }, ct);
    }

    private static int BreakStyle(string? style) => DrawingLayout.Norm(style) switch
    {
        null or "zigzag" or "zig_zag" => (int)swBreakLineStyle_e.swBreakLine_ZigZag,
        "straight" => (int)swBreakLineStyle_e.swBreakLine_Straight,
        "small_zigzag" => (int)swBreakLineStyle_e.swBreakLine_SmallZigZag,
        "curve" or "curved" => (int)swBreakLineStyle_e.swBreakLine_Curve,
        "jagged" => (int)swBreakLineStyle_e.swBreakLine_Jagged,
        _ => throw SwException.InvalidArgument($"Unknown style '{style}'.", "Use zigzag (default), straight, small_zigzag, curve or jagged."),
    };

    /// <summary>
    /// IView.InsertBreak3 on the selected view with the two positions relative to the view's position (the API: "relative
    /// to the drawing view origin"). Verified: the view now has break lines and its outline got shorter along the axis.
    /// </summary>
    private static DrwViewEditResult BreakView(SwContext ctx, DrwViewEditRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        bool vertical = DrawingLayout.Norm(q.Lines) == "vertical";
        double lo = Math.Min(q.FromMm!.Value, q.ToMm!.Value), hi = Math.Max(q.FromMm.Value, q.ToMm.Value);
        var before = Outline(hit);
        double low = vertical ? before.X0 : before.Y0, high = vertical ? before.X1 : before.Y1;
        if (lo <= low || hi >= high)
            throw SwException.InvalidArgument($"The break lines ({lo} to {hi} mm) must lie inside the view, which spans {Math.Round(low, 2)} to {Math.Round(high, 2)} mm along sheet {(vertical ? "x" : "y")}.",
                "drw_info gives the view's boundsMm.");
        int linesBefore = Safe(() => hit.View.GetBreakLineCount2(out _), 0);
        var pos = hit.View.DrwPosition();
        double origin = pos.Length >= 2 ? (vertical ? pos[0] : pos[1]) : 0;
        var checks = new List<string>();
        d.ClearSelection2(true);
        SelectView(d, hit.Name);
        var line = (object?)hit.View.InsertBreak3(vertical ? (int)swBreakLineOrientation_e.swBreakLineVertical : (int)swBreakLineOrientation_e.swBreakLineHorizontal,
            Units.M(lo) - origin, Units.M(hi) - origin, BreakStyle(q.Style), 3, true) as IBreakLine;
        if (line is null)
        {
            d.ClearSelection2(true);
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS put no break into '{hit.Name}'.", "Break model views of long parts; pick positions across the part.");
        }
        // VERIFIED 2026-10-31: InsertBreak3 only draws the break lines; BreakView on the selected view applies them.
        d.ClearSelection2(true);
        SelectView(d, hit.Name);
        drw.BreakView();
        d.ClearSelection2(true);
        d.EditRebuild3();
        // GetOutline keeps the unbroken outline (VERIFIED), so the check reads the break itself.
        int linesAfter = Safe(() => hit.View.GetBreakLineCount2(out _), 0);
        double p0 = Safe(() => line.GetPosition(0), double.NaN), p1 = Safe(() => line.GetPosition(1), double.NaN);
        double gap = Safe(() => hit.View.BreakLineGap, 0) * 1000;
        bool placed = Math.Abs(Math.Min(p0, p1) - (Units.M(lo) - origin)) <= 1e-6 && Math.Abs(Math.Max(p0, p1) - (Units.M(hi) - origin)) <= 1e-6;
        if (!hit.View.IsBroken() || !placed)
        {
            string why = !hit.View.IsBroken() ? "the view is not broken" : $"the break lines read {Math.Round((p0 + origin) * 1000, 3)} and {Math.Round((p1 + origin) * 1000, 3)} mm";
            string undo = "";
            try
            {
                d.ClearSelection2(true);
                if (line.Select(false, null) && d.Extension.DeleteSelection2(0)) undo = " The break lines were removed.";
                d.ClearSelection2(true);
            }
            catch (COMException e) when (!ComArgs.IsDisconnect(e))
            {
            }
            throw new SwException(ErrorCodes.VerifyFailed, $"drw_view_edit break: {why}.{undo}", "Break model views of long parts, with positions across the part.");
        }
        checks.Add($"'{hit.Name}' is broken between {lo} and {hi} mm along sheet {(vertical ? "x" : "y")} (break lines read back; {linesAfter} in the view, had {linesBefore})");
        checks.Add($"gap {Math.Round(gap, 3)} mm: the view is drawn {Math.Round(hi - lo - gap, 3)} mm shorter");
        d.GraphicsRedraw2();
        return new DrwViewEditResult { Doc = ctx.Title(d), Kind = "break", View = ViewInfo(ctx, hit.View, false), BoundsBeforeMm = before.ToArray(), Checks = checks };
    }

    /// <summary>
    /// A closed circle sketched in the active view, selected, then IDrawingDoc.CreateBreakOutSection(depth) (SOLIDWORKS'
    /// example). Verified: the view's broken-out section count went up by one.
    /// </summary>
    private static DrwViewEditResult BrokenOut(SwContext ctx, DrwViewEditRequest q)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        var (cx, cy) = Sheet2(q.CenterMm, "centerMm");
        var before = Outline(hit);
        if (!before.Contains(cx, cy)) throw SwException.InvalidArgument($"centerMm [{cx}, {cy}] is outside view '{hit.Name}' [{Fmt(before.ToArray())}] mm.");
        int count = Safe(() => hit.View.GetBreakOutSectionCount(), 0);
        var checks = new List<string>();
        Activate(drw, hit);
        d.ClearSelection2(true);
        // In an active view, sketch coordinates are the sheet's (m).
        var seg = d.SketchManager.CreateCircleByRadius(Units.M(cx), Units.M(cy), 0, Units.M(q.RadiusMm!.Value));
        if (seg is null) throw new SwException(ErrorCodes.FeatureFailed, $"The cut's circle could not be sketched in '{hit.Name}'.");
        d.ClearSelection2(true);
        seg.Select4(false, null);
        bool ok = drw.CreateBreakOutSection(Units.M(q.DepthMm!.Value));
        d.ClearSelection2(true);
        int now = Safe(() => hit.View.GetBreakOutSectionCount(), 0);
        if (!ok || now != count + 1)
        {
            DeleteSegment(d, seg);
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no broken-out section in '{hit.Name}' (sections {count} → {now}).",
                "Put the circle over the part, and give a depth that reaches into it (model mm from the front).");
        }
        checks.Add($"'{hit.Name}' has {now} broken-out section(s), cut {q.DepthMm} mm deep");
        d.GraphicsRedraw2();
        return new DrwViewEditResult { Doc = ctx.Title(d), Kind = "broken_out", View = ViewInfo(ctx, hit.View, false), BoundsBeforeMm = before.ToArray(), Checks = checks };
    }
}
