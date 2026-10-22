using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs

/// <summary>One material of an installed material database.</summary>
public sealed class MaterialRow
{
    public required string Name { get; init; }
    public required string Classification { get; init; }
    /// <summary>The database name as SOLIDWORKS takes it ("SOLIDWORKS Materials", else the .sldmat file name).</summary>
    public required string Database { get; init; }
    /// <summary>Mass density from the database's physical properties (kg/m³).</summary>
    public double? DensityKgM3 { get; init; }
}

public sealed class MaterialDatabaseRow
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public int Materials { get; init; }
}

public sealed class MaterialListResult : ToolResult
{
    public required IReadOnlyList<MaterialRow> Materials { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
    public required IReadOnlyList<MaterialDatabaseRow> Databases { get; init; }
}

public sealed class MaterialSetResult : ToolResult
{
    public required string Material { get; init; }
    public string? Database { get; init; }
    public string? Classification { get; init; }
    /// <summary>The configuration the material was set in.</summary>
    public required string Configuration { get; init; }
    /// <summary>The material before the call (null = none).</summary>
    public string? Previous { get; init; }
    public string? PreviousDatabase { get; init; }
    /// <summary>Density from the document's mass properties after the change (mass / volume, kg/m³).</summary>
    public double? DensityKgM3 { get; init; }
    public double? DatabaseDensityKgM3 { get; init; }
    public double? MassKg { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>One custom property.</summary>
public sealed class CustomPropRow
{
    public required string Name { get; init; }
    /// <summary>The raw value as stored (links stay links, e.g. "D1@Boss-Extrude1" in quotes).</summary>
    public required string Value { get; init; }
    /// <summary>The evaluated value when it differs from the raw one (numbers use the Windows locale).</summary>
    public string? Resolved { get; init; }
    /// <summary>text | number | double | yesno | date | equation | unknown.</summary>
    public required string Type { get; init; }
    /// <summary>True when the property is linked to its parent's property (derived part, cut list): it is changed there.</summary>
    public bool? Linked { get; init; }
}

public sealed class PropListResult : ToolResult
{
    /// <summary>null = file level (the Custom tab); otherwise the configuration whose properties are listed.</summary>
    public string? Configuration { get; init; }
    public required IReadOnlyList<CustomPropRow> Properties { get; init; }
}

public sealed class PropSetResult : ToolResult
{
    public string? Configuration { get; init; }
    public required CustomPropRow Property { get; init; }
    /// <summary>The property before the call (null = it is new).</summary>
    public CustomPropRow? Previous { get; init; }
}

public sealed class PropDeleteResult : ToolResult
{
    public string? Configuration { get; init; }
    public required CustomPropRow Deleted { get; init; }
    public int Remaining { get; init; }
}

/// <summary>One equation or global variable.</summary>
public sealed class EquationRow
{
    public int Index { get; init; }
    public required string Text { get; init; }
    /// <summary>The left-hand side without quotes: a global variable (W) or a dimension (D1@Sketch1).</summary>
    public required string Lhs { get; init; }
    /// <summary>The value SOLIDWORKS evaluates (IEquationMgr.Value; document units).</summary>
    public double? Value { get; init; }
    public bool? Global { get; init; }
}

public sealed class EquationListResult : ToolResult
{
    public required IReadOnlyList<EquationRow> Equations { get; init; }
}

public sealed class EquationSetResult : ToolResult
{
    public required EquationRow Equation { get; init; }
    public bool Created { get; init; }
    /// <summary>The equation with this left-hand side before the call (null = it is new).</summary>
    public EquationRow? Previous { get; init; }
    /// <summary>For a dimension left-hand side: the dimension after the rebuild (mm / deg / count).</summary>
    public DimensionInfo? Dimension { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

public sealed class EquationDeleteResult : ToolResult
{
    public required EquationRow Deleted { get; init; }
    public int Remaining { get; init; }
}

public sealed class ConfigListResult : ToolResult
{
    public required IReadOnlyList<string> Configurations { get; init; }
    public required string Active { get; init; }
}

public sealed class ConfigChangeResult : ToolResult
{
    /// <summary>The configuration the call created, activated or deleted.</summary>
    public required string Configuration { get; init; }
    public required string Active { get; init; }
    /// <summary>The configuration that was active before the call.</summary>
    public string? Previous { get; init; }
    public required IReadOnlyList<string> Configurations { get; init; }
    public string? Message { get; init; }
}

public sealed class AppearanceRow
{
    /// <summary>[r, g, b], 0-255.</summary>
    public required int[] ColorRgb { get; init; }
    /// <summary>0 = opaque, 1 = fully transparent.</summary>
    public double Transparency { get; init; }
}

public sealed class AppearanceSetResult : ToolResult
{
    /// <summary>"part", or the body / face that was colored.</summary>
    public required string Target { get; init; }
    public required AppearanceRow Appearance { get; init; }
    /// <summary>The appearance before the call (null when the target had none of its own).</summary>
    public AppearanceRow? Previous { get; init; }
    public ResolvedInfo? Resolved { get; init; }
}

// ====================================================================== service

/// <summary>
/// Materials, custom properties, equations, configurations and appearance (DESIGN §4.12). Every set tool reads back
/// what it set and returns the previous state; tools that rebuild keep the "errors at every rebuild" dialog off.
/// </summary>
public sealed class PropertiesService(SwSession session)
{
    /// <summary>Rebuilds can take a while on large models (progress is reported by the tool layer).</summary>
    private static readonly TimeSpan RebuildTimeout = TimeSpan.FromSeconds(120);

    private const double DensityRelTol = 0.005;

    // ------------------------------------------------------------------ material_list

    public Task<MaterialListResult> MaterialListAsync(string? filter, int limit = 100, int offset = 0, CancellationToken ct = default) =>
        session.RunAsync("material_list", ctx =>
        {
            var cat = LoadCatalog(ctx);
            string f = filter?.Trim() ?? "";
            var rows = cat.Materials.Where(m => f.Length == 0 || Has(m.Name, f) || Has(m.Classification, f) || Has(m.Database, f)).ToList();
            int skip = Math.Max(0, offset);
            var page = rows.Skip(skip).Take(Math.Clamp(limit, 1, 1000)).Select(m => m.ToRow()).ToList();
            if (rows.Count == 0 && f.Length > 0 && cat.Materials.Count > 0)
                ctx.Warn($"No material matches '{f}'. Closest names: {string.Join(", ", MaterialCatalog.Suggest(cat.Materials.Select(m => m.Name), f))}.");
            return new MaterialListResult
            {
                Materials = page,
                Total = rows.Count,
                Truncated = skip + page.Count < rows.Count ? true : null,
                Databases = cat.Databases,
            };
        }, null, ct);

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The databases SOLIDWORKS itself uses (Tools &gt; Options &gt; File Locations &gt; Material Databases). The folders come
    /// from SOLIDWORKS' own setting, never from tool arguments ('database' is matched against the names found here).
    /// </summary>
    private static MaterialCatalog.Catalog LoadCatalog(SwContext ctx)
    {
        string folders = ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsMaterialDatabases) ?? "";
        var cat = MaterialCatalog.Load(folders, ctx.Paths.AllowNetwork);
        foreach (var w in cat.Warnings) ctx.Warn(w);
        return cat;
    }

    // ------------------------------------------------------------------ material_set

    public Task<MaterialSetResult> MaterialSetAsync(string? doc, string name, string? database = null, string? configuration = null,
        CancellationToken ct = default) =>
        session.RunAsync("material_set", ctx =>
        {
            string want = (name ?? "").Trim();
            if (want.Length == 0)
                throw SwException.InvalidArgument("'name' is required, e.g. \"AISI 1020\".", "material_list shows the installed materials.");
            var d = ctx.Doc;
            var part = ctx.Part();
            string cfg = ConfigArg(d, configuration, "configuration");
            string active = ActiveConfig(d);
            string cfgName = cfg.Length == 0 ? active : cfg;

            // Validate first: SetMaterialPropertyName2 silently ignores names it doesn't know.
            var cat = LoadCatalog(ctx);
            MaterialCatalog.Entry? entry = null;
            if (cat.Materials.Count > 0) entry = PickMaterial(cat, want, database);
            else ctx.Warn("No material database could be read, so the name was not checked in advance (the read-back still is).");
            string material = entry?.Name ?? want;
            string db = entry?.Database ?? MaterialCatalog.ApiName(string.IsNullOrWhiteSpace(database) ? MaterialCatalog.DefaultDatabase : database!.Trim());

            string prev = part.GetMaterialPropertyName2(cfg, out string prevDb) ?? "";
            part.SetMaterialPropertyName2(cfg, db, material);
            string now = part.GetMaterialPropertyName2(cfg, out string nowDb) ?? "";
            var checks = new List<string>();
            if (!string.Equals(now, material, StringComparison.Ordinal))
            {
                string restored = "";
                if (!string.Equals(now, prev, StringComparison.Ordinal) && prev.Length > 0)
                {
                    part.SetMaterialPropertyName2(cfg, prevDb ?? "", prev);
                    restored = part.GetMaterialPropertyName2(cfg, out _) == prev ? $" Restored '{prev}'." : $" Could not restore '{prev}'.";
                }
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"SOLIDWORKS did not apply '{material}' from '{db}': the material of '{cfgName}' reads '{(now.Length == 0 ? "(none)" : now)}'.{restored}",
                    "Check the name and database with material_list (SOLIDWORKS ignores unknown names).");
            }
            checks.Add($"material reads back '{now}'");
            if (!MaterialCatalog.SameDatabase(nowDb, db))
                ctx.Warn($"SOLIDWORKS reports the database as '{nowDb}' (requested '{db}').");

            double? density = null, mass = null;
            if (!string.Equals(cfgName, active, StringComparison.OrdinalIgnoreCase))
                checks.Add($"density not checked ('{cfgName}' is not the active configuration)");
            else if (d.SketchManager?.ActiveSketch is not null)
                ctx.Warn("A sketch is open, so mass properties read zero: the density was not checked (sketch_finish, then model_info).");
            else if (MassProps(d) is { } mp && mp.Volume > 1e-15)
            {
                mass = mp.Mass;
                density = mp.Mass / mp.Volume;
                if (entry?.DensityKgM3 is double expect)
                {
                    if (Math.Abs(density.Value - expect) <= DensityRelTol * expect)
                        checks.Add($"density {F(density.Value)} kg/m³ matches the database");
                    else
                        ctx.Warn($"The mass properties give {F(density.Value)} kg/m³, the database {F(expect)} kg/m³ " +
                                 "(bodies with their own material, or an overridden mass?).");
                }
            }
            else checks.Add("density not checked (no solid volume)");

            return new MaterialSetResult
            {
                Doc = ctx.Title(d),
                Material = now,
                Database = string.IsNullOrEmpty(nowDb) ? db : nowDb,
                Classification = entry?.Classification is { Length: > 0 } c ? c : null,
                Configuration = cfgName,
                Previous = prev.Length == 0 ? null : prev,
                PreviousDatabase = prev.Length == 0 || string.IsNullOrEmpty(prevDb) ? null : prevDb,
                DensityKgM3 = density is double x ? Math.Round(x, 3) : null,
                DatabaseDensityKgM3 = entry?.DensityKgM3,
                MassKg = mass is double m ? Units.R(m) : null,
                Checks = checks,
            };
        }, RunOptions.Write(doc), ct);

    /// <summary>Exact name, then case-insensitive; a miss lists the closest names (ENTITY_NOT_FOUND).</summary>
    private static MaterialCatalog.Entry PickMaterial(MaterialCatalog.Catalog cat, string name, string? database)
    {
        IEnumerable<MaterialCatalog.Entry> pool = cat.Materials;
        string where = "the installed databases";
        if (!string.IsNullOrWhiteSpace(database))
        {
            string dbWant = database.Trim();
            var dbHit = cat.Databases.FirstOrDefault(x => x.Name.Equals(dbWant, StringComparison.OrdinalIgnoreCase))
                        ?? throw new SwException(ErrorCodes.EntityNotFound, $"No material database named '{dbWant}'.",
                            $"Databases: {string.Join(", ", cat.Databases.Select(x => x.Name).Distinct())}.");
            pool = pool.Where(m => m.Database.Equals(dbHit.Name, StringComparison.OrdinalIgnoreCase));
            where = $"'{dbHit.Name}'";
        }
        var list = pool.ToList();
        var hits = list.Where(m => m.Name == name).ToList();
        if (hits.Count == 0) hits = list.Where(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
        {
            var near = MaterialCatalog.Suggest(list.Select(m => m.Name), name);
            throw new SwException(ErrorCodes.EntityNotFound,
                $"No material named '{name}' in {where} (SOLIDWORKS would silently ignore it)." +
                (near.Count > 0 ? $" Closest: {string.Join(", ", near.Select(s => $"'{s}'"))}." : ""),
                "Pass an exact name from material_list (its filter searches names and classifications).");
        }
        if (hits.Count > 1)
        {
            var std = hits.FirstOrDefault(h => MaterialCatalog.IsDefault(h.Database));
            if (std is not null) return std;
            throw new SwException(ErrorCodes.EntityAmbiguous,
                $"'{name}' is in several databases: {string.Join(", ", hits.Select(h => h.Database).Distinct())}.", "Pass 'database'.");
        }
        return hits[0];
    }

    private static IMassProperty2? MassProps(IModelDoc2 d)
    {
        if ((object?)d.Extension.CreateMassProperty2() is not IMassProperty2 mp) return null;
        mp.UseSystemUnits = true; // kg, m³ whatever the document units
        return mp;
    }

    // ------------------------------------------------------------------ custom properties

    public Task<PropListResult> PropListAsync(string? doc, string? configuration = null, CancellationToken ct = default) =>
        session.RunAsync("prop_list", ctx =>
        {
            var d = ctx.Doc;
            string cfg = ConfigArg(d, configuration, "configuration");
            var mgr = PropManager(d, cfg);
            var rows = new List<CustomPropRow>();
            foreach (var n in PropNames(mgr))
                if (ReadProp(mgr, n) is { } row) rows.Add(row);
            return new PropListResult { Doc = ctx.Title(d), Configuration = cfg.Length == 0 ? null : cfg, Properties = rows };
        }, RunOptions.Read(doc), ct);

    public Task<PropSetResult> PropSetAsync(string? doc, string name, string value, string? configuration = null, string? type = "text",
        CancellationToken ct = default) =>
        session.RunAsync("prop_set", ctx =>
        {
            string n = PropName(name);
            var (code, v) = PropValue(type, value);
            var d = ctx.Doc;
            string cfg = ConfigArg(d, configuration, "configuration");
            var mgr = PropManager(d, cfg);
            string? existing = FindProp(PropNames(mgr), n);
            var previous = existing is null ? null : ReadProp(mgr, existing);
            int previousCode = existing is null ? 0 : mgr.GetType2(existing);
            string target = existing ?? n;

            int r = mgr.Add3(target, code, v, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            if (r == (int)swCustomInfoAddResult_e.swCustomInfoAddResult_MismatchAgainstExistingType)
            {
                // The property exists with another type: replace it with one of the requested type.
                r = mgr.Add3(target, code, v, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
                if (r == 0) ctx.Warn($"'{target}' was a {previous?.Type ?? "different"} property; it is now a {TypeName(code)} property.");
            }
            if (r != 0)
            {
                // A failed delete-and-add may already have removed the old property: put it back.
                string restored = previous is not null && ReadProp(mgr, target) is null ? " " + RestoreProp(mgr, target, previous, previousCode) : "";
                throw AddError(r, target, v, code, restored);
            }

            var now = ReadProp(mgr, target);
            bool dateReformatted = now is not null && code == (int)swCustomInfoType_e.swCustomInfoDate && !SameValue(code, now.Value, v);
            if (now is null || (!dateReformatted && !SameValue(code, now.Value, v)))
                throw new SwException(ErrorCodes.VerifyFailed,
                    (now is null ? $"'{target}' is missing after SOLIDWORKS reported it as added." : $"'{target}' reads '{now.Value}' after setting '{v}'.") +
                    " " + RestoreProp(mgr, target, previous, previousCode),
                    "SOLIDWORKS may have rejected the value for this type; try type 'text'.");
            if (dateReformatted) ctx.Warn($"SOLIDWORKS stored the date as '{now.Value}' (requested '{v}').");
            if (!SameType(code, now.Type))
                ctx.Warn($"'{target}' reads back as type {now.Type} (requested {TypeName(code)}).");
            return new PropSetResult { Doc = ctx.Title(d), Configuration = cfg.Length == 0 ? null : cfg, Property = now, Previous = previous };
        }, RunOptions.Write(doc), ct);

    public Task<PropDeleteResult> PropDeleteAsync(string? doc, string name, string? configuration = null, CancellationToken ct = default) =>
        session.RunAsync("prop_delete", ctx =>
        {
            string n = PropName(name);
            var d = ctx.Doc;
            string cfg = ConfigArg(d, configuration, "configuration");
            var mgr = PropManager(d, cfg);
            var names = PropNames(mgr);
            string where = cfg.Length == 0 ? "at file level" : $"in configuration '{cfg}'";
            string existing = FindProp(names, n)
                              ?? throw new SwException(ErrorCodes.EntityNotFound, $"No custom property '{n}' {where}.",
                                  names.Length == 0 ? "There are no custom properties there (configuration-specific ones need 'configuration')."
                                      : $"Properties: {string.Join(", ", names.Take(20))}{(names.Length > 20 ? ", …" : "")}.");
            var before = ReadProp(mgr, existing) ?? new CustomPropRow { Name = existing, Value = "", Type = "unknown" };
            int r = mgr.Delete2(existing);
            if (r == (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_LinkedProp)
                throw new SwException(ErrorCodes.NotSupported, $"'{existing}' is linked to its parent (a derived or cut-list property) and can't be deleted here.",
                    "Change it where it is defined.");
            if (r == (int)swCustomInfoDeleteResult_e.swCustomInfoDeleteResult_NotPresent)
                throw new SwException(ErrorCodes.EntityNotFound, $"SOLIDWORKS reports no property '{existing}' {where}.");
            if (r != 0) throw new SwException(ErrorCodes.FeatureFailed, $"Deleting '{existing}' failed (result {r}).");
            var after = PropNames(mgr);
            if (FindProp(after, existing) is not null)
                throw new SwException(ErrorCodes.VerifyFailed, $"'{existing}' is still present after SOLIDWORKS reported it deleted.");
            return new PropDeleteResult { Doc = ctx.Title(d), Configuration = cfg.Length == 0 ? null : cfg, Deleted = before, Remaining = after.Length };
        }, RunOptions.Write(doc), ct);

    private static ICustomPropertyManager PropManager(IModelDoc2 d, string cfg) =>
        d.Extension.CustomPropertyManager[cfg]
        ?? throw new SwException(ErrorCodes.NotSupported, $"SOLIDWORKS returned no custom property manager for '{(cfg.Length == 0 ? "the file" : cfg)}'.");

    private static string[] PropNames(ICustomPropertyManager mgr) => ComArgs.Strings((object?)mgr.GetNames());

    private static string? FindProp(string[] names, string name) =>
        names.FirstOrDefault(x => x == name) ?? names.FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static CustomPropRow? ReadProp(ICustomPropertyManager mgr, string name)
    {
        int r = mgr.Get6(name, false, out string raw, out string resolved, out _, out bool linked);
        if (r == (int)swCustomInfoGetResult_e.swCustomInfoGetResult_NotPresent) return null;
        raw ??= "";
        return new CustomPropRow
        {
            Name = name,
            Value = raw,
            Resolved = resolved is not null && resolved != raw ? resolved : null,
            Type = TypeName(mgr.GetType2(name)),
            Linked = linked ? true : null,
        };
    }

    private static string PropName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) throw SwException.InvalidArgument("'name' is required (the custom property's name).");
        if (n.Any(char.IsControl) || n.Length > 255) throw SwException.InvalidArgument("'name' must be at most 255 printable characters.");
        return n;
    }

    /// <summary>The swCustomInfoType_e code and the value to store, validated per type.</summary>
    internal static (int Code, string Value) PropValue(string? type, string? value)
    {
        string v = value ?? "";
        switch ((type ?? "text").Trim().ToLowerInvariant())
        {
            case "" or "text":
                return ((int)swCustomInfoType_e.swCustomInfoText, v);
            case "number":
            {
                string s = v.Trim();
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
                    throw SwException.InvalidArgument($"type 'number' needs a numeric value (got '{v}').",
                        "Use a dot for decimals (12.5); links like \"D1@Boss-Extrude1\" need type 'text'.");
                return ((int)swCustomInfoType_e.swCustomInfoNumber, s);
            }
            case "yesno" or "yes_no" or "boolean" or "bool":
                return ((int)swCustomInfoType_e.swCustomInfoYesOrNo, v.Trim().ToLowerInvariant() switch
                {
                    "yes" or "y" or "true" or "1" => "Yes",
                    "no" or "n" or "false" or "0" => "No",
                    _ => throw SwException.InvalidArgument($"type 'yesno' needs Yes or No (got '{v}')."),
                });
            case "date":
                if (v.Trim().Length == 0) throw SwException.InvalidArgument("type 'date' needs a date, e.g. 2026-10-22.");
                return ((int)swCustomInfoType_e.swCustomInfoDate, v.Trim());
            default:
                throw SwException.InvalidArgument($"Unknown property type '{type}'.", "Use text, number, date or yesno.");
        }
    }

    internal static string TypeName(int code) => code switch
    {
        (int)swCustomInfoType_e.swCustomInfoText => "text",
        (int)swCustomInfoType_e.swCustomInfoNumber => "number",
        (int)swCustomInfoType_e.swCustomInfoDouble => "double",
        (int)swCustomInfoType_e.swCustomInfoYesOrNo => "yesno",
        (int)swCustomInfoType_e.swCustomInfoDate => "date",
        (int)swCustomInfoType_e.swCustomInfoEquation => "equation",
        _ => "unknown",
    };

    private static bool SameType(int code, string readBack) =>
        readBack == TypeName(code) || (code == (int)swCustomInfoType_e.swCustomInfoNumber && readBack == "double");

    /// <summary>
    /// The stored raw value matches the request: numbers compared numerically, Yes/No without case, and a link
    /// ("\"D1@Boss-Extrude1\"") also in the document-qualified form SOLIDWORKS may store ("\"D1@Boss-Extrude1@Part1.SLDPRT\"").
    /// </summary>
    internal static bool SameValue(int code, string stored, string requested)
    {
        string a = stored.Trim(), b = requested.Trim();
        if (code == (int)swCustomInfoType_e.swCustomInfoNumber)
        {
            bool pa = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                      double.TryParse(a, NumberStyles.Float, CultureInfo.CurrentCulture, out x);
            return pa && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
                   Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(y));
        }
        if (code == (int)swCustomInfoType_e.swCustomInfoYesOrNo) return a.Equals(b, StringComparison.OrdinalIgnoreCase);
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        bool link = b.Length > 2 && b[0] == '"' && b[^1] == '"' && b.IndexOf('"', 1) == b.Length - 1;
        return link && a.Length > b.Length && a[^1] == '"' && a.StartsWith(b[..^1] + "@", StringComparison.OrdinalIgnoreCase);
    }

    private static SwException AddError(int result, string name, string value, int code, string suffix = "") => result switch
    {
        (int)swCustomInfoAddResult_e.swCustomInfoAddResult_MismatchAgainstSpecifiedType => SwException.InvalidArgument(
            $"SOLIDWORKS rejected '{value}' as a {TypeName(code)} value for '{name}'.{suffix}",
            code == (int)swCustomInfoType_e.swCustomInfoNumber || code == (int)swCustomInfoType_e.swCustomInfoDate
                ? "It may expect the Windows number/date format; type 'text' stores the value as given."
                : "Check the value, or use type 'text'."),
        (int)swCustomInfoAddResult_e.swCustomInfoAddResult_MismatchAgainstExistingType or
        (int)swCustomInfoAddResult_e.swCustomInfoAddResult_MismatchAgainstLegacyTypes => SwException.InvalidArgument(
            $"'{name}' already exists with another type and SOLIDWORKS would not change it.{suffix}", "Delete it with prop_delete first, or use its current type."),
        _ => new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS could not set '{name}' (result {result}).{suffix}",
            "The document may be read-only, or the property linked to a parent."),
    };

    /// <summary>After a failed prop_set: puts the previous property back, or removes the one the call added. Returns a report.</summary>
    private static string RestoreProp(ICustomPropertyManager mgr, string name, CustomPropRow? previous, int previousCode)
    {
        try
        {
            if (previous is null)
            {
                if (ReadProp(mgr, name) is null) return "Nothing was left behind.";
                mgr.Delete2(name);
                return ReadProp(mgr, name) is null ? "The new property was removed again." : "Could not remove it again; prop_list shows it.";
            }
            int code = previousCode > 0 ? previousCode : (int)swCustomInfoType_e.swCustomInfoText;
            mgr.Add3(name, code, previous.Value, (int)swCustomPropertyAddOption_e.swCustomPropertyDeleteAndAdd);
            return ReadProp(mgr, name) is { } back && back.Value == previous.Value
                ? $"Restored the previous value '{previous.Value}'."
                : "Could not restore the previous value; prop_list shows the current one.";
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
        {
            return $"Restoring the previous state failed ({ex.Message}).";
        }
    }

    // ------------------------------------------------------------------ equations

    public Task<EquationListResult> EquationListAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("equation_list", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            return new EquationListResult { Doc = ctx.Title(d), Equations = ReadEquations(EquationMgr(d)) };
        }, RunOptions.Read(doc), ct);

    public Task<EquationSetResult> EquationSetAsync(string? doc, string lhs, string expression, CancellationToken ct = default) =>
        session.RunAsync("equation_set", ctx =>
        {
            string key = EquationText.NormalizeLhs(lhs);
            string expr = EquationText.Expression(expression);
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            NoOpenSketch(ctx, d, "equation_set");
            bool isDim = key.Contains('@');
            if (isDim && Dim(d, key) is null)
                throw new SwException(ErrorCodes.EntityNotFound, $"No dimension named '{key}'.",
                    "Dimension names look like D1@Sketch1 or D1@Boss-Extrude1; feature and sketch results list them.");
            var em = EquationMgr(d);
            var before = ReadEquations(em);
            var prev = before.FirstOrDefault(e => EquationText.SameLhs(e.Lhs, key));
            CheckReferences(d, key, expr, before);
            var bare = EquationText.BareNumbers(expr);
            if (bare.Count > 0)
                ctx.Warn($"Bare number(s) {string.Join(", ", bare)} in '{expr}' are in the document's units; write units (60mm, 30deg) " +
                         "unless they are counts or factors.");
            string text = EquationText.Build(key, expr);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false); // no modal What's Wrong box on a bad expression
            bool created = prev is null;
            if (prev is not null) em.Equation[prev.Index] = text; // assigning Equation(i) updates in place (VERIFIED)
            else if (em.Add2(-1, text, true) < 0)
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS rejected the equation {text}.",
                    "Check the syntax: names in double quotes, units on numbers (60mm), operators and function names.");
            bool rebuilt = d.EditRebuild3();

            var checks = new List<string>();
            DimensionInfo? dimInfo = null;
            var (now, problems) = Verify();
            if (problems.Count > 0 && now is not null)
            {
                // Equations are solved during the rebuild; give dependent features one full rebuild before calling it a failure.
                rebuilt = d.ForceRebuild3(false);
                checks.Clear();
                (now, problems) = Verify();
                if (problems.Count == 0) checks.Add("values settled after a forced rebuild");
            }
            if (problems.Count > 0)
            {
                string rb = RestoreEquation(d, em, key, prev);
                throw new SwException(ErrorCodes.VerifyFailed, $"equation_set {text}: {string.Join("; ", problems)}. {rb}",
                    "Check the expression (units, quoted names, existing variables) and the dimension name.");
            }
            if (!EquationText.SameText(now!.Text, text)) ctx.Warn($"SOLIDWORKS stored the equation as {now.Text}.");
            if (now.Value == 0 && EquationText.Literal(expr) is null)
                ctx.Warn($"{now.Text} evaluates to 0; check the expression (SOLIDWORKS may keep an equation it can't evaluate).");
            if (!rebuilt) ctx.Warn("The rebuild reported problems; feature_list shows features with errors.");
            return new EquationSetResult
            {
                Doc = ctx.Title(d), Equation = now, Created = created, Previous = prev, Dimension = dimInfo, Checks = checks,
            };

            // Tier B: the equation is there, evaluates to the literal it was given, and drives its dimension.
            (EquationRow? Row, List<string> Problems) Verify()
            {
                var row = ReadEquations(em).FirstOrDefault(e => EquationText.SameLhs(e.Lhs, key));
                var found = new List<string>();
                dimInfo = null;
                if (row is null) return (null, [$"no equation for \"{key}\" after the rebuild"]);
                checks.Add($"equation for \"{key}\" present");
                if (row.Value is not double value) return (row, [$"{row.Text} does not evaluate to a number"]);
                var (lin, ang) = DocUnitFactors(d);
                if (EquationText.Literal(expr) is { } lit)
                {
                    if (LiteralAgrees(value, lit, lin, ang)) checks.Add($"value {F(value)} matches {expr}");
                    else found.Add($"it evaluates to {F(value)}, which is not {expr}");
                }
                if (isDim && Dim(d, key) is { } dim)
                {
                    dimInfo = FeatureVerifier.Dimension(dim);
                    double factor = dimInfo.Unit switch { "deg" => ang, "count" => 1, _ => lin };
                    if (Agrees(value, dim.SystemValue, factor)) checks.Add($"{key} = {F(dimInfo.Value)} {dimInfo.Unit}");
                    else found.Add($"{key} is {F(dimInfo.Value)} {dimInfo.Unit} after the rebuild, but the equation evaluates to {F(value)}");
                }
                return (row, found);
            }
        }, RunOptions.Write(doc) with { Timeout = RebuildTimeout }, ct);

