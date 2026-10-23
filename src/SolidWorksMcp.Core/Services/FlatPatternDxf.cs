using System.Globalization;
using System.Text;

namespace SolidWorksMcp.Core.Services;

// Pure DXF reading for sheetmetal_export_flat (no SOLIDWORKS types; linked into the offline unit tests).

/// <summary>
/// What an ASCII DXF file holds: its release, units, entity counts, layers, and the extents of its geometry (lines,
/// arcs, circles, polylines, ellipses, points; splines by their control points; block inserts through their blocks), in
/// file units.
/// </summary>
public sealed class DxfSummary
{
    /// <summary>$ACADVER, e.g. AC1015.</summary>
    public string? Version { get; init; }

    /// <summary>$INSUNITS when written: 4 = mm, 1 = inch, 0 = unitless.</summary>
    public int? InsUnits { get; init; }

    /// <summary>Entities in the ENTITIES section by type, e.g. LINE: 4.</summary>
    public required IReadOnlyDictionary<string, int> Entities { get; init; }

    /// <summary>Layer names used by those entities (and by the entities of inserted blocks), in order of first use.</summary>
    public required IReadOnlyList<string> Layers { get; init; }

    /// <summary>[xmin, ymin, xmax, ymax] of the geometry (file units); null when the file has none.</summary>
    public double[]? Extents { get; init; }

    /// <summary>[width, height] of <see cref="Extents"/> (file units).</summary>
    public double[]? Size => Extents is { } e ? [Math.Round(e[2] - e[0], 6), Math.Round(e[3] - e[1], 6)] : null;

    /// <summary>Entities on layers whose name contains BEND (how flat-pattern exports usually mark bend lines).</summary>
    public int BendLayerEntities { get; init; }

    /// <summary>Entities inside the blocks that ENTITIES inserts (0 when the geometry is in ENTITIES itself).</summary>
    public int InsertedEntities { get; init; }
}

/// <summary>Reads <see cref="DxfSummary"/> from a DXF file: group-code/value line pairs, sections HEADER, BLOCKS and ENTITIES.</summary>
internal static class DxfReader
{
    /// <summary>Entity types whose geometry counts toward the extents.</summary>
    private static readonly HashSet<string> Geometric = new(StringComparer.Ordinal)
    {
        "LINE", "ARC", "CIRCLE", "LWPOLYLINE", "VERTEX", "POINT", "SPLINE", "ELLIPSE",
    };

