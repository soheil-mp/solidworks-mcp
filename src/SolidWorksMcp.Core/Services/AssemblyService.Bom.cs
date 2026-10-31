using System.Runtime.InteropServices;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

/// <summary>Input of asm_bom: the assembly's bill of materials as data, optionally written to a CSV file.</summary>
public sealed record AsmBomRequest
{
    public string? Doc { get; init; }
    /// <summary>top (default) | parts | indented.</summary>
    public string? Levels { get; init; }
    /// <summary>A .csv file to write (relative paths go under the output folder).</summary>
    public string? Path { get; init; }
    public bool Overwrite { get; init; }
}

public sealed class AsmBomResult : ToolResult
{
    /// <summary>top | parts | indented.</summary>
    public required string Levels { get; init; }
    public required IReadOnlyList<BomRow> Rows { get; init; }
    public int RowCount { get; init; }
    /// <summary>The component instances the rows stand for.</summary>
    public int Instances { get; init; }
    /// <summary>Components left out (suppressed, excluded from the BOM, envelopes), with why.</summary>
    public IReadOnlyList<string>? Skipped { get; init; }
    public string? File { get; init; }
    public string? BackupPath { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    public bool? Truncated { get; init; }
}

public sealed partial class AssemblyService
{
    private const int MaxBomRowsReturned = 300;
    private static readonly string[] PartNumberProps = ["PartNo", "Part Number", "PartNumber", "Part No", "Number"];

    public Task<AsmBomResult> BomAsync(AsmBomRequest q, CancellationToken ct = default)
    {
        string levels = AsmParse.Norm(q.Levels) switch
        {
            null or "top" or "top_level" => "top",
            "parts" or "parts_only" or "flat" => "parts",
            "indented" or "tree" => "indented",
            _ => throw SwException.InvalidArgument($"Unknown levels '{q.Levels}'.", "Use top (default), parts (every part, flattened) or indented."),
        };
        if (q.Path is { } p && !p.Trim().EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw SwException.InvalidArgument($"'path' must be a .csv file (got '{p}').", "Excel opens CSV files; e.g. path:\"bom.csv\".");
        return session.RunAsync("asm_bom", ctx => Bom(ctx, levels, q), RunOptions.Read(q.Doc) with { Timeout = Long(180) }, ct);
    }

    /// <summary>asm_bom of the bound assembly for model_export_all.</summary>
    internal static AsmBomResult BomOf(SwContext ctx, string levels, AsmBomRequest q) => Bom(ctx, levels, q);

    private static AsmBomResult Bom(SwContext ctx, string levels, AsmBomRequest q)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        var skipped = new List<string>();
        var models = new Dictionary<string, IComponent2>(StringComparer.OrdinalIgnoreCase);
        var top = BomNodes(InTreeOrder(asm.Components(true), TreeOrder(d), c => c.Name2), skipped, models);
        if (top.Count == 0)
            throw SwException.InvalidArgument($"'{ctx.Title(d)}' has no components in its bill of materials.", skipped.Count > 0 ? $"Left out: {string.Join("; ", skipped.Take(5))}." : null);
        var lines = levels switch
        {
            "top" => AssemblyBom.Top(top),
            "parts" => AssemblyBom.Parts(top),
            _ => AssemblyBom.Indented(top),
        };
        var props = new Dictionary<string, (string PartNo, string? Desc, string? Material, double? Mass)>(StringComparer.OrdinalIgnoreCase);
        var unread = new List<string>();
        var rows = lines.Select(l =>
        {
            string key = l.Path + "|" + l.Config;
            if (!props.TryGetValue(key, out var pr))
            {
                pr = BomProps(models.TryGetValue(key, out var c) ? c : null, l.Path, l.Config, unread);
                props[key] = pr;
            }
            return new BomRow
            {
                Item = l.Item, Level = l.Level, PartNumber = pr.PartNo, Description = pr.Desc, Material = pr.Material, Quantity = l.Quantity,
                MassKg = pr.Mass, Kind = l.IsAssembly ? "assembly" : "part", File = l.Path, Configuration = l.Config.Length == 0 ? null : l.Config,
            };
        }).ToList();
        if (unread.Count > 0)
            ctx.Warn($"Properties of {string.Join(", ", unread.Take(5))} could not be read (lightweight or not loaded); their part number is the file name.");

        var checks = new List<string>();
        int instances = AssemblyBom.Instances(top, levels);
        int counted = levels == "indented" ? lines.Where(l => l.Level == 1).Sum(l => l.Quantity) : lines.Sum(l => l.Quantity);
        if (counted != instances)
            throw new SwException(ErrorCodes.Internal, $"The BOM counts {counted} instances but the assembly has {instances}.");
        checks.Add($"{rows.Count} row(s) for {instances} instance(s){(levels == "parts" ? " of parts at every level" : levels == "indented" ? " at the top level" : "")}" +
                   (skipped.Count > 0 ? $"; {skipped.Count} left out" : ""));

        string? file = null, backup = null;
        if (q.Path is not null)
        {
            string csv = AssemblyBom.Csv(rows);
            file = ctx.Paths.ForWrite(q.Path, q.Overwrite, out backup);
            File.WriteAllText(file, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // the BOM mark makes Excel read UTF-8
            ctx.Paths.RecordWritten(file);
            int back = AssemblyBom.CsvRows(File.ReadAllText(file, Encoding.UTF8));
            if (back != rows.Count) throw new SwException(ErrorCodes.VerifyFailed, $"'{file}' reads {back} row(s), not {rows.Count}.");
            checks.Add($"CSV written and read back: {back} row(s) and the header");
        }
        return new AsmBomResult
        {
            Doc = ctx.Title(d),
            Levels = levels,
            Rows = rows.Take(MaxBomRowsReturned).ToList(),
            RowCount = rows.Count,
            Instances = instances,
            Skipped = skipped.Count == 0 ? null : skipped.Take(50).ToList(),
            File = file,
            BackupPath = backup,
            Checks = checks,
            Truncated = rows.Count > MaxBomRowsReturned ? true : null,
        };
    }

    /// <summary>The BOM tree below <paramref name="components"/>: suppressed components, envelopes and those excluded from the BOM are left out.</summary>
    private static List<BomNode> BomNodes(IEnumerable<IComponent2> components, List<string> skipped, Dictionary<string, IComponent2> models)
    {
        var list = new List<BomNode>();
        foreach (var c in components)
        {
            string name = Safe(() => c.Name2, "?") ?? "?";
            if (Safe(() => c.GetSuppression2(), -1) == (int)swComponentSuppressionState_e.swComponentSuppressed)
            {
                skipped.Add($"{name}: suppressed");
                continue;
            }
            if (Safe(() => c.ExcludeFromBOM, false))
            {
                skipped.Add($"{name}: excluded from the BOM");
                continue;
            }
            if (Safe(() => c.IsEnvelope(), false))
            {
                skipped.Add($"{name}: an envelope");
                continue;
            }
            string path = CompPath(c);
            if (path.Length == 0)
            {
                skipped.Add($"{name}: no file");
                continue;
            }
            string config = Safe(() => c.ReferencedConfiguration, "") ?? "";
            bool isAsm = path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
            models.TryAdd(path + "|" + config, c);
            // A subassembly's children in its own tree's order; in the top assembly their names are "sub-1/child-1".
            var children = isAsm ? InTreeOrder(c.AsmChildren(), c.AsmModelDoc() is { } sub ? TreeOrder(sub) : new Dictionary<string, int>(), LastSegment) : [];
            list.Add(new BomNode(path, config, isAsm, isAsm ? BomNodes(children, skipped, models) : []));
        }
        return list;
    }

    /// <summary>Where each component (and pattern instance) sits in a document's FeatureManager tree, by name.</summary>
    private static Dictionary<string, int> TreeOrder(IModelDoc2 d)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Tree.WalkAll(d))
        {
            if (Safe(() => f.GetTypeName2(), "") is not ("Reference" or "ReferencePattern")) continue;
            if ((object?)Safe<object?>(() => f.GetSpecificFeature2(), null) is IComponent2 c && Safe(() => c.Name2, null) is { Length: > 0 } n)
                order.TryAdd(n, order.Count);
        }
        return order;
    }