    /// <summary>
    /// Before anything changes: every name the expression uses exists (SOLIDWORKS can keep an equation it can't evaluate,
    /// which then reads 0), and the new equation doesn't depend on its own left-hand side.
    /// </summary>
    private static void CheckReferences(IModelDoc2 d, string key, string expr, IReadOnlyList<EquationRow> equations)
    {
        var refs = EquationText.ExpressionReferences(expr);
        var others = equations.Where(e => !EquationText.SameLhs(e.Lhs, key)).ToList();
        if (EquationText.Cycle(key, refs, others.Select(e => (e.Lhs, e.Text))) is { } cycle)
            throw SwException.InvalidArgument($"\"{key}\" = {expr} would be circular: {string.Join(" -> ", cycle.Select(c => $"\"{c}\""))}.",
                "An equation can't depend on its own left-hand side, directly or through other equations.");
        var unknown = refs.Where(r => !EquationText.IsExternal(r) && !others.Any(e => EquationText.SameLhs(e.Lhs, r)) &&
                                      Dim(d, r) is null && EntityResolver.FeatureByName(d, r) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count == 0) return;
        var globals = others.Where(e => e.Global == true).Select(e => e.Lhs).Take(12).ToList();
        throw new SwException(ErrorCodes.EntityNotFound,
            $"The expression uses {string.Join(", ", unknown.Take(6).Select(u => $"\"{u}\""))}, which is not a global variable, dimension or feature.",
            (globals.Count > 0 ? $"Global variables: {string.Join(", ", globals)}. " : "") +
            "Create the variable first (equation_set with lhs W), or use a dimension name such as D1@Sketch1.");
    }

    public Task<EquationDeleteResult> EquationDeleteAsync(string? doc, string lhs, CancellationToken ct = default) =>
        session.RunAsync("equation_delete", ctx =>
        {
            string key = EquationText.NormalizeLhs(lhs);
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            NoOpenSketch(ctx, d, "equation_delete");
            var em = EquationMgr(d);
            var all = ReadEquations(em);
            var target = all.FirstOrDefault(e => EquationText.SameLhs(e.Lhs, key))
                         ?? throw new SwException(ErrorCodes.EntityNotFound, $"No equation for \"{key}\".",
                             all.Count == 0 ? "The document has no equations." : $"Left-hand sides: {string.Join(", ", all.Select(e => e.Lhs).Take(20))}.");
            if (!key.Contains('@'))
            {
                // Deleting a global variable breaks every equation that uses it.
                var users = all.Where(e => e.Index != target.Index &&
                                           EquationText.References(e.Text).Any(r => EquationText.SameLhs(r, target.Lhs))).ToList();
                if (users.Count > 0)
                    throw SwException.InvalidArgument($"\"{target.Lhs}\" is used by {string.Join(", ", users.Select(u => u.Text).Take(6))}.",
                        "Delete or change those equations first (equation_set / equation_delete).");
            }
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            em.Delete(target.Index);
            d.EditRebuild3();
            var after = ReadEquations(em);
            if (after.Any(e => EquationText.SameLhs(e.Lhs, key)))
                throw new SwException(ErrorCodes.VerifyFailed, $"The equation for \"{key}\" is still there after deleting it.");
            if (after.Count != all.Count - 1)
                ctx.Warn($"The document had {all.Count} equations and now has {after.Count}.");
            return new EquationDeleteResult { Doc = ctx.Title(d), Deleted = target, Remaining = after.Count };
        }, RunOptions.Write(doc) with { Timeout = RebuildTimeout }, ct);

    private static IEquationMgr EquationMgr(IModelDoc2 d) =>
        d.GetEquationMgr() ?? throw new SwException(ErrorCodes.NotSupported, "SOLIDWORKS returned no equation manager for this document.");

    private static List<EquationRow> ReadEquations(IEquationMgr em)
    {
        int n = em.GetCount();
        var list = new List<EquationRow>(Math.Max(0, n));
        for (int i = 0; i < n; i++)
        {
            string text = em.Equation[i] ?? "";
            double? value = null;
            try
            {
                double v = em.Value[i];
                if (double.IsFinite(v)) value = Units.R(v);
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* an equation with an error has no value */ }
            bool global = false;
            try { global = em.GlobalVariable[i]; }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* older documents */ }
            list.Add(new EquationRow { Index = i, Text = text, Lhs = EquationText.Lhs(text) ?? "", Value = value, Global = global ? true : null });
        }
        return list;
    }

    /// <summary>Puts back the previous equation (or removes the new one) after a failed verification.</summary>
    private static string RestoreEquation(IModelDoc2 d, IEquationMgr em, string key, EquationRow? prev)
    {
        try
        {
            var now = ReadEquations(em).FirstOrDefault(e => EquationText.SameLhs(e.Lhs, key));
            if (prev is null)
            {
                if (now is not null) em.Delete(now.Index);
            }
            else if (now is not null) em.Equation[now.Index] = prev.Text;
            else em.Add2(-1, prev.Text, true);
            d.EditRebuild3();
            var check = ReadEquations(em).FirstOrDefault(e => EquationText.SameLhs(e.Lhs, key));
            bool ok = prev is null ? check is null : check is not null && EquationText.SameText(check.Text, prev.Text);
            if (!ok) return "Rollback incomplete: equation_list shows the current state.";
            return prev is null ? "Rolled back: the new equation was removed." : $"Rolled back to {prev.Text}.";
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
        {
            return $"Rollback failed ({ex.Message}); equation_list shows the current state.";
        }
    }

    /// <summary>The dimension of that name (D1@Sketch1), or null; IModelDoc2.Parameter may fault on names it can't parse.</summary>
    private static IDimension? Dim(IModelDoc2 d, string name)
    {
        try { return (object?)d.Parameter(name) as IDimension; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>Meters per document length unit and radians per document angle unit.</summary>
    private static (double Linear, double Angular) DocUnitFactors(IModelDoc2 d)
    {
        int lu = d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear, 0);
        int au = d.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsAngular, 0);
        double lin = lu switch
        {
            (int)swLengthUnit_e.swMM => 1e-3,
            (int)swLengthUnit_e.swCM => 1e-2,
            (int)swLengthUnit_e.swMETER => 1,
            (int)swLengthUnit_e.swINCHES or (int)swLengthUnit_e.swFEETINCHES => 0.0254,
            (int)swLengthUnit_e.swFEET => 0.3048,
            (int)swLengthUnit_e.swANGSTROM => 1e-10,
            (int)swLengthUnit_e.swNANOMETER => 1e-9,
            (int)swLengthUnit_e.swMICRON => 1e-6,
            (int)swLengthUnit_e.swMIL => 2.54e-5,
            (int)swLengthUnit_e.swUIN => 2.54e-8,
            _ => 1e-3,
        };
        double ang = au == (int)swAngleUnit_e.swRADIANS ? 1 : Math.PI / 180;
        return (lin, ang);
    }

    /// <summary>
    /// A value SOLIDWORKS reports agrees with an SI quantity, read either in SI or in document units (which one
    /// IEquationMgr.Value uses is not pinned down, so both are accepted; the check still catches wrong values).
    /// </summary>
    private static bool Agrees(double reported, double si, double docFactor) =>
        Close(reported, si) || (docFactor > 0 && Close(reported, si / docFactor));

    private static bool LiteralAgrees(double reported, (double Value, string Unit) lit, double lin, double ang)
    {
        if (lit.Unit.Length == 0) return Close(reported, lit.Value) || Close(reported, lit.Value * lin) || Close(reported, lit.Value * ang);
        if (EquationText.UnitFactor(lit.Unit) is not { } u) return true; // a unit this check doesn't know: not judged
        return Agrees(reported, lit.Value * u.Factor, u.Angle ? ang : lin);
    }

    private static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Abs(b));

    // ------------------------------------------------------------------ configurations

    public Task<ConfigListResult> ConfigListAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("config_list", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            return new ConfigListResult { Doc = ctx.Title(d), Configurations = ConfigNames(d), Active = ActiveConfig(d) };
        }, RunOptions.Read(doc), ct);

    public Task<ConfigChangeResult> ConfigAddAsync(string? doc, string name, bool activate = true, CancellationToken ct = default) =>
        session.RunAsync("config_add", ctx =>
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) throw SwException.InvalidArgument("'name' is required (the new configuration's name).");
            if (n.Any(char.IsControl)) throw SwException.InvalidArgument("'name' must not contain control characters.");
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            NoOpenSketch(ctx, d, "config_add");
            var names = ConfigNames(d);
            if (names.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)) is { } clash)
                throw SwException.InvalidArgument($"A configuration named '{clash}' already exists.", "Activate it with config_activate, or choose another name.");
            string previous = ActiveConfig(d);

            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var made = (object?)d.AddConfiguration3(n, "", "", 0) as IConfiguration; // creates AND activates it (VERIFIED)
            var after = ConfigNames(d);
            if (!after.Contains(n, StringComparer.Ordinal))
            {
                var added = after.Where(x => !names.Contains(x, StringComparer.Ordinal)).ToList();
                if (added.Count != 1)
                    throw new SwException(ErrorCodes.FeatureFailed,
                        $"SOLIDWORKS did not create configuration '{n}'{(made is null ? " (AddConfiguration3 returned nothing)" : "")}.",
                        "Try a simpler name (letters, digits, spaces, '-' and '_').");
                ctx.Warn($"SOLIDWORKS named the new configuration '{added[0]}' (requested '{n}').");
                n = added[0];
            }
            string active = ActiveConfig(d);
            string? message = null;
            string want = activate ? n : previous;
            if (active != want)
            {
                d.ShowConfiguration2(want);
                active = ActiveConfig(d);
                if (active != want)
                    ctx.Warn($"'{n}' was created, but '{want}' could not be (re)activated; '{active}' is active.");
            }
            if (!activate) message = $"Created '{n}'; '{active}' stays active.";
            return new ConfigChangeResult
            {
                Doc = ctx.Title(d), Configuration = n, Active = active, Previous = previous, Configurations = after, Message = message,
            };
        }, RunOptions.Write(doc) with { Timeout = RebuildTimeout }, ct);

    public Task<ConfigChangeResult> ConfigActivateAsync(string? doc, string name, CancellationToken ct = default) =>
        session.RunAsync("config_activate", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            string target = ExistingConfig(d, name);
            string previous = ActiveConfig(d);
            if (target == previous)
                return new ConfigChangeResult
                {
                    Doc = ctx.Title(d), Configuration = target, Active = previous, Previous = previous, Configurations = ConfigNames(d),
                    Message = $"'{target}' was already active.",
                };
            NoOpenSketch(ctx, d, "config_activate");
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            bool ok = d.ShowConfiguration2(target);
            string active = ActiveConfig(d);
            if (active != target)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"'{target}' did not become active (ShowConfiguration2 returned {ok}; '{active}' is active).",
                    "A modal dialog may have interrupted it (dialog_list), or the configuration is a SpeedPak/derived one SOLIDWORKS can't show.");
            return new ConfigChangeResult { Doc = ctx.Title(d), Configuration = target, Active = active, Previous = previous, Configurations = ConfigNames(d) };
        }, RunOptions.Write(doc) with { Timeout = RebuildTimeout }, ct);

    public Task<ConfigChangeResult> ConfigDeleteAsync(string? doc, string name, CancellationToken ct = default) =>
        session.RunAsync("config_delete", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            string target = ExistingConfig(d, name);
            string active = ActiveConfig(d);
            if (target == active)
                throw SwException.InvalidArgument($"'{target}' is the active configuration, which SOLIDWORKS can't delete.",
                    "Activate another one with config_activate first.");
            var before = ConfigNames(d);
            bool ok = d.DeleteConfiguration2(target);
            var after = ConfigNames(d);
            if (after.Contains(target, StringComparer.Ordinal))
                throw new SwException(ok ? ErrorCodes.VerifyFailed : ErrorCodes.FeatureFailed,
                    $"Configuration '{target}' still exists (DeleteConfiguration2 returned {ok}).",
                    "It may be referenced (an assembly component or a drawing view uses it) or locked.");
            var lost = before.Where(x => x != target && !after.Contains(x, StringComparer.Ordinal)).ToList();
            if (lost.Count > 0) ctx.Warn($"SOLIDWORKS also deleted: {string.Join(", ", lost)} (derived configurations).");
            if (ActiveConfig(d) != active) ctx.Warn($"The active configuration changed to '{ActiveConfig(d)}'.");
            return new ConfigChangeResult
            {
                Doc = ctx.Title(d), Configuration = target, Active = ActiveConfig(d), Previous = active, Configurations = after,
                Message = $"Deleted '{target}'.",
            };
        }, RunOptions.Write(doc), ct);

    private static string[] ConfigNames(IModelDoc2 d) => ComArgs.Strings((object?)d.GetConfigurationNames());

    private static string ActiveConfig(IModelDoc2 d) => d.ConfigurationManager?.ActiveConfiguration?.Name ?? "";

    /// <summary>The canonical name of an existing configuration (exact, then case-insensitive).</summary>
    private static string ExistingConfig(IModelDoc2 d, string? name, string argName = "name")
    {
        string want = (name ?? "").Trim();
        if (want.Length == 0) throw SwException.InvalidArgument($"'{argName}' is required (a configuration name; config_list shows them).");
        var names = ConfigNames(d);
        return names.FirstOrDefault(x => x == want) ?? names.FirstOrDefault(x => x.Equals(want, StringComparison.OrdinalIgnoreCase))
               ?? throw new SwException(ErrorCodes.EntityNotFound, $"No configuration named '{want}'.",
                   $"Configurations: {string.Join(", ", names.Take(20))}{(names.Length > 20 ? ", …" : "")}.");
    }

    /// <summary>"" for none (file level / the active configuration), else an existing configuration's name.</summary>
    private static string ConfigArg(IModelDoc2 d, string? configuration, string argName)
    {
        if (string.IsNullOrWhiteSpace(configuration)) return "";
        if (d.GetType() == (int)swDocumentTypes_e.swDocDRAWING)
            throw SwException.InvalidArgument($"Drawings have no configurations; omit '{argName}'.");
        return ExistingConfig(d, configuration, argName);
    }

    private static void NoOpenSketch(SwContext ctx, IModelDoc2 d, string tool)
    {
        if (d.SketchManager?.ActiveSketch is not ISketch open) return;
        string name = ((IFeature)open).Name;
        throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open; {tool} rebuilds the model, which would close it.",
            ctx.DocEntry.ServerSketch == name ? "Finish it with sketch_finish first." : "The user is editing it; ask them to exit the sketch.");
    }

    // ------------------------------------------------------------------ appearance

    public Task<AppearanceSetResult> AppearanceSetAsync(string? doc, int[]? colorRgb, double? transparency = null, EntityRef? target = null,
        CancellationToken ct = default) =>
        session.RunAsync("appearance_set", ctx =>
        {
            var rgb = Appearance.Rgb(colorRgb);
            if (transparency is double t && (!double.IsFinite(t) || t < 0 || t > 1))
                throw SwException.InvalidArgument($"'transparency' must be between 0 (opaque) and 1 (clear) (got {F(t)}).");
            var d = ctx.Doc;
            ctx.Part();
            Resolved? r = null;
            if (target is not null)
            {
                // Faces have no names, so a bare name means a body (otherwise it would match the feature of the same name).
                var tr = target is { Name: not null, Type: null, Id: null, At: null, Where: null }
                    ? new EntityRef { Name = target.Name, Type = "body" }
                    : target;
                r = EntityResolver.Resolve(ctx, d, tr, "target", EntityKind.Body, EntityKind.Face);
            }
            if (rgb.All(c => c <= 1) && rgb.Any(c => c == 1))
                ctx.Warn($"colorRgb is 0-255, so [{string.Join(", ", rgb)}] is almost black; multiply 0-1 values by 255 (red is [255, 0, 0]).");
            string label = r?.Label ?? "part";
            double[] prev = Appearance.Read(d, r?.Obj);
            // A body or face without an appearance of its own starts from the part's lighting values.
            double[] basis = r is null || Appearance.Valid(prev) ? prev : Appearance.Read(d, null);
            double[] values = Appearance.Compose(basis, rgb, transparency);
            Appearance.Write(d, r?.Obj, values);
            d.GraphicsRedraw2();
            double[] now = Appearance.Read(d, r?.Obj);
            if (!Appearance.Matches(now, values))
            {
                string restored = "";
                if (Appearance.Valid(prev))
                {
                    Appearance.Write(d, r?.Obj, prev);
                    d.GraphicsRedraw2();
                    restored = Appearance.Matches(Appearance.Read(d, r?.Obj), prev)
                        ? " The previous appearance was restored." : " The previous appearance could not be restored.";
                }
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The appearance of {label} reads [{string.Join(", ", now.Select(F))}] after setting [{string.Join(", ", values.Select(F))}].{restored}",
                    r is null ? "A body or face appearance may override the part color; target it directly." : "Try the whole part (omit 'target').");
            }
            return new AppearanceSetResult
            {
                Doc = ctx.Title(d),
                Target = label,
                Appearance = Appearance.Row(now)!,
                Previous = Appearance.Row(prev),
                Resolved = r?.Info("target"),
            };
        }, RunOptions.Write(doc), ct);

    /// <summary>The legacy "material property values": r, g, b (0-1), ambient, diffuse, specular, shininess, transparency, emission.</summary>
    private static class Appearance
    {
        public static double[] Read(IModelDoc2 d, object? target) => target switch
        {
            IBody2 b => ComArgs.Doubles((object?)b.MaterialPropertyValues2),
            IFace2 f => ComArgs.Doubles((object?)f.MaterialPropertyValues),
            _ => ComArgs.Doubles((object?)d.MaterialPropertyValues),
        };

        public static void Write(IModelDoc2 d, object? target, double[] v)
        {
            switch (target)
            {
                case IBody2 b: b.MaterialPropertyValues2 = v; break;
                case IFace2 f: f.MaterialPropertyValues = v; break;
                default: d.MaterialPropertyValues = v; break; // VERIFIED (red part)
            }
        }

        public static int[] Rgb(int[]? rgb)
        {
            if (rgb is null || rgb.Length != 3)
                throw SwException.InvalidArgument("'colorRgb' must be [r, g, b] with 0-255 each, e.g. [255, 0, 0].");
            if (rgb.Any(c => c is < 0 or > 255))
                throw SwException.InvalidArgument($"'colorRgb' values must be 0-255 (got [{string.Join(", ", rgb)}]).");
            return rgb;
        }

        /// <summary>Nine finite values in 0-1: an appearance of the entity's own.</summary>
        public static bool Valid(double[] v) => v.Length >= 9 && v.Take(9).All(x => double.IsFinite(x) && x >= 0 && x <= 1);

        /// <summary>New values: the requested color and transparency, the other lighting values kept when sane.</summary>
        public static double[] Compose(double[] prev, int[] rgb, double? transparency)
        {
            bool keep = Valid(prev);
            return
            [
                rgb[0] / 255.0, rgb[1] / 255.0, rgb[2] / 255.0,
                keep ? prev[3] : 1.0,   // ambient
                keep ? prev[4] : 1.0,   // diffuse
                keep ? prev[5] : 0.5,   // specular
                keep ? prev[6] : 0.4,   // shininess
                transparency ?? (keep ? prev[7] : 0.0),
                keep ? prev[8] : 0.0,   // emission
            ];
        }

        public static bool Matches(double[] now, double[] want) =>
            now.Length >= 9 && new[] { 0, 1, 2, 7 }.All(i => Math.Abs(now[i] - want[i]) <= 2e-3);

        public static AppearanceRow? Row(double[] v)
        {
            if (v.Length < 9 || v.Take(3).Any(x => !double.IsFinite(x) || x < 0 || x > 1)) return null;
            return new AppearanceRow
            {
                ColorRgb = [To255(v[0]), To255(v[1]), To255(v[2])],
                Transparency = double.IsFinite(v[7]) ? Math.Round(Math.Clamp(v[7], 0, 1), 4) : 0,
            };
        }

        private static int To255(double x) => (int)Math.Round(Math.Clamp(x, 0, 1) * 255);
    }

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}

