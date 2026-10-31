using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("properties")]
public sealed class PropertiesTools(SwSession session)
{
    private const string DocArg = "Document (handle, title or path); default: the target document.";
    private readonly PropertiesService _svc = new(session);

    // ------------------------------------------------------------------ materials

    [McpServerTool(Name = "material_list", Title = "List materials", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Installed materials from the SOLIDWORKS material databases: name, classification, database and density (kg/m³). " +
                 "Use the exact name with material_set.")]
    public async Task<string> MaterialList(
        [Description("Case-insensitive substring of the name, classification or database, e.g. \"steel\" or \"6061\".")] string? filter = null,
        [Description("Maximum rows (1-1000).")] int limit = 100,
        [Description("Rows to skip (paging).")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.MaterialListAsync(filter, limit, offset, cancellationToken));

    [McpServerTool(Name = "material_set", Title = "Set material", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Assigns a material to the part, e.g. \"AISI 1020\", \"6061 Alloy\", \"ABS\". The name is checked against the installed " +
                 "databases first (SOLIDWORKS silently ignores unknown names; a miss lists the closest ones). Verified: the material reads back " +
                 "and the mass properties show its density. Returns the previous material and the new mass.")]
    public Task<string> MaterialSet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Material name as material_list shows it.")] string name,
        [Description("Database from material_list (default: the one that has the name).")] string? database = null,
        [Description("Configuration (default: the active one).")] string? configuration = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MaterialSetAsync(doc, name, database, configuration, cancellationToken), progress, "material_set");

