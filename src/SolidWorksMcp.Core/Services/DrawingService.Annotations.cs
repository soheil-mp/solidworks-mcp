using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of drw_annotation_add: a datum, surface finish symbol, geometric tolerance or balloon in a view.</summary>
public sealed record DrwAnnotationRequest
{
    public string? Doc { get; init; }
    /// <summary>datum | surface_finish | gtol | balloon.</summary>
    public string? Kind { get; init; }
    public string? View { get; init; }
    /// <summary>The edge or vertex of the view's model it is attached to (model mm).</summary>
    public EntityRef? At { get; init; }
    /// <summary>Where the symbol or text goes, [x, y] sheet mm; default: beside the attachment, outside the view.</summary>
    public double[]? AtMm { get; init; }
    public string? Label { get; init; }
    public string? Roughness { get; init; }
    public string? Finish { get; init; }
    public string? Characteristic { get; init; }
    public string? Tolerance { get; init; }
    public bool Diameter { get; init; }
    public string? Material { get; init; }
    public IReadOnlyList<string>? Datums { get; init; }
}

public sealed class DrwAnnotationResult : ToolResult
{
    public required string View { get; init; }
    public required string Kind { get; init; }
    /// <summary>The annotation's name in SOLIDWORKS (e.g. "DatumTag1").</summary>
    public required string Annotation { get; init; }
    /// <summary>What it reads back: the datum letter, "Ra 1.6", the frame, the balloon's item number.</summary>
    public required string Text { get; init; }
    public double[]? AtMm { get; init; }
    /// <summary>How many model entities it is attached to (0: free-standing).</summary>
    public int AttachedTo { get; init; }
    /// <summary>How many annotations were added (several for the balloons of a whole view).</summary>
    public int Count { get; init; } = 1;
    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_annotation_add

    private sealed record AnnotationPlan(string Kind, string? Label, string? Roughness, (int Code, string Name) Finish,
        DrwAnnotationSpec.Characteristic? Gtol, string? Tolerance, string Material, IReadOnlyList<string> Datums);

    public Task<DrwAnnotationResult> AnnotationAddAsync(DrwAnnotationRequest q, CancellationToken ct = default)
    {
        string kind = DrwAnnotationSpec.Kind(q.Kind);
        void Only(string what, bool given, params string[] kinds)
        {
            if (given && !kinds.Contains(kind)) throw SwException.InvalidArgument($"'{what}' does not apply to kind '{kind}' (only {string.Join(", ", kinds)}).");
        }
        Only("label", q.Label is not null, "datum");
        Only("roughness", q.Roughness is not null, "surface_finish");
        Only("finish", q.Finish is not null, "surface_finish");
        Only("characteristic", q.Characteristic is not null, "gtol");
        Only("tolerance", q.Tolerance is not null, "gtol");
        Only("diameter", q.Diameter, "gtol");
        Only("material", q.Material is not null, "gtol");
        Only("datums", q.Datums is { Count: > 0 }, "gtol");
        if (kind is "datum" or "surface_finish" && q.At is null)
            throw SwException.InvalidArgument($"kind '{kind}' needs 'at': the edge it points to, e.g. {{type:'edge', at:[x,y,z]}} (model mm).");
        if (kind == "balloon" && q.At is null && q.AtMm is not null)
            throw SwException.InvalidArgument("Balloons for the whole view (no 'at') are laid out by SOLIDWORKS; 'atMm' needs 'at'.");
        if (q.AtMm is not null) Sheet2(q.AtMm, "atMm");

        AnnotationPlan plan;
        switch (kind)
        {
            case "datum":
                plan = new(kind, DrwAnnotationSpec.Label(q.Label ?? "A", "label"), null, default, null, null, "", []);
                break;
            case "surface_finish":
                plan = new(kind, null, q.Roughness is null ? null : DrwAnnotationSpec.Tolerance(q.Roughness, "roughness"),
                    DrwAnnotationSpec.Finish(q.Finish), null, null, "", []);
                break;
            case "gtol":
            {
                var c = DrwAnnotationSpec.Gtol(q.Characteristic);
                var datums = (q.Datums ?? []).Select((x, i) => DrwAnnotationSpec.Label(x, $"datums[{i}]")).ToList();
                if (datums.Count > 3) throw SwException.InvalidArgument($"At most 3 datums (got {datums.Count}).");
                if (c.NeedsDatum && datums.Count == 0)
                    throw SwException.InvalidArgument($"{c.Name} is measured against a datum: pass datums, e.g. [\"A\"].", "Add the datum with kind 'datum' first.");
                if (!c.TakesDatum && datums.Count > 0) throw SwException.InvalidArgument($"{c.Name} is a form tolerance and takes no datums.");
                plan = new(kind, null, null, default, c, DrwAnnotationSpec.Tolerance(q.Tolerance, "tolerance"), DrwAnnotationSpec.Material(q.Material), datums);
                break;
            }
            default:
                plan = new(kind, null, null, default, null, null, "", []);
                break;
        }
        return session.RunAsync("drw_annotation_add", ctx => AnnotationAdd(ctx, q, plan), RunOptions.Write(q.Doc), ct);
    }

