using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Feature management and dimensions (toolset "features").</summary>
[McpServerToolType, Toolset("features")]
public sealed class FeatureManagementTools(SwSession session)
{
    internal const string DocArg = "Document (handle, title or path); default: the target document.";
    private readonly FeatureManagementService _svc = new(session);

    [McpServerTool(Name = "feature_info", Title = "Feature details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Feature details: type, rebuild error, suppressed/rolled back, parents, children, absorbed sketches, dimensions, and the " +
                 "definition of extrudes (end condition, depth, draft), revolves, fillets (radius, edges), chamfers, shells, Hole Wizard " +
                 "holes (standard, size, diameter, depth) and linear/circular patterns (counts, spacing). mm and degrees.")]
    public async Task<string> FeatureInfo(
        [Description("Feature name, e.g. Boss-Extrude1 (feature_list shows them).")] string feature,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.InfoAsync(doc, feature, cancellationToken));

    [McpServerTool(Name = "feature_delete", Title = "Delete features", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes features by name. By default only the features themselves: absorbed sketches stay, and the call refuses " +
                 "(listing them) when other features depend on them. dryRun lists what would go. Verified: the features are gone; " +
                 "returns everything removed and the volume change.")]
    public Task<string> FeatureDelete(
        IProgress<ProgressNotificationValue> progress,
        [Description("Feature names to delete.")] string[] features,
        [Description("Also delete the features that depend on them.")] bool deleteChildren = false,
        [Description("Also delete their absorbed sketches.")] bool deleteAbsorbed = false,
        [Description("Only report what would be deleted.")] bool dryRun = false,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DeleteAsync(doc, features, deleteChildren, deleteAbsorbed, dryRun, cancellationToken), progress, "feature_delete");

    [McpServerTool(Name = "feature_suppress_set", Title = "Suppress or unsuppress", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Suppresses or unsuppresses features (dependents follow a suppressed parent). Verified by reading the state back; " +
                 "returns the previous states, the other features that changed with them and the new volume.")]
    public Task<string> FeatureSuppressSet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Feature names.")] string[] features,
        [Description("true = suppress, false = unsuppress.")] bool suppressed,
        [Description("this (the active configuration) | all")] string configs = "this",
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.SuppressAsync(doc, features, suppressed, configs, cancellationToken), progress, "feature_suppress_set");

    [McpServerTool(Name = "feature_rename", Title = "Rename feature", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Renames a feature (names are unique). Verified by reading the name back; returns the previous name and the renamed " +
                 "dimensions (D1@NewName).")]
    public async Task<string> FeatureRename(
        [Description("Current feature name.")] string feature,
        [Description("New name (no '@').")] string newName,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.RenameAsync(doc, feature, newName, cancellationToken));

    [McpServerTool(Name = "feature_rollback", Title = "Move rollback bar", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Moves the rollback bar after a feature (before it with before=true), or back to the end. Verified with every feature's " +
                 "rolled-back state. Rolled-back features are inactive and new features are inserted at the bar: roll to 'end' when done.")]
    public Task<string> FeatureRollback(
        IProgress<ProgressNotificationValue> progress,
        [Description("Feature name, or 'end'.")] string to,
        [Description("Put the bar before the feature instead of after it.")] bool before = false,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.RollbackAsync(doc, to, before, cancellationToken), progress, "feature_rollback");

    [McpServerTool(Name = "dimension_list", Title = "List dimensions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Dimensions of a feature (with its absorbed sketch), of a sketch, or of the whole model: name for dimension_set " +
                 "(D1@Boss-Extrude1), value in mm, deg or count, and a driven flag for reference dimensions.")]
    public async Task<string> DimensionList(
        [Description("Feature name (default: all features).")] string? feature = null,
        [Description("Sketch name.")] string? sketch = null,
        [Description("Maximum rows (1-1000).")] int limit = 100,
        [Description("Rows to skip (paging).")] int offset = 0,
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.DimensionListAsync(doc, feature, sketch, limit, offset, cancellationToken));

    [McpServerTool(Name = "dimension_set", Title = "Set dimensions", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sets dimension values in one batch with one rebuild: mm for lengths, degrees for angles, whole numbers for counts. " +
                 "Refuses driven or equation-driven dimensions and an open sketch. Verified: every value reads back after the rebuild " +
                 "and no feature fails, else all values are restored. Returns previous and new values and the volume change.")]
    public Task<string> DimensionSet(
        IProgress<ProgressNotificationValue> progress,
        [Description("Values to set, e.g. [{name:'D1@Boss-Extrude1', value:30}].")] DimensionValue[] values,
        [Description("this (the active configuration) | all")] string configs = "this",
        [Description(DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.DimensionSetAsync(doc, values, configs, cancellationToken), progress, "dimension_set");
}

/// <summary>Rebuild, undo and units of a document (toolset "documents").</summary>
[McpServerToolType, Toolset("documents")]
public sealed class DocumentStateTools(SwSession session)
{
    private readonly FeatureManagementService _svc = new(session);

    [McpServerTool(Name = "doc_rebuild", Title = "Rebuild document", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rebuilds the document (force=true regenerates every feature) and returns the features with rebuild errors or " +
                 "warnings (What's Wrong), the duration and the model state.")]
    public Task<string> DocRebuild(
        IProgress<ProgressNotificationValue> progress,
        [Description("Rebuild every feature, not only the changed ones.")] bool force = false,
        [Description(FeatureManagementTools.DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.RebuildAsync(doc, force, cancellationToken), progress, "doc_rebuild");

    [McpServerTool(Name = "doc_undo", Title = "Undo", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Undoes the last SOLIDWORKS undo steps (one feature creation is one step), only in documents this server may edit, " +
                 "only while nobody else changed them since the server's last change, and never more steps than the server made. " +
                 "Returns the features removed or restored and the volume change.")]
    public Task<string> DocUndo(
        IProgress<ProgressNotificationValue> progress,
        [Description("Number of undo steps (1-50).")] int steps = 1,
        [Description(FeatureManagementTools.DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.UndoAsync(doc, steps, cancellationToken), progress, "doc_undo");

    [McpServerTool(Name = "doc_units_set", Title = "Set document units", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sets the unit system of a document this server created. Tool inputs and outputs stay mm and degrees. Refused, and " +
                 "set back, when equations with unitless numbers would change the geometry. Returns the previous units.")]
    public Task<string> DocUnitsSet(
        IProgress<ProgressNotificationValue> progress,
        [Description("mmgs | cgs | mks | ips")] string units,
        [Description(FeatureManagementTools.DocArg)] string? doc = null,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.UnitsSetAsync(doc, units, cancellationToken), progress, "doc_units_set");
}
