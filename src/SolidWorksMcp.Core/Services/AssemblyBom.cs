using System.Globalization;
using System.Text;

namespace SolidWorksMcp.Core.Services;

// Pure bill-of-materials logic for asm_bom (no SOLIDWORKS types; linked into the offline unit tests).

/// <summary>One component instance in the BOM tree: its file and configuration, and its children (subassemblies).</summary>
internal sealed record BomNode(string Path, string Config, bool IsAssembly, IReadOnlyList<BomNode> Children);

/// <summary>A grouped BOM line before its properties are filled in: item number, level, quantity and the file it counts.</summary>
internal sealed record BomLine(string Item, int Level, string Path, string Config, bool IsAssembly, int Quantity);

/// <summary>One row of asm_bom.</summary>
public sealed class BomRow
{
    /// <summary>Item number: 1, 2, … (indented: 1, 1.1, 1.2, …).</summary>
    public required string Item { get; init; }
    public int Level { get; init; }
    /// <summary>The PartNo / Part Number property, else the file name.</summary>
    public required string PartNumber { get; init; }
    public string? Description { get; init; }
    public string? Material { get; init; }
    public int Quantity { get; init; }
    /// <summary>Mass of one, kg (the file's mass properties).</summary>
    public double? MassKg { get; init; }
    /// <summary>part | assembly.</summary>
    public required string Kind { get; init; }
    public required string File { get; init; }
    public string? Configuration { get; init; }
}

internal static class AssemblyBom
{
    private static string Key(BomNode n) => n.Path.ToUpperInvariant() + "|" + n.Config;

    /// <summary>Instances grouped by file and configuration, in the order they first appear.</summary>
    private static List<(BomNode First, int Count)> Group(IEnumerable<BomNode> nodes)
    {
        var order = new List<string>();
        var map = new Dictionary<string, (BomNode First, int Count)>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            string k = Key(n);
            if (map.TryGetValue(k, out var e)) map[k] = (e.First, e.Count + 1);
            else
            {
                map[k] = (n, 1);
                order.Add(k);
            }
        }
        return order.Select(k => map[k]).ToList();
    }

    /// <summary>levels "top": one line per file and configuration of the top level; a subassembly is one line.</summary>
    public static List<BomLine> Top(IReadOnlyList<BomNode> top) =>
        Group(top).Select((g, i) => new BomLine((i + 1).ToString(CultureInfo.InvariantCulture), 1, g.First.Path, g.First.Config, g.First.IsAssembly, g.Count)).ToList();

    /// <summary>levels "parts": every part at any depth, the quantities multiplied through the subassemblies.</summary>
    public static List<BomLine> Parts(IReadOnlyList<BomNode> top)
    {
        var leaves = new List<BomNode>();
        void Walk(BomNode n)
        {
            if (n.IsAssembly)
                foreach (var c in n.Children) Walk(c);
            else leaves.Add(n);
        }
        foreach (var n in top) Walk(n);
        return Group(leaves).Select((g, i) => new BomLine((i + 1).ToString(CultureInfo.InvariantCulture), 1, g.First.Path, g.First.Config, false, g.Count)).ToList();
    }

    /// <summary>
    /// levels "indented": each level grouped among its siblings (a subassembly's contents once, under its line), item
    /// numbers 1, 1.1, 1.2, 2, … and the quantity per parent.
    /// </summary>
    public static List<BomLine> Indented(IReadOnlyList<BomNode> top)
    {
        var lines = new List<BomLine>();
        void Level(IEnumerable<BomNode> nodes, string prefix, int depth)
        {
            int i = 0;
            foreach (var (first, count) in Group(nodes))
            {
                string item = prefix.Length == 0 ? (++i).ToString(CultureInfo.InvariantCulture) : $"{prefix}.{++i}";
                lines.Add(new BomLine(item, depth, first.Path, first.Config, first.IsAssembly, count));
                if (first.IsAssembly) Level(first.Children, item, depth + 1);
            }
        }
        Level(top, "", 1);
        return lines;
    }

    /// <summary>How many instances the lines stand for: top and parts lines add up; indented counts the top level only.</summary>
    public static int Instances(IReadOnlyList<BomNode> top, string levels)
    {
        int Leaves(BomNode n) => n.IsAssembly ? n.Children.Sum(Leaves) : 1;
        return levels == "parts" ? top.Sum(Leaves) : top.Count;
    }

    public static readonly string[] Columns = ["Item", "Level", "Part number", "Description", "Material", "Quantity", "Mass (kg)", "Kind", "File", "Configuration"];

    /// <summary>The rows as CSV (RFC 4180 quoting, invariant numbers), header first.</summary>
    public static string Csv(IReadOnlyList<BomRow> rows)
    {
        static string Q(string? s)
        {
            s ??= "";
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new StringBuilder();
        sb.Append(string.Join(",", Columns.Select(Q))).Append("\r\n");
        foreach (var r in rows)
        {
            string[] cells =
            [
                r.Item, r.Level.ToString(CultureInfo.InvariantCulture), r.PartNumber, r.Description ?? "", r.Material ?? "",
                r.Quantity.ToString(CultureInfo.InvariantCulture), r.MassKg?.ToString("0.######", CultureInfo.InvariantCulture) ?? "", r.Kind,
                System.IO.Path.GetFileName(r.File), r.Configuration ?? "",
            ];
            sb.Append(string.Join(",", cells.Select(Q))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>The number of data rows in a CSV this class wrote (quoted fields may hold line breaks).</summary>
    public static int CsvRows(string csv)
    {
        int rows = 0;
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (c == '"') quoted = !quoted;
            else if (c == '\n' && !quoted) rows++;
        }
        return Math.Max(0, rows - 1);
    }
}