    private static DrwAnnotationResult AnnotationAdd(SwContext ctx, DrwAnnotationRequest q, AnnotationPlan plan)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        var model = ModelOf(hit);
        var xf = hit.View.DrwModelToSheet();
        if (xf.Length < 13)
            throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform, so model references can't be placed in it.");
        var t = new Xform(xf);
        bool part = model.GetType() == (int)swDocumentTypes_e.swDocPART;
        Resolved? r = null;
        OnSheet? g = null;
        if (q.At is not null)
        {
            r = EntityResolver.Resolve(ctx, model, q.At, "at", EntityKind.Edge, EntityKind.Vertex);
            g = Seen(r, t, part, "at");
        }
        if (plan.Kind == "balloon" && r is null) return AutoBalloons(ctx, d, drw, hit);
        var outline = Outline(hit);
        var taken = hit.View.DrwAnnotations().Select(a => a.DrwPosition()).Where(p => p.Length >= 2).Select(p => (p[0] * 1000, p[1] * 1000)).ToList();
        var spot = q.AtMm is not null ? Sheet2(q.AtMm, "atMm") : DrawingLayout.AnnotationSpot(outline, g is null ? null : (g.Pick.X * 1000, g.Pick.Y * 1000), taken);

        var checks = new List<string>();
        Activate(drw, hit);
        d.ClearSelection2(true);
        if (r is not null) SelectInView(ctx, d, hit, r, g!, t, part, append: false, "at", checks, pickFirst: true);

