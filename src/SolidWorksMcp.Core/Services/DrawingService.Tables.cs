using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of drw_table_add: a hole table for a view, or a revision table with its rows.</summary>
public sealed record DrwTableRequest
{
    public string? Doc { get; init; }
    /// <summary>hole | revision.</summary>
    public string? Kind { get; init; }
    public string? View { get; init; }
    /// <summary>hole: the datum origin vertex (model mm); default: the face's lowest-left vertex in the view.</summary>
    public EntityRef? Origin { get; init; }
    /// <summary>hole: the planar face whose holes are listed; default: the face toward the viewer with the most holes.</summary>
    public EntityRef? Face { get; init; }
    /// <summary>The table's top-left (hole) or top-right (revision) corner, [x, y] sheet mm.</summary>
    public double[]? AtMm { get; init; }
    /// <summary>revision: one description per new revision (A, B, … follow the existing ones).</summary>
    public IReadOnlyList<string>? Revisions { get; init; }
    public string? Template { get; init; }
}

public sealed class DrwTableResult : ToolResult
{
    public required string Kind { get; init; }
    public string? View { get; init; }
    public required string Sheet { get; init; }
    /// <summary>Data rows (without the header).</summary>
    public int Rows { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Items { get; init; }
    public double[]? AtMm { get; init; }
    public string? Template { get; init; }
    /// <summary>revision: the latest revision letter.</summary>
    public string? CurrentRevision { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

public sealed partial class DrawingService
{
    // ================================================================== drw_table_add

    public Task<DrwTableResult> TableAddAsync(DrwTableRequest q, CancellationToken ct = default)
    {
        string kind = DrawingLayout.Norm(q.Kind) switch
        {
            "hole" or "hole_table" or "holes" => "hole",
            "revision" or "revisions" or "revision_table" => "revision",
            null => throw SwException.InvalidArgument("'kind' is required: hole or revision."),
            _ => throw SwException.InvalidArgument($"Unknown kind '{q.Kind}'.", "Use hole (a hole table for a view) or revision (a revision table)."),
        };
        (double X, double Y)? at = q.AtMm is null ? null : Sheet2(q.AtMm, "atMm");
        if (kind == "hole")
        {
            if (string.IsNullOrWhiteSpace(q.View)) throw SwException.InvalidArgument("kind 'hole' needs 'view': the view whose holes are listed (drw_info).");
            if (q.Revisions is not null) throw SwException.InvalidArgument("'revisions' only applies to kind 'revision'.");
        }
        else
        {
            if (q.Origin is not null || q.Face is not null) throw SwException.InvalidArgument("'origin' and 'face' only apply to kind 'hole'.");
            if (q.Revisions is not { Count: > 0 })
                throw SwException.InvalidArgument("kind 'revision' needs 'revisions': one description per new revision, e.g. [\"First release\"].");
            if (q.Revisions.Count > 25) throw SwException.InvalidArgument($"At most 25 revisions per call (got {q.Revisions.Count}).");
            foreach (var (r, i) in q.Revisions.Select((r, i) => (r, i)))
                if (string.IsNullOrWhiteSpace(r) || r.Length > 500) throw SwException.InvalidArgument($"revisions[{i}] must be a description of 1 to 500 characters.");
        }
        string? tpl = null;
        if (!string.IsNullOrWhiteSpace(q.Template))
        {
            string ext = kind == "hole" ? ".sldholtbt" : ".sldrevtbt";
            if (!q.Template.Trim().EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"'template' must be a {ext} {kind} table template (got '{q.Template}').");
            tpl = session.Paths.ForRead(q.Template, "template");
        }
        return session.RunAsync("drw_table_add", ctx => kind == "hole" ? HoleTable(ctx, q, tpl, at) : RevisionTable(ctx, q, tpl, at),
            RunOptions.Write(q.Doc) with { Timeout = Longer(session, 90) }, ct);
    }

    /// <summary>A hole the table should list: its centre (model m) and diameter (m).</summary>
    private sealed record HoleOnFace(Vec3 Center, double Diameter);

    private static DrwTableResult HoleTable(SwContext ctx, DrwTableRequest q, string? tpl, (double X, double Y)? at)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        var hit = FindView(ctx, drw, q.View, "view");
        var model = ModelOf(hit);
        if (model.GetType() != (int)swDocumentTypes_e.swDocPART)
            throw new SwException(ErrorCodes.NotSupported, "Hole tables are made in views of parts here; this view shows an assembly.",
                "Add a view of the part (drw_views_standard {model:'part.sldprt'}) and list its holes there.");
        var xf = hit.View.DrwModelToSheet();
        if (xf.Length < 13)
            throw new SwException(ErrorCodes.NotSupported, $"View '{hit.Name}' gives no model-to-sheet transform, so its holes can't be placed.");
        var t = new Xform(xf);
        double s = SheetScale(xf, hit.View.ScaleDecimal);
        var resolved = new List<ResolvedInfo>();

        // The face: given, or the face toward the viewer with the most holes.
        IFace2 face;
        if (q.Face is not null)
        {
            var rf = EntityResolver.Resolve(ctx, model, q.Face, "face", EntityKind.Face);
            face = (IFace2)rf.Obj;
            resolved.Add(rf.Info("face"));
        }
        else
        {
            face = ((IPartDoc)model).Bodies(swBodyType_e.swSolidBody, visibleOnly: true).SelectMany(b => b.Faces())
                       .Where(f => Topology.Describe(f) is { Surface: "plane", Normal: Vec3 n } && t.ApplyVector(n).Normalized().Z > 1 - 1e-6)
                       .Select(f => (F: f, N: HolesOn(f).Count)).Where(x => x.N > 0).OrderByDescending(x => x.N).Select(x => x.F).FirstOrDefault()
                   ?? throw SwException.InvalidArgument($"No face toward the viewer in '{hit.Name}' has holes.",
                       "Pass 'face' (a planar face with holes), or use a view that looks at the holes.");
        }
        var fg = Topology.Describe(face);
        if (fg is not { Surface: "plane", Normal: Vec3 fnorm })
            throw SwException.InvalidArgument("'face' must be planar (the face the holes go through).");
        if (Math.Abs(t.ApplyVector(fnorm).Normalized().Z) < 1 - 1e-6)
            throw SwException.InvalidArgument($"The face is not seen face-on in '{hit.Name}', so hole positions can't be read in it.");
        var holes = HolesOn(face);
        if (holes.Count == 0) throw SwException.InvalidArgument("The face has no holes (no circular edges of hole faces on it).");

        // The datum origin: given, or the face's vertex drawn lowest-left.
        Resolved ro;
        if (q.Origin is not null) ro = EntityResolver.Resolve(ctx, model, q.Origin, "origin", EntityKind.Vertex);
        else
        {
            var corner = face.Edges().SelectMany(e => new[] { e.StartVertex(), e.EndVertex() }).OfType<IVertex>()
                             .Select(v => Vec3.From(v.Point())).OrderBy(p => t.ApplyPoint(p).X + t.ApplyPoint(p).Y).Cast<Vec3?>().FirstOrDefault()
                         ?? throw SwException.InvalidArgument("The face has no vertex to use as the datum origin; pass 'origin'.");
            ro = EntityResolver.Resolve(ctx, model, new EntityRef { Type = "vertex", At = Units.Mm(corner) }, "origin", EntityKind.Vertex);
        }
        resolved.Insert(0, ro.Info("origin"));
        var go = Seen(ro, t, true, "origin");
        var expected = holes.Select(h =>
        {
            var c = t.ApplyPoint(h.Center);
            return (X: Units.Mm((c.X - go.P0.X) / s), Y: Units.Mm((c.Y - go.P0.Y) / s), Dia: Units.Mm(h.Diameter));
        }).ToList();

        var outline = Outline(hit);
        var (x, y) = at ?? (outline.X0, outline.Y0 - 10);
        string path = tpl ?? TableTemplate(ctx, swUserPreferenceStringValue_e.swFileLocationsHoleTableTemplates, "standard hole table--letters.sldholtbt");
        var checks = new List<string>();
        Activate(drw, hit);
        d.ClearSelection2(true);
        SelectInView(ctx, d, hit, ro, go, t, true, append: false, "origin", checks, pickFirst: true, mark: 1);
        var sm = d.Sel();
        var data = sm.CreateSelectData();
        data.View = (View)(object)hit.View;
        data.Mark = 2;
        int before = sm.GetSelectedObjectCount2(-1);
        if (!(((IEntity)face).Select4(true, data) && sm.GetSelectedObjectCount2(-1) == before + 1))
            throw new SwException(ErrorCodes.EntityNotFound, $"The face could not be selected in '{hit.Name}'.", "Pass a face that is visible in the view.");
        var ann = (object?)hit.View.InsertHoleTable3(false, Units.M(x), Units.M(y), (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft, "A",
            path, (int)swHoleTableTagOrder_e.swHoleTableTagOrder_XY, (int)swHoleTableTagStyle_e.swHoleTable_AlphaNumericTags, null) as IHoleTableAnnotation;
        d.ClearSelection2(true);
        if (ann is null)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no hole table in '{hit.Name}'.", "Check the template, the origin vertex and the face.");
        var table = (ITableAnnotation)(object)ann;
        var (columns, items) = ReadTable(table);

        // Tier B: one row per hole, at the hole's position from the origin and with its diameter.
        int cx = DrwAnnotationSpec.Column(columns, "X LOC", "XLOC", "X"), cy = DrwAnnotationSpec.Column(columns, "Y LOC", "YLOC", "Y"),
            cs = DrwAnnotationSpec.Column(columns, "SIZE", "DIA");
        string? problem = null;
        if (cx < 0 || cy < 0) problem = $"no X/Y location columns among [{string.Join(", ", columns)}]";
        else if (items.Count != expected.Count) problem = $"{items.Count} row(s) for {expected.Count} hole(s)";
        else
        {
            var left = items.ToList();
            foreach (var e in expected)
            {
                var row = left.FirstOrDefault(r => DrwAnnotationSpec.Number(r[cx]) is double rx && DrwAnnotationSpec.Number(r[cy]) is double ry &&
                                                   Math.Abs(rx - e.X) <= 0.006 && Math.Abs(ry - e.Y) <= 0.006);
                if (row is null) { problem = $"no row at X {e.X}, Y {e.Y} mm (a Ø{e.Dia} hole)"; break; }
                if (cs >= 0 && DrwAnnotationSpec.Number(row[cs]) is double dia && Math.Abs(dia - e.Dia) > 0.006)
                {
                    problem = $"the hole at X {e.X}, Y {e.Y} mm reads size '{row[cs]}', not Ø{e.Dia}";
                    break;
                }
                left.Remove(row);
            }
        }
        if (problem is not null)
        {
            bool gone = DeleteFeature(d, (object?)ann.HoleTable?.GetFeature() as IFeature);
            throw new SwException(ErrorCodes.VerifyFailed, $"The hole table doesn't match the holes: {problem}; it was {(gone ? "" : "not ")}removed.",
                "Pass the origin vertex and the face explicitly.");
        }
        checks.Add($"{items.Count} row(s), each hole's X/Y from the origin and size read back");
        return new DrwTableResult
        {
            Doc = ctx.Title(d),
            Kind = "hole",
            View = hit.Name,
            Sheet = SheetOf(drw, hit).GetName(),
            Rows = items.Count,
            Columns = columns,
            Items = items.Take(MaxBomRows).ToList(),
            AtMm = [Math.Round(x, 3), Math.Round(y, 3)],
            Template = path,
            Checks = checks,
            Resolved = resolved,
        };
    }

    /// <summary>The holes through a planar face: its full circular edges whose other face is a hole's cylinder.</summary>
    private static List<HoleOnFace> HolesOn(IFace2 face)
    {
        var list = new List<HoleOnFace>();
        foreach (var e in face.Edges())
        {
            var g = Topology.Describe(e);
            if (g is not { Curve: "circle", Closed: true, Center: Vec3 c, Radius: double r }) continue;
            var other = ComArgs.Objects<IFace2>((object?)e.GetTwoAdjacentFaces2()).FirstOrDefault(f => !ReferenceEquals(f, face) && !f.IsSame(face));
            if (other is not null && Topology.IsHole(other, Topology.Describe(other))) list.Add(new HoleOnFace(c, 2 * r));
        }
        return list;
    }

    private static DrwTableResult RevisionTable(SwContext ctx, DrwTableRequest q, string? tpl, (double X, double Y)? at)
    {
        var d = ctx.Doc;
        var drw = ctx.Drawing();
        ViewHit? hit = q.View is null ? null : FindView(ctx, drw, q.View, "view");
        var sheet = hit is null ? (drw.DrwCurrentSheet() ?? throw new SwException(ErrorCodes.Internal, "The drawing has no current sheet.")) : SheetOf(drw, hit);
        var checks = new List<string>();
        var rt = (object?)sheet.RevisionTable as IRevisionTableAnnotation;
        bool created = false;
        string? path = null;
        (double X, double Y)? placed = null;
        if (rt is null)
        {
            path = tpl ?? TableTemplate(ctx, swUserPreferenceStringValue_e.swFileLocationsRevisionTableTemplates, "standard revision block.sldrevtbt");
            var sp = sheet.DrwProps();
            placed = at ?? (sp.Length >= 7 ? (sp[5] * 1000 - 10, sp[6] * 1000 - 10) : (400, 280));
            d.ClearSelection2(true);
            rt = sheet.InsertRevisionTable2(false, Units.M(placed.Value.X), Units.M(placed.Value.Y), (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopRight,
                     path, (int)swRevisionTableSymbolShape_e.swRevisionTable_CircleSymbol, true)
                 ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no revision table on '{sheet.GetName()}'.", "Check the template.");
            created = true;
            checks.Add("revision table inserted");
        }
        else
        {
            if (at is not null) ctx.Warn("The sheet already has a revision table; its rows were added there and atMm was not used.");
            checks.Add("added to the sheet's revision table");
        }
        var table = (ITableAnnotation)(object)rt;
        var (titles, _) = ReadTable(table);
        int desc = DrwAnnotationSpec.Column(titles, "DESCRIPTION", "DESC");
        if (desc < 0)
        {
            string tail = created && DeleteFeature(d, (object?)rt.RevisionTableFeature?.GetFeature() as IFeature) ? " The new table was removed." : "";
            throw new SwException(ErrorCodes.NotSupported, $"The revision table has no DESCRIPTION column (columns: {string.Join(", ", titles)}).{tail}",
                "Use a revision template with a description column.");
        }
        int existing = Math.Max(0, table.RowCount - Math.Max(0, table.GetHeaderCount()));
        var added = new List<(int Id, string Letter, string Desc)>();
        string? problem = null;
        try
        {
            for (int i = 0; i < q.Revisions!.Count; i++)
            {
                string letter = DrwAnnotationSpec.RevisionLetter(existing + i);
                int id = rt.AddRevision(letter);
                if (id <= 0) { problem = $"AddRevision('{letter}') returned {id}"; break; }
                added.Add((id, letter, q.Revisions[i].Trim()));
                int row = rt.GetRowNumberForId(id);
                table.Text[row, desc] = q.Revisions[i].Trim();
            }
            if (problem is null)
                foreach (var (id, letter, text) in added)
                {
                    string got = rt.GetRevisionForId(id) ?? "";
                    string gotDesc = (table.DisplayedText[rt.GetRowNumberForId(id), desc] ?? "").Trim();
                    if (got != letter) { problem = $"revision {id} reads '{got}', not '{letter}'"; break; }
                    if (gotDesc != text) { problem = $"revision {letter}'s description reads '{gotDesc}'"; break; }
                }
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e))
        {
            problem = e.Message;
        }
        if (problem is not null)
        {
            foreach (var (id, _, _) in added) Safe(() => rt.DeleteRevision(id, true), false);
            string tail = created && DeleteFeature(d, (object?)rt.RevisionTableFeature?.GetFeature() as IFeature) ? " The new table was removed." : "";
            throw new SwException(ErrorCodes.VerifyFailed, $"The revisions were not added as asked: {problem}. The added rows were deleted.{tail}");
        }
        string current = rt.CurrentRevision ?? "";
        if (current != added[^1].Letter) ctx.Warn($"The current revision reads '{current}', not '{added[^1].Letter}'.");
        checks.Add($"revision(s) {string.Join(", ", added.Select(a => a.Letter))} read back with their descriptions; current revision {current}");
        var (columns, items) = ReadTable(table);
        return new DrwTableResult
        {
            Doc = ctx.Title(d),
            Kind = "revision",
            View = hit?.Name,
            Sheet = sheet.GetName(),
            Rows = items.Count,
            Columns = columns,
            Items = items.Take(MaxBomRows).ToList(),
            AtMm = placed is { } p ? [Math.Round(p.X, 3), Math.Round(p.Y, 3)] : null,
            Template = path,
            CurrentRevision = current,
            Checks = checks,
        };
    }

    /// <summary>A table's column titles and data rows (header rows left out, at the top or the bottom).</summary>
    private static (List<string> Columns, List<IReadOnlyList<string>> Rows) ReadTable(ITableAnnotation table)
    {
        int rows = table.RowCount, cols = table.ColumnCount, headers = Math.Max(0, table.GetHeaderCount());
        bool headerAtBottom = table.GetHeaderStyle() == (int)swTableHeaderPosition_e.swTableHeader_Bottom;
        int headerRow = headerAtBottom ? rows - 1 : 0;
        var columns = Enumerable.Range(0, cols)
            .Select(i => table.GetColumnTitle(i) is { Length: > 0 } title ? title : headers > 0 && rows > 0 ? table.DisplayedText[headerRow, i] ?? "" : "")
            .ToList();
        var dataRows = headerAtBottom ? Enumerable.Range(0, Math.Max(0, rows - headers)) : Enumerable.Range(headers, Math.Max(0, rows - headers));
        var items = dataRows.Select(r => (IReadOnlyList<string>)Enumerable.Range(0, cols).Select(ci => table.DisplayedText[r, ci] ?? "").ToList()).ToList();
        return (columns, items);
    }

    /// <summary>A table template from its file-location folders (Tools > Options > File Locations).</summary>
    private static string TableTemplate(SwContext ctx, swUserPreferenceStringValue_e pref, string file)
    {
        var folders = Folders(ctx, pref);
        foreach (var dir in folders)
        {
            string p = Path.Combine(dir, file);
            if (File.Exists(p)) return p;
        }
        throw new SwException(ErrorCodes.IoError, $"'{file}' is not in the table template folders ({(folders.Count == 0 ? "none set" : string.Join("; ", folders))}).",
            "Pass 'template' with the path of a template file.");
    }

    private static bool DeleteFeature(IModelDoc2 d, IFeature? f)
    {
        if (f is null) return false;
        try
        {
            d.ClearSelection2(true);
            bool ok = f.Select2(false, 0) && d.Extension.DeleteSelection2(0);
            d.ClearSelection2(true);
            return ok;
        }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return false; }
    }
}
