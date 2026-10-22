using System.Diagnostics;
using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

// ---------------------------------------------------------------------------------------------------- DTOs

/// <summary>The type-specific definition of a feature (IFeature.GetDefinition), in mm / degrees. Unset fields are omitted.</summary>
public sealed class FeatureDefinition
{
    /// <summary>extrude | revolve | fillet | chamfer | shell | hole | linear_pattern | circular_pattern.</summary>
    public required string Kind { get; init; }
    /// <summary>Revolve: one_direction | mid_plane | two_direction …; fillet: constant_radius …; chamfer: angle_distance …; hole: e.g. hole_blind.</summary>
    public string? Type { get; init; }
    public bool? Cut { get; init; }
    public bool? Thin { get; init; }
    /// <summary>blind | through_all | through_all_both | mid_plane | up_to_next | up_to_vertex | up_to_surface | offset_from_surface | up_to_body …</summary>
    public string? EndCondition { get; init; }
    /// <summary>Extrude depth (mid_plane: the total thickness); hole depth.</summary>
    public double? DepthMm { get; init; }
    public bool? BothDirections { get; init; }
    public string? EndCondition2 { get; init; }
    public double? Depth2Mm { get; init; }
    public bool? Reverse { get; init; }
    public double? DraftDeg { get; init; }
    public bool? DraftOutward { get; init; }
    /// <summary>Revolve angle; chamfer angle; circular pattern angle (the total when equalSpacing, else between instances).</summary>
    public double? AngleDeg { get; init; }
    public double? Angle2Deg { get; init; }
    public double? RadiusMm { get; init; }
    public int? EdgeCount { get; init; }
    public bool? Propagate { get; init; }
    public double? DistanceMm { get; init; }
    public double? Distance2Mm { get; init; }
    public double? ThicknessMm { get; init; }
    public bool? Outward { get; init; }
    public int? FacesRemoved { get; init; }
    public string? Standard { get; init; }
    public string? FastenerType { get; init; }
    /// <summary>Hole Wizard size, e.g. "Ø6.0" or "M6x1.0".</summary>
    public string? Size { get; init; }
    public double? DiameterMm { get; init; }
    public double? DrillAngleDeg { get; init; }
    /// <summary>Pattern: instances in direction 1 (the seed included); hole: number of hole positions.</summary>
    public int? Instances { get; init; }
    public double? SpacingMm { get; init; }
    public bool? EqualSpacing { get; init; }
    public int? Count2 { get; init; }
    public double? Spacing2Mm { get; init; }
    public bool? Reverse2 { get; init; }
}

public sealed class FeatureInfoResult : ToolResult
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int? Error { get; init; }
    public string? ErrorName { get; init; }
    public bool? Warning { get; init; }
    public bool? Suppressed { get; init; }
    public bool? RolledBack { get; init; }
    /// <summary>Features this one depends on (its sketch, the body it modifies, …).</summary>
    public IReadOnlyList<string>? Parents { get; init; }
    /// <summary>Features that depend on this one (deleting it needs deleteChildren).</summary>
    public IReadOnlyList<string>? Children { get; init; }
    /// <summary>Absorbed sub-features (profile sketches, hole sketches).</summary>
    public IReadOnlyList<string>? SubFeatures { get; init; }
    public int? FaceCount { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
    public FeatureDefinition? Definition { get; init; }
}

/// <summary>A feature that a delete removed (or, for a dry run, would remove).</summary>
public sealed class RemovedFeature
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    /// <summary>requested | child of X | absorbed by X | also removed.</summary>
    public required string Why { get; init; }
}

public sealed class FeatureDeleteResult : ToolResult
{
    public bool? DryRun { get; init; }
    public required IReadOnlyList<RemovedFeature> Removed { get; init; }
    /// <summary>Dry run without deleteChildren: the dependent features that make the real call refuse.</summary>
    public IReadOnlyList<string>? Dependents { get; init; }
    /// <summary>Absorbed sketches that stay in the model (deleteAbsorbed=false).</summary>
    public IReadOnlyList<string>? KeptAbsorbed { get; init; }
    public double? VolumeDeltaMm3 { get; init; }
    public ModelState? Model { get; init; }
}

public sealed class SuppressionChange
{
    public required string Name { get; init; }
    /// <summary>State in the active configuration before the call.</summary>
    public bool Previous { get; init; }
    public bool Suppressed { get; init; }
}

public sealed class FeatureSuppressResult : ToolResult
{
    /// <summary>this | all.</summary>
    public required string Configs { get; init; }
    public required IReadOnlyList<SuppressionChange> Features { get; init; }
    /// <summary>Other features whose state changed with them (dependents follow a suppressed parent).</summary>
    public IReadOnlyList<string>? AlsoChanged { get; init; }
    public double? VolumeDeltaMm3 { get; init; }
    public ModelState? Model { get; init; }
}