    public static DxfSummary ParseFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.Latin1);
        return Parse(reader);
    }

    public static DxfSummary Parse(string text)
    {
        using var reader = new StringReader(text);
        return Parse(reader);
    }

    /// <summary>One block definition: its base point, the box of its geometry and what it holds.</summary>
    private sealed class Block
    {
        public double BaseX, BaseY;
        public Box2 Box { get; } = new();
        public int Count;
        public int Bend;
        public List<string> Layers { get; } = [];
    }

    private sealed record Insert(string Name, double X, double Y, double Sx, double Sy, double RotDeg);

    public static DxfSummary Parse(TextReader reader)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var layers = new List<string>();
        var box = new Box2();
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var inserts = new List<Insert>();
        string? version = null;
        int? units = null;
        int bend = 0;

        string section = "";
        bool expectSectionName = false;
        string? headerVar = null;
        Block? block = null; // the block being read (BLOCKS section)
        // The entity being read: its type, layer and geometry groups.
        string? type = null;
        string? layer = null;
        var groups = new List<(int Code, string Value)>();

        void AddLayer(List<string> list, string name)
        {
            if (!list.Contains(name)) list.Add(name);
        }

        void Finish()
        {
            if (type is null) return;
            if (section == "BLOCKS")
            {
                if (type == "BLOCK")
                {
                    string name = First(groups, 2) is { } n ? n : "";
                    block = new Block { BaseX = Num(First(groups, 10)) ?? 0, BaseY = Num(First(groups, 20)) ?? 0 };
                    if (name.Length > 0) blocks[name] = block;
                }
                else if (block is not null)
                {
                    block.Count++;
                    if (layer is not null)
                    {
                        AddLayer(block.Layers, layer);
                        if (layer.Contains("BEND", StringComparison.OrdinalIgnoreCase)) block.Bend++;
                    }
                    if (Geometric.Contains(type)) AddGeometry(type, groups, block.Box);
                }
            }
            else
            {
                counts[type] = counts.TryGetValue(type, out int c) ? c + 1 : 1;
                if (layer is not null)
                {
                    AddLayer(layers, layer);
                    if (layer.Contains("BEND", StringComparison.OrdinalIgnoreCase)) bend++;
                }
                if (Geometric.Contains(type)) AddGeometry(type, groups, box);
                else if (type == "INSERT" && First(groups, 2) is { Length: > 0 } name)
                    inserts.Add(new Insert(name, Num(First(groups, 10)) ?? 0, Num(First(groups, 20)) ?? 0,
                        Num(First(groups, 41)) ?? 1, Num(First(groups, 42)) ?? 1, Num(First(groups, 50)) ?? 0));
            }
            type = null;
            layer = null;
            groups.Clear();
        }

        while (reader.ReadLine() is { } codeLine)
        {
            string? value = reader.ReadLine();
            if (value is null) break;
            if (!int.TryParse(codeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) continue;
            value = value.Trim();

            if (code == 0)
            {
                if (section is "ENTITIES" or "BLOCKS") Finish();
                if (value == "SECTION") { expectSectionName = true; continue; }
                if (value == "ENDSEC") { section = ""; block = null; continue; }
                if (value == "EOF") break;
                if (section == "BLOCKS")
                {
                    if (value == "ENDBLK") block = null;
                    else type = value; // BLOCK (the header) or an entity of the block
                }
                else if (section == "ENTITIES" && value is not ("SEQEND" or "ENDBLK")) type = value;
                continue;
            }
            if (expectSectionName && code == 2)
            {
                section = value.ToUpperInvariant();
                expectSectionName = false;
                continue;
            }
            if (section == "HEADER")
            {
                if (code == 9) { headerVar = value; continue; }
                if (headerVar == "$ACADVER" && code == 1) version = value;
                else if (headerVar == "$INSUNITS" && code == 70 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)) units = u;
                continue;
            }
            if (section is "ENTITIES" or "BLOCKS" && type is not null)
            {
                if (code == 8) layer = value;
                else groups.Add((code, value));
            }
        }
        if (section is "ENTITIES" or "BLOCKS") Finish();

        // Inserted blocks: their geometry's box, moved by the insert (scale, rotation, translation; base point at the origin).
        int inserted = 0;
        foreach (var ins in inserts)
        {
            if (!blocks.TryGetValue(ins.Name, out var b)) continue;
            inserted += b.Count;
            bend += b.Bend;
            foreach (var l in b.Layers) AddLayer(layers, l);
            if (b.Box.Empty) continue;
            double rad = ins.RotDeg * Math.PI / 180, cs = Math.Cos(rad), sn = Math.Sin(rad);
            foreach (var (x, y) in new[] { (b.Box.X0, b.Box.Y0), (b.Box.X1, b.Box.Y0), (b.Box.X0, b.Box.Y1), (b.Box.X1, b.Box.Y1) })
            {
                double lx = (x - b.BaseX) * ins.Sx, ly = (y - b.BaseY) * ins.Sy;
                box.Add(ins.X + lx * cs - ly * sn, ins.Y + lx * sn + ly * cs);
            }
        }

        return new DxfSummary
        {
            Version = version,
            InsUnits = units,
            Entities = counts,
            Layers = layers,
            Extents = box.Empty ? null : [Math.Round(box.X0, 9), Math.Round(box.Y0, 9), Math.Round(box.X1, 9), Math.Round(box.Y1, 9)],
            BendLayerEntities = bend,
            InsertedEntities = inserted,
        };
    }

    private static void AddGeometry(string type, List<(int Code, string Value)> g, Box2 box)
    {
        switch (type)
        {
            case "LINE":
                AddPoint(g, 10, 20, box);
                AddPoint(g, 11, 21, box);
                break;
            case "POINT" or "VERTEX":
                AddPoint(g, 10, 20, box);
                break;
            case "LWPOLYLINE":
            case "SPLINE":
            {
                // Every x (10, and 11 for spline fit points) is followed by its y (20 / 21). Control points bound a spline.
                double? x = null;
                foreach (var (code, value) in g)
                {
                    if (code is 10 or 11) x = Num(value);
                    else if (code is 20 or 21 && x is double px && Num(value) is double py)
                    {
                        box.Add(px, py);
                        x = null;
                    }
                }
                break;
            }
            case "CIRCLE":
            {
                if (FirstNum(g, 10) is double cx && FirstNum(g, 20) is double cy && FirstNum(g, 40) is double r)
                {
                    box.Add(cx - r, cy - r);
                    box.Add(cx + r, cy + r);
                }
                break;
            }
            case "ARC":
            {
                if (FirstNum(g, 10) is not double cx || FirstNum(g, 20) is not double cy || FirstNum(g, 40) is not double r) break;
                double a0 = FirstNum(g, 50) ?? 0, a1 = FirstNum(g, 51) ?? 360;
                foreach (double deg in ArcExtremeAngles(a0, a1))
                {
                    double rad = deg * Math.PI / 180;
                    box.Add(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
                }
                break;
            }
            case "ELLIPSE":
            {
                // Center (10/20), major-axis end relative to it (11/21), minor/major ratio (40): the full ellipse's box.
                if (FirstNum(g, 10) is not double cx || FirstNum(g, 20) is not double cy || FirstNum(g, 11) is not double mx || FirstNum(g, 21) is not double my) break;
                double ratio = FirstNum(g, 40) ?? 1;
                double nx = -my * ratio, ny = mx * ratio; // the minor semi-axis vector
                double ex = Math.Sqrt(mx * mx + nx * nx), ey = Math.Sqrt(my * my + ny * ny);
                box.Add(cx - ex, cy - ey);
                box.Add(cx + ex, cy + ey);
                break;
            }
        }
    }

    /// <summary>The angles (degrees) where a counter-clockwise arc from <paramref name="start"/> to <paramref name="end"/> is extreme in x or y: its ends and the quadrant points it passes.</summary>
    internal static List<double> ArcExtremeAngles(double start, double end)
    {
        double s = Mod360(start), e = Mod360(end);
        if (e <= s) e += 360;
        var list = new List<double> { s, e };
        for (double q = 0; q < 720; q += 90)
            if (q > s && q < e) list.Add(q);
        return list;
    }

    private static double Mod360(double a)
    {
        double m = a % 360;
        return m < 0 ? m + 360 : m;
    }

    private static void AddPoint(List<(int Code, string Value)> g, int xCode, int yCode, Box2 box)
    {
        if (FirstNum(g, xCode) is double x && FirstNum(g, yCode) is double y) box.Add(x, y);
    }

    private static string? First(List<(int Code, string Value)> g, int code)
    {
        foreach (var (c, v) in g)
            if (c == code) return v;
        return null;
    }

    private static double? FirstNum(List<(int Code, string Value)> g, int code) => Num(First(g, code));

    private static double? Num(string? s) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;

    private sealed class Box2
    {
        public double X0 = double.MaxValue, Y0 = double.MaxValue, X1 = double.MinValue, Y1 = double.MinValue;
        public bool Empty => X0 > X1;

        public void Add(double x, double y)
        {
            X0 = Math.Min(X0, x);
            Y0 = Math.Min(Y0, y);
            X1 = Math.Max(X1, x);
            Y1 = Math.Max(Y1, y);
        }
    }
}
