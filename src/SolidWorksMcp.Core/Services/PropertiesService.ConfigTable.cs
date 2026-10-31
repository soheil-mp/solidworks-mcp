using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>A config_table cell: a number (a dimension, in mm or degrees) or text (a state, property value, material or configuration).</summary>
public sealed record ConfigCell(double? Number, string? Text);

public sealed class ConfigTableRow
{
    /// <summary>The configuration; created from the configuration active at the start when it doesn't exist.</summary>
    public required string Name { get; init; }
    /// <summary>
    /// Column → cell, with design-table column names: a dimension (D1@Boss-Extrude1), $STATE@&lt;feature or component&gt;,
    /// $PRP@&lt;property&gt;, $MATERIAL (parts) and $CONFIGURATION@&lt;component&gt; (assemblies).
    /// </summary>
    public required IReadOnlyDictionary<string, ConfigCell> Values { get; init; }
}

public sealed class ConfigTableRowResult
{
    public required string Configuration { get; init; }
    /// <summary>Column → value: mm or degrees for dimensions, "suppressed"/"unsuppressed", text for the rest.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
    /// <summary>Measured with the configuration active (rows this call changed).</summary>
    public double? VolumeMm3 { get; init; }
    public double? MassKg { get; init; }
}

public sealed class ConfigTableResult : ToolResult
{
    public required string Active { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public required IReadOnlyList<ConfigTableRowResult> Rows { get; init; }
    /// <summary>Configurations this call created.</summary>
    public IReadOnlyList<string>? Created { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
}

public sealed partial class PropertiesService
{
    private static readonly TimeSpan TableTimeout = TimeSpan.FromSeconds(600);
    private const int MaxAutoColumns = 60;

    private enum ColKind { Dimension, State, Property, Material, ComponentConfig }

    /// <summary>A resolved table column. Dimensions are looked up again by name after each rebuild (objects go stale).</summary>
    private sealed class TableColumn
    {
        public required string Name { get; init; }
        public required ColKind Kind { get; init; }
        public int DimType { get; init; }
        /// <summary>The feature, component or property name.</summary>
        public string Target { get; init; } = "";
        public bool IsComponent { get; init; }
    }

    /// <summary>One configuration's change: the planned values and, for an existing configuration, what they were.</summary>
    private sealed class RowPlan
    {
        public required string Name { get; set; }
        public required Dictionary<TableColumn, object> Values { get; init; }
        public bool Created { get; set; }
        public Dictionary<TableColumn, object?> Previous { get; } = [];
        public double? Volume { get; set; }
        public double? Mass { get; set; }
    }

    /// <summary>
    /// config_table: with rows, creates or updates configurations like a design table (each row activated, set "in this
    /// configuration", rebuilt and read back; every row read again at the end without activating it); without rows, reads
    /// the table for the given columns or, by default, the ones that differ between configurations. A failure undoes the call.
    /// </summary>
    public Task<ConfigTableResult> ConfigTableAsync(string? doc, IReadOnlyList<ConfigTableRow>? rows, IReadOnlyList<string>? columns = null,
        CancellationToken ct = default) =>
        rows is { Count: > 0 }
            ? session.RunAsync("config_table", ctx => WriteTable(ctx, rows), RunOptions.Write(doc) with { Timeout = TableTimeout }, ct)
            : session.RunAsync("config_table", ctx => ReadTable(ctx, columns), RunOptions.Read(doc) with { Timeout = TableTimeout }, ct);

    // ------------------------------------------------------------------ read

    private static ConfigTableResult ReadTable(SwContext ctx, IReadOnlyList<string>? columns)
    {
        var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
        var names = ConfigNames(d);
        var cols = columns is { Count: > 0 }
            ? columns.Select(c => ParseColumn(d, c)).DistinctBy(c => c.Name).ToList()
            : AutoColumns(ctx, d, names);
        var rows = names.Select(n => new ConfigTableRowResult
        {
            Configuration = n,
            Values = cols.ToDictionary(c => c.Name, c => Shown(c, ReadIn(d, c, n))),
        }).ToList();
        return new ConfigTableResult { Doc = ctx.Title(d), Active = ActiveConfig(d), Columns = cols.Select(c => c.Name).ToList(), Rows = rows };
    }

    /// <summary>Dimensions, feature states, configuration-specific properties and the material that differ between configurations.</summary>
    private static List<TableColumn> AutoColumns(SwContext ctx, IModelDoc2 d, string[] names)
    {
        var cols = new List<TableColumn>();
        if (names.Length < 2)
        {
            foreach (var n in names)
                foreach (var p in PropNames(PropManager(d, n)))
                    cols.Add(new TableColumn { Name = $"$PRP@{p}", Kind = ColKind.Property, Target = p });
            return cols;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Tree.WalkAll(d))
        {
            if (Tree.SystemTypes.Contains(f.GetTypeName2())) continue;
            foreach (var di in FeatureVerifier.Dimensions(f))
            {
                if (!seen.Add(di.Name) || d.DimensionNamed(di.Name) is not { } dim) continue;
                var v = ValuesIn(dim, names);
                if (v.Length == names.Length && v.Max() - v.Min() > 1e-12)
                    cols.Add(new TableColumn { Name = di.Name, Kind = ColKind.Dimension, DimType = dim.GetType(), Target = di.Name });
            }
            var s = StatesIn(f, names);
            if (s.Length == names.Length && s.Distinct().Count() > 1)
                cols.Add(new TableColumn { Name = $"$STATE@{f.Name}", Kind = ColKind.State, Target = f.Name });
        }
        foreach (var p in names.SelectMany(n => PropNames(PropManager(d, n))).Distinct(StringComparer.OrdinalIgnoreCase))
            cols.Add(new TableColumn { Name = $"$PRP@{p}", Kind = ColKind.Property, Target = p });
        if (d.GetType() == (int)swDocumentTypes_e.swDocPART &&
            names.Select(n => ((IPartDoc)d).GetMaterialPropertyName2(n, out _) ?? "").Distinct().Count() > 1)
            cols.Add(new TableColumn { Name = "$MATERIAL", Kind = ColKind.Material });
        if (cols.Count > MaxAutoColumns)
        {
            ctx.Warn($"{cols.Count} columns differ; the first {MaxAutoColumns} are shown (pass 'columns' for others).");
            cols = cols.Take(MaxAutoColumns).ToList();
        }
        return cols;
    }

    // ------------------------------------------------------------------ write

    private ConfigTableResult WriteTable(SwContext ctx, IReadOnlyList<ConfigTableRow> rows)
    {
        var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
        NoOpenSketch(ctx, d, "config_table");
        var existing = ConfigNames(d);
        var plans = Plan(ctx, d, rows, existing);
        var cols = plans.SelectMany(p => p.Values.Keys).Distinct().ToList();
        string start = ActiveConfig(d);

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var baseErrors = Errors(d);
        var checks = new List<string>();
        try
        {
            foreach (var p in plans)
            {
                if (p.Created)
                {
                    Activate(d, start);
                    var made = (object?)d.AddConfiguration3(p.Name, "", "", 0) as IConfiguration; // creates and activates it
                    if (ActiveConfig(d) != p.Name)
                    {
                        string got = ConfigNames(d).FirstOrDefault(x => !existing.Contains(x, StringComparer.Ordinal) && !plans.Any(o => o.Created && o.Name == x)) ?? "";
                        if (got.Length == 0)
                            throw new SwException(ErrorCodes.FeatureFailed,
                                $"SOLIDWORKS did not create configuration '{p.Name}'{(made is null ? " (AddConfiguration3 returned nothing)" : "")}.",
                                "Try a simpler name (letters, digits, spaces, '-' and '_').");
                        ctx.Warn($"SOLIDWORKS named the new configuration '{got}' (requested '{p.Name}').");
                        p.Name = got;
                        Activate(d, got);
                    }
                }
                else
                {
                    Activate(d, p.Name);
                    foreach (var c in p.Values.Keys) p.Previous[c] = ReadActive(d, c, p.Name);
                }
                foreach (var (c, v) in p.Values) SetActive(ctx, d, c, p.Name, v);
                d.EditRebuild3();

                var bad = p.Values.Where(kv => !Same(kv.Key, ReadActive(d, kv.Key, p.Name), kv.Value)).ToList();
                if (bad.Count > 0)
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"In '{p.Name}' after the rebuild: {string.Join("; ", bad.Select(kv => $"{kv.Key.Name} reads {Fmt(kv.Key, ReadActive(d, kv.Key, p.Name))} instead of {Fmt(kv.Key, kv.Value)}"))}.",
                        "An equation, a design table or a relation may drive it; check dimension_list and equation_list.");
                var broken = Errors(d).Except(baseErrors).ToList();
                if (broken.Count > 0)
                    throw new SwException(ErrorCodes.RebuildError,
                        $"'{p.Name}' doesn't rebuild with these values: {string.Join(", ", broken.Take(6))} fail.",
                        "Use values that fit the geometry (doc_rebuild lists the errors).");
                if (MassProps(d) is { } mp && mp.Volume > 0)
                {
                    p.Volume = mp.Volume;
                    p.Mass = mp.Mass;
                }
            }
            Activate(d, start);

            // Each value once more in its own configuration without activating it: a later row must not have changed it.
            foreach (var p in plans)
                foreach (var (c, v) in p.Values)
                {
                    if (c.IsComponent) continue; // checked while active; the inactive tree isn't read reliably
                    var now = ReadIn(d, c, p.Name);
                    if (!Same(c, now, v))
                        throw new SwException(ErrorCodes.VerifyFailed,
                            $"'{c.Name}' in '{p.Name}' reads {Fmt(c, now)} after the other rows were set (expected {Fmt(c, v)}).",
                            "The value was not stored per configuration (a linked or global value?).");
                }
            checks.Add($"each of {plans.Count} configuration(s) activated, rebuilt without new errors and read back");
            checks.Add($"every value read again per configuration with '{start}' active");
        }
        catch (Exception ex) when (ex is SwException || (ex is not OperationCanceledException && !ComArgs.IsDisconnect(ex)))
        {
            string undone = Undo(ctx, d, plans, start);
            if (ex is SwException se) throw new SwException(se.Code, se.Message + " " + undone, se.Hint);
            throw new SwException(ErrorCodes.FeatureFailed, $"config_table failed: {ex.Message} {undone}");
        }

        var colNames = cols.Select(c => c.Name).ToList();
        return new ConfigTableResult
        {
            Doc = ctx.Title(d),
            Active = ActiveConfig(d),
            Columns = colNames,
            Rows = plans.Select(p => new ConfigTableRowResult
            {
                Configuration = p.Name,
                Values = cols.Where(p.Values.ContainsKey).ToDictionary(c => c.Name, c => Shown(c, ReadIn(d, c, p.Name, p.Values[c]))),
                VolumeMm3 = p.Volume is double v ? Units.Mm3(v) : null,
                MassKg = p.Mass is double m ? Units.R(m) : null,
            }).ToList(),
            Created = plans.Where(p => p.Created).Select(p => p.Name).ToList() is { Count: > 0 } newOnes ? newOnes : null,
            Checks = checks,
        };
    }

    /// <summary>Validates every row and cell before anything changes.</summary>
    private static List<RowPlan> Plan(SwContext ctx, IModelDoc2 d, IReadOnlyList<ConfigTableRow> rows, string[] existing)
    {
        var cols = new Dictionary<string, TableColumn>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<RowPlan>();
        MaterialCatalog.Catalog? cat = null;
        foreach (var r in rows)
        {
            string name = (r?.Name ?? "").Trim();
            if (name.Length == 0) throw SwException.InvalidArgument("Every row needs a 'name' (the configuration).");
            if (name.Any(char.IsControl)) throw SwException.InvalidArgument($"'{name}' contains control characters.");
            if (plans.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw SwException.InvalidArgument($"Configuration '{name}' is listed twice.");
            if (r!.Values is not { Count: > 0 })
                throw SwException.InvalidArgument($"Row '{name}' has no values.", "e.g. {name:'Long', values:{'D1@Boss-Extrude1':40}}.");
            string? have = existing.FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            var values = new Dictionary<TableColumn, object>();
            foreach (var (key, cell) in r.Values)
            {
                var c = cols.TryGetValue(key.Trim(), out var known) ? known : ParseColumn(d, key);
                c = cols.Values.FirstOrDefault(x => x.Name == c.Name) ?? c; // "bolt<1>" and "bolt-1" are one column
                cols[key.Trim()] = c;
                if (values.Keys.Any(x => x.Name == c.Name)) throw SwException.InvalidArgument($"Row '{name}' sets '{c.Name}' twice.");
                if (c.Kind == ColKind.Material) cat ??= LoadCatalog(ctx);
                values[c] = PlanCell(d, c, cell, name, cat);
            }
            plans.Add(new RowPlan { Name = have ?? name, Values = values, Created = have is null });
        }
        return plans;
    }

    /// <summary>A cell as the value SetActive writes: m/rad/count for dimensions, bool (suppressed) for states, text otherwise.</summary>
    private static object PlanCell(IModelDoc2 d, TableColumn c, ConfigCell? cell, string row, MaterialCatalog.Catalog? cat)
    {
        string where = $"'{c.Name}' in row '{row}'";
        string? text = cell?.Text?.Trim() ?? (cell?.Number is double num ? num.ToString(CultureInfo.InvariantCulture) : null);
        switch (c.Kind)
        {
            case ColKind.Dimension:
                if (cell?.Number is not double v)
                    throw SwException.InvalidArgument($"{where} needs a number in {FeatureManagementLogic.UnitOf(c.DimType)} (got '{text}').");
                return FeatureManagementLogic.ToApi(c.DimType, v, c.Name);
            case ColKind.State:
                return (text ?? "").ToUpperInvariant() switch
                {
                    "S" or "SUPPRESSED" or "SUPPRESS" => true,
                    "U" or "R" or "UNSUPPRESSED" or "UNSUPPRESS" or "RESOLVED" => false,
                    _ => throw SwException.InvalidArgument($"{where} must be \"suppressed\" or \"unsuppressed\" (or S / U), got '{text}'."),
                };
            case ColKind.Material:
                if (string.IsNullOrEmpty(text)) throw SwException.InvalidArgument($"{where} needs a material name, e.g. \"AISI 1020\".");
                if (cat is { Materials.Count: > 0 }) return PickMaterial(cat, text, null);
                return new MaterialCatalog.Entry(text, "", MaterialCatalog.ApiName(MaterialCatalog.DefaultDatabase), null);
            case ColKind.ComponentConfig:
                if (string.IsNullOrEmpty(text)) throw SwException.InvalidArgument($"{where} needs the component's configuration name.");
                var comp = Component(d, c.Target);
                var model = (object?)comp?.GetModelDoc2() as IModelDoc2;
                if (model is not null)
                {
                    var names = ConfigNames(model);
                    return names.FirstOrDefault(x => x.Equals(text, StringComparison.OrdinalIgnoreCase))
                           ?? throw new SwException(ErrorCodes.EntityNotFound, $"{where}: '{c.Target}' has no configuration '{text}'.",
                               $"Its configurations: {string.Join(", ", names.Take(20))}.");
                }
                return text;
            default:
                if (text is null) throw SwException.InvalidArgument($"{where} needs a value.");
                return text;
        }
    }

    private static TableColumn ParseColumn(IModelDoc2 d, string raw)
    {
        string name = (raw ?? "").Trim();
        if (name.Length == 0) throw SwException.InvalidArgument("A column name is empty.");
        bool part = d.GetType() == (int)swDocumentTypes_e.swDocPART;
        if (name.StartsWith('$'))
        {
            int at = name.IndexOf('@');
            string head = (at < 0 ? name : name[..at]).Trim().ToUpperInvariant();
            string arg = at < 0 ? "" : name[(at + 1)..].Trim();
            switch (head)
            {
                case "$STATE":
                    if (arg.Length == 0) throw SwException.InvalidArgument("'$STATE@' needs a feature or component, e.g. $STATE@Fillet1.");
                    if (!part && Component(d, arg) is { } comp)
                        return new TableColumn { Name = $"$STATE@{comp.Name2}", Kind = ColKind.State, Target = comp.Name2, IsComponent = true };
                    var f = EntityResolver.FeatureByName(d, arg)
                            ?? throw new SwException(ErrorCodes.EntityNotFound, $"No feature{(part ? "" : " or component")} named '{arg}'.",
                                part ? "feature_list shows the names." : "feature_list and asm_component_list show the names.");
                    return new TableColumn { Name = $"$STATE@{f.Name}", Kind = ColKind.State, Target = f.Name };
                case "$PRP":
                    if (arg.Length == 0) throw SwException.InvalidArgument("'$PRP@' needs a property name, e.g. $PRP@PartNo.");
                    return new TableColumn { Name = $"$PRP@{arg}", Kind = ColKind.Property, Target = arg };
                case "$MATERIAL":
                    if (!part) throw SwException.InvalidArgument("$MATERIAL is for parts (an assembly's parts keep their own).");
                    return new TableColumn { Name = "$MATERIAL", Kind = ColKind.Material };
                case "$CONFIGURATION":
                    if (part) throw SwException.InvalidArgument("$CONFIGURATION@<component> is for assemblies.");
                    var c = Component(d, arg) ?? throw new SwException(ErrorCodes.EntityNotFound, $"No component named '{arg}'.",
                        "asm_component_list shows the names, e.g. bracket-1.");
                    return new TableColumn { Name = $"$CONFIGURATION@{c.Name2}", Kind = ColKind.ComponentConfig, Target = c.Name2, IsComponent = true };
                default:
                    throw SwException.InvalidArgument($"Unknown column '{name}'.",
                        "Columns: a dimension (D1@Boss-Extrude1), $STATE@<feature or component>, $PRP@<property>, $MATERIAL (parts), " +
                        "$CONFIGURATION@<component> (assemblies).");
            }
        }
        var dim = d.DimensionNamed(name)
                  ?? throw new SwException(ErrorCodes.EntityNotFound, $"No dimension named '{name}'.",
                      "dimension_list shows the names (D1@Boss-Extrude1); other columns start with $: $STATE@, $PRP@, $MATERIAL, $CONFIGURATION@.");
        string full = FeatureVerifier.ShortName(dim.FullName);
        if (dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven)
            throw SwException.InvalidArgument($"'{full}' is a driven (reference) dimension; set the driving one.");
        if (dim.ReadOnly)
            throw SwException.InvalidArgument($"'{full}' is read-only (a design table or link drives it).");
        return new TableColumn { Name = full, Kind = ColKind.Dimension, DimType = dim.GetType(), Target = full };
    }

    // ------------------------------------------------------------------ cells

    private void SetActive(SwContext ctx, IModelDoc2 d, TableColumn c, string cfg, object v)
    {
        switch (c.Kind)
        {
            case ColKind.Dimension:
                var dim = d.DimensionNamed(c.Name) ?? throw new SwException(ErrorCodes.EntityNotFound, $"Dimension '{c.Name}' is gone.");
                int st = dim.SetSystemValue3((double)v, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, ComArgs.Empty);
                if (st != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                {
                    var e = FeatureManagementLogic.SetValueError(st, c.Name, FeatureManagementLogic.FromApi(c.DimType, (double)v), FeatureManagementLogic.UnitOf(c.DimType));
                    throw new SwException(e.Code, $"In '{cfg}': {e.Message}", e.Hint);
                }
                break;
            case ColKind.State when c.IsComponent:
                var comp = Component(d, c.Target) ?? throw new SwException(ErrorCodes.EntityNotFound, $"Component '{c.Target}' is gone.");
                comp.SetSuppression2((int)((bool)v ? swComponentSuppressionState_e.swComponentSuppressed : swComponentSuppressionState_e.swComponentFullyResolved));
                break;
            case ColKind.State:
                var f = EntityResolver.FeatureByName(d, c.Target) ?? throw new SwException(ErrorCodes.EntityNotFound, $"Feature '{c.Target}' is gone.");
                f.SetSuppression2((int)((bool)v ? swFeatureSuppressionAction_e.swSuppressFeature : swFeatureSuppressionAction_e.swUnSuppressFeature),
                    (int)swInConfigurationOpts_e.swThisConfiguration, ComArgs.Empty);
                break;
            case ColKind.Property:
                var mgr = PropManager(d, cfg);
                string target = FindProp(PropNames(mgr), c.Target) ?? c.Target;
                if (v is null) { mgr.Delete2(target); break; }
                int r = mgr.Add3(target, (int)swCustomInfoType_e.swCustomInfoText, (string)v, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                if (r != 0) mgr.Add3(target, (int)swCustomInfoType_e.swCustomInfoText, (string)v, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                break;
            case ColKind.Material:
                var m = (MaterialCatalog.Entry)v;
                ((IPartDoc)d).SetMaterialPropertyName2(cfg, m.Database, m.Name);
                break;
            case ColKind.ComponentConfig:
                var cc = Component(d, c.Target) ?? throw new SwException(ErrorCodes.EntityNotFound, $"Component '{c.Target}' is gone.");
                cc.ReferencedConfiguration = (string)v;
                break;
        }
    }

    /// <summary>A value in the active configuration, as SetActive takes it (null: a missing property or component).</summary>
    private static object? ReadActive(IModelDoc2 d, TableColumn c, string cfg) => c.Kind switch
    {
        ColKind.Dimension => d.DimensionNamed(c.Name) is { } dim ? Safe(() => dim.SystemValue) : null,
        ColKind.State when c.IsComponent => Component(d, c.Target)?.GetSuppression2() is int s ? s == (int)swComponentSuppressionState_e.swComponentSuppressed : null,
        ColKind.State => EntityResolver.FeatureByName(d, c.Target) is { } f ? f.SuppressionStates((int)swInConfigurationOpts_e.swThisConfiguration).FirstOrDefault() : null,
        ColKind.ComponentConfig => Component(d, c.Target)?.ReferencedConfiguration,
        _ => ReadIn(d, c, cfg),
    };

    /// <summary>A value in a named configuration without activating it; components through that configuration's tree.</summary>
    private static object? ReadIn(IModelDoc2 d, TableColumn c, string cfg, object? hint = null)
    {
        switch (c.Kind)
        {
            case ColKind.Dimension:
                return d.DimensionNamed(c.Name) is { } dim && ValuesIn(dim, [cfg]) is [double v] ? v : null;
            case ColKind.State when !c.IsComponent:
                return EntityResolver.FeatureByName(d, c.Target) is { } f && StatesIn(f, [cfg]) is [bool s] ? s : null;
            case ColKind.Property:
                var mgr = PropManager(d, cfg);
                return FindProp(PropNames(mgr), c.Target) is { } p ? ReadProp(mgr, p)?.Value : null;
            case ColKind.Material:
                string m = ((IPartDoc)d).GetMaterialPropertyName2(cfg, out string db) ?? "";
                return m.Length == 0 ? null : hint is MaterialCatalog.Entry e && e.Name == m ? e : new MaterialCatalog.Entry(m, "", db ?? "", null);
            default:
                var comp = ComponentIn(d, cfg, c.Target);
                if (comp is null) return null;
                return c.Kind == ColKind.ComponentConfig ? comp.ReferencedConfiguration
                    : comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed;
        }
    }

    private static bool Same(TableColumn c, object? actual, object expected) => (c.Kind, actual, expected) switch
    {
        (ColKind.Dimension, double a, double e) => FeatureManagementLogic.SameApi(c.DimType, a, e),
        (ColKind.State, bool a, bool e) => a == e,
        (ColKind.Material, MaterialCatalog.Entry a, MaterialCatalog.Entry e) => a.Name == e.Name,
        (ColKind.Property or ColKind.ComponentConfig, string a, string e) => string.Equals(a.Trim(), e.Trim(), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The value as the result shows it: mm/deg/count, "suppressed"/"unsuppressed", text.</summary>
    private static object? Shown(TableColumn c, object? v) => (c.Kind, v) switch
    {
        (ColKind.Dimension, double a) => FeatureManagementLogic.FromApi(c.DimType, a),
        (ColKind.State, bool s) => s ? "suppressed" : "unsuppressed",
        (ColKind.Material, MaterialCatalog.Entry e) => e.Name,
        (_, null) => null,
        _ => v.ToString(),
    };

    private static string Fmt(TableColumn c, object? v) => Shown(c, v) switch
    {
        null => "(none)",
        double x => $"{F(x)} {FeatureManagementLogic.UnitOf(c.DimType)}",
        var s => $"'{s}'",
    };

    private static double[] ValuesIn(IDimension dim, string[] cfgs)
    {
        try { return ComArgs.Doubles((object?)dim.GetSystemValue3((int)swInConfigurationOpts_e.swSpecifyConfiguration, cfgs)); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return []; }
    }

    private static bool[] StatesIn(IFeature f, string[] cfgs)
    {
        try { return SwExt.Bools((object?)f.IsSuppressed2((int)swInConfigurationOpts_e.swSpecifyConfiguration, cfgs)); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return []; }
    }

    private static double? Safe(Func<double> read)
    {
        try { return read(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>A top-level component by name ("bracket-1"; the design-table form "bracket&lt;1&gt;" too).</summary>
    private static IComponent2? Component(IModelDoc2 d, string name)
    {
        if (d is not IAssemblyDoc asm) return null;
        string n = name.Trim();
        if (n.EndsWith('>') && n.LastIndexOf('<') is int lt and > 0) n = $"{n[..lt]}-{n[(lt + 1)..^1]}";
        try { return (object?)asm.GetComponentByName(n) as IComponent2; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>A top-level component as a given configuration has it (its root component's children).</summary>
    private static IComponent2? ComponentIn(IModelDoc2 d, string cfg, string name)
    {
        if ((object?)d.GetConfigurationByName(cfg) is not IConfiguration conf) return null;
        if ((object?)conf.GetRootComponent3(true) is not IComponent2 root) return null;
        return ComArgs.Objects<IComponent2>((object?)root.GetChildren())
            .FirstOrDefault(x => string.Equals(x.Name2, name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Activate(IModelDoc2 d, string cfg)
    {
        if (ActiveConfig(d) == cfg) return;
        d.ShowConfiguration2(cfg);
        if (ActiveConfig(d) != cfg)
            throw new SwException(ErrorCodes.VerifyFailed, $"Configuration '{cfg}' could not be activated ('{ActiveConfig(d)}' is active).",
                "A modal dialog may have interrupted it (dialog_list).");
    }

    /// <summary>Names of the features with rebuild errors (not warnings) in the active configuration.</summary>
    private static HashSet<string> Errors(IModelDoc2 d) =>
        d.Extension.WhatsWrong().Where(x => !x.Warning).Select(x => x.Feature.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Deletes the configurations this call created and puts the previous values back; a sentence for the error.</summary>
    private string Undo(SwContext ctx, IModelDoc2 d, List<RowPlan> plans, string start)
    {
        var problems = new List<string>();
        try { Activate(d, start); }
        catch (SwException) { problems.Add($"'{start}' could not be activated again"); }
        foreach (var p in plans.Where(p => p.Created && ConfigNames(d).Contains(p.Name, StringComparer.Ordinal)))
            if (!d.DeleteConfiguration2(p.Name)) problems.Add($"'{p.Name}' could not be deleted");
        foreach (var p in plans.Where(p => !p.Created && p.Previous.Count > 0))
        {
            try
            {
                Activate(d, p.Name);
                foreach (var (c, v) in p.Previous)
                    if (v is not null || c.Kind == ColKind.Property) SetActive(ctx, d, c, p.Name, v!);
                d.EditRebuild3();
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { problems.Add($"'{p.Name}' was not fully restored ({ex.Message})"); }
        }
        try { Activate(d, start); d.EditRebuild3(); }
        catch (SwException) { problems.Add($"'{start}' could not be activated again"); }
        return problems.Count == 0 ? "Nothing was kept: new configurations were deleted and changed values restored."
            : $"Undo incomplete: {string.Join("; ", problems)}.";
    }

    // ------------------------------------------------------------------ display states

    /// <summary>The active configuration's display states, the active one first (IConfiguration.GetDisplayStates).</summary>
    private static string[] DisplayStates(IModelDoc2 d)
    {
        if (d.ConfigurationManager?.ActiveConfiguration is not IConfiguration conf) return [];
        try { return ComArgs.Strings((object?)conf.GetDisplayStates()); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return []; }
    }

    /// <summary>config_add / config_activate / config_delete with kind display_state, on the active configuration.</summary>
    public Task<ConfigChangeResult> DisplayStateAsync(string? doc, string action, string name, bool activate = true, CancellationToken ct = default) =>
        session.RunAsync($"config_{action}", ctx =>
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0 || n.Any(char.IsControl)) throw SwException.InvalidArgument("'name' is required (the display state's name).");
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var conf = d.ConfigurationManager?.ActiveConfiguration
                       ?? throw new SwException(ErrorCodes.NotSupported, "The document has no active configuration.");
            var before = DisplayStates(d);
            string previous = before.FirstOrDefault() ?? "";
            string? have = before.FirstOrDefault(x => x == n) ?? before.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            string? message = null;
            switch (action)
            {
                case "add":
                    if (have is not null)
                        throw SwException.InvalidArgument($"Display state '{have}' already exists in '{conf.Name}'.", "Activate it with config_activate {kind:'display_state'}.");
                    bool made = conf.CreateDisplayState(n);
                    if (!DisplayStates(d).Contains(n, StringComparer.Ordinal))
                        throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not create display state '{n}' (CreateDisplayState returned {made}).",
                            "Try a simpler name.");
                    string want = activate ? n : previous;
                    if (DisplayStates(d).FirstOrDefault() != want) conf.ApplyDisplayState(want);
                    if (DisplayStates(d).FirstOrDefault() != want) ctx.Warn($"'{n}' was created, but '{want}' is not the active display state.");
                    if (!activate) message = $"Created '{n}'; '{previous}' stays active.";
                    break;
                case "activate":
                    string target = have ?? throw NoDisplayState(n, conf.Name, before);
                    if (target == previous) { message = $"'{target}' was already active."; n = target; break; }
                    bool ok = conf.ApplyDisplayState(target);
                    if (DisplayStates(d).FirstOrDefault() != target)
                        throw new SwException(ErrorCodes.VerifyFailed,
                            $"'{target}' did not become the active display state (ApplyDisplayState returned {ok}; '{DisplayStates(d).FirstOrDefault()}' is active).");
                    n = target;
                    break;
                default:
                    string gone = have ?? throw NoDisplayState(n, conf.Name, before);
                    if (gone == previous)
                        throw SwException.InvalidArgument($"'{gone}' is the active display state, which SOLIDWORKS can't delete.",
                            "Activate another one first (config_activate {kind:'display_state'}).");
                    bool del = conf.DeleteDisplayState(gone);
                    if (DisplayStates(d).Contains(gone, StringComparer.Ordinal))
                        throw new SwException(del ? ErrorCodes.VerifyFailed : ErrorCodes.FeatureFailed,
                            $"Display state '{gone}' still exists (DeleteDisplayState returned {del}).");
                    message = $"Deleted '{gone}'.";
                    n = gone;
                    break;
            }
            var now = DisplayStates(d);
            return new ConfigChangeResult
            {
                Doc = ctx.Title(d), Configuration = conf.Name, Active = ActiveConfig(d), Configurations = ConfigNames(d),
                DisplayState = n, ActiveDisplayState = now.FirstOrDefault(), PreviousDisplayState = previous, DisplayStates = now, Message = message,
            };
        }, RunOptions.Write(doc), ct);

    private static SwException NoDisplayState(string name, string cfg, string[] have) =>
        new(ErrorCodes.EntityNotFound, $"No display state named '{name}' in '{cfg}'.", $"Display states: {string.Join(", ", have)}.");
}