// ====================================================================== interop-free helpers

/// <summary>
/// The installed material databases: SOLIDWORKS .sldmat files (UTF-16 XML with classification / material elements and
/// the mass density in physicalproperties/DENS, kg/m³). Each file is parsed once per version and cached for the process.
/// </summary>
internal static class MaterialCatalog
{
    internal sealed record Entry(string Name, string Classification, string Database, double? DensityKgM3)
    {
        public MaterialRow ToRow() => new() { Name = Name, Classification = Classification, Database = Database, DensityKgM3 = DensityKgM3 };
    }

    internal sealed record Catalog(IReadOnlyList<Entry> Materials, IReadOnlyList<MaterialDatabaseRow> Databases, IReadOnlyList<string> Warnings);

    /// <summary>The standard database as the API names it (its file is "solidworks materials.sldmat").</summary>
    public const string DefaultDatabase = "SOLIDWORKS Materials";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, long Length, List<Entry> Rows)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsDefault(string database) => database.Equals(DefaultDatabase, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name SetMaterialPropertyName2 takes: the file name without extension ("SOLIDWORKS Materials" for the standard one).</summary>
    public static string ApiName(string fileStem) => IsDefault(fileStem) ? DefaultDatabase : fileStem;

    /// <summary>The database SOLIDWORKS reports is the one requested (by name, or by a path to it).</summary>
    public static bool SameDatabase(string? reported, string requested)
    {
        if (string.IsNullOrWhiteSpace(reported)) return true;
        string r = reported.Trim();
        if (r.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        try { return Path.GetFileNameWithoutExtension(r).Equals(requested, StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>All materials of the *.sldmat files in the ';'-separated folders (top level of each folder).</summary>
    public static Catalog Load(string folderList, bool allowNetwork)
    {
        var warnings = new List<string>();
        var files = new List<(int Folder, string File, string Db)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int folderIndex = 0;
        foreach (var raw in (folderList ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string folder = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
            if (folder.Length == 0 || !seen.Add(folder.TrimEnd('\\', '/'))) continue;
            if (!allowNetwork && (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)))
            {
                warnings.Add($"Skipped the network folder '{folder}' (SWMCP_ALLOW_NETWORK_PATHS=1 allows it).");
                continue;
            }
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var f in Directory.GetFiles(folder, "*.sldmat", SearchOption.TopDirectoryOnly))
                    files.Add((folderIndex, f, ApiName(Path.GetFileNameWithoutExtension(f))));
                folderIndex++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                warnings.Add($"Could not read '{folder}': {ex.Message}");
            }
        }

        // The standard database first, then the others in folder and name order.
        var materials = new List<Entry>();
        var dbs = new List<MaterialDatabaseRow>();
        foreach (var (_, file, db) in files.OrderBy(x => IsDefault(x.Db) ? 0 : 1).ThenBy(x => x.Folder)
                     .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase))
        {
            List<Entry> rows;
            try { rows = Cached(file, db); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                warnings.Add($"Could not parse '{file}': {ex.Message}");
                continue;
            }
            materials.AddRange(rows);
            dbs.Add(new MaterialDatabaseRow { Name = db, Path = file, Materials = rows.Count });
        }
        if (dbs.Count == 0)
            warnings.Add($"No material database (*.sldmat) found in '{folderList}' (Tools > Options > File Locations > Material Databases).");
        return new Catalog(materials, dbs, warnings);
    }

    private static List<Entry> Cached(string file, string database)
    {
        var fi = new FileInfo(file);
        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Length == fi.Length) return c.Rows;
        }
        var rows = Parse(file, database);
        lock (Gate) Cache[file] = (fi.LastWriteTimeUtc, fi.Length, rows);
        return rows;
    }

    /// <summary>Parses one database file (any encoding with a BOM or declaration; no DTDs, no external resources).</summary>
    public static List<Entry> Parse(string file, string database)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Parse(fs, database);
    }

    public static List<Entry> Parse(Stream stream, string database)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
        };
        using var r = XmlReader.Create(stream, settings);
        var rows = new List<Entry>();
        var classes = new Stack<(string Name, int Depth)>();
        string? material = null;
        int materialDepth = -1, physDepth = -1;
        double? density = null;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Element)
            {
                string ln = r.LocalName;
                int depth = r.Depth;
                bool empty = r.IsEmptyElement;
                if (material is null)
                {
                    if (ln == "classification")
                    {
                        if (!empty) classes.Push((r.GetAttribute("name") ?? "", depth));
                    }
                    else if (ln == "material" && r.GetAttribute("name") is { Length: > 0 } name)
                    {
                        if (empty) rows.Add(new Entry(name, Class(classes), database, null));
                        else { material = name; materialDepth = depth; density = null; physDepth = -1; }
                    }
                }
                else if (ln == "physicalproperties" && depth == materialDepth + 1 && !empty) physDepth = depth;
                else if (physDepth >= 0 && depth == physDepth + 1 && ln == "DENS") density = Number(r.GetAttribute("value"));
            }
            else if (r.NodeType == XmlNodeType.EndElement)
            {
                string ln = r.LocalName;
                int depth = r.Depth;
                if (material is not null)
                {
                    if (ln == "physicalproperties" && depth == physDepth) physDepth = -1;
                    else if (ln == "material" && depth == materialDepth)
                    {
                        rows.Add(new Entry(material, Class(classes), database, density));
                        material = null;
                    }
                }
                else if (ln == "classification" && classes.Count > 0 && classes.Peek().Depth == depth) classes.Pop();
            }
        }
        return rows;
    }

    private static string Class(Stack<(string Name, int Depth)> classes) => classes.Count > 0 ? classes.Peek().Name : "";

    private static double? Number(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v > 0 ? v : null;

    /// <summary>Up to <paramref name="max"/> names closest to <paramref name="query"/>: shared words first, then spelling.</summary>
    public static List<string> Suggest(IEnumerable<string> names, string query, int max = 5)
    {
        string q = Squash(query);
        var qt = Words(query);
        var scored = new List<(string Name, double Score, int Dist)>();
        foreach (var n in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string s = Squash(n);
            var nt = Words(n);
            double score = q.Length > 0 && (s.Contains(q, StringComparison.Ordinal) || (s.Length >= 3 && q.Contains(s, StringComparison.Ordinal))) ? 2 : 0;
            foreach (var a in qt)
            {
                double best = 0;
                foreach (var b in nt)
                {
                    double sim = a == b ? 1
                        : (a.Length >= 2 && b.StartsWith(a, StringComparison.Ordinal)) || (b.Length >= 2 && a.StartsWith(b, StringComparison.Ordinal)) ? 0.7
                        : Similarity(a, b);
                    best = Math.Max(best, sim);
                }
                score += best;
            }
            scored.Add((n, score, Levenshtein(q, s)));
        }
        var ranked = scored.Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Dist)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Name).Take(max).ToList();
        if (ranked.Count < max)
            ranked.AddRange(scored.Where(x => x.Score == 0 && x.Dist <= Math.Max(2, q.Length / 3)).OrderBy(x => x.Dist)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.Name).Take(max - ranked.Count));
        return ranked;
    }

    private static string Squash(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static List<string> Words(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();

    private static double Similarity(string a, string b)
    {
        if (Math.Min(a.Length, b.Length) < 2) return 0;
        double sim = 1 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        return sim >= 0.6 ? sim : 0;
    }

    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>Equation text handling: "\"lhs\" = expression", left-hand sides, references, literals and unit-less numbers.</summary>
internal static class EquationText
{
    private static readonly Regex LhsRx = new(@"^\s*""([^""]+)""\s*=", RegexOptions.CultureInvariant);
    private static readonly Regex QuotedRx = new(@"""([^""]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex LiteralRx = new(@"^\s*([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*([A-Za-zµ°'""]*)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex DocSuffixRx = new(@"\.(part|assembly|sldprt|sldasm)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The left-hand side of an equation (without quotes), or null.</summary>
    public static string? Lhs(string text)
    {
        var m = LhsRx.Match(text);
        if (m.Success) return m.Groups[1].Value.Trim();
        int eq = text.IndexOf('=');
        return eq <= 0 ? null : text[..eq].Trim().Trim('"').Trim();
    }

    /// <summary>A user left-hand side: quotes stripped, "D1@Sketch1@Part1.Part" shortened to "D1@Sketch1".</summary>
    public static string NormalizeLhs(string? lhs)
    {
        string s = (lhs ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (s.Length == 0)
            throw SwException.InvalidArgument("'lhs' is required: a global variable name (W) or a dimension name (D1@Sketch1).");
        if (s.Contains('"') || s.Contains('=') || s.Any(char.IsControl))
            throw SwException.InvalidArgument($"'lhs' must be a bare name without quotes or '=' (got {lhs}).", "E.g. lhs W with expression 60mm.");
        var parts = s.Split('@');
        if (parts.Length >= 3 && DocSuffixRx.IsMatch(parts[^1])) s = string.Join('@', parts[..^1]);
        return s;
    }

    /// <summary>A user right-hand side, checked for the obvious mistakes.</summary>
    public static string Expression(string? expression)
    {
        string e = (expression ?? "").Trim();
        if (e.Length == 0) throw SwException.InvalidArgument("'expression' is required, e.g. 60mm or \"W\" / 2.");
        if (e.Contains('\n') || e.Contains('\r')) throw SwException.InvalidArgument("'expression' must be a single line.");
        if (LhsRx.IsMatch(e))
            throw SwException.InvalidArgument("'expression' is only the right-hand side (e.g. 60mm); the name goes in 'lhs'.");
        if (e.Count(c => c == '"') % 2 != 0)
            throw SwException.InvalidArgument($"'expression' has an unmatched double quote: {e}", "Names go in double quotes (\"W\"); write inches as 'in'.");
        return e;
    }

    public static string Build(string lhs, string expression) => $"\"{lhs}\" = {expression}";

    /// <summary>Same left-hand side: letter case and a trailing document qualifier ("@Part1.Part") are ignored.</summary>
    public static bool SameLhs(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

    private static string Key(string lhs)
    {
        string s = lhs.Trim().Trim('"').Trim();
        var parts = s.Split('@');
        return parts.Length >= 3 && DocSuffixRx.IsMatch(parts[^1]) ? string.Join('@', parts[..^1]) : s;
    }

    /// <summary>Equal apart from white space and letter case.</summary>
    public static bool SameText(string a, string b) => Squash(a) == Squash(b);

    private static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The quoted names used on the right-hand side.</summary>
    public static List<string> References(string text)
    {
        var m = LhsRx.Match(text);
        int start = m.Success ? m.Length : Math.Max(0, text.IndexOf('=') + 1);
        return QuotedRx.Matches(text[start..]).Select(x => x.Groups[1].Value.Trim()).ToList();
    }

    /// <summary>Quoted words that are values, not names (feature suppression equations).</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase) { "suppressed", "unsuppressed" };

    /// <summary>The names a right-hand side uses (quoted, without the suppression keywords).</summary>
    public static List<string> ExpressionReferences(string expression) =>
        QuotedRx.Matches(expression).Select(x => x.Groups[1].Value.Trim()).Where(n => n.Length > 0 && !Keywords.Contains(n)).ToList();

    /// <summary>
    /// A name SOLIDWORKS resolves in another document or component ("D1@Sketch1@bracket-1.Part", "W@Part1&lt;1&gt;.Part"),
    /// or a file property ("SW-Mass@Part1.SLDPRT"): not checked against this document.
    /// </summary>
    public static bool IsExternal(string name)
    {
        if (name.StartsWith("SW-", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = name.Split('@');
        return parts.Length >= 2 && (DocSuffixRx.IsMatch(parts[^1]) || parts[^1].Contains('<'));
    }

    /// <summary>
    /// The chain from <paramref name="lhs"/> through the equations its <paramref name="references"/> use back to itself
    /// (e.g. [W, H, W]), or null when the new equation creates no cycle. <paramref name="others"/> must not contain the
    /// equation being replaced.
    /// </summary>
    public static List<string>? Cycle(string lhs, IEnumerable<string> references, IEnumerable<(string Lhs, string Text)> others)
    {
        var uses = new Dictionary<string, (string Name, List<string> Refs)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (l, t) in others)
            uses.TryAdd(Key(l), (Key(l), References(t).Select(Key).ToList()));
        string start = Key(lhs);
        var path = new List<string> { start };
        var explored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in references.Select(Key))
            if (Walk(r)) return path;
        return null;

        bool Walk(string node)
        {
            if (string.Equals(node, start, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(start);
                return true;
            }
            if (!explored.Add(node) || !uses.TryGetValue(node, out var next)) return false;
            path.Add(next.Name); // the spelling of the equation, not of the reference
            foreach (var n in next.Refs)
                if (Walk(n)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    /// <summary>A single literal such as "60mm", "2.5in", "30deg" or "12": its value and lower-case unit ("" for none).</summary>
    public static (double Value, string Unit)? Literal(string expression)
    {
        var m = LiteralRx.Match(expression);
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return null;
        return (v, m.Groups[2].Value.ToLowerInvariant());
    }

    /// <summary>SI factor of an equation unit (meters or radians per unit), or null for a unit not known here.</summary>
    public static (double Factor, bool Angle)? UnitFactor(string unit) => unit switch
    {
        "mm" => (1e-3, false),
        "cm" => (1e-2, false),
        "m" => (1, false),
        "um" or "µm" => (1e-6, false),
        "nm" => (1e-9, false),
        "in" or "\"" => (0.0254, false),
        "ft" or "'" => (0.3048, false),
        "mil" => (2.54e-5, false),
        "deg" or "°" => (Math.PI / 180, true),
        "rad" => (1, true),
        _ => null,
    };

    /// <summary>
    /// Numbers written without a unit, outside quoted names, that are not plain factors (operands of *, / or ^):
    /// SOLIDWORKS reads them in the document's units.
    /// </summary>
    public static List<string> BareNumbers(string expression)
    {
        var found = new List<string>();
        string s = expression;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"')
            {
                int close = s.IndexOf('"', i + 1);
                i = close < 0 ? s.Length : close + 1;
                continue;
            }
            bool number = char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]));
            if (!number) { i++; continue; }
            if (i > 0 && (char.IsLetter(s[i - 1]) || s[i - 1] == '_'))
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++; // part of a name
                continue;
            }
            int start = i;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
            if (i + 1 < s.Length && (s[i] is 'e' or 'E') &&
                (char.IsDigit(s[i + 1]) || (s[i + 1] is '+' or '-' && i + 2 < s.Length && char.IsDigit(s[i + 2]))))
            {
                i += 2;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            string token = s[start..i];
            int k = i;
            while (k < s.Length && s[k] == ' ') k++;
            if (k < s.Length && (char.IsLetter(s[k]) || s[k] is '°' or '\'' || (s[k] == '"' && k == i)))
            {
                i = k; // a unit follows
                while (i < s.Length && (char.IsLetter(s[i]) || s[i] is '°' or '\'' or '"')) i++;
                continue;
            }
            int p = start - 1;
            while (p >= 0 && s[p] == ' ') p--;
            int q = i;
            while (q < s.Length && s[q] == ' ') q++;
            char before = p >= 0 ? s[p] : '\0', after = q < s.Length ? s[q] : '\0';
            if (before is '*' or '/' or '^' || after is '*' or '/' or '^') continue; // a factor or divisor
            if (!found.Contains(token)) found.Add(token);
        }
        return found;
    }
}