public sealed class FeatureRenameResult : ToolResult
{
    public required string Previous { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    /// <summary>The feature's dimensions under the new name (D1@NewName).</summary>
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
}

public sealed class FeatureRollbackResult : ToolResult
{
    /// <summary>end | after X | before X.</summary>
    public required string Bar { get; init; }
    /// <summary>Features now rolled back (inactive), in tree order.</summary>
    public IReadOnlyList<string>? RolledBack { get; init; }
    public double? VolumeDeltaMm3 { get; init; }
    public ModelState? Model { get; init; }
}

public sealed class DimensionListResult : ToolResult
{
    public required IReadOnlyList<DimensionInfo> Dimensions { get; init; }
    public int Total { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>One dimension_set entry.</summary>
[System.ComponentModel.Description("{name:'D1@Boss-Extrude1', value:30}")]
public sealed class DimensionValue
{
    [System.ComponentModel.Description("Dimension name from dimension_list, e.g. D1@Boss-Extrude1 or D2@Sketch1.")]
    public required string Name { get; set; }

    [System.ComponentModel.Description("New value: mm for lengths, degrees for angles, a whole number for counts.")]
    public required double Value { get; set; }
}

public sealed class DimensionChange
{
    public required string Name { get; init; }
    /// <summary>mm | deg | count.</summary>
    public required string Unit { get; init; }
    public double Previous { get; init; }
    /// <summary>The value read back after the rebuild.</summary>
    public double Value { get; init; }
}

public sealed class DimensionSetResult : ToolResult
{
    /// <summary>this | all.</summary>
    public required string Configs { get; init; }
    public required IReadOnlyList<DimensionChange> Changes { get; init; }
    public double? VolumeDeltaMm3 { get; init; }
    public ModelState? Model { get; init; }
}

/// <summary>A feature with a rebuild error or warning.</summary>
public sealed class FeatureError
{
    public required string Feature { get; init; }
    public required string Type { get; init; }
    public int Code { get; init; }
    /// <summary>The swFeatureError_e name, e.g. swFeatureErrorFilletRadiusTooBig.</summary>
    public required string Error { get; init; }
    public bool? Warning { get; init; }
}

public sealed class RebuildResult : ToolResult
{
    public bool? Forced { get; init; }
    /// <summary>What EditRebuild3 / ForceRebuild3 returned.</summary>
    public bool Rebuilt { get; init; }
    public required IReadOnlyList<FeatureError> Errors { get; init; }
    public double DurationS { get; init; }
    public ModelState? Model { get; init; }
}

public sealed class UndoResult : ToolResult
{
    public int Steps { get; init; }
    /// <summary>Features that disappeared.</summary>
    public IReadOnlyList<string>? Removed { get; init; }
    /// <summary>Features that came back (an undone delete).</summary>
    public IReadOnlyList<string>? Restored { get; init; }
    /// <summary>"old → new" for undone renames.</summary>
    public IReadOnlyList<string>? Renamed { get; init; }
    public double? VolumeDeltaMm3 { get; init; }
    public ModelState? Model { get; init; }
    /// <summary>The sketch that is open again when the undone step was leaving it.</summary>
    public string? OpenSketch { get; init; }
}

public sealed class UnitsResult : ToolResult
{
    public required string Previous { get; init; }
    public required string Units { get; init; }
    public bool Changed { get; init; }
    public string? Message { get; init; }
}

// ---------------------------------------------------------------------------------------------------- service

/// <summary>
/// Feature management: read a feature's definition, delete / suppress / rename features, move the rollback bar, list and
/// set dimensions, and the document-level rebuild, undo and units tools. Nothing here creates features, so instead of
/// <see cref="FeatureVerifier.Check"/> every change is read back, and a change that doesn't read back is restored.
/// </summary>
public sealed class FeatureManagementService(SwSession session)
{
    private const double VolEps = 1e-13; // m³, as FeatureVerifier

    /// <summary>Calls that rebuild the model get at least 120 s (progress heartbeats keep clients waiting).</summary>
    private TimeSpan LongTimeout => session.Options.DefaultTimeout > TimeSpan.FromSeconds(120)
        ? session.Options.DefaultTimeout : TimeSpan.FromSeconds(120);

    // ------------------------------------------------------------------ feature_info

    public async Task<FeatureInfoResult> InfoAsync(string? doc, string feature, CancellationToken ct = default)
    {
        string name = RequireName(feature, "feature");
        return await session.RunAsync("feature_info", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var f = Find(d, name, "feature", planeAliases: true);
            int err = f.GetErrorCode2(out bool warn);
            var dims = FeatureVerifier.Dimensions(f);
            int faces = Try(() => f.GetFaceCount()) ?? 0;
            return new FeatureInfoResult
            {
                Doc = ctx.Title(d),
                Name = f.Name,
                Type = f.GetTypeName2(),
                Error = err == 0 ? null : err,
                ErrorName = err == 0 ? null : FeatureVerifier.ErrorName(err),
                Warning = err != 0 && warn ? true : null,
                Suppressed = IsSuppressed(f) ? true : null,
                RolledBack = IsRolledBack(f) ? true : null,
                Parents = NullIfEmpty(f.ParentFeatures().Select(p => p.Name)),
                Children = NullIfEmpty(f.ChildFeatures().Select(c => c.Name)),
                SubFeatures = NullIfEmpty(Tree.Children(f).Select(c => c.Name)),
                FaceCount = faces > 0 ? faces : null,
                Dimensions = dims.Count == 0 ? null : dims,
                Definition = ReadDefinition(f),
            };
        }, RunOptions.Read(doc), ct).ConfigureAwait(false);
    }

    /// <summary>The type-specific definition (null for types without a reader). Read-only: no AccessSelections.</summary>
    private static FeatureDefinition? ReadDefinition(IFeature f)
    {
        object? def;
        try { def = f.Definition(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
        return def switch
        {
            IExtrudeFeatureData2 x => Extrude(x),
            IRevolveFeatureData2 r => Revolve(r),
            ISimpleFilletFeatureData2 s => Fillet(s),
            IChamferFeatureData2 c => Chamfer(c),
            IShellFeatureData sh => Shell(sh),
            IWizardHoleFeatureData2 h => Hole(h),
            ILinearPatternFeatureData lp => LinearPattern(lp),
            ICircularPatternFeatureData cp => CircularPattern(cp),
            _ => null,
        };
    }

    private static FeatureDefinition Extrude(IExtrudeFeatureData2 x)
    {
        int end1 = Try(() => x.GetEndCondition(true)) ?? -1;
        bool both = Try(() => x.BothDirections) == true;
        int? end2 = both ? Try(() => x.GetEndCondition(false)) : null;
        bool draft = Try(() => x.GetDraftWhileExtruding(true)) == true;
        return new FeatureDefinition
        {
            Kind = "extrude",
            Cut = Try(() => x.IsBossFeature()) == false ? true : null,
            Thin = Try(() => x.IsThinFeature()) == true ? true : null,
            EndCondition = FeatureManagementLogic.EndConditionName(end1),
            DepthMm = FeatureManagementLogic.HasDepth(end1) ? Mm(Try(() => x.GetDepth(true))) : null,
            BothDirections = both ? true : null,
            EndCondition2 = end2 is int e2 ? FeatureManagementLogic.EndConditionName(e2) : null,
            Depth2Mm = end2 is int e && FeatureManagementLogic.HasDepth(e) ? Mm(Try(() => x.GetDepth(false))) : null,
            Reverse = Try(() => x.ReverseDirection) == true ? true : null,
            DraftDeg = draft ? Deg(Try(() => x.GetDraftAngle(true))) : null,
            DraftOutward = draft && Try(() => x.GetDraftOutward(true)) == true ? true : null,
        };
    }

    private static FeatureDefinition Revolve(IRevolveFeatureData2 r)
    {
        int type = Try(() => r.Type) ?? -1;
        bool two = type is (int)swRevolveType_e.swRevolveTypeTwoDirection or (int)swRevolveType_e.swRevolveTypeTwoDirection360Degrees;
        return new FeatureDefinition
        {
            Kind = "revolve",
            Type = FeatureManagementLogic.RevolveTypeName(type),
            Cut = Try(() => r.IsBossFeature()) == false ? true : null,
            Thin = Try(() => r.IsThinFeature()) == true ? true : null,
            AngleDeg = Deg(Try(() => r.GetRevolutionAngle(true))),
            Angle2Deg = two ? Deg(Try(() => r.GetRevolutionAngle(false))) : null,
            Reverse = Try(() => r.ReverseDirection) == true ? true : null,
        };
    }

    private static FeatureDefinition Fillet(ISimpleFilletFeatureData2 s) => new()
    {
        Kind = "fillet",
        Type = FeatureManagementLogic.FilletTypeName(Try(() => s.Type) ?? -1),
        RadiusMm = PosMm(Try(() => s.DefaultRadius)),
        // Without AccessSelections (a read must not roll the model back); unverified whether the count needs it.
        EdgeCount = Try(() => s.GetEdgeCount()) is int n and > 0 ? n : null,
        Propagate = Try(() => s.PropagateToTangentFaces) == true ? true : null,
    };

    private static FeatureDefinition Chamfer(IChamferFeatureData2 c)
    {
        int type = Try(() => c.Type) ?? -1;
        return new FeatureDefinition
        {
            Kind = "chamfer",
            Type = FeatureManagementLogic.ChamferTypeName(type),
            DistanceMm = PosMm(Try(() => c.GetEdgeChamferDistance(0))),
            Distance2Mm = type == (int)swChamferType_e.swChamferDistanceDistance ? PosMm(Try(() => c.GetEdgeChamferDistance(1))) : null,
            AngleDeg = type == (int)swChamferType_e.swChamferAngleDistance ? Deg(Try(() => c.EdgeChamferAngle)) : null,
            EdgeCount = Try(() => c.GetEdgeCount()) is int n and > 0 ? n : null,
            Propagate = Try(() => c.TangentPropagation) == true ? true : null,
        };
    }

    private static FeatureDefinition Shell(IShellFeatureData s) => new()
    {
        Kind = "shell",
        ThicknessMm = PosMm(Try(() => s.Thickness)),
        Outward = Try(() => s.Direction) is int dir && dir != 0 ? true : null, // unverified: 0 = inward
        FacesRemoved = Try(() => s.FacesRemovedCount),
    };

    private static FeatureDefinition Hole(IWizardHoleFeatureData2 h)
    {
        int end = Try(() => h.EndCondition) ?? -1;
        double? dia = PosMm(Try(() => h.Diameter)) ?? PosMm(Try(() => h.HoleDiameter));
        return new FeatureDefinition
        {
            Kind = "hole",
            Type = FeatureManagementLogic.HoleTypeName(Try(() => h.Type) ?? -1),
            Standard = TryS(() => h.Standard) ?? FeatureManagementLogic.HoleStandardName(Try(() => h.Standard2) ?? -1),
            FastenerType = TryS(() => h.FastenerType),
            Size = TryS(() => h.FastenerSize),
            DiameterMm = dia,
            EndCondition = end < 0 ? null : FeatureManagementLogic.EndConditionName(end),
            DepthMm = end == (int)swEndConditions_e.swEndCondBlind ? PosMm(Try(() => h.Depth)) ?? PosMm(Try(() => h.HoleDepth)) : null,
            DrillAngleDeg = Try(() => h.DrillAngle) is double a && a > 0 ? Deg(a) : null,
            Instances = Try(() => h.GetSketchPointCount()) is int n and > 0 ? n : null,
        };
    }

    private static FeatureDefinition LinearPattern(ILinearPatternFeatureData p)
    {
        bool two = Try(() => p.IsDirection2Specified()) == true;
        return new FeatureDefinition
        {
            Kind = "linear_pattern",
            Instances = Try(() => p.D1TotalInstances),
            SpacingMm = PosMm(Try(() => p.D1Spacing)),
            Reverse = Try(() => p.D1ReverseDirection) == true ? true : null,
            Count2 = two ? Try(() => p.D2TotalInstances) : null,
            Spacing2Mm = two ? PosMm(Try(() => p.D2Spacing)) : null,
            Reverse2 = two && Try(() => p.D2ReverseDirection) == true ? true : null,
        };
    }

    private static FeatureDefinition CircularPattern(ICircularPatternFeatureData p)
    {
        bool two = Try(() => p.Direction2) == true;
        return new FeatureDefinition
        {
            Kind = "circular_pattern",
            Instances = Try(() => p.TotalInstances),
            AngleDeg = Deg(Try(() => p.Spacing)),
            EqualSpacing = Try(() => p.EqualSpacing) == true ? true : null,
            Reverse = Try(() => p.ReverseDirection) == true ? true : null,
            Count2 = two ? Try(() => p.TotalInstances2) : null,
            Angle2Deg = two ? Deg(Try(() => p.Spacing2)) : null,
        };
    }

    // ------------------------------------------------------------------ feature_delete

    private sealed record Dependent(IFeature Feature, int Id, string Parent);

    /// <summary>A feature a delete is expected to remove, and why (requested | child of X | absorbed by X).</summary>
    private sealed record Planned(IFeature Feature, int Id, string Why);

    public async Task<FeatureDeleteResult> DeleteAsync(string? doc, IReadOnlyList<string> features, bool deleteChildren = false,
        bool deleteAbsorbed = false, bool dryRun = false, CancellationToken ct = default)
    {
        var names = FeatureManagementLogic.RequireNames(features, "features");
        // A dry run changes nothing, so it may look at any document (the user's too).
        var options = (dryRun ? RunOptions.Read(doc) : RunOptions.Write(doc)) with { Timeout = LongTimeout };
        return await session.RunAsync("feature_delete", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            RequireNoOpenSketch(ctx, d, "features can't be deleted while it is open");
            var targets = DistinctById(names.Select(n => Find(d, n, "features")));
            foreach (var t in targets) RefuseSystem(d, t, "deleted");
            var plan = DeletePlan(targets, deleteChildren, deleteAbsorbed, out var dependents, out var keepAbsorbed);

            if (dryRun)
                return new FeatureDeleteResult
                {
                    Doc = ctx.Title(d),
                    DryRun = true,
                    Removed = plan.Select(p => Row(p.Feature, p.Why)).ToList(),
                    Dependents = !deleteChildren && dependents.Count > 0 ? dependents.Select(x => x.Feature.Name).ToList() : null,
                    KeptAbsorbed = keepAbsorbed.Count > 0 ? keepAbsorbed.Select(x => x.Feature.Name).ToList() : null,
                };
            if (dependents.Count > 0 && !deleteChildren)
                throw SwException.InvalidArgument(
                    $"{Quote(targets.Select(t => t.Name))} {(targets.Count == 1 ? "has" : "have")} dependent features: " +
                    $"{Quote(dependents.Select(x => x.Feature.Name), 10)}. Deleting only the requested feature(s) would leave them broken.",
                    "Pass deleteChildren=true to delete them too (dryRun=true lists everything first), or delete or suppress them yourself.");

            var reasons = plan.ToDictionary(p => p.Id, p => p.Why);
            var before = Snapshot.Take(d);
            var known = before.Features.ToDictionary(kv => kv.Key, kv => (Name: SafeName(kv.Value), Type: SafeType(kv.Value)));
            var owners = SubFeatureOwners(d);
            var errorsBefore = ErrorMap(d);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false); // the What's Wrong box is modal
            bool ok;
            d.ClearSelection2(true);
            try
            {
                foreach (var t in targets)
                    if (!t.Select2(true, 0))
                        throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{t.Name}'; nothing was deleted.");
                int opt = (deleteChildren ? (int)swDeleteSelectionOptions_e.swDelete_Children : 0) |
                          (deleteAbsorbed ? (int)swDeleteSelectionOptions_e.swDelete_Absorbed : 0);
                ok = d.Extension.DeleteSelection2(opt);
            }
            finally
            {
                d.ClearSelection2(true);
            }
            RebuildIfNeeded(d);

            var after = Snapshot.Take(d);
            var gone = known.Keys.Where(id => !after.Features.ContainsKey(id)).ToHashSet();
            if (gone.Count == 0)
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS deleted nothing (DeleteSelection2 returned {ok}).",
                    "feature_info shows the feature's state; system features can't be deleted.");
            var removed = new List<RemovedFeature>();
            foreach (var id in known.Keys.Where(gone.Contains))
            {
                string why;
                if (reasons.TryGetValue(id, out var r)) why = r;
                else
                {
                    why = owners.TryGetValue(id, out var o) && gone.Contains(o.Id) ? $"absorbed by {o.Name}" : "also removed";
                    ctx.Warn($"SOLIDWORKS also removed '{known[id].Name}' ({why}), which was not planned.");
                }
                removed.Add(new RemovedFeature { Name = known[id].Name, Type = known[id].Type, Why = why });
            }
            foreach (var p in plan)
                if (!gone.Contains(p.Id) && p.Why != "requested")
                    ctx.Warn($"'{known.GetValueOrDefault(p.Id).Name}' ({p.Why}) was expected to go but is still in the model.");
            var left = targets.Where(t => !gone.Contains(SafeId(t))).Select(t => known.GetValueOrDefault(SafeId(t)).Name ?? "?").ToList();
            if (left.Count > 0)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"{Quote(left)} still exist after the delete (removed: {Quote(removed.Select(x => x.Name), 10)}).",
                    "Check feature_list; SOLIDWORKS may refuse to delete a feature that others reference.");
            foreach (var e in NewErrors(errorsBefore, ErrorMap(d)))
                ctx.Warn($"'{e.Feature}' now has rebuild {(e.Warning == true ? "warning" : "error")} {e.Code} ({e.Error}).");
            var entry = ctx.DocEntry;
            if (entry.LastSketch is { } last && removed.Any(x => x.Name == last)) entry.LastSketch = null;
            var kept = keepAbsorbed.Where(a => !gone.Contains(a.Id)).Select(a => known.GetValueOrDefault(a.Id).Name ?? "?").ToList();
            bool part = IsPart(d);
            return new FeatureDeleteResult
            {
                Doc = ctx.Title(d),
                Removed = removed,
                KeptAbsorbed = kept.Count == 0 ? null : kept,
                VolumeDeltaMm3 = part ? Units.Mm3(after.Volume - before.Volume) : null,
                Model = part ? after.ToState() : null,
            };
        }, options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What deleting <paramref name="targets"/> removes: the targets; every feature that depends on them, transitively
    /// (only with deleteChildren, else <paramref name="dependents"/> makes the call refuse); and the absorbed sub-features
    /// (profile sketches) of everything deleted (only with deleteAbsorbed, else they are returned in <paramref name="kept"/>).
    /// </summary>
    private static List<Planned> DeletePlan(IReadOnlyList<IFeature> targets, bool deleteChildren, bool deleteAbsorbed,
        out List<Dependent> dependents, out List<Planned> kept)
    {
        var plan = targets.Select(t => new Planned(t, SafeId(t), "requested")).ToList();
        var ids = plan.Select(p => p.Id).ToHashSet();
        var absorbed = new List<Planned>();
        void AbsorbedOf(IFeature owner)
        {
            foreach (var c in Tree.Children(owner))
                if (SafeId(c) is int id && id >= 0 && ids.Add(id)) absorbed.Add(new Planned(c, id, $"absorbed by {owner.Name}"));
        }
        foreach (var t in targets) AbsorbedOf(t);
        dependents = Dependents(targets, ids);
        if (deleteChildren)
        {
            foreach (var x in dependents)
                if (ids.Add(x.Id)) plan.Add(new Planned(x.Feature, x.Id, $"child of {x.Parent}"));
            // A deleted child's own sketch is listed too, so the dry run never under-reports what may go.
            foreach (var x in dependents) AbsorbedOf(x.Feature);
        }
        if (deleteAbsorbed) plan.AddRange(absorbed);
        kept = deleteAbsorbed ? [] : absorbed;
        return plan;
    }

    /// <summary>Every feature that depends on <paramref name="roots"/>, transitively (IFeature.GetChildren), with its parent.</summary>
    private static List<Dependent> Dependents(IReadOnlyList<IFeature> roots, IEnumerable<int> exclude)
    {
        var seen = new HashSet<int>(exclude);
        var queue = new Queue<IFeature>(roots);
        var list = new List<Dependent>();
        while (queue.Count > 0 && list.Count < 5000)
        {
            var f = queue.Dequeue();
            string parent = f.Name;
            foreach (var c in f.ChildFeatures())
            {
                int id = SafeId(c);
                if (id < 0 || !seen.Add(id)) continue;
                list.Add(new Dependent(c, id, parent));
                queue.Enqueue(c);
            }
        }
        return list;
    }

    /// <summary>Sub-feature id → its owner (id, name), from the tree before a change.</summary>
    private static Dictionary<int, (int Id, string Name)> SubFeatureOwners(IModelDoc2 d)
    {
        var map = new Dictionary<int, (int, string)>();
        foreach (var f in Tree.WalkAll(d))
        {
            int owner = SafeId(f);
            if (owner < 0) continue;
            foreach (var c in Tree.Children(f))
                if (SafeId(c) is int id && id >= 0) map.TryAdd(id, (owner, f.Name));
        }
        return map;
    }

    private static RemovedFeature Row(IFeature f, string why) => new() { Name = f.Name, Type = f.GetTypeName2(), Why = why };

    // ------------------------------------------------------------------ feature_suppress_set

    public async Task<FeatureSuppressResult> SuppressAsync(string? doc, IReadOnlyList<string> features, bool suppressed,
        string? configs = "this", CancellationToken ct = default)
    {
        var names = FeatureManagementLogic.RequireNames(features, "features");
        int cfg = FeatureManagementLogic.ParseConfigs(configs);
        return await session.RunAsync("feature_suppress_set", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            RequireNoOpenSketch(ctx, d, "changing suppression rebuilds the model, which would close it");
            var targets = DistinctById(names.Select(n => Find(d, n, "features")));
            foreach (var t in targets) RefuseSystem(d, t, suppressed ? "suppressed" : "unsuppressed");
            var order = TreeOrder(d);
            var stateBefore = SuppressionMap(d);
            var before = Snapshot.Take(d);
            var errorsBefore = ErrorMap(d);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);

            // Children before parents when suppressing; parents before children when unsuppressing.
            int Pos(IFeature f) => order.TryGetValue(SafeId(f), out int i) ? i : int.MaxValue;
            var ordered = suppressed ? targets.OrderByDescending(Pos).ToList() : targets.OrderBy(Pos).ToList();
            int action = suppressed ? (int)swFeatureSuppressionAction_e.swSuppressFeature : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;
            var previous = targets.ToDictionary(SafeId, t => stateBefore.TryGetValue(SafeId(t), out var s) ? s.Suppressed : IsSuppressed(t));
            var changed = new List<IFeature>();
            foreach (var t in ordered)
            {
                if (cfg == 1 && previous[SafeId(t)] == suppressed) continue; // already so in the active configuration
                if (t.SetSuppression2(action, cfg, ComArgs.Empty)) changed.Add(t);
            }
            RebuildIfNeeded(d);

            var wrong = targets.Where(t => !InState(t, cfg, suppressed)).ToList();
            if (wrong.Count > 0)
            {
                foreach (var t in Enumerable.Reverse(changed))
                {
                    int back = previous[SafeId(t)] ? (int)swFeatureSuppressionAction_e.swSuppressFeature : (int)swFeatureSuppressionAction_e.swUnSuppressFeature;
                    try { t.SetSuppression2(back, cfg, ComArgs.Empty); }
                    catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* reported below */ }
                }
                try { RebuildIfNeeded(d); }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* the error below still holds */ }
                var blockers = suppressed ? [] : wrong.SelectMany(t => t.ParentFeatures()).Where(IsSuppressed).Select(p => p.Name).Distinct().ToList();
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"{Quote(wrong.Select(t => t.Name))} did not become {(suppressed ? "suppressed" : "unsuppressed")}" +
                    (blockers.Count > 0 ? $" (suppressed parents: {Quote(blockers)})" : "") +
                    $". {(changed.Count == 0 ? "Nothing was changed." : "The other features were set back.")}",
                    blockers.Count > 0 ? "Unsuppress the parents in the same call (they are processed first)." : "feature_info shows the feature's parents and state.");
            }