        IAnnotation? an = null;
        string text;
        try
        {
            switch (plan.Kind)
            {
                case "datum":
                {
                    var tag = (object?)d.InsertDatumTag2() as IDatumTag
                              ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no datum tag on {r!.Label} in '{hit.Name}'.",
                                  "Attach it to a straight edge seen in the view (the edge of a planar face).");
                    an = (object?)tag.GetAnnotation() as IAnnotation;
                    if (!tag.SetLabel(plan.Label!)) ctx.Warn($"SetLabel('{plan.Label}') returned false.");
                    text = tag.GetLabel() ?? "";
                    if (!string.Equals(text, plan.Label, StringComparison.Ordinal))
                        throw new SwException(ErrorCodes.VerifyFailed, $"The datum tag reads '{text}', not '{plan.Label}'.");
                    checks.Add($"label '{text}' read back");
                    break;
                }
                case "surface_finish":
                {
                    int leader = r is null ? (int)swLeaderStyle_e.swNO_LEADER : (int)swLeaderStyle_e.swSTRAIGHT;
                    var sf = d.Extension.InsertSurfaceFinishSymbol3(plan.Finish.Code, leader, Units.M(spot.X), Units.M(spot.Y), 0,
                                 (int)swSFLaySym_e.swSFNone, (int)swArrowStyle_e.swDOT_ARROWHEAD, "", "", "", "", plan.Roughness ?? "", "", "")
                             ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no surface finish symbol in '{hit.Name}'.",
                                 "Attach it to an edge seen in the view.");
                    an = (object?)sf.GetAnnotation() as IAnnotation;
                    int type = sf.GetSymbol();
                    string rough = sf.GetText((int)swSurfaceFinishSymbolText_e.swSFSymbolMaximumRoughness) ?? "";
                    if (type != plan.Finish.Code)
                        throw new SwException(ErrorCodes.VerifyFailed, $"The symbol is of type {type}, not {plan.Finish.Name} ({plan.Finish.Code}).");
                    if (plan.Roughness is not null && rough.Trim() != plan.Roughness)
                        throw new SwException(ErrorCodes.VerifyFailed, $"The symbol's roughness reads '{rough}', not '{plan.Roughness}'.");
                    text = plan.Roughness is null ? plan.Finish.Name : $"{plan.Finish.Name} Ra {plan.Roughness}";
                    checks.Add($"symbol {plan.Finish.Name}{(plan.Roughness is null ? "" : $", roughness {rough}")} read back");
                    break;
                }
                case "gtol":
                {
                    var gt = (object?)d.InsertGtol() as IGtol
                             ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no geometric tolerance in '{hit.Name}'.");
                    an = (object?)gt.GetAnnotation() as IAnnotation;
                    var c = plan.Gtol!;
                    gt.SetFrameSymbols2(1, DrwAnnotationSpec.Symbol(c), q.Diameter, plan.Material, false, "", "", "", "");
                    string D(int i) => i < plan.Datums.Count ? plan.Datums[i] : "";
                    if (!gt.SetFrameValues2(1, plan.Tolerance!, "", D(0), D(1), D(2)))
                        ctx.Warn("SetFrameValues2 returned false.");
                    var values = ComArgs.Strings((object?)gt.GetFrameValues(1));
                    var symbols = ComArgs.Strings((object?)gt.GetFrameSymbols3(1));
                    string gotTol = values.Length > 0 ? values[0] : "";
                    var gotDatums = values.Skip(2).Take(3).Where(x => !string.IsNullOrEmpty(x)).ToList();
                    if (symbols.Length == 0 || !symbols[0].Contains(c.Code, StringComparison.OrdinalIgnoreCase))
                        throw new SwException(ErrorCodes.VerifyFailed, $"The frame's symbol reads '{(symbols.Length > 0 ? symbols[0] : "")}', not {DrwAnnotationSpec.Symbol(c)}.");
                    if (gotTol.Trim() != plan.Tolerance)
                        throw new SwException(ErrorCodes.VerifyFailed, $"The frame's tolerance reads '{gotTol}', not '{plan.Tolerance}'.");
                    if (!gotDatums.SequenceEqual(plan.Datums))
                        throw new SwException(ErrorCodes.VerifyFailed, $"The frame's datums read [{string.Join(", ", gotDatums)}], not [{string.Join(", ", plan.Datums)}].");
                    text = DrwAnnotationSpec.FrameText(c, plan.Tolerance!, q.Diameter, plan.Material, plan.Datums);
                    checks.Add($"frame read back: symbol {symbols[0]}, tolerance {gotTol}{(gotDatums.Count > 0 ? $", datums {string.Join(" ", gotDatums)}" : "")}");
                    break;
                }
                default:
                {
                    var opt = d.Extension.CreateBalloonOptions();
                    opt.Style = (int)swBalloonStyle_e.swBS_Circular;
                    opt.Size = (int)swBalloonFit_e.swBF_2Chars;
                    opt.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
                    var note = d.Extension.InsertBOMBalloon2(opt)
                               ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no balloon on {r!.Label} in '{hit.Name}'.",
                                   "Attach it to an edge of a component seen in the view.");
                    an = note.DrwAnnotation();
                    text = (note.GetBomBalloonText(true) is { Length: > 0 } up ? up : note.GetText()) ?? "";
                    if (!note.IsBomBalloon()) ctx.Warn("The balloon is not linked to a BOM item (no BOM in the drawing?).");
                    if (text.Length == 0) throw new SwException(ErrorCodes.VerifyFailed, "The balloon has no text.");
                    checks.Add($"balloon text '{text}' read back{(note.IsBomBalloon() ? " (a BOM balloon)" : "")}");
                    break;
                }
            }
            if (an is null) throw new SwException(ErrorCodes.Internal, "The new annotation has no IAnnotation.");
        }
        catch (Exception ex) when (ex is SwException or COMException && !ComArgs.IsDisconnect(ex))
        {
            d.ClearSelection2(true);
            string tail = an is not null && DeleteAnnotation(d, an) ? " It was removed." : an is not null ? " It could not be removed." : "";
            if (ex is SwException se) throw new SwException(se.Code, se.Message + tail, se.Hint, se);
            throw new SwException(ErrorCodes.FeatureFailed, $"drw_annotation_add: {ex.Message}{tail}", null, ex);
        }
        d.ClearSelection2(true);

        if (plan.Kind != "surface_finish") an.SetPosition2(Units.M(spot.X), Units.M(spot.Y), 0);
        var pos = an.DrwPosition();
        int attached = Safe(() => an.GetAttachedEntityCount3(), 0);
        if (r is not null)
        {
            if (attached < 1)
            {
                bool gone = DeleteAnnotation(d, an);
                throw new SwException(ErrorCodes.VerifyFailed, $"The {plan.Kind.Replace('_', ' ')} was created but is not attached to {r.Label}; it was {(gone ? "" : "not ")}removed.",
                    "Pick an edge that is visible in the view.");
            }
            checks.Add($"attached to {attached} entity(ies)");
        }
        if (pos.Length >= 2 && Math.Abs(pos[0] - Units.M(spot.X)) <= 1e-4 && Math.Abs(pos[1] - Units.M(spot.Y)) <= 1e-4)
            checks.Add("position read back");
        d.GraphicsRedraw2();
        return new DrwAnnotationResult
        {
            Doc = ctx.Title(d),
            View = hit.Name,
            Kind = plan.Kind,
            Annotation = Safe(() => an.GetName(), "") ?? "",
            Text = text,
            AtMm = pos.Length >= 2 ? [Math.Round(pos[0] * 1000, 3), Math.Round(pos[1] * 1000, 3)] : null,
            AttachedTo = attached,
            Checks = checks,
            Resolved = r is null ? null : [r.Info("at")],
        };
    }

    /// <summary>
    /// Balloons for every component in the view (IDrawingDoc.AutoBalloon5 on the selected view, one per part, square
    /// layout). Verified: new BOM balloons with distinct item numbers.
    /// </summary>
    private static DrwAnnotationResult AutoBalloons(SwContext ctx, IModelDoc2 d, IDrawingDoc drw, ViewHit hit)
    {
        int before = hit.View.DrwNotes().Count(n => Safe(() => n.IsBomBalloon(), false));
        var opt = drw.CreateAutoBalloonOptions();
        opt.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square;
        opt.IgnoreMultiple = true;
        opt.InsertMagneticLine = false;
        opt.Style = (int)swBalloonStyle_e.swBS_Circular;
        opt.Size = (int)swBalloonFit_e.swBF_2Chars;
        opt.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
        Activate(drw, hit);
        d.ClearSelection2(true);
        SelectView(d, hit.Name);
        var notes = ComArgs.Objects<INote>((object?)drw.AutoBalloon5(opt));
        d.ClearSelection2(true);
        var balloons = hit.View.DrwNotes().Where(n => Safe(() => n.IsBomBalloon(), false)).ToList();
        int added = balloons.Count - before;
        if (notes.Count == 0 && added <= 0)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS added no balloons to '{hit.Name}'.",
                "Balloons need a view of an assembly (or a part) with a BOM in the drawing (drw_bom_add).");
        var made = notes.Count > 0 ? notes : balloons.Skip(before).ToList();
        var texts = made.Select(n => (n.GetBomBalloonText(true) is { Length: > 0 } up ? up : n.GetText()) ?? "").ToList();
        var checks = new List<string> { $"{made.Count} balloon(s) added, {balloons.Count} BOM balloon(s) in the view" };
        var dup = texts.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) ctx.Warn($"Item number(s) {string.Join(", ", dup)} appear on more than one balloon.");
        else checks.Add("item numbers distinct");
        return new DrwAnnotationResult
        {
            Doc = ctx.Title(d),
            View = hit.Name,
            Kind = "balloon",
            Annotation = string.Join(", ", made.Select(n => n.GetName() ?? "")),
            Text = string.Join(", ", texts),
            AttachedTo = made.Sum(n => Safe(() => n.DrwAnnotation()?.GetAttachedEntityCount3() ?? 0, 0)),
            Count = made.Count,
            Checks = checks,
        };
    }

    private static bool DeleteAnnotation(IModelDoc2 d, IAnnotation an)
    {
        try
        {
            d.ClearSelection2(true);
            bool ok = an.Select2(false, 0) && d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
            return ok;
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return false; }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return fallback; }
    }
}