    /// <summary>
    /// Components in tree order, so item numbers follow the FeatureManager tree as SOLIDWORKS' own BOM does
    /// (IAssemblyDoc.GetComponents returns them in no fixed order: the same assembly came back in two orders); any the
    /// tree doesn't list go last, by name.
    /// </summary>
    private static List<IComponent2> InTreeOrder(IEnumerable<IComponent2> components, Dictionary<string, int> order, Func<IComponent2, string> key) =>
        components.Select(c => (Comp: c, Name: Safe(() => key(c), "") ?? ""))
            .OrderBy(x => order.TryGetValue(x.Name, out int i) ? i : int.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Comp).ToList();

    private static string LastSegment(IComponent2 c)
    {
        string n = c.Name2 ?? "";
        int slash = n.LastIndexOf('/');
        return slash >= 0 ? n[(slash + 1)..] : n;
    }

    /// <summary>
    /// Part number (PartNo / Part Number / … in the configuration's properties, then the file's; else the file name),
    /// description, material (parts) and mass (the file's mass properties) of a component's model.
    /// </summary>
    private static (string PartNo, string? Desc, string? Material, double? Mass) BomProps(IComponent2? c, string path, string config, List<string> unread)
    {
        string fallback = System.IO.Path.GetFileNameWithoutExtension(path);
        if (c is null || (object?)c.GetModelDoc2() is not IModelDoc2 m)
        {
            unread.Add(fallback);
            return (fallback, null, null, null);
        }
        string? Prop(params string[] names)
        {
            foreach (string cfg in new[] { config, "" }.Distinct())
            {
                ICustomPropertyManager? mgr;
                try { mgr = m.Extension.CustomPropertyManager[cfg]; }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { continue; }
                if (mgr is null) continue;
                var present = ComArgs.Strings((object?)mgr.GetNames());
                foreach (var n in names)
                {
                    string? hit = present.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;
                    mgr.Get6(hit, false, out string raw, out string resolved, out _, out _);
                    string v = (resolved is { Length: > 0 } ? resolved : raw ?? "").Trim();
                    if (v.Length > 0) return v;
                }
            }
            return null;
        }
        string? material = null;
        if (m is IPartDoc part)
            material = Safe(() => part.GetMaterialPropertyName2(config, out _), null) is { Length: > 0 } mat ? mat : null;
        double? mass = Safe(() => (object?)m.Extension.CreateMassProperty2() is IMassProperty2 mp ? Math.Round(mp.Mass, 6) : (double?)null, null);
        return (Prop(PartNumberProps) ?? fallback, Prop("Description"), material, mass);
    }
}
