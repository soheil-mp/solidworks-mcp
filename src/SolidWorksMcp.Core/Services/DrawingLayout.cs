using System.Globalization;
using System.Text.RegularExpressions;
using SolidWorksMcp.Core.Geometry;

namespace SolidWorksMcp.Core.Services;

/// <summary>An axis-aligned rectangle on a drawing sheet (mm; the origin is the sheet's lower-left corner).</summary>
internal readonly record struct SheetRect(double X0, double Y0, double X1, double Y1)
{
    public double W => X1 - X0;
    public double H => Y1 - Y0;
    public double Cx => (X0 + X1) / 2;
    public double Cy => (Y0 + Y1) / 2;

    public static SheetRect Around(double cx, double cy, double w, double h) => new(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2);

    /// <summary>[xMin, yMin, xMax, yMax] in m (a VARIANT from IView.GetOutline) → mm; null when not 4 finite numbers.</summary>
    public static SheetRect? FromMeters(double[]? m)
    {
        if (m is null || m.Length < 4 || m.Take(4).Any(v => !double.IsFinite(v))) return null;
        return new SheetRect(Math.Min(m[0], m[2]) * 1000, Math.Min(m[1], m[3]) * 1000, Math.Max(m[0], m[2]) * 1000, Math.Max(m[1], m[3]) * 1000);
    }

    /// <summary>True when the rectangles overlap or come closer than <paramref name="gap"/>.</summary>
    public bool Overlaps(SheetRect o, double gap = 0) => X0 < o.X1 + gap && o.X0 < X1 + gap && Y0 < o.Y1 + gap && o.Y0 < Y1 + gap;

    public bool Inside(SheetRect o, double tol = 1e-6) => X0 >= o.X0 - tol && Y0 >= o.Y0 - tol && X1 <= o.X1 + tol && Y1 <= o.Y1 + tol;

    public bool Contains(double x, double y, double tol = 0) => x >= X0 - tol && x <= X1 + tol && y >= Y0 - tol && y <= Y1 + tol;

    public SheetRect Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);

    /// <summary>[xMin, yMin, xMax, yMax] rounded to 0.001 mm.</summary>
    public double[] ToArray() => [R(X0), R(Y0), R(X1), R(Y1)];

    private static double R(double v) => Math.Round(v, 3);
}

/// <summary>A paper size: the SOLIDWORKS swDwgPaperSizes_e value and the size in mm (width = horizontal).</summary>
internal sealed record PaperSpec(string Size, int Paper, double WidthMm, double HeightMm)
{
    public bool Portrait => HeightMm > WidthMm;
    public string Orientation => Portrait ? "portrait" : "landscape";
}

/// <summary>A standard model orientation: the model directions that point at the viewer and along the sheet's x and y.</summary>
internal sealed record ViewAxes(Vec3 Toward, Vec3 SheetX, Vec3 SheetY);

/// <summary>Where one standard view goes: its grid cell, its paper size at scale 1 and (after layout) its centre on the sheet.</summary>
internal sealed record ViewSlot(string View, int Col, int Row, double W1, double H1)
{
    public double Cx { get; init; }
    public double Cy { get; init; }
}

/// <summary>The standard-view layout: the scale chosen and every view's centre (mm).</summary>
internal sealed record StandardLayout(double Scale, IReadOnlyList<ViewSlot> Slots, bool Fits, double FitScale)
{
    /// <summary>The rectangle the views take on the sheet (their model boxes at the scale, without SOLIDWORKS' margins).</summary>
    public SheetRect Bounds
    {
        get
        {
            var rs = Slots.Select(s => SheetRect.Around(s.Cx, s.Cy, s.W1 * Scale, s.H1 * Scale)).ToList();
            return rs.Count == 0 ? default : new SheetRect(rs.Min(r => r.X0), rs.Min(r => r.Y0), rs.Max(r => r.X1), rs.Max(r => r.Y1));
        }
    }