            var after = Snapshot.Take(d);
            var targetIds = targets.Select(SafeId).ToHashSet();
            var also = SuppressionMap(d)
                .Where(kv => !targetIds.Contains(kv.Key) && stateBefore.TryGetValue(kv.Key, out var b) && b.Suppressed != kv.Value.Suppressed)
                .Select(kv => kv.Value.Name).ToList();
            foreach (var e in NewErrors(errorsBefore, ErrorMap(d)))
                ctx.Warn($"'{e.Feature}' now has rebuild {(e.Warning == true ? "warning" : "error")} {e.Code} ({e.Error}).");
            bool part = IsPart(d);
            return new FeatureSuppressResult
            {
                Doc = ctx.Title(d),
                Configs = FeatureManagementLogic.ConfigsName(cfg),
                Features = targets.Select(t => new SuppressionChange { Name = t.Name, Previous = previous[SafeId(t)], Suppressed = IsSuppressed(t) }).ToList(),
                AlsoChanged = also.Count == 0 ? null : also,
                VolumeDeltaMm3 = part ? Units.Mm3(after.Volume - before.Volume) : null,
                Model = part ? after.ToState() : null,
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);
    }

    /// <summary>True when <paramref name="f"/> has the wanted state in the active configuration, or in every one (cfg 2).</summary>
    private static bool InState(IFeature f, int cfg, bool suppressed)
    {
        if (IsSuppressed(f) != suppressed) return false;
        if (cfg != 2) return true;
        bool[] all;
        try { all = f.SuppressionStates(cfg); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return true; } // the active configuration was checked
        return all.All(s => s == suppressed);
    }

    // ------------------------------------------------------------------ feature_rename

    public async Task<FeatureRenameResult> RenameAsync(string? doc, string feature, string newName, CancellationToken ct = default)
    {
        string name = RequireName(feature, "feature");
        string target = FeatureManagementLogic.ValidateNewName(newName);
        return await session.RunAsync("feature_rename", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var f = Find(d, name, "feature");
            string previous = f.Name;
            if (!string.Equals(previous, target, StringComparison.Ordinal))
            {
                var clash = EntityResolver.FeatureByName(d, target);
                if (clash is not null && SafeId(clash) != SafeId(f))
                    throw SwException.InvalidArgument($"A feature named '{clash.Name}' already exists.",
                        "Choose another name (feature names are unique, ignoring case).");
                f.Name = target;
                string now = f.Name;
                if (!string.Equals(now, target, StringComparison.Ordinal))
                {
                    if (!string.Equals(now, previous, StringComparison.Ordinal)) f.Name = previous;
                    throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS kept the name '{now}' instead of '{target}'; the name is unchanged.",
                        "The name may be invalid or reserved; try another one.");
                }
                var entry = ctx.DocEntry;
                if (entry.LastSketch == previous) entry.LastSketch = target;
                if (entry.ServerSketch == previous) entry.ServerSketch = target;
            }
            var dims = FeatureVerifier.Dimensions(f);
            return new FeatureRenameResult
            {
                Doc = ctx.Title(d),
                Previous = previous,
                Name = f.Name,
                Type = f.GetTypeName2(),
                Dimensions = dims.Count == 0 ? null : dims,
            };
        }, RunOptions.Write(doc), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ feature_rollback

    public async Task<FeatureRollbackResult> RollbackAsync(string? doc, string to, bool before = false, CancellationToken ct = default)
    {
        string target = RequireName(to, "to");
        bool toEnd = target.Equals("end", StringComparison.OrdinalIgnoreCase);
        if (toEnd && before) throw SwException.InvalidArgument("'before' needs a feature in 'to', not 'end'.");
        return await session.RunAsync("feature_rollback", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            RequireNoOpenSketch(ctx, d, "moving the rollback bar would close it");
            IFeature? f = toEnd ? null : Find(d, target, "to");
            bool origin = f is not null && f.GetTypeName2() == "OriginProfileFeature";
            if (f is not null && !origin) RefuseSystem(d, f, "a rollback position");
            if (origin && before) throw SwException.InvalidArgument("The rollback bar can't go before the origin.", "Use after the origin to roll back every feature.");
            var history = History(d);
            int first = toEnd ? history.Count : origin ? 0 : FirstRolledBack(history, f!, before);
            var snapBefore = Snapshot.Take(d);
            int stamp = d.GetUpdateStamp();
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false); // the What's Wrong box is modal

            int location = toEnd ? (int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd
                : before ? (int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature : (int)swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature;
            bool ok = d.FeatureManager.EditRollback(location, toEnd ? "" : f!.Name);

            history = History(d);
            var mismatch = history.Select((h, i) => (h, i)).Where(x => IsRolledBack(x.h) != (x.i >= first)).Select(x => x.h.Name).ToList();
            if (mismatch.Count > 0)
            {
                try { d.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToPreviousPosition, ""); }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* reported below */ }
                throw new SwException(ok ? ErrorCodes.VerifyFailed : ErrorCodes.FeatureFailed,
                    $"The rollback bar is not where requested (EditRollback returned {ok}); unexpected state: {Quote(mismatch, 8)}. " +
                    "Tried to move it back to its previous position.",
                    "feature_list shows each feature's rolledBack flag.");
            }
            if (!ok) ctx.Warn("EditRollback returned false, but every feature is in the requested state.");
            var rolled = history.Where(IsRolledBack).Select(h => h.Name).ToList();
            if (rolled.Count > 0)
                ctx.Warn($"{rolled.Count} feature(s) below the rollback bar are inactive, and new features are inserted at the bar. " +
                         "Call feature_rollback {to:'end'} when done.");
            var after = Snapshot.Take(d);
            NoUndoStep(ctx, d, stamp);
            return new FeatureRollbackResult
            {
                Doc = ctx.Title(d),
                Bar = toEnd ? "end" : $"{(before ? "before" : "after")} {f!.Name}",
                RolledBack = rolled.Count == 0 ? null : rolled,
                VolumeDeltaMm3 = Units.Mm3(after.Volume - snapBefore.Volume),
                Model = after.ToState(),
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);
    }

    /// <summary>The model history: features after the origin in tree order, without system folders.</summary>
    private static List<IFeature> History(IModelDoc2 d)
    {
        var all = Tree.Walk(d).ToList();
        int origin = all.FindIndex(x => x.GetTypeName2() == "OriginProfileFeature");
        return all.Skip(origin + 1).Where(x => !Tree.SystemTypes.Contains(x.GetTypeName2())).ToList();
    }

    /// <summary>Index in <paramref name="history"/> of the first feature that must be rolled back for this bar position.</summary>
    private static int FirstRolledBack(List<IFeature> history, IFeature f, bool before)
    {
        int id = SafeId(f);
        int i = history.FindIndex(h => SafeId(h) == id);
        if (i >= 0) return before ? i : i + 1;
        // An absorbed sub-feature: the bar goes just before its owner, so the owner is rolled back either way.
        var owner = TryRef(() => f.GetOwnerFeature());
        int j = owner is null ? -1 : history.FindIndex(h => SafeId(h) == SafeId(owner));
        if (j >= 0) return j;
        throw SwException.InvalidArgument($"'{f.Name}' is not part of the model history, so the rollback bar can't go there.",
            "Pass a feature after the origin (feature_list shows the order), or 'end'.");
    }

    // ------------------------------------------------------------------ dimension_list

    public async Task<DimensionListResult> DimensionListAsync(string? doc, string? feature = null, string? sketch = null,
        int limit = 100, int offset = 0, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000) throw SwException.InvalidArgument($"limit must be between 1 and 1000 (got {limit}).");
        if (offset < 0) throw SwException.InvalidArgument($"offset must be ≥ 0 (got {offset}).");
        return await session.RunAsync("dimension_list", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var scope = new List<IFeature>();
            if (!string.IsNullOrWhiteSpace(sketch))
            {
                var s = Find(d, sketch.Trim(), "sketch");
                if (EntityResolver.FeatureKind(s) != EntityKind.Sketch)
                    throw SwException.InvalidArgument($"'{s.Name}' is a {s.GetTypeName2()}, not a sketch.", "Pass it as 'feature' instead.");
                scope.Add(s);
            }
            if (!string.IsNullOrWhiteSpace(feature))
            {
                var f = Find(d, feature.Trim(), "feature", planeAliases: true);
                scope.Add(f);
                scope.AddRange(Tree.Children(f)); // the absorbed sketch carries the profile's dimensions
            }
            if (scope.Count == 0) scope.AddRange(Tree.WalkAll(d).Where(f => !Tree.SystemTypes.Contains(f.GetTypeName2())));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = new List<DimensionInfo>();
            foreach (var f in scope)
                foreach (var di in FeatureVerifier.Dimensions(f))
                    if (seen.Add(di.Name)) all.Add(di);
            var page = all.Skip(offset).Take(limit).ToList();
            return new DimensionListResult
            {
                Doc = ctx.Title(d),
                Dimensions = page,
                Total = all.Count,
                Truncated = offset + page.Count < all.Count ? true : null,
            };
        }, RunOptions.Read(doc), ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ dimension_set

    private sealed record PlannedValue(IDimension Dim, string Name, int Type, double Previous, double Api);

    public async Task<DimensionSetResult> DimensionSetAsync(string? doc, IReadOnlyList<DimensionValue> values, string? configs = "this",
        CancellationToken ct = default)
    {
        var items = FeatureManagementLogic.RequireValues(values);
        int cfg = FeatureManagementLogic.ParseConfigs(configs); // also a swSetValueInConfiguration_e value (1 this, 2 all)
        return await session.RunAsync("dimension_set", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            RequireNoOpenSketch(ctx, d, "dimension_set rebuilds the model, which would close it");

            // Resolve and check everything before changing anything.
            var byEquation = EquationTargets(d);
            var plan = new List<PlannedValue>();
            foreach (var (name, value) in items)
            {
                var dim = d.DimensionNamed(name)
                          ?? throw new SwException(ErrorCodes.EntityNotFound, $"No dimension named '{name}'.",
                              "dimension_list shows the names, e.g. D1@Boss-Extrude1 (feature) or D1@Sketch1 (sketch).");
                string full = FeatureVerifier.ShortName(dim.FullName);
                if (dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven || Try(() => dim.IsReference()) == true)
                    throw SwException.InvalidArgument($"'{full}' is a driven (reference) dimension: it reports a value but doesn't control the geometry.",
                        "Set the driving dimension instead (dimension_list marks driven ones).");
                if (byEquation.Contains(full) || byEquation.Contains(name))
                    throw SwException.InvalidArgument($"'{full}' is driven by an equation; the next rebuild would overwrite a direct change.",
                        "Change the equation instead.");
                if (dim.ReadOnly)
                    throw SwException.InvalidArgument($"'{full}' is read-only (driven by a design table, configuration or link?).");
                if (plan.Any(p => string.Equals(p.Name, full, StringComparison.OrdinalIgnoreCase)))
                    throw SwException.InvalidArgument($"'{full}' is listed twice.");
                int type = dim.GetType();
                plan.Add(new PlannedValue(dim, full, type, dim.SystemValue, FeatureManagementLogic.ToApi(type, value, full)));
            }

            var before = Snapshot.Take(d);
            var errorsBefore = ErrorMap(d);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var applied = new List<PlannedValue>();
            foreach (var p in plan)
            {
                int status = p.Dim.SetSystemValue3(p.Api, cfg, ComArgs.Empty);
                if (status != (int)swSetValueReturnStatus_e.swSetValue_Successful)
                {
                    string restored = RestoreValues(d, applied, cfg);
                    var e = FeatureManagementLogic.SetValueError(status, p.Name, FeatureManagementLogic.FromApi(p.Type, p.Api), FeatureManagementLogic.UnitOf(p.Type));
                    throw new SwException(e.Code, e.Message + " " + restored, e.Hint);
                }
                applied.Add(p);
            }
            d.EditRebuild3(); // one rebuild for the whole batch

            var mismatch = plan.Where(p => !FeatureManagementLogic.SameApi(p.Type, ReadBack(d, p), p.Api)).ToList();
            var newErrors = NewErrors(errorsBefore, ErrorMap(d));
            var broken = newErrors.Where(e => e.Warning != true).ToList();
            if (mismatch.Count > 0 || broken.Count > 0)
            {
                string got = string.Join("; ", mismatch.Select(p =>
                    $"'{p.Name}' reads {FeatureManagementLogic.FromApi(p.Type, ReadBack(d, p))} {FeatureManagementLogic.UnitOf(p.Type)} instead of {FeatureManagementLogic.FromApi(p.Type, p.Api)}"));
                string errs = string.Join("; ", broken.Take(6).Select(e => $"{e.Feature}: {e.Error}"));
                string restored = RestoreValues(d, plan, cfg);
                if (mismatch.Count > 0)
                    throw new SwException(ErrorCodes.VerifyFailed, $"After the rebuild {got}. {restored}",
                        "Something else drives the dimension (an equation, a configuration or a relation); check dimension_list and feature_info.");
                throw new SwException(ErrorCodes.RebuildError, $"The new value(s) break features: {errs}. {restored}",
                    "Use values that fit the geometry (e.g. a fillet radius smaller than the faces it joins).");
            }
            foreach (var e in newErrors) ctx.Warn($"'{e.Feature}' now has rebuild warning {e.Code} ({e.Error}).");

            var after = Snapshot.Take(d);
            bool part = IsPart(d);
            return new DimensionSetResult
            {
                Doc = ctx.Title(d),
                Configs = FeatureManagementLogic.ConfigsName(cfg),
                Changes = plan.Select(p => new DimensionChange
                {
                    Name = p.Name,
                    Unit = FeatureManagementLogic.UnitOf(p.Type),
                    Previous = FeatureManagementLogic.FromApi(p.Type, p.Previous),
                    Value = FeatureManagementLogic.FromApi(p.Type, ReadBack(d, p)),
                }).ToList(),
                VolumeDeltaMm3 = part ? Units.Mm3(after.Volume - before.Volume) : null,
                Model = part ? after.ToState() : null,
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);
    }

    /// <summary>Sets the previous values back, rebuilds and checks them; returns a sentence for the error message.</summary>
    private static string RestoreValues(IModelDoc2 d, IReadOnlyList<PlannedValue> items, int cfg)
    {
        if (items.Count == 0) return "Nothing was changed.";
        foreach (var p in items)
        {
            try { Fresh(d, p).SetSystemValue3(p.Previous, cfg, ComArgs.Empty); }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* checked below */ }
        }
        try { d.EditRebuild3(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* checked below */ }
        var bad = items.Where(p => !FeatureManagementLogic.SameApi(p.Type, ReadBack(d, p), p.Previous)).Select(p => p.Name).ToList();
        return bad.Count == 0 ? "The previous values were restored."
            : $"Restore incomplete: {Quote(bad)} still differ from their previous values.";
    }

    /// <summary>The dimension looked up again by name (an object from before a rebuild may be stale), else the old one.</summary>
    private static IDimension Fresh(IModelDoc2 d, PlannedValue p) => d.DimensionNamed(p.Name) ?? p.Dim;

    /// <summary>The dimension's current system value (m / rad / count), NaN when it can't be read.</summary>
    private static double ReadBack(IModelDoc2 d, PlannedValue p) => SystemValue(Fresh(d, p));

    private static double SystemValue(IDimension dim)
    {
        try { return dim.SystemValue; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return double.NaN; }
    }

    /// <summary>Names that equations assign (dimensions like D1@Boss-Extrude1 and global variables), suppressed ones excluded.</summary>
    private static HashSet<string> EquationTargets(IModelDoc2 d)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (text, suppressed) in d.EquationList())
            if (!suppressed && FeatureManagementLogic.EquationTarget(text) is { } t) set.Add(t);
        return set;
    }

    // ------------------------------------------------------------------ doc_rebuild

    public async Task<RebuildResult> RebuildAsync(string? doc, bool force = false, CancellationToken ct = default) =>
        await session.RunAsync("doc_rebuild", ctx =>
        {
            var d = ctx.Doc;
            RequireNoOpenSketch(ctx, d, "a rebuild would close it");
            int stamp = d.GetUpdateStamp();
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false); // the What's Wrong box is modal
            var sw = Stopwatch.StartNew();
            bool ok = force ? d.ForceRebuild3(false) : d.EditRebuild3();
            sw.Stop();
            NoUndoStep(ctx, d, stamp);
            return new RebuildResult
            {
                Doc = ctx.Title(d),
                Forced = force ? true : null,
                Rebuilt = ok,
                Errors = CollectErrors(d),
                DurationS = Math.Round(sw.Elapsed.TotalSeconds, 2),
                Model = IsPart(d) ? Snapshot.Take(d).ToState() : null,
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);

    /// <summary>The What's Wrong list, completed with any feature whose own error code is set.</summary>
    private static List<FeatureError> CollectErrors(IModelDoc2 d)
    {
        var list = new List<FeatureError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (f, code, warn) in d.Extension.WhatsWrong())
                if (seen.Add(f.Name)) list.Add(ToError(new ErrorState(f.Name, SafeType(f), code, warn)));
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* the per-feature codes below still count */ }
        foreach (var e in ErrorMap(d).Values)
            if (seen.Add(e.Name)) list.Add(ToError(e));
        return list;
    }

    // ------------------------------------------------------------------ doc_undo

    public async Task<UndoResult> UndoAsync(string? doc, int steps = 1, CancellationToken ct = default)
    {
        if (steps is < 1 or > 50) throw SwException.InvalidArgument($"steps must be between 1 and 50 (got {steps}).");
        return await session.RunAsync("doc_undo", ctx =>
        {
            var d = ctx.Doc;
            var entry = ctx.DocEntry;
            if (entry.Class is not (DocClass.Owned or DocClass.Adopted))
                throw new SwException(ErrorCodes.NotOwned, $"'{entry.Title}' is the user's document; the server doesn't undo in it.");
            RequireNoOpenSketch(ctx, d, "undo would act on the sketch edits");
            int stamp = d.GetUpdateStamp();
            if (entry.StampAfterLastOp is not int last || last != stamp)
                throw new SwException(ErrorCodes.NotOwned,
                    $"'{entry.Title}' changed after this server's last completed change (by the user, or by a call that failed and " +
                    "was rolled back), so undo could revert work that isn't the server's.",
                    "Ask the user to undo in SOLIDWORKS (Ctrl+Z), or repair the model with the feature tools (feature_delete, dimension_set).");
            int mine = entry.ServerWritesStamp == stamp ? entry.ServerWrites : 0;
            if (mine == 0)
                throw new SwException(ErrorCodes.NotOwned,
                    $"No change by this server is on record in '{entry.Title}' since it was opened or last changed by someone else, " +
                    "so there is nothing of the server's to undo.",
                    "Repair the model with the feature tools instead, or ask the user to undo in SOLIDWORKS.");
            if (steps > mine)
                throw SwException.InvalidArgument(
                    $"Only {mine} change(s) by this server are on record in '{entry.Title}'; undoing {steps} step(s) could revert work that isn't the server's.",
                    $"Pass steps ≤ {mine}.");

            var before = Snapshot.Take(d);
            var namesBefore = before.Features.ToDictionary(kv => kv.Key, kv => SafeName(kv.Value));
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false); // the What's Wrong box is modal
            d.EditUndo2(steps);
            var after = Snapshot.Take(d);
            var namesAfter = after.Features.ToDictionary(kv => kv.Key, kv => SafeName(kv.Value));
            var removed = namesBefore.Where(kv => !namesAfter.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
            var restored = namesAfter.Where(kv => !namesBefore.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
            var renamed = namesAfter.Where(kv => namesBefore.TryGetValue(kv.Key, out var old) && old != kv.Value)
                .Select(kv => $"{namesBefore[kv.Key]} → {kv.Value}").ToList();
            double dv = after.Volume - before.Volume;
            bool geometry = Math.Abs(dv) > VolEps || after.Faces != before.Faces || after.Bodies != before.Bodies;
            if (removed.Count == 0 && restored.Count == 0 && renamed.Count == 0 && !geometry && d.GetUpdateStamp() == stamp)
                throw new SwException(ErrorCodes.VerifyFailed, "Nothing changed: SOLIDWORKS had nothing to undo, or the last step can't be undone.",
                    "Check the model with feature_list.");

            if (entry.LastSketch is { } ls && !namesAfter.ContainsValue(ls)) entry.LastSketch = null;
            string? open = null;
            if (d.SketchManager?.ActiveSketch is ISketch sketch)
            {
                open = ((IFeature)sketch).Name;
                entry.ServerSketch = open; // the server's own step was undone, so the sketch is the server's
                ctx.Warn($"Sketch '{open}' is open again (an undone step was leaving it); finish it with sketch_finish.");
            }
            // The undone steps are no longer the server's to undo; this call itself is no new step (see SwContext.AfterWrite).
            entry.ServerWrites = Math.Max(0, mine - steps);
            entry.ServerWritesStamp = d.GetUpdateStamp();
            bool part = IsPart(d);
            return new UndoResult
            {
                Doc = ctx.Title(d),
                Steps = steps,
                Removed = removed.Count == 0 ? null : removed,
                Restored = restored.Count == 0 ? null : restored,
                Renamed = renamed.Count == 0 ? null : renamed,
                VolumeDeltaMm3 = part ? Units.Mm3(dv) : null,
                Model = part ? after.ToState() : null,
                OpenSketch = open,
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ doc_units_set

    public async Task<UnitsResult> UnitsSetAsync(string? doc, string units, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(units)) throw SwException.InvalidArgument("'units' is required: mmgs, cgs, mks or ips.");
        int want = DocumentService.ParseUnits(units);
        if (want == 0) throw SwException.InvalidArgument($"'{units}' is not a unit system.", "Use mmgs, cgs, mks or ips.");
        return await session.RunAsync("doc_units_set", ctx =>
        {
            var d = ctx.Doc;
            var entry = ctx.DocEntry;
            if (entry.Class != DocClass.Owned)
                throw new SwException(ErrorCodes.NotOwned, $"'{entry.Title}' wasn't created by this server; doc_units_set only changes the server's own documents.",
                    "Ask the user to change the units (Tools > Options > Document Properties > Units).");
            var ext = d.Extension;
            const int pref = (int)swUserPreferenceIntegerValue_e.swUnitSystem;
            const int noOption = (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
            int stamp = d.GetUpdateStamp();
            int previous = ext.GetUserPreferenceInteger(pref, noOption);
            if (previous == want)
                return new UnitsResult { Doc = ctx.Title(d), Previous = DocumentService.UnitName(previous), Units = DocumentService.UnitName(want) };

            // Bare numbers in equations mean document units: with equations, rebuild and compare every dimension.
            var equations = d.EquationList().Where(e => !e.Suppressed).Select(e => e.Text).ToList();
            var dimsBefore = equations.Count > 0 ? DimensionValues(d) : null;
            ext.SetUserPreferenceInteger(pref, noOption, want);
            int now = ext.GetUserPreferenceInteger(pref, noOption);
            if (now != want)
            {
                ext.SetUserPreferenceInteger(pref, noOption, previous);
                throw new SwException(ErrorCodes.VerifyFailed, $"The unit system reads {DocumentService.UnitName(now)} after setting {DocumentService.UnitName(want)}; it was set back.");
            }
            if (dimsBefore is not null)
            {
                using var prefs = ctx.SystemPrefs();
                prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
                d.EditRebuild3();
                var changed = ChangedDimensions(dimsBefore, DimensionValues(d));
                if (changed.Count > 0)
                {
                    ext.SetUserPreferenceInteger(pref, noOption, previous);
                    d.EditRebuild3();
                    bool back = ChangedDimensions(dimsBefore, DimensionValues(d)).Count == 0 &&
                                ext.GetUserPreferenceInteger(pref, noOption) == previous;
                    throw new SwException(ErrorCodes.NotSupported,
                        $"Switching to {DocumentService.UnitName(want)} changed {Quote(changed, 6)}, because equations use numbers without units " +
                        $"({Quote(equations, 4)}). {(back ? $"Set back to {DocumentService.UnitName(previous)}." : "Setting the units back did not restore every dimension; check the model.")}",
                        "Write units into those equations (e.g. 60mm instead of 60), then retry.");
                }
            }
            NoUndoStep(ctx, d, stamp);
            return new UnitsResult
            {
                Doc = ctx.Title(d),
                Previous = DocumentService.UnitName(previous),
                Units = DocumentService.UnitName(now),
                Changed = true,
                Message = "Only the document's units changed; tool inputs and outputs stay in mm and degrees.",
            };
        }, RunOptions.Write(doc) with { Timeout = LongTimeout }, ct).ConfigureAwait(false);
    }

    /// <summary>Every dimension's system value (m / rad / count), by full name.</summary>
    private static Dictionary<string, double> DimensionValues(IModelDoc2 d)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Tree.WalkAll(d))
        {
            object? dd = f.GetFirstDisplayDimension();
            int guard = 0;
            while (dd is IDisplayDimension disp && guard++ < 500)
            {
                if (disp.GetDimension2(0) is IDimension dim) map.TryAdd(dim.FullName, SystemValue(dim));
                dd = f.GetNextDisplayDimension(disp);
            }
        }
        return map;
    }

    private static List<string> ChangedDimensions(Dictionary<string, double> before, Dictionary<string, double> after) =>
        before.Where(kv => after.TryGetValue(kv.Key, out double v) && Math.Abs(v - kv.Value) > 1e-9 * Math.Max(1, Math.Abs(kv.Value)))
            .Select(kv => FeatureVerifier.ShortName(kv.Key)).ToList();

    // ------------------------------------------------------------------ shared helpers

    private static string RequireName(string? value, string arg) =>
        string.IsNullOrWhiteSpace(value) ? throw SwException.InvalidArgument($"'{arg}' is required.") : value.Trim();

    /// <summary>A feature by name (exact, then case-insensitive); read tools also accept front/top/right.</summary>
    private static IFeature Find(IModelDoc2 d, string name, string arg, bool planeAliases = false)
    {
        var f = EntityResolver.FeatureByName(d, name) ?? (planeAliases ? EntityResolver.StandardPlane(d, name) : null);
        return f ?? throw new SwException(ErrorCodes.EntityNotFound, $"No feature named '{name}' ({arg}).", Suggest(d, name));
    }

    private static string Suggest(IModelDoc2 d, string name)
    {
        var names = Tree.WalkAll(d).Where(f => !Tree.SystemTypes.Contains(f.GetTypeName2())).Select(f => f.Name).Distinct().ToList();
        string stem = name.TrimEnd("0123456789 ".ToCharArray());
        var near = stem.Length == 0 ? [] : names.Where(n => n.Contains(stem, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
        if (near.Count > 0) return $"Similar names: {string.Join(", ", near)}.";
        return names.Count == 0 ? "The model has no features yet." : $"feature_list shows the names (the newest: {string.Join(", ", names.TakeLast(4))}).";
    }

    /// <summary>Origin, standard planes and system folders are never deleted, suppressed or used as a rollback position.</summary>
    private static void RefuseSystem(IModelDoc2 d, IFeature f, string what)
    {
        string type = f.GetTypeName2();
        bool standardPlane = type == "RefPlane" &&
                             new[] { "front", "top", "right" }.Any(p => EntityResolver.StandardPlane(d, p) is { } sp && SafeId(sp) == SafeId(f));
        if (Tree.SystemTypes.Contains(type) || type == "OriginProfileFeature" || standardPlane)
            throw SwException.InvalidArgument($"'{f.Name}' ({type}) is a system feature; it can't be {what}.",
                "Pass a modeling feature (feature_list shows them).");
    }

    /// <summary>Rebuilds when SOLIDWORKS reports the model out of date, so a read-back sees the new geometry.</summary>
    private static void RebuildIfNeeded(IModelDoc2 d)
    {
        int status;
        try { status = d.Extension.NeedsRebuild2; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { status = -1; }
        if (status != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt) d.EditRebuild3();
    }

    /// <summary>
    /// For a write that leaves no SOLIDWORKS undo step (rebuild, rollback bar, units): doc_undo's count of server steps stays
    /// as it was (or restarts at 0 after an outside change) instead of counting this call (see SwContext.AfterWrite).
    /// </summary>
    private static void NoUndoStep(SwContext ctx, IModelDoc2 d, int stampAtStart)
    {
        var e = ctx.DocEntry;
        if (e.ServerWritesStamp != stampAtStart) e.ServerWrites = 0;
        e.ServerWritesStamp = d.GetUpdateStamp();
    }

    private static void RequireNoOpenSketch(SwContext ctx, IModelDoc2 d, string why)
    {
        if (d.SketchManager?.ActiveSketch is not ISketch open) return;
        string name = ((IFeature)open).Name;
        bool ours = ctx.DocEntry.ServerSketch == name;
        throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open; {why}.",
            ours ? "Finish it with sketch_finish first." : "The user is editing it; ask them to exit the sketch first.");
    }

    private static List<IFeature> DistinctById(IEnumerable<IFeature> features)
    {
        var seen = new HashSet<int>();
        var list = new List<IFeature>();
        foreach (var f in features)
            if (seen.Add(SafeId(f))) list.Add(f);
        return list;
    }

    private static Dictionary<int, int> TreeOrder(IModelDoc2 d)
    {
        var order = new Dictionary<int, int>();
        int i = 0;
        foreach (var f in Tree.WalkAll(d))
            if (SafeId(f) is int id && id >= 0) order.TryAdd(id, i++);
        return order;
    }

    private static Dictionary<int, (string Name, bool Suppressed)> SuppressionMap(IModelDoc2 d)
    {
        var map = new Dictionary<int, (string, bool)>();
        foreach (var f in Tree.WalkAll(d))
        {
            int id = SafeId(f);
            if (id >= 0 && !map.ContainsKey(id) && !Tree.SystemTypes.Contains(f.GetTypeName2())) map[id] = (f.Name, IsSuppressed(f));
        }
        return map;
    }

    private sealed record ErrorState(string Name, string Type, int Code, bool Warning);

    /// <summary>Features (sub-features included) with a rebuild error or warning, by id.</summary>
    private static Dictionary<int, ErrorState> ErrorMap(IModelDoc2 d)
    {
        var map = new Dictionary<int, ErrorState>();
        foreach (var f in Tree.WalkAll(d))
        {
            int code;
            bool warn;
            try { code = f.GetErrorCode2(out warn); }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { continue; }
            if (code == 0) continue;
            int id = SafeId(f);
            if (id >= 0) map.TryAdd(id, new ErrorState(f.Name, SafeType(f), code, warn));
        }
        return map;
    }

    /// <summary>Errors that are new, or got worse (warning → error, another code), since <paramref name="before"/>.</summary>
    private static List<FeatureError> NewErrors(Dictionary<int, ErrorState> before, Dictionary<int, ErrorState> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var b) || b.Code != kv.Value.Code || (b.Warning && !kv.Value.Warning))
            .Select(kv => ToError(kv.Value)).ToList();

    private static FeatureError ToError(ErrorState e) => new()
    {
        Feature = e.Name,
        Type = e.Type,
        Code = e.Code,
        Error = FeatureVerifier.ErrorName(e.Code),
        Warning = e.Warning ? true : null,
    };

    private static bool IsPart(IModelDoc2 d) => d.GetType() == (int)swDocumentTypes_e.swDocPART;

    private static bool IsSuppressed(IFeature f)
    {
        try { return f.IsSuppressed(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return false; }
    }

    private static bool IsRolledBack(IFeature f)
    {
        try { return f.IsRolledBack(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return false; }
    }

    private static int SafeId(IFeature f)
    {
        try { return f.GetID(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return -1; }
    }

    private static string SafeName(IFeature f)
    {
        try { return f.Name ?? ""; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return ""; }
    }

    private static string SafeType(IFeature f)
    {
        try { return f.GetTypeName2() ?? ""; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return ""; }
    }

    /// <summary>Reads an optional value: null when this feature type or state doesn't support the getter.</summary>
    private static T? Try<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    private static T? TryRef<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    private static string? TryS(Func<string?> read) => TryRef(read) is { Length: > 0 } s ? s : null;

    private static double? Mm(double? m) => m is double v && double.IsFinite(v) ? Units.Mm(v) : null;
    private static double? PosMm(double? m) => m is double v && double.IsFinite(v) && v > 0 ? Units.Mm(v) : null;
    private static double? Deg(double? rad) => rad is double v && double.IsFinite(v) ? Units.Deg(v) : null;

    private static IReadOnlyList<string>? NullIfEmpty(IEnumerable<string> items)
    {
        var list = items.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        return list.Count == 0 ? null : list;
    }

    private static string Quote(IEnumerable<string> names, int max = 6)
    {
        var all = names.ToList();
        string shown = string.Join(", ", all.Take(max).Select(n => $"'{n}'"));
        return all.Count > max ? $"{shown} and {all.Count - max} more" : shown;
    }
}

// ---------------------------------------------------------------------------------------------------- pure logic

/// <summary>
/// The COM-free rules of the feature-management tools. Tested by FeatureManagementLogicTests (in the LiveTests project,
/// which sees Core's internals and the swconst enums; those tests run without SOLIDWORKS).
/// </summary>
internal static class FeatureManagementLogic
{
    /// <summary>
    /// "this" → 1, "all" → 2. The same numbers are swInConfigurationOpts_e (IFeature.SetSuppression2:
    /// swThisConfiguration / swAllConfiguration) and swSetValueInConfiguration_e (IDimension.SetSystemValue3).
    /// </summary>
    public static int ParseConfigs(string? configs) => (configs ?? "this").Trim().ToLowerInvariant() switch
    {
        "this" or "" or "active" => (int)swInConfigurationOpts_e.swThisConfiguration,
        "all" => (int)swInConfigurationOpts_e.swAllConfiguration,
        _ => throw SwException.InvalidArgument($"configs '{configs}' is not supported.", "Use 'this' (the active configuration) or 'all'."),
    };

    public static string ConfigsName(int opt) => opt == (int)swInConfigurationOpts_e.swAllConfiguration ? "all" : "this";

    /// <summary>swEndConditions_e → the snake_case names feature_extrude uses.</summary>
    public static string EndConditionName(int end) => end switch
    {
        0 => "blind",
        1 => "through_all",
        2 => "through_next",
        3 => "up_to_vertex",
        4 => "up_to_surface",
        5 => "offset_from_surface",
        6 => "mid_plane",
        7 => "up_to_body",
        9 => "through_all_both",
        10 => "up_to_selection",
        11 => "up_to_next",
        _ => end.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>End conditions whose depth is a length the user gave (blind, mid-plane).</summary>
    public static bool HasDepth(int end) => end is (int)swEndConditions_e.swEndCondBlind or (int)swEndConditions_e.swEndCondMidPlane;

    public static string? RevolveTypeName(int type) => type switch
    {
        0 => "one_direction",
        1 => "mid_plane",
        2 => "two_direction",
        3 => "one_direction_360",
        4 => "mid_plane_360",
        5 => "two_direction_360",
        _ => null,
    };

    public static string? FilletTypeName(int type) => type switch
    {
        0 => "constant_radius",
        1 => "variable_radius",
        2 => "face",
        3 => "full_round",
        _ => null,
    };

    public static string? ChamferTypeName(int type) => type switch
    {
        (int)swChamferType_e.swChamferAngleDistance => "angle_distance",
        (int)swChamferType_e.swChamferDistanceDistance => "distance_distance",
        (int)swChamferType_e.swChamferVertex => "vertex",
        (int)swChamferType_e.swChamferEqualDistance => "equal_distance",
        _ => null,
    };

    /// <summary>swWzdHoleTypes_e → snake_case without the prefix (swHoleBlind → hole_blind).</summary>
    public static string? HoleTypeName(int type) =>
        Enum.IsDefined(typeof(swWzdHoleTypes_e), type) ? Snake(((swWzdHoleTypes_e)type).ToString()[2..]) : null;

    /// <summary>swWzdHoleStandards_e → its name without the prefix (swStandardAnsiMetric → AnsiMetric).</summary>
    public static string? HoleStandardName(int standard) =>
        Enum.IsDefined(typeof(swWzdHoleStandards_e), standard) ? ((swWzdHoleStandards_e)standard).ToString()["swStandard".Length..] : null;

    public static string Snake(string pascal)
    {
        var sb = new System.Text.StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(pascal[i - 1])) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>mm | deg | count, by swDimensionParamType_e.</summary>
    public static string UnitOf(int dimType) => dimType switch
    {
        (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular => "deg",
        (int)swDimensionParamType_e.swDimensionParamTypeInteger => "count",
        _ => "mm",
    };

    /// <summary>A tool value (mm / deg / count) → the API value (m / rad / count), validated for the dimension's type.</summary>
    public static double ToApi(int dimType, double value, string name)
    {
        if (!double.IsFinite(value)) throw SwException.InvalidArgument($"The value for '{name}' must be a finite number.");
        switch (dimType)
        {
            case (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular:
                if (value <= 0 || value > 360)
                    throw SwException.InvalidArgument($"'{name}' is an angle: the value must be in (0, 360] degrees (got {Fmt(value)}).");
                return Units.Rad(value);
            case (int)swDimensionParamType_e.swDimensionParamTypeInteger:
                if (value < 1 || Math.Abs(value - Math.Round(value)) > 1e-9)
                    throw SwException.InvalidArgument($"'{name}' is a count: the value must be a whole number ≥ 1 (got {Fmt(value)}).");
                return Math.Round(value);
            default:
                if (value <= 0)
                    throw SwException.InvalidArgument($"'{name}' is a length: the value must be > 0 mm (got {Fmt(value)}).",
                        "To flip a direction, use the feature's reverse option instead of a negative value.");
                return Units.M(value);
        }
    }

    /// <summary>An API value → the tool value (mm / deg / count), rounded like every result.</summary>
    public static double FromApi(int dimType, double api) => !double.IsFinite(api) ? double.NaN : dimType switch
    {
        (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular => Units.Deg(api),
        (int)swDimensionParamType_e.swDimensionParamTypeInteger => Math.Round(api),
        _ => Units.Mm(api),
    };

    /// <summary>Read-back equality: counts exactly, lengths and angles to 1e-9 (m / rad), relative for large values.</summary>
    public static bool SameApi(int dimType, double actual, double expected)
    {
        if (!double.IsFinite(actual)) return false;
        if (dimType == (int)swDimensionParamType_e.swDimensionParamTypeInteger) return Math.Round(actual) == Math.Round(expected);
        return Math.Abs(actual - expected) <= 1e-9 * Math.Max(1, Math.Abs(expected));
    }

    /// <summary>What an equation assigns: "\"D1@Boss-Extrude1\" = 20mm" → D1@Boss-Extrude1 (null without '=').</summary>
    public static string? EquationTarget(string? equation)
    {
        if (string.IsNullOrWhiteSpace(equation)) return null;
        int eq = equation.IndexOf('=');
        if (eq <= 0) return null;
        string lhs = equation[..eq].Trim();
        if (lhs.Length >= 2 && lhs[0] == '"' && lhs[^1] == '"') lhs = lhs[1..^1].Trim();
        return lhs.Length == 0 ? null : lhs;
    }

    /// <summary>A usable feature name: trimmed, non-empty, without '@' (it separates dimension names) or quotes.</summary>
    public static string ValidateNewName(string? name)
    {
        string n = (name ?? "").Trim();
        if (n.Length == 0) throw SwException.InvalidArgument("'newName' must not be empty.");
        if (n.Length > 200) throw SwException.InvalidArgument($"'newName' is {n.Length} characters long (at most 200).");
        if (n.Contains('@')) throw SwException.InvalidArgument("'newName' must not contain '@': it separates the parts of dimension names like D1@Fillet1.");
        if (n.Contains('"') || n.Any(char.IsControl)) throw SwException.InvalidArgument("'newName' must not contain quotes or control characters.");
        return n;
    }

    /// <summary>Trimmed, de-duplicated (case-insensitive) feature names; at least one, at most 100.</summary>
    public static List<string> RequireNames(IReadOnlyList<string>? names, string arg)
    {
        if (names is null || names.Count == 0) throw SwException.InvalidArgument($"'{arg}' must list at least one feature name.");
        if (names.Count > 100) throw SwException.InvalidArgument($"'{arg}' lists {names.Count} features (at most 100 per call).");
        var list = new List<string>();
        foreach (var raw in names)
        {
            string n = raw?.Trim() ?? "";
            if (n.Length == 0) throw SwException.InvalidArgument($"'{arg}' contains an empty name.");
            if (!list.Contains(n, StringComparer.OrdinalIgnoreCase)) list.Add(n);
        }
        return list;
    }

    /// <summary>Checked (name, value) pairs for dimension_set: at least one, names unique (case-insensitive), values finite.</summary>
    public static List<(string Name, double Value)> RequireValues(IReadOnlyList<DimensionValue>? values)
    {
        if (values is null || values.Count == 0)
            throw SwException.InvalidArgument("'values' must contain at least one {name, value}.", "E.g. values:[{name:'D1@Boss-Extrude1', value:30}].");
        if (values.Count > 100) throw SwException.InvalidArgument($"'values' has {values.Count} entries (at most 100 per call).");
        var list = new List<(string, double)>();
        for (int i = 0; i < values.Count; i++)
        {
            var v = values[i] ?? throw SwException.InvalidArgument($"values[{i}] is null.");
            string n = v.Name?.Trim() ?? "";
            if (n.Length == 0) throw SwException.InvalidArgument($"values[{i}].name is empty.");
            if (!double.IsFinite(v.Value)) throw SwException.InvalidArgument($"values[{i}].value must be a finite number.");
            if (list.Any(x => string.Equals(x.Item1, n, StringComparison.OrdinalIgnoreCase)))
                throw SwException.InvalidArgument($"'{n}' is listed twice in 'values'.");
            list.Add((n, v.Value));
        }
        return list;
    }

    /// <summary>The typed error for a failed IDimension.SetSystemValue3 (swSetValueReturnStatus_e).</summary>
    public static SwException SetValueError(int status, string name, double value, string unit) => status switch
    {
        (int)swSetValueReturnStatus_e.swSetValue_InvalidValue => SwException.InvalidArgument(
            $"SOLIDWORKS rejected {Fmt(value)} {unit} for '{name}' as invalid.", "Use a value that fits the geometry."),
        (int)swSetValueReturnStatus_e.swSetValue_DrivenDimension => SwException.InvalidArgument(
            $"'{name}' is a driven dimension; SOLIDWORKS won't set it.", "Change the driving dimension or the geometry it measures."),
        (int)swSetValueReturnStatus_e.swSetValue_ModelNotLoaded => new SwException(ErrorCodes.NotSupported,
            $"'{name}' belongs to a model that isn't loaded (lightweight or suppressed).", "Resolve or unsuppress it first."),
        (int)swSetValueReturnStatus_e.swSetValue_FrozenFeatureOwner => new SwException(ErrorCodes.NotSupported,
            $"'{name}' belongs to a frozen feature (above the freeze bar).", "Move the freeze bar below that feature first."),
        _ => new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS could not set '{name}' to {Fmt(value)} {unit} (status {status})."),
    };

    private static string Fmt(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