    [McpServerTool(Name = "material_add", Title = "Add a custom material", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a custom material (density, E, ν; optionally G, yield and tensile strength, thermal properties) to this server's " +
                 "material library in SOLIDWORKS' custom material folder, so material_set {name, database} and the simulations can use it. " +
                 "Verified: the library reads back with every value and material_list lists it. Only libraries this server wrote are changed.")]
    public Task<string> MaterialAdd(
        IProgress<ProgressNotificationValue> progress,
        [Description("Material name.")] string name,
        [Description("Density, kg/m³.")] double densityKgM3,
        [Description("Elastic modulus E, GPa.")] double elasticModulusGpa,
        [Description("Poisson's ratio ν.")] double poissonRatio,
        [Description("Shear modulus G, GPa (default E/(2(1+ν))).")] double? shearModulusGpa = null,
        [Description("Yield strength, MPa (factor of safety).")] double? yieldMpa = null,
        [Description("Tensile strength, MPa.")] double? tensileMpa = null,
        [Description("Thermal conductivity, W/(m·K).")] double? thermalConductivityWmK = null,
        [Description("Specific heat, J/(kg·K).")] double? specificHeatJkgK = null,
        [Description("Thermal expansion, 1/K.")] double? thermalExpansionPerK = null,
        [Description("Category shown in the material tree (default solidworks-mcp).")] string? classification = null,
        [Description("Library name (default solidworks-mcp).")] string? database = null,
        [Description("Replace a material of that name in the library.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.MaterialAddAsync(new MaterialAddRequest
        {
            Name = name, DensityKgM3 = densityKgM3, ElasticModulusGpa = elasticModulusGpa, PoissonRatio = poissonRatio, ShearModulusGpa = shearModulusGpa,
            YieldMpa = yieldMpa, TensileMpa = tensileMpa, ThermalConductivityWmK = thermalConductivityWmK, SpecificHeatJkgK = specificHeatJkgK,
            ThermalExpansionPerK = thermalExpansionPerK, Classification = classification, Database = database, Overwrite = overwrite,
        }, cancellationToken), progress, "material_add");

    // ------------------------------------------------------------------ custom properties

    [McpServerTool(Name = "prop_list", Title = "List custom properties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Custom properties (File > Properties): name, raw value, resolved value (when different, e.g. a linked dimension; numbers " +
                 "in the Windows locale) and type. File level by default, or one configuration's.")]
    public async Task<string> PropList(
        [Description("Configuration for configuration-specific properties (default: file level).")] string? configuration = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.PropListAsync(doc, configuration, cancellationToken));

    [McpServerTool(Name = "prop_set", Title = "Set custom property", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Adds or replaces a custom property. Text values may link to a dimension, e.g. \"\\\"D1@Boss-Extrude1\\\"\". " +
                 "Verified by reading the value and type back (on a mismatch the previous state is restored). Returns the previous value.")]
    public async Task<string> PropSet(
        [Description("Property name, e.g. \"PartNo\".")] string name,
        [Description("Value, e.g. \"ACME-001\"; number: \"12.5\"; yesno: \"Yes\"; date: \"2026-10-22\".")] string value,
        [Description("Configuration for a configuration-specific property (default: file level).")] string? configuration = null,
        [Description("text | number | date | yesno")] string type = "text",
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.PropSetAsync(doc, name, value, configuration, type, cancellationToken));

    [McpServerTool(Name = "prop_delete", Title = "Delete custom property", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes a custom property and verifies it is gone. Returns what was deleted.")]
    public async Task<string> PropDelete(
        [Description("Property name.")] string name,
        [Description("Configuration for a configuration-specific property (default: file level).")] string? configuration = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.PropDeleteAsync(doc, name, configuration, cancellationToken));

    // ------------------------------------------------------------------ equations

    [McpServerTool(Name = "equation_list", Title = "List equations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Equations and global variables: index, text, left-hand side, value (as SOLIDWORKS evaluates it, document units) " +
                 "and whether it is a global variable.")]
    public async Task<string> EquationList(
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.EquationListAsync(doc, cancellationToken));

    [McpServerTool(Name = "equation_set", Title = "Set equation", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Creates or updates the equation of a global variable (lhs W) or a dimension (lhs D1@Boss-Extrude1), e.g. W = 60mm, " +
                 "D1@Boss-Extrude1 = \"W\" / 2. Write units on numbers: bare numbers are document units (a warning says so). Unknown names " +
                 "and circular references are refused. Rebuilds, then verifies the value and the driven dimension; rolled back on failure. " +
                 "Returns the previous equation.")]
    public Task<string> EquationSet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Left-hand side without quotes: a global variable (W) or a dimension (D1@Sketch1).")] string lhs,
        [Description("Right-hand side, e.g. 60mm or \"W\" / 2 + 5mm; names in double quotes.")] string expression,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.EquationSetAsync(doc, lhs, expression, cancellationToken), progress, "equation_set");

    [McpServerTool(Name = "equation_delete", Title = "Delete equation", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes the equation of a left-hand side (a global variable, or a dimension's driving equation; the dimension stays). " +
                 "Refuses a global variable other equations use. Rebuilds and verifies it is gone.")]
    public Task<string> EquationDelete(
        IProgress<ProgressNotificationValue> progress,
        [Description("Left-hand side without quotes: W or D1@Sketch1.")] string lhs,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.EquationDeleteAsync(doc, lhs, cancellationToken), progress, "equation_delete");

    // ------------------------------------------------------------------ configurations

    [McpServerTool(Name = "config_list", Title = "List configurations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Configurations of the part or assembly and the active one.")]
    public async Task<string> ConfigList(
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ConfigListAsync(doc, cancellationToken));

    [McpServerTool(Name = "config_add", Title = "Add configuration", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a configuration and activates it (activate=false keeps the current one active). Verified by listing and by the " +
                 "active configuration; returns the previously active one.")]
    public Task<string> ConfigAdd(
        IProgress<ProgressNotificationValue> progress,
        [Description("New configuration name.")] string name,
        [Description("Make it the active configuration.")] bool activate = true,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ConfigAddAsync(doc, name, activate, cancellationToken), progress, "config_add");

    [McpServerTool(Name = "config_activate", Title = "Activate configuration", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Makes a configuration active (the model rebuilds in it). Verified; returns the previously active one.")]
    public Task<string> ConfigActivate(
        IProgress<ProgressNotificationValue> progress,
        [Description("Configuration name (config_list shows them).")] string name,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ConfigActivateAsync(doc, name, cancellationToken), progress, "config_activate");

    [McpServerTool(Name = "config_delete", Title = "Delete configuration", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes a configuration that is not active (activate another first). Verified by listing.")]
    public async Task<string> ConfigDelete(
        [Description("Configuration name.")] string name,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.ConfigDeleteAsync(doc, name, cancellationToken));

    // ------------------------------------------------------------------ appearance

    [McpServerTool(Name = "appearance_set", Title = "Set color", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Colors the whole part, one body or one face (color and optional transparency). Verified by reading the appearance " +
                 "back; returns the previous color.")]
    public async Task<string> AppearanceSet(
        [Description("[r, g, b], 0-255 each, e.g. [255, 0, 0] for red.")] int[] colorRgb,
        [Description("0 (opaque) to 1 (clear); default: keep the current one.")] double? transparency = null,
        [Description("A body ({id:'B1'} or {name:'Boss-Extrude1'}) or a face ({id:'F3'} or {at:[x,y,z]}); default: the whole part.")] EntityRef? target = null,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.AppearanceSetAsync(doc, colorRgb, transparency, target, cancellationToken));
}