    /// <summary>The same layout moved by (dx, dy) mm.</summary>
    public StandardLayout Shift(double dx, double dy) => this with { Slots = Slots.Select(s => s with { Cx = s.Cx + dx, Cy = s.Cy + dy }).ToList() };
}

/// <summary>The sheet as it is: paper (swDwgPaperSizes_e) and size, scale, projection and its sheet-format file (null = none).</summary>
internal sealed record SheetState(int Paper, double WidthMm, double HeightMm, double Num, double Den, bool FirstAngle, string? FormatPath);

/// <summary>
/// A drw_sheet_setup request resolved against the current sheet. <see cref="Family"/> is iso | ansi | none (look the
/// format up for <see cref="Paper"/>) or file (use <see cref="FormatFile"/>).
/// </summary>
internal sealed record SheetPlan(PaperSpec Paper, double Num, double Den, bool FirstAngle, string Family, string? FormatFile);

/// <summary>
/// Pure drawing logic (no COM, unit-tested offline): scale strings, paper sizes and sheet-format file names, the
/// standard model orientations and their projection rules (first and third angle), and placing views on the sheet
/// without overlaps.
/// </summary>
internal static class DrawingLayout
{
    /// <summary>Gap between views on the sheet (mm).</summary>
    public const double Gap = 15;

    // swDwgPaperSizes_e (docs/solidworks-api-notes.md §9): 0 A, 1 A portrait, 2 B, 3 C, 4 D, 5 E, 6 A4, 7 A4 portrait,
    // 8 A3, 9 A2, 10 A1, 11 A0, 12 user-defined.
    private static readonly Dictionary<string, (int Paper, double W, double H, int? PortraitPaper)> Papers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a4"] = (6, 297, 210, 7),
        ["a3"] = (8, 420, 297, null),
        ["a2"] = (9, 594, 420, null),
        ["a1"] = (10, 841, 594, null),
        ["a0"] = (11, 1189, 841, null),
        ["a"] = (0, 279.4, 215.9, 1),
        ["b"] = (2, 431.8, 279.4, null),
        ["c"] = (3, 558.8, 431.8, null),
        ["d"] = (4, 863.6, 558.8, null),
        ["e"] = (5, 1117.6, 863.6, null),
    };

    public const int UserPaper = 12;
    public const string Sizes = "a4 | a3 | a2 | a1 | a0 | a | b | c | d | e | user";

    /// <summary>The paper for a size name and orientation (user: widthMm × heightMm).</summary>
    public static PaperSpec Paper(string? size, string? orientation, double? widthMm, double? heightMm)
    {
        string s = Norm(size) ?? "a3";
        string? o = Norm(orientation);
        if (o is not (null or "landscape" or "portrait"))
            throw SwException.InvalidArgument($"Unknown orientation '{orientation}'.", "Use landscape or portrait.");
        if (s is "user" or "custom")
        {
            if (widthMm is not double w || heightMm is not double h || !double.IsFinite(w) || !double.IsFinite(h) || w < 50 || h < 50 || w > 5000 || h > 5000)
                throw SwException.InvalidArgument("size 'user' needs widthMm and heightMm between 50 and 5000.");
            if (o is "portrait" && w > h || o is "landscape" && h > w) (w, h) = (h, w);
            return new PaperSpec("user", UserPaper, w, h);
        }
        if (widthMm is not null || heightMm is not null)
            throw SwException.InvalidArgument("widthMm and heightMm only apply to size 'user'.");
        if (!Papers.TryGetValue(s, out var p))
            throw SwException.InvalidArgument($"Unknown sheet size '{size}'.", $"Use {Sizes}.");
        if (o != "portrait") return new PaperSpec(s, p.Paper, p.W, p.H);
        if (p.PortraitPaper is not int pp)
            throw SwException.InvalidArgument($"{s.ToUpperInvariant()} has no standard portrait sheet in SOLIDWORKS.",
                $"Use landscape, or size 'user' with widthMm {p.H} and heightMm {p.W}.");
        return new PaperSpec(s, pp, p.H, p.W);
    }

    /// <summary>The size name of a swDwgPaperSizes_e value ("user" for 12 or unknown values).</summary>
    public static string SizeName(int paper) =>
        Papers.FirstOrDefault(kv => kv.Value.Paper == paper || kv.Value.PortraitPaper == paper).Key ?? "user";

    /// <summary>The paper a sheet has now (from ISheet.GetProperties2: paper size and size in mm).</summary>
    public static PaperSpec CurrentPaper(int paper, double widthMm, double heightMm)
    {
        string s = SizeName(paper);
        if (s == "user" || !Papers.TryGetValue(s, out var p)) return new PaperSpec("user", UserPaper, widthMm, heightMm);
        bool portrait = p.PortraitPaper == paper && p.Paper != paper;
        return portrait ? new PaperSpec(s, paper, p.H, p.W) : new PaperSpec(s, p.Paper, p.W, p.H);
    }

    /// <summary>iso | ansi | none | file: the family of a sheet-format file, by its SOLIDWORKS name ("a3 - iso", "a - landscape").</summary>
    public static string FormatFamily(string? formatPath)
    {
        if (string.IsNullOrWhiteSpace(formatPath)) return "none";
        string f = Path.GetFileName(formatPath.Trim()).ToLowerInvariant();
        if (Regex.IsMatch(f, @"^(a[0-4]|[a-e]) - iso\.slddrt$")) return "iso";
        if (Regex.IsMatch(f, @"^(a[0-4]|[a-e]) - (landscape|portrait)\.slddrt$")) return "ansi";
        return "file";
    }

    /// <summary>
    /// Resolves a drw_sheet_setup request against the sheet as it is: whatever is left out stays. A new size without an
    /// orientation keeps the current orientation when that size has it (else landscape); a new format family without a
    /// projection takes the family's (iso: first angle, ansi: third angle).
    /// </summary>
    public static SheetPlan PlanSheet(SheetState now, string? size, string? orientation, double? widthMm, double? heightMm,
        string? scale, string? projection, string? format, string? formatFile)
    {
        var current = CurrentPaper(now.Paper, now.WidthMm, now.HeightMm);
        string? s = Norm(size) ?? (widthMm is not null || heightMm is not null ? "user" : null);
        string? o = Norm(orientation);
        PaperSpec paper;
        if (s is null && o is null) paper = current;
        else
        {
            string sz = s ?? current.Size;
            if (sz is "user" or "custom")
            {
                bool keep = current.Size == "user" && widthMm is null && heightMm is null;
                paper = Paper("user", o, widthMm ?? (keep ? current.WidthMm : null), heightMm ?? (keep ? current.HeightMm : null));
            }
            else if (o is not null) paper = Paper(sz, o, null, null);
            else
            {
                // Keep the current orientation if this size comes that way, else landscape.
                paper = current.Portrait && HasPortrait(sz) ? Paper(sz, "portrait", null, null) : Paper(sz, "landscape", null, null);
            }
        }

        var (num, den) = scale is null ? (now.Num, now.Den) : ParseScale(scale, "scale");
        string? fam = Norm(format);
        string family;
        string? file = null;
        if (formatFile is not null) { family = "file"; file = formatFile; }
        else if (fam is null)
        {
            family = FormatFamily(now.FormatPath);
            if (family == "file") file = now.FormatPath;
        }
        else if (fam is "iso" or "ansi" or "none") family = fam;
        else throw SwException.InvalidArgument($"Unknown format '{format}'.", "Use iso, ansi, none or the path of a .slddrt sheet format.");

        bool firstAngle = Norm(projection) switch
        {
            null => fam switch { "iso" => true, "ansi" => false, _ => now.FirstAngle },
            "first_angle" or "first" => true,
            "third_angle" or "third" => false,
            _ => throw SwException.InvalidArgument($"Unknown projection '{projection}'.", "Use first_angle (ISO) or third_angle (ANSI)."),
        };
        return new SheetPlan(paper, num, den, firstAngle, family, file);
    }

    private static bool HasPortrait(string size) => Papers.TryGetValue(size, out var p) && p.PortraitPaper is not null;

    /// <summary>
    /// Sheet-format file names to look for (lower case, in order) for a paper and a format family, and a note when the
    /// family has no format for this paper (another one is used). Empty for "none".
    /// </summary>
    public static (IReadOnlyList<string> Files, string? Note) FormatFiles(PaperSpec paper, string family)
    {
        string s = paper.Size.ToLowerInvariant();
        if (family == "none") return ([], null);
        if (s == "user") return ([], $"there is no standard '{family}' sheet format for a user-defined size, so the sheet has none");
        bool metric = s.StartsWith('a') && s.Length == 2;
        string ansi = $"{s} - {paper.Orientation}.slddrt";
        if (family == "iso")
        {
            // ISO formats: A0..A3 landscape, A4 portrait ("a4 - iso").
            bool isoExists = metric && (s == "a4" ? paper.Portrait : !paper.Portrait);
            if (isoExists) return ([$"{s} - iso.slddrt"], null);
            return ([ansi], $"there is no ISO sheet format for {s.ToUpperInvariant()} {paper.Orientation}; used '{Path.GetFileNameWithoutExtension(ansi)}'");
        }
        return ([ansi], null);
    }

    // ------------------------------------------------------------------ scales

    /// <summary>"1:2", "2:1", "1/2", "0.5" → (numerator, denominator).</summary>
    public static (double Num, double Den) ParseScale(string? text, string argName)
    {
        string t = (text ?? "").Trim().Replace(" ", "");
        var m = Regex.Match(t, @"^(\d+(?:\.\d+)?)[:/](\d+(?:\.\d+)?)$");
        double num, den;
        if (m.Success)
        {
            num = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            den = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
        {
            (num, den) = Ratio(v);
        }
        else
        {
            throw SwException.InvalidArgument($"'{argName}' must be a scale like \"1:2\" or \"2:1\" (got '{text}').");
        }
        if (num <= 0 || den <= 0 || !double.IsFinite(num / den) || num / den < 1e-4 || num / den > 1e4)
            throw SwException.InvalidArgument($"'{argName}' {text} is out of range (1:10000 to 10000:1).");
        return (num, den);
    }

    /// <summary>A decimal scale as a ratio: 0.5 → (1, 2), 2.5 → (2.5, 1).</summary>
    public static (double Num, double Den) Ratio(double scale) =>
        scale >= 1 ? (Math.Round(scale, 6), 1) : (1, Math.Round(1 / scale, 6));

    public static string FormatScale(double num, double den) => $"{F(num)}:{F(den)}";

    public static string FormatScale(double scale)
    {
        var (n, d) = Ratio(scale);
        return FormatScale(n, d);
    }

    /// <summary>ISO 5455 scales, largest first.</summary>
    public static readonly double[] StandardScales = [50, 20, 10, 5, 2, 1, 0.5, 0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001];

    /// <summary>The largest standard scale not above <paramref name="fit"/> (the smallest one if none is).</summary>
    public static double StandardScaleAtMost(double fit) =>
        StandardScales.FirstOrDefault(s => s <= fit * (1 + 1e-9), StandardScales[^1]);

    // ------------------------------------------------------------------ orientations

    private static readonly double R2 = Math.Sqrt(2), R3 = Math.Sqrt(3), R6 = Math.Sqrt(6);

    /// <summary>The standard views: tool name → (SOLIDWORKS named view, axes). SOLIDWORKS is Y-up (DESIGN §3.9).</summary>
    public static readonly IReadOnlyDictionary<string, (string Api, ViewAxes Axes)> NamedViews = new Dictionary<string, (string, ViewAxes)>
    {
        ["front"] = ("*Front", new ViewAxes(Vec3.UnitZ, Vec3.UnitX, Vec3.UnitY)),
        ["back"] = ("*Back", new ViewAxes(-Vec3.UnitZ, -Vec3.UnitX, Vec3.UnitY)),
        ["top"] = ("*Top", new ViewAxes(Vec3.UnitY, Vec3.UnitX, -Vec3.UnitZ)),
        ["bottom"] = ("*Bottom", new ViewAxes(-Vec3.UnitY, Vec3.UnitX, Vec3.UnitZ)),
        ["right"] = ("*Right", new ViewAxes(Vec3.UnitX, -Vec3.UnitZ, Vec3.UnitY)),
        ["left"] = ("*Left", new ViewAxes(-Vec3.UnitX, Vec3.UnitZ, Vec3.UnitY)),
        ["iso"] = ("*Isometric", new ViewAxes(new Vec3(1 / R3, 1 / R3, 1 / R3), new Vec3(1 / R2, 0, -1 / R2), new Vec3(-1 / R6, 2 / R6, -1 / R6))),
    };

    public const string ViewNames = "front, top, right, iso, back, bottom, left";

    /// <summary>The view axes of a model-to-sheet transform (IMathTransform.ArrayData: rotation rows 0..8, row-vector convention).</summary>
    public static ViewAxes AxesOf(double[] xform)
    {
        var x = new Vec3(xform[0], xform[3], xform[6]).Normalized();
        var y = new Vec3(xform[1], xform[4], xform[7]).Normalized();
        var z = new Vec3(xform[2], xform[5], xform[8]).Normalized();
        return new ViewAxes(z, x, y);
    }

    /// <summary>The standard view an orientation looks along (front, top, …, iso; a rotated view still counts), or null.</summary>
    public static string? Classify(ViewAxes a, double tol = 1e-3)
    {
        foreach (var (name, (_, v)) in NamedViews)
            if (a.Toward.DistanceTo(v.Toward) < tol) return name;
        return null;
    }

    /// <summary>
    /// The direction a projected view looks from, placed on <paramref name="side"/> of its parent: third angle shows
    /// what is seen from that side (the right view on the right); first angle the opposite (the left view on the right).
    /// </summary>
    public static Vec3 ProjectedToward(ViewAxes parent, string side, bool firstAngle)
    {
        var v = side switch
        {
            "right" => parent.SheetX,
            "left" => -parent.SheetX,
            "up" => parent.SheetY,
            "down" => -parent.SheetY,
            _ => throw SwException.InvalidArgument($"Unknown direction '{side}'.", "Use left, right, up or down."),
        };
        return firstAngle ? -v : v;
    }

    /// <summary>A box's extent along a direction: Σ |u_i| · size_i.</summary>
    public static double Extent(Vec3 u, double dx, double dy, double dz) => Math.Abs(u.X) * dx + Math.Abs(u.Y) * dy + Math.Abs(u.Z) * dz;

    // ------------------------------------------------------------------ standard views layout

    /// <summary>
    /// The grid cell of a standard view around the front view (column +1 = right, row +1 = up): third angle puts the
    /// top view above and the right view to the right, first angle the other way round; the back view goes at the far
    /// right. The iso view takes a free corner of the block the other views span (the empty quadrant of the usual L, so
    /// the grid stays compact), else the first free corner around the front view.
    /// </summary>
    public static (int Col, int Row) Cell(string view, bool firstAngle, ICollection<(int, int)> used)
    {
        int f = firstAngle ? -1 : 1;
        (int, int) c = view switch
        {
            "front" => (0, 0),
            "top" => (0, f),
            "bottom" => (0, -f),
            "right" => (f, 0),
            "left" => (-f, 0),
            "back" => (2, 0),
            _ => (int.MinValue, 0),
        };
        if (c.Item1 != int.MinValue) return c;
        if (used.Count > 0)
        {
            int c0 = used.Min(u => u.Item1), c1 = used.Max(u => u.Item1), r0 = used.Min(u => u.Item2), r1 = used.Max(u => u.Item2);
            foreach (var corner in new[] { (c1, r1), (c1, r0), (c0, r1), (c0, r0) })
                if (!used.Contains(corner)) return corner;
        }
        foreach (var corner in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1), (2, 1), (2, -1), (0, 2), (1, 2) })
            if (!used.Contains(corner)) return corner;
        return (3, 0);
    }

    /// <summary>
    /// Lays the views out on a grid in <paramref name="usable"/> (mm): column widths and row heights from the views'
    /// paper sizes at the scale, <see cref="Gap"/> between them, centred. With <paramref name="fixedScale"/> null the
    /// scale is <paramref name="sheetScale"/> when it fits, else the largest standard scale that does.
    /// </summary>
    public static StandardLayout LayoutStandard(IReadOnlyList<string> views, bool firstAngle, double dx, double dy, double dz,
        SheetRect usable, double sheetScale, double? fixedScale)
    {
        var used = new List<(int, int)>();
        var slots = new List<ViewSlot>();
        foreach (var v in views.Where(v => v != "iso").Concat(views.Where(v => v == "iso")))
        {
            var (col, row) = Cell(v, firstAngle, used);
            used.Add((col, row));
            var a = NamedViews[v].Axes;
            slots.Add(new ViewSlot(v, col, row, Extent(a.SheetX, dx, dy, dz), Extent(a.SheetY, dx, dy, dz)));
        }
        // Restore the requested order (iso was placed last so it takes a free corner).
        slots = views.Select(v => slots.First(s => s.View == v)).ToList();

        var cols = slots.Select(s => s.Col).Distinct().OrderBy(c => c).ToList();
        var rows = slots.Select(s => s.Row).Distinct().OrderBy(r => r).ToList();
        double w1 = cols.Sum(c => slots.Where(s => s.Col == c).Max(s => s.W1));
        double h1 = rows.Sum(r => slots.Where(s => s.Row == r).Max(s => s.H1));
        double gapsW = Gap * (cols.Count - 1), gapsH = Gap * (rows.Count - 1);
        double fit = Math.Min(w1 > 0 ? (usable.W - gapsW) / w1 : double.MaxValue, h1 > 0 ? (usable.H - gapsH) / h1 : double.MaxValue);
        if (fit <= 0) fit = 1e-6;
        double scale = fixedScale ?? (sheetScale <= fit * (1 + 1e-9) ? sheetScale : StandardScaleAtMost(fit));

        var colW = cols.ToDictionary(c => c, c => slots.Where(s => s.Col == c).Max(s => s.W1) * scale);
        var rowH = rows.ToDictionary(r => r, r => slots.Where(s => s.Row == r).Max(s => s.H1) * scale);
        double totalW = colW.Values.Sum() + gapsW, totalH = rowH.Values.Sum() + gapsH;
        var colX = new Dictionary<int, double>();
        double x = usable.Cx - totalW / 2;
        foreach (var c in cols)
        {
            colX[c] = x + colW[c] / 2;
            x += colW[c] + Gap;
        }
        var rowY = new Dictionary<int, double>();
        double y = usable.Cy - totalH / 2;
        foreach (var r in rows)
        {
            rowY[r] = y + rowH[r] / 2;
            y += rowH[r] + Gap;
        }
        var placed = slots.Select(s => s with { Cx = colX[s.Col], Cy = rowY[s.Row] }).ToList();
        bool fits = totalW <= usable.W + 1e-6 && totalH <= usable.H + 1e-6;
        return new StandardLayout(scale, placed, fits, fit);
    }

    /// <summary>
    /// The sheet area views should use: inside 10 mm margins (20 mm binding margin on the left with a sheet format),
    /// above a band for the title block when there is a format.
    /// </summary>
    public static SheetRect Usable(double widthMm, double heightMm, bool hasFormat)
    {
        double left = hasFormat ? 20 : 10, margin = 10;
        double band = hasFormat ? Math.Clamp(0.2 * heightMm, 40, 60) : 0;
        return new SheetRect(left, margin + band, widthMm - margin, heightMm - margin);
    }

    // ------------------------------------------------------------------ placing one view

    /// <summary>The centre of a <paramref name="w"/> × <paramref name="h"/> view beside <paramref name="parent"/> on one side.</summary>
    public static (double X, double Y) Beside(SheetRect parent, string side, double w, double h, double gap = Gap) => side switch
    {
        "right" => (parent.X1 + gap + w / 2, parent.Cy),
        "left" => (parent.X0 - gap - w / 2, parent.Cy),
        "up" => (parent.Cx, parent.Y1 + gap + h / 2),
        "down" => (parent.Cx, parent.Y0 - gap - h / 2),
        _ => throw SwException.InvalidArgument($"Unknown direction '{side}'.", "Use left, right, up or down."),
    };

    /// <summary>
    /// The first candidate centre whose rectangle fits <paramref name="usable"/> without touching <paramref name="occupied"/>,
    /// else the free grid position nearest to the first candidate; Free is false when nothing fits (the first candidate is returned).
    /// </summary>
    public static (double X, double Y, bool Free) FindSpot(SheetRect usable, IReadOnlyList<SheetRect> occupied, double w, double h,
        IReadOnlyList<(double X, double Y)> candidates, double gap = 5)
    {
        bool Ok(double cx, double cy)
        {
            var r = SheetRect.Around(cx, cy, w, h);
            return r.Inside(usable) && !occupied.Any(o => r.Overlaps(o, gap));
        }
        foreach (var (cx, cy) in candidates)
            if (Ok(cx, cy)) return (cx, cy, true);
        var first = candidates.Count > 0 ? candidates[0] : (usable.Cx, usable.Cy);
        (double X, double Y)? best = null;
        double bestD = double.MaxValue;
        const double step = 5;
        for (double cx = usable.X0 + w / 2; cx <= usable.X1 - w / 2 + 1e-9; cx += step)
            for (double cy = usable.Y0 + h / 2; cy <= usable.Y1 - h / 2 + 1e-9; cy += step)
            {
                double dd = (cx - first.Item1) * (cx - first.Item1) + (cy - first.Item2) * (cy - first.Item2);
                if (dd < bestD && Ok(cx, cy)) { bestD = dd; best = (cx, cy); }
            }
        return best is { } b ? (b.X, b.Y, true) : (first.Item1, first.Item2, false);
    }

    /// <summary>Candidate centres beside a parent view: the preferred side first, then the others.</summary>
    public static List<(double X, double Y)> SideCandidates(SheetRect parent, double w, double h, params string[] preferred)
    {
        var sides = preferred.Concat(new[] { "right", "down", "left", "up" }).Distinct();
        return sides.Select(s => Beside(parent, s, w, h)).ToList();
    }

    /// <summary>
    /// The centre of a <paramref name="w"/> × <paramref name="h"/> view on one side of <paramref name="parent"/> and aligned
    /// with it, as close as possible without touching <paramref name="occupied"/> and inside <paramref name="area"/>. Free is
    /// false when there is no such place (the closest position is returned).
    /// </summary>
    public static (double X, double Y, bool Free) SideSpot(SheetRect parent, string side, double w, double h,
        IReadOnlyList<SheetRect> occupied, SheetRect area, double gap = Gap, double clearance = 5)
    {
        for (double d = gap; d <= 5000; d += 5)
        {
            var (x, y) = Beside(parent, side, w, h, d);
            var r = SheetRect.Around(x, y, w, h);
            if (!r.Inside(area)) break; // farther out only leaves the sheet
            if (!occupied.Any(o => r.Overlaps(o, clearance))) return (x, y, true);
        }
        var (fx, fy) = Beside(parent, side, w, h, gap);
        return (fx, fy, false);
    }

    /// <summary>
    /// Moves a standard-view layout (centred in <paramref name="usable"/>) to the free place nearest the centre when other
    /// views are already on the sheet. Free is false when no free place fits the whole group (the layout is unchanged).
    /// </summary>
    public static (StandardLayout Layout, bool Free) PlaceGroup(StandardLayout layout, SheetRect usable, IReadOnlyList<SheetRect> occupied)
    {
        var b = layout.Bounds;
        if (occupied.Count == 0 || !occupied.Any(o => b.Overlaps(o, 5))) return (layout, true);
        var (x, y, free) = FindSpot(usable, occupied, b.W, b.H, [(b.Cx, b.Cy)]);
        return free ? (layout.Shift(x - b.Cx, y - b.Cy), true) : (layout, false);
    }

    // ------------------------------------------------------------------ labels and dimension text

    /// <summary>The next free section/detail label (A, B, … without I, O and Q) given the views' names ("Section View A-A", "Detail View B (2 : 1)").</summary>
    public static string NextLabel(IEnumerable<string> viewNames)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in viewNames)
        {
            foreach (Match m in Regex.Matches(n, @"\b([A-Z]{1,2})\s*-\s*\1\b")) used.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(n, @"\b([A-Z]{1,2})\s*\(\s*\d")) used.Add(m.Groups[1].Value);
        }
        var letters = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).Where(c => c is not ("I" or "O" or "Q")).ToList();
        foreach (var l in letters.Concat(letters.SelectMany(a => letters.Select(b => a + b))))
            if (!used.Contains(l)) return l;
        return "ZZ";
    }

    /// <summary>
    /// Where an annotation goes by default (sheet mm): out from its attachment point, away from the view's centre, until it
    /// is 8 mm clear of the view (and at least 15 mm from the point); above the view's top-left corner without one. A spot
    /// within 12 mm of an annotation already there (<paramref name="taken"/>) moves down 12 mm at a time until it is free.
    /// </summary>
    public static (double X, double Y) AnnotationSpot(SheetRect view, (double X, double Y)? pick, IReadOnlyList<(double X, double Y)>? taken = null)
    {
        (double X, double Y) spot;
        if (pick is not (double px, double py)) spot = (view.X0, view.Y1 + 12);
        else
        {
            double dx = px - view.Cx, dy = py - view.Cy, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) (dx, dy, len) = (1, 1, Math.Sqrt(2));
            dx /= len;
            dy /= len;
            spot = (px + dx * 15, py + dy * 15);
            for (double s = 15; s < 1000; s += 5)
            {
                double x = px + dx * s, y = py + dy * s;
                if (x <= view.X0 - 8 || x >= view.X1 + 8 || y <= view.Y0 - 8 || y >= view.Y1 + 8) { spot = (x, y); break; }
            }
        }
        if (taken is { Count: > 0 })
            for (int k = 0; k < 20 && taken.Any(t => Math.Sqrt((t.X - spot.X) * (t.X - spot.X) + (t.Y - spot.Y) * (t.Y - spot.Y)) < 12); k++)
                spot = (spot.X, spot.Y - 12);
        return (Math.Round(spot.X, 3), Math.Round(spot.Y, 3));
    }

    /// <summary>
    /// Where a dimension's text goes by default (sheet mm): horizontal above or below the view, vertical to its right or
    /// left, others 10 mm outward from the view centre through the picked points' centroid.
    /// </summary>
    public static (double X, double Y) DimensionText(string kind, IReadOnlyList<(double X, double Y)> picks, SheetRect view, double offset = 10)
    {
        double mx = picks.Average(p => p.X), my = picks.Average(p => p.Y);
        switch (kind)
        {
            case "horizontal":
                return (mx, my >= view.Cy ? view.Y1 + offset : view.Y0 - offset);
            case "vertical":
                return (mx >= view.Cx ? view.X1 + offset : view.X0 - offset, my);
        }
        double vx = mx - view.Cx, vy = my - view.Cy, len = Math.Sqrt(vx * vx + vy * vy);
        if (len < 1e-9) (vx, vy, len) = (0, 1, 1);
        return (mx + vx / len * offset, my + vy / len * offset);
    }

    internal static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
