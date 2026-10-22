using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

// ---------------------------------------------------------------------------------------------------------- DTOs

/// <summary>One sketch segment and its geometry: [x, y] sketch mm (model [x, y, z] mm in a 3D sketch).</summary>
public sealed class SketchSegmentInfo
{
    public required string Name { get; init; }
    /// <summary>line | arc | circle | ellipse | spline | text | parabola | other.</summary>
    public required string Kind { get; init; }
    public bool? Construction { get; init; }
    public double[]? StartMm { get; init; }
    public double[]? EndMm { get; init; }
    public double[]? CenterMm { get; init; }
    public double? RadiusMm { get; init; }
    public double? MajorRadiusMm { get; init; }
    public double? MinorRadiusMm { get; init; }
    public double? LengthMm { get; init; }
    /// <summary>In 'changed' lists: the length before the call.</summary>
    public double? LengthBeforeMm { get; init; }
    public string? Text { get; init; }
}

/// <summary>sketch_edit: the re-opened sketch.</summary>
public sealed class SketchReopenResult : ToolResult
{
    public required string Sketch { get; init; }
    public SketchFrame? Frame { get; init; }
    public bool? ThreeD { get; init; }
    public int Segments { get; init; }
    public int Relations { get; init; }
    /// <summary>under | fully | over | unknown | no_solution | invalid | autosolve_off.</summary>
    public required string Constrained { get; init; }
    public bool? AlreadyOpen { get; init; }
}

/// <summary>sketch_info.</summary>
public sealed class SketchInfoResult : ToolResult
{
    public required string Sketch { get; init; }
    public bool Open { get; init; }
    public bool? ThreeD { get; init; }
    public SketchFrame? Frame { get; init; }
    public required IReadOnlyList<SketchSegmentInfo> Segments { get; init; }
    public int SegmentCount { get; init; }
    public bool? Truncated { get; init; }
    public int Points { get; init; }
    public int Relations { get; init; }
    public IReadOnlyDictionary<string, int>? RelationTypes { get; init; }
    public required string Constrained { get; init; }
    public required ContourStatus Contours { get; init; }
    public IReadOnlyList<DimensionInfo>? Dimensions { get; init; }
}

/// <summary>sketch_dimension_add.</summary>
public sealed class SketchDimensionResult : ToolResult
{
    public required string Sketch { get; init; }
    public required string Kind { get; init; }
    /// <summary>The new driving dimension, e.g. D1@Sketch1 (the name dimension tools and equations take).</summary>
    public required DimensionInfo Dimension { get; init; }
    /// <summary>The same quantity measured on the sketch geometry after the change (mm, or deg for angular).</summary>
    public double? Measured { get; init; }
    public required string Constrained { get; init; }
    public IReadOnlyList<SketchSegmentInfo>? Changed { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>sketch_relation_add.</summary>
public sealed class SketchRelationResult : ToolResult
{
    public required string Sketch { get; init; }
    public required string Relation { get; init; }
    public int RelationsBefore { get; init; }
    public int RelationsAfter { get; init; }
    public required string Constrained { get; init; }
    /// <summary>The related segments after the relation solved.</summary>
    public IReadOnlyList<SketchSegmentInfo>? Segments { get; init; }
    /// <summary>Other segments the solver moved.</summary>
    public IReadOnlyList<SketchSegmentInfo>? Changed { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>The sketch editing tools (trim, extend, offset, fillet, chamfer, pattern, mirror, convert, construction, delete).</summary>
public sealed class SketchChangeResult : ToolResult
{
    public required string Sketch { get; init; }
    public required string Operation { get; init; }
    public IReadOnlyList<SketchSegmentInfo>? Created { get; init; }
    /// <summary>Set when more segments were created than listed.</summary>
    public int? CreatedCount { get; init; }
    public IReadOnlyList<SketchSegmentInfo>? Changed { get; init; }
    public IReadOnlyList<string>? Removed { get; init; }
    /// <summary>offset: outward | inward | both | open.</summary>
    public string? Side { get; init; }
    /// <summary>offset: box of the new segments [xMin, yMin, xMax, yMax] (sketch mm).</summary>
    public double[]? BoxMm { get; init; }
    public IReadOnlyList<string>? Checks { get; init; }
    public required ContourStatus Contours { get; init; }
    public string? Constrained { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>sketch_pattern arguments.</summary>
public sealed record SketchPatternRequest
{
    public string? Doc { get; init; }
    /// <summary>linear | circular.</summary>
    public string Kind { get; init; } = "linear";
    public IReadOnlyList<EntityRef>? Entities { get; init; }
    /// <summary>linear: instances along the direction (seed included).</summary>
    public int? CountX { get; init; }
    public double? SpacingXMm { get; init; }
    /// <summary>linear: instances along the second (perpendicular) direction (seed included).</summary>
    public int? CountY { get; init; }
    public double? SpacingYMm { get; init; }
    /// <summary>linear: direction of the first axis, degrees from sketch +x (the second is +90°).</summary>
    public double? AngleDeg { get; init; }
    /// <summary>circular: rotation center, [x, y] sketch mm or [x, y, z] model mm.</summary>
    public double[]? Center { get; init; }
    /// <summary>circular: instances (seed included).</summary>
    public int? Count { get; init; }
    public double TotalAngleDeg { get; init; } = 360;
}

// ------------------------------------------------------------------------------------------------------- service

/// <summary>
/// Editing the sketch this server has open (sketch_start / sketch_edit): re-open and inspect sketches, add dimensions
/// and relations, trim, extend, offset, fillet, chamfer, pattern, mirror, convert edges, toggle construction and delete.
/// Every editing call compares the sketch before and after (segments by name, their geometry, relation and point
/// counts) and checks its intent on the geometry; when SOLIDWORKS changed something that fails the check, the change is
/// undone (IModelDoc2.EditUndo2) and the error says whether the sketch is back to where it was.
/// </summary>
public sealed class SketchEditService(SwSession session)
{
    private const double Tol = 1e-7;        // m: geometry comparisons (0.0001 mm)
    private const double AngTol = 1e-6;     // rad
    private const double JoinTol = 1e-8;    // m: end points that coincide
    private const int ListMax = 50;         // segments listed per result list
    private const int InfoMax = 300;        // segments listed by sketch_info
    private const int MaxInstances = 500;   // pattern instances
    private const double TrimReach = 1e-3;  // m: how far a trim pick point may lie from the segment it trims

    private static readonly EntityKind[] SketchKinds = [EntityKind.SketchSegment, EntityKind.SketchPoint];

    // ================================================================================================== sketch_edit

    public Task<SketchReopenResult> EditAsync(string? doc, string sketch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sketch))
            throw SwException.InvalidArgument("'sketch' is required (its name, e.g. \"Sketch1\").", "feature_list shows the sketches.");
        return session.RunAsync("sketch_edit", ctx =>
        {
            var d = DocOf(ctx);
            var entry = ctx.DocEntry;
            var sm = d.SketchManager;
            var feat = EntityResolver.FeatureByName(d, sketch.Trim())
                       ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{sketch}'.",
                           "feature_list shows the sketches (type ProfileFeature, or 3DProfileFeature).");
            if (EntityResolver.FeatureKind(feat) != EntityKind.Sketch || feat.Specific() is not ISketch)
                throw SwException.InvalidArgument($"'{feat.Name}' is a {feat.GetTypeName2()}, not a sketch.",
                    "Pass a sketch name such as \"Sketch1\"; feature_list shows each feature's type (and its sketch as a child).");
            string name = feat.Name;
            if (sm.ActiveSketch is ISketch open)
            {
                string openName = ((IFeature)open).Name;
                bool ours = entry.ServerSketch == openName;
                if (ours && string.Equals(openName, name, StringComparison.OrdinalIgnoreCase))
                    return Reopened(ctx, d, open, openName, alreadyOpen: true);
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{openName}' is open.",
                    ours ? "Finish it with sketch_finish first." : "The user is editing it; ask them to exit the sketch.");
            }
            d.ClearSelection2(true);
            if (!feat.Select2(false, 0))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select sketch '{name}'.");
            d.EditSketch();
            d.ClearSelection2(true);
            var now = sm.ActiveSketch
                      ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not open sketch '{name}' for editing.",
                          "It may be suppressed, rolled back or derived; check feature_list.");
            string nowName = ((IFeature)now).Name;
            if (!string.Equals(nowName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (now.Is3D()) sm.Insert3DSketch(true);
                else sm.InsertSketch(true);
                throw new SwException(ErrorCodes.VerifyFailed, $"SOLIDWORKS opened '{nowName}' instead of '{name}'; it was closed again.");
            }
            entry.ServerSketch = nowName;
            return Reopened(ctx, d, now, nowName, alreadyOpen: false);
        }, RunOptions.Write(doc), ct);
    }

    private static SketchReopenResult Reopened(SwContext ctx, IModelDoc2 d, ISketch s, string name, bool alreadyOpen)
    {
        bool is3D = s.Is3D();
        return new SketchReopenResult
        {
            Doc = ctx.Title(d),
            Sketch = name,
            Frame = is3D ? null : SketchService.Frame(SketchService.SketchToModel(s)),
            ThreeD = is3D ? true : null,
            Segments = s.Segments().Count,
            Relations = RelationCount(s),
            Constrained = ConstrainedName(s.GetConstrainedStatus()),
            AlreadyOpen = alreadyOpen ? true : null,
        };
    }

    // ================================================================================================== sketch_info

    public Task<SketchInfoResult> InfoAsync(string? doc, string? sketch, CancellationToken ct = default) =>
        session.RunAsync("sketch_info", ctx =>
        {
            var d = DocOf(ctx);
            var active = d.SketchManager.ActiveSketch;
            ISketch s;
            bool open;
            if (string.IsNullOrWhiteSpace(sketch))
            {
                s = active ?? throw new SwException(ErrorCodes.SketchState, "No sketch is open.",
                    "Pass 'sketch' (e.g. \"Sketch1\") to read a closed sketch; feature_list shows them.");
                open = true;
            }
            else
            {
                var f = EntityResolver.FeatureByName(d, sketch.Trim())
                        ?? throw new SwException(ErrorCodes.EntityNotFound, $"No sketch named '{sketch}'.", "feature_list shows the sketches.");
                if (EntityResolver.FeatureKind(f) != EntityKind.Sketch || f.Specific() is not ISketch closed)
                    throw SwException.InvalidArgument($"'{f.Name}' is a {f.GetTypeName2()}, not a sketch.");
                open = active is not null && string.Equals(((IFeature)active).Name, f.Name, StringComparison.OrdinalIgnoreCase);
                s = open ? active! : closed;
            }
            var feat = (IFeature)s;
            bool is3D = s.Is3D();
            var segs = s.Segments();
            var list = segs.Take(InfoMax).Select(x => Info(x.GetName(), Read(x), is3D)).ToList();
            var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
            try
            {
                if (s.RelationManager is ISketchRelationManager rm)
                    foreach (var r in rm.SkRelations((int)swSketchRelationFilterType_e.swAll))
                    {
                        string t = RelationTypeName(r.GetRelationType());
                        types[t] = types.GetValueOrDefault(t) + 1;
                    }
            }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* relation listing is informational */ }
            var dims = FeatureVerifier.Dimensions(feat);
            return new SketchInfoResult
            {
                Doc = ctx.Title(d),
                Sketch = feat.Name,
                Open = open,
                ThreeD = is3D ? true : null,
                Frame = is3D ? null : SketchService.Frame(SketchService.SketchToModel(s)),
                Segments = list,
                SegmentCount = segs.Count,
                Truncated = segs.Count > list.Count ? true : null,
                Points = s.Points().Count,
                Relations = RelationCount(s),
                RelationTypes = types.Count == 0 ? null : types,
                Constrained = ConstrainedName(s.GetConstrainedStatus()),
                Contours = SafeContours(s),
                Dimensions = dims.Count == 0 ? null : dims,
            };
        }, RunOptions.Read(doc), ct);

    // ========================================================================================= sketch_dimension_add

    public Task<SketchDimensionResult> DimensionAddAsync(string? doc, IReadOnlyList<EntityRef>? entities, string kind,
        double? valueMm = null, double? valueDeg = null, double[]? textAt = null, CancellationToken ct = default)
    {
        string k = Lower(kind);
        if (k is not ("linear" or "horizontal" or "vertical" or "radial" or "diameter" or "angular"))
            throw SwException.InvalidArgument($"kind '{kind}' is not supported.", "Use linear, horizontal, vertical, radial, diameter or angular.");
        if (entities is null || entities.Count is < 1 or > 2)
            throw SwException.InvalidArgument($"'entities' needs 1 or 2 references (got {entities?.Count ?? 0}).",
                "A segment {name:'Line1'}, a sketch point {at:[x,y]} or {name:'origin'}.");
        double? target = null;
        if (k == "angular")
        {
            if (valueMm is not null) throw SwException.InvalidArgument("An angular dimension takes valueDeg, not valueMm.");
            if (valueDeg is double vd)
            {
                if (!double.IsFinite(vd) || vd <= 0 || vd >= 180)
                    throw SwException.InvalidArgument($"valueDeg must be between 0 and 180 (got {vd}).");
                target = Units.Rad(vd);
            }
        }
        else
        {
            if (valueDeg is not null) throw SwException.InvalidArgument($"A {k} dimension takes valueMm, not valueDeg.");
            if (valueMm is double vm) target = Units.PositiveM(vm, "valueMm");
        }
        CheckPoint(textAt, "textAt", required: false);
        var options = RunOptions.Write(doc) with { DialogRules = [DialogRules.ModifyDimension, DialogRules.OverdefiningDimension] };
        return session.RunAsync("sketch_dimension_add", ctx => AddDimension(ctx, k, entities, target, textAt), options, ct);
    }

    private static SketchDimensionResult AddDimension(SwContext ctx, string kind, IReadOnlyList<EntityRef> entities, double? target, double[]? textAt)
    {
        var d = DocOf(ctx);
        var sketch = OpenSketch(ctx, d, "sketch_dimension_add", twoD: true);
        var feat = (IFeature)sketch;
        var s2m = SketchService.SketchToModel(sketch);
        var m2s = s2m.Inverse();
        var refs = Refs(ctx, d, sketch, m2s, entities, "entities", EntityKind.SketchSegment, EntityKind.SketchPoint, EntityKind.Origin);
        if (refs.Count != entities.Count)
            throw SwException.InvalidArgument("'entities' names the same entity twice.");
        var items = refs.Select(r => ItemOf(r, m2s)).ToList();
        CheckDimensionTargets(kind, items);
        double? expected = Measure(kind, items);
        var text = textAt is null ? DefaultTextPoint(kind, items) : ToSketch(textAt, m2s, "textAt", ctx);
        var before = SkState.Take(sketch);
        var dimsBefore = FeatureVerifier.Dimensions(feat).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var checks = new List<string>();
        string labels = string.Join(" and ", refs.Select(r => r.Label));

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.InputDimValOnCreate, false); // no modal Modify box on the new dimension

        // The text location decides which quantity an ambiguous dimension measures (aligned/horizontal/vertical, or which
        // angle). Model coordinates first (like SelectByID2), sketch coordinates as the fallback.
        var places = new List<Vec3> { s2m.ApplyPoint(text) };
        if (!s2m.IsIdentity()) places.Add(text);
        IDisplayDimension? disp = null;
        IDimension? dim = null;
        for (int attempt = 0; attempt < places.Count; attempt++)
        {
            d.ClearSelection2(true);
            SelectAll(d, refs, 0);
            var at = places[attempt];
            disp = kind switch
            {
                "horizontal" => (object?)d.AddHorizontalDimension2(at.X, at.Y, at.Z) as IDisplayDimension,
                "vertical" => (object?)d.AddVerticalDimension2(at.X, at.Y, at.Z) as IDisplayDimension,
                "radial" => (object?)d.AddRadialDimension2(at.X, at.Y, at.Z) as IDisplayDimension,
                "diameter" => (object?)d.AddDiameterDimension2(at.X, at.Y, at.Z) as IDisplayDimension,
                _ => (object?)d.AddDimension2(at.X, at.Y, at.Z) as IDisplayDimension,
            };
            d.ClearSelection2(true);
            if (disp is null)
            {
                if (ctx.Op.HandledDialogs.Any(h => ReferenceEquals(h.Rule, DialogRules.OverdefiningDimension)))
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"A {kind} dimension on {labels} would over-define the sketch (SOLIDWORKS offered to make it driven); nothing was added.",
                        "Delete the dimension or relation that already fixes this size (sketch_info lists them), or dimension something else.");
                if (attempt < places.Count - 1) continue;
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no {kind} dimension on {labels}.",
                    "Check that the entities can carry this dimension (sketch_info shows their kinds).");
            }
            dim = disp.GetDimension2(0);
            if (dim is null)
            {
                bool gone = DeleteDimension(d, disp, feat, dimsBefore);
                throw new SwException(ErrorCodes.FeatureFailed, $"The new display dimension has no dimension; {Removed(gone)}.");
            }
            if (dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven || BecameOverDefined(sketch, before.Status))
            {
                bool gone = DeleteDimension(d, disp, feat, dimsBefore);
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"A {kind} dimension on {labels} would over-define the sketch (SOLIDWORKS made it a driven reference dimension); {Removed(gone)}.",
                    "Delete the dimension or relation that already fixes this size (sketch_info lists them), or dimension something else.");
            }
            if (expected is not double e || Math.Abs(dim.SystemValue - e) <= MeasureTol(kind)) break;
            // It measures something else (e.g. a horizontal distance instead of the aligned one, or the other angle).
            string got = Fmt(kind, dim.SystemValue);
            bool removed = DeleteDimension(d, disp, feat, dimsBefore);
            disp = null;
            dim = null;
            if (!removed || attempt == places.Count - 1)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"SOLIDWORKS dimensioned {got} instead of the {kind} {Fmt(kind, e)} of {labels}; {Removed(removed)}.",
                    "Pass textAt next to the quantity you want (inside the angle for angular, beside the segment for a length), or use kind horizontal / vertical.");
        }
        if (disp is null || dim is null)
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no {kind} dimension on {labels}.");

        double v0 = dim.SystemValue;
        double? measured = expected;
        if (target is double t)
        {
            int st = dim.SetSystemValue3(t, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, ComArgs.Empty);
            if (st != (int)swSetValueReturnStatus_e.swSetValue_Successful)
            {
                bool gone = DeleteDimension(d, disp, feat, dimsBefore);
                throw new SwException(st == (int)swSetValueReturnStatus_e.swSetValue_DrivenDimension ? ErrorCodes.VerifyFailed : ErrorCodes.FeatureFailed,
                    $"SOLIDWORKS refused the value {Fmt(kind, t)} ({SetValueStatus(st)}); {Removed(gone)}.",
                    "The value may be impossible for the sketch (e.g. it would make a segment vanish or a relation fail).");
            }
            double back = dim.SystemValue;
            var now = refs.Select(r => ItemOf(r, m2s)).ToList();
            measured = Measure(kind, now);
            string? problem =
                Math.Abs(back - t) > MeasureTol(kind) ? $"the dimension reads {Fmt(kind, back)}" :
                BecameOverDefined(sketch, before.Status) ? $"the sketch became {ConstrainedName(sketch.GetConstrainedStatus())}" :
                measured is double m && Math.Abs(m - t) > MeasureTol(kind) ? $"the geometry measures {Fmt(kind, m)} (the sketch did not solve to it)" :
                null;
            if (problem is not null)
            {
                try { dim.SetSystemValue3(v0, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, ComArgs.Empty); }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* best effort */ }
                bool gone = DeleteDimension(d, disp, feat, dimsBefore);
                throw new SwException(ErrorCodes.VerifyFailed, $"After setting {Fmt(kind, t)}, {problem}; the old value was restored and {Removed(gone)}.",
                    "Other relations or dimensions may fix this geometry; check sketch_info.");
            }
            checks.Add($"value reads back {Fmt(kind, back)}");
            if (measured is double mm) checks.Add($"geometry measures {Fmt(kind, mm)}");
        }
        else
        {
            checks.Add($"driving dimension at the current size {Fmt(kind, v0)}");
        }

        var after = SkState.Take(sketch);
        var diff = Diff(before, after);
        bool is3D = false;
        return new SketchDimensionResult
        {
            Doc = ctx.Title(d),
            Sketch = feat.Name,
            Kind = kind,
            Dimension = FeatureVerifier.Dimension(dim),
            Measured = measured is double ms ? (kind == "angular" ? Units.Deg(ms) : Units.Mm(ms)) : null,
            Constrained = ConstrainedName(after.Status),
            Changed = ChangedList(diff, before, after, is3D),
            Checks = checks,
            Resolved = refs.Select(r => r.Info("entities")).ToList(),
        };
    }

    /// <summary>Deletes a display dimension; true when the sketch's dimension names are back to <paramref name="before"/>.</summary>
    private static bool DeleteDimension(IModelDoc2 d, IDisplayDimension disp, IFeature sketchFeat, HashSet<string> before)
    {
        try
        {
            d.ClearSelection2(true);
            bool sel = false;
            try { sel = disp.SkAnnotation()?.Select3(false, null) == true; }
            catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* try by name */ }
            if (!sel)
            {
                string n = disp.GetNameForSelection();
                if (!string.IsNullOrEmpty(n)) sel = d.Extension.SelectByID2(n, "DIMENSION", 0, 0, 0, false, 0, null, 0);
            }
            if (sel) d.EditDelete();
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* reported through the check below */ }
        finally
        {
            d.ClearSelection2(true);
        }
        return FeatureVerifier.Dimensions(sketchFeat).All(x => before.Contains(x.Name));
    }

    private static string Removed(bool gone) => gone ? "it was removed" : "it could not be removed (it is still in the sketch)";

    private static void CheckDimensionTargets(string kind, IReadOnlyList<Item> it)
    {
        string got = string.Join(" + ", it.Select(i => i.Kind));
        switch (kind)
        {
            case "radial" or "diameter":
                if (it.Count != 1 || it[0].Kind is not ("arc" or "circle"))
                    throw SwException.InvalidArgument($"A {kind} dimension needs one arc or circle (got {got}).");
                return;
            case "angular":
                if (it.Count != 2 || it.Any(i => i.Kind != "line"))
                    throw SwException.InvalidArgument($"An angular dimension needs two lines (got {got}).");
                if (Parallel(it[0], it[1]))
                    throw SwException.InvalidArgument("The two lines are parallel, so there is no angle between them.", "Use kind linear for their distance.");
                return;
        }
        if (it.Any(i => i.Kind is "other" or "external"))
            throw new SwException(ErrorCodes.NotSupported, $"sketch_dimension_add dimensions lines, arcs, circles and points (got {got}).");
        if (it.Count == 1 && it[0].Kind != "line")
            throw SwException.InvalidArgument($"A {kind} dimension of one entity needs a line (got {got}).",
                it[0].Kind is "arc" or "circle" ? "Use kind radial or diameter for arcs and circles." : "Pass two entities for a distance.");
        if (it.Count == 2 && it[0].Kind == "line" && it[1].Kind == "line" && !Parallel(it[0], it[1]))
            throw SwException.InvalidArgument($"Two non-parallel lines have an angle, not a {kind} distance.", "Use kind angular, or dimension a line and a point.");
    }

    /// <summary>What a dimension of <paramref name="kind"/> on these entities measures (m or rad); null when not computable.</summary>
    private static double? Measure(string kind, IReadOnlyList<Item> it)
    {
        switch (kind)
        {
            case "radial": return it is [{ Kind: "arc" or "circle" } a] ? a.Radius : null;
            case "diameter": return it is [{ Kind: "arc" or "circle" } b] ? 2 * b.Radius : null;
            case "angular": return it is [{ Kind: "line" } l1, { Kind: "line" } l2] && Rays(l1, l2) is var (_, u1, u2) ? Math.Acos(Math.Clamp(u1.Dot(u2), -1, 1)) : null;
        }
        if (it.Any(i => i.Kind is "other" or "external")) return null;
        if (it.Count == 1)
        {
            if (it[0].Kind != "line") return null;
            var dv = it[0].B - it[0].A;
            return kind switch { "linear" => dv.Length, "horizontal" => Math.Abs(dv.X), "vertical" => Math.Abs(dv.Y), _ => null };
        }
        var (x, y) = (it[0], it[1]);
        if (x.Kind == "line" && y.Kind == "line")
        {
            if (!Parallel(x, y)) return null;
            return kind switch
            {
                "linear" => PointLineDistance(y.A, x.A, x.B),
                "horizontal" when IsVertical(x) => Math.Abs(x.A.X - y.A.X),
                "vertical" when IsHorizontal(x) => Math.Abs(x.A.Y - y.A.Y),
                _ => null,
            };
        }
        if (x.Kind == "line" || y.Kind == "line")
        {
            var line = x.Kind == "line" ? x : y;
            var q = (x.Kind == "line" ? y : x).Anchor;
            return kind switch
            {
                "linear" => PointLineDistance(q, line.A, line.B),
                "horizontal" when IsVertical(line) => Math.Abs(q.X - line.A.X),
                "vertical" when IsHorizontal(line) => Math.Abs(q.Y - line.A.Y),
                _ => null,
            };
        }
        var p1 = x.Anchor;
        var p2 = y.Anchor;
        return kind switch { "linear" => p1.DistanceTo(p2), "horizontal" => Math.Abs(p1.X - p2.X), "vertical" => Math.Abs(p1.Y - p2.Y), _ => null };
    }

    /// <summary>A text location (sketch coordinates) that makes the dimension measure the requested quantity.</summary>
    private static Vec3 DefaultTextPoint(string kind, IReadOnlyList<Item> it)
    {
        const double minOff = 0.005;
        switch (kind)
        {
            case "radial" or "diameter":
            {
                var a = it[0];
                double ang = a.Kind == "arc" ? ArcMidAngle(a.A, a.B, a.C, a.Dir) : Math.PI / 4;
                return a.C + new Vec3(Math.Cos(ang), Math.Sin(ang), 0) * (a.Radius * 1.4 + 0.002);
            }
            case "angular":
            {
                var (p, u1, u2) = Rays(it[0], it[1]);
                var bisector = (u1 + u2).Normalized();
                double reach = Math.Min(Reach(it[0], p), Reach(it[1], p));
                return p + bisector * (reach > 1e-4 ? 0.5 * reach : 0.01);
            }
        }
        if (it.Count == 1)
        {
            var l = it[0];
            var mid = (l.A + l.B) / 2;
            double off = Math.Max(minOff, 0.15 * l.A.DistanceTo(l.B));
            return kind switch
            {
                "horizontal" => new Vec3(mid.X, Math.Max(l.A.Y, l.B.Y) + off, 0),
                "vertical" => new Vec3(Math.Max(l.A.X, l.B.X) + off, mid.Y, 0),
                _ => mid + Perp((l.B - l.A).Normalized()) * off,
            };
        }
        var (pa, pb) = NearestPair(it[0], it[1]);
        var m = (pa + pb) / 2;
        double dist = pa.DistanceTo(pb);
        double o = Math.Max(minOff, 0.15 * dist);
        return kind switch
        {
            "horizontal" => new Vec3(m.X, Math.Max(pa.Y, pb.Y) + o, 0),
            "vertical" => new Vec3(Math.Max(pa.X, pb.X) + o, m.Y, 0),
            _ => dist < 1e-12 ? m + new Vec3(o, o, 0) : m + Perp((pb - pa).Normalized()) * o,
        };
    }

    /// <summary>The two points a distance between the entities runs between (a line contributes the foot of the perpendicular).</summary>
    private static (Vec3, Vec3) NearestPair(Item a, Item b)
    {
        if (a.Kind == "line" && b.Kind == "line")
        {
            var mid = (a.A + a.B) / 2;
            return (mid, Foot(mid, b.A, b.B));
        }
        if (a.Kind == "line") return (Foot(b.Anchor, a.A, a.B), b.Anchor);
        if (b.Kind == "line") return (a.Anchor, Foot(a.Anchor, b.A, b.B));
        return (a.Anchor, b.Anchor);
    }

    private static double MeasureTol(string kind) => kind == "angular" ? AngTol : Tol;

    private static string Fmt(string kind, double v) => kind == "angular" ? $"{Units.Deg(v)}°" : $"{Units.Mm(v)} mm";

    private static string SetValueStatus(int st) => Enum.IsDefined(typeof(swSetValueReturnStatus_e), st)
        ? ((swSetValueReturnStatus_e)st).ToString().Replace("swSetValue_", "") : $"status {st}";

    // ========================================================================================== sketch_relation_add

    private sealed record RelSpec(string[] Ids, int Min, int Max, string Takes);

    /// <summary>Relation → SketchAddConstraints ids (sgATPIERCE and sgMERGEPOINTS are unverified; the rest are verified).</summary>
    private static readonly Dictionary<string, RelSpec> RelationSpecs = new(StringComparer.Ordinal)
    {
        ["horizontal"] = new(["sgHORIZONTAL"], 1, 50, "lines, or two or more points"),
        ["vertical"] = new(["sgVERTICAL"], 1, 50, "lines, or two or more points"),
        ["coincident"] = new(["sgCOINCIDENT"], 2, 2, "a point and a point, line or arc"),
        ["collinear"] = new(["sgCOLINEAR"], 2, 50, "two or more lines"),
        ["parallel"] = new(["sgPARALLEL"], 2, 50, "two or more lines"),
        ["perpendicular"] = new(["sgPERPENDICULAR"], 2, 2, "two lines"),
        ["tangent"] = new(["sgTANGENT"], 2, 2, "an arc or circle and a line, arc or circle"),
        ["concentric"] = new(["sgCONCENTRIC"], 2, 50, "arcs/circles, or a point and an arc/circle"),
        ["equal"] = new(["sgSAMELENGTH"], 2, 50, "two or more lines, or arcs/circles"),
        ["symmetric"] = new(["sgSYMMETRIC"], 3, 3, "two points (or two like segments) and the symmetry line"),
        ["midpoint"] = new(["sgATMIDDLE"], 2, 2, "a point and a line"),
        ["intersection"] = new(["sgATINTERSECT"], 3, 3, "a point and two lines"),
        ["fix"] = new(["sgFIXED"], 1, 50, "sketch entities"),
        ["pierce"] = new(["sgATPIERCE"], 2, 2, "a sketch point and a curve that crosses the sketch plane"),
        ["merge"] = new(["sgMERGEPOINTS"], 2, 2, "two sketch points"),
    };

    private static readonly EntityKind[] RelationKinds =
    [
        EntityKind.SketchSegment, EntityKind.SketchPoint, EntityKind.Edge, EntityKind.Vertex, EntityKind.Face,
        EntityKind.Plane, EntityKind.Axis, EntityKind.Origin,
    ];

    public Task<SketchRelationResult> RelationAddAsync(string? doc, string relation, IReadOnlyList<EntityRef>? entities, CancellationToken ct = default)
    {
        string rel = Lower(relation);
        if (!RelationSpecs.TryGetValue(rel, out var spec))
            throw SwException.InvalidArgument($"Unknown relation '{relation}'.", $"Use one of: {string.Join(", ", RelationSpecs.Keys)}.");
        int n = entities?.Count ?? 0;
        if (n < spec.Min || n > spec.Max)
            throw SwException.InvalidArgument($"'{rel}' takes {spec.Takes}: {(spec.Min == spec.Max ? $"{spec.Min}" : $"at least {spec.Min}")} entities (got {n}).");
        return session.RunAsync("sketch_relation_add", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_relation_add", twoD: false);
            string name = ((IFeature)sketch).Name;
            bool is3D = sketch.Is3D();
            var m2s = is3D ? Xform.Identity : SketchService.SketchToModel(sketch).Inverse();
            var refs = Refs(ctx, d, sketch, m2s, entities!, "entities", RelationKinds);
            if (refs.Count < spec.Min)
                throw SwException.InvalidArgument($"'{rel}' needs {spec.Min} different entities; some references name the same one.");
            var items = refs.Select(r => ItemOf(r, m2s)).ToList();
            if (rel == "symmetric" && items.Count(i => i.Kind == "line") == 1 && items[^1].Kind != "line")
            {
                // The symmetry line goes last.
                int li = items.FindIndex(i => i.Kind == "line");
                var (lr, lit) = (refs[li], items[li]);
                refs.RemoveAt(li); items.RemoveAt(li);
                refs.Add(lr); items.Add(lit);
            }
            CheckRelationTargets(rel, items);
            // Unverified ids around the verified ones: the point forms of horizontal/vertical are tried first, and merge is
            // tried after coincident for two points (SOLIDWORKS offers Merge, not Coincident, for two sketch points).
            bool points = items.All(i => i.Kind == "point");
            string[] ids = rel switch
            {
                "horizontal" when points => ["sgHORIZPOINTS", "sgHORIZONTAL"],
                "vertical" when points => ["sgVERTPOINTS", "sgVERTICAL"],
                "coincident" when points => ["sgCOINCIDENT", "sgMERGEPOINTS"],
                _ => spec.Ids,
            };
            var before = SkState.Take(sketch);
            string labels = string.Join(", ", refs.Take(6).Select(r => r.Label));
            SkState? after = null;
            bool merged = false;
            foreach (var id in ids)
            {
                d.ClearSelection2(true);
                SelectAll(d, refs, 0);
                d.SketchAddConstraints(id); // silently ignores ids that don't apply
                d.ClearSelection2(true);
                var now = SkState.Take(sketch);
                merged = id == "sgMERGEPOINTS" && now.Points < before.Points;
                if (now.Relations > before.Relations || merged) { after = now; break; }
                if (!Diff(before, now).None)
                {
                    string rb0 = Undo(ctx, d, name, before);
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{id}' moved the sketch but added no relation. {rb0}");
                }
            }
            if (after is null)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"SOLIDWORKS ignored the relation '{rel}' for {labels}: no relation was added.",
                    "It may not apply to these entity types, already exist, or follow from other relations (sketch_info lists them).");
            var checks = new List<string> { merged ? $"points merged ({before.Points} → {after.Points} sketch points)" : $"relations {before.Relations} → {after.Relations}" };
            if (BecameOverDefined(sketch, before.Status))
            {
                string state = ConstrainedName(sketch.GetConstrainedStatus());
                string rb = Undo(ctx, d, name, before);
                throw new SwException(ErrorCodes.VerifyFailed, $"'{rel}' conflicts with existing relations or dimensions (the sketch became {state}). {rb}",
                    "Delete the conflicting relation or dimension first, or relate other entities.");
            }
            if (!is3D && !merged)
            {
                var nowItems = refs.Select(r => ItemOf(r, m2s)).ToList();
                if (RelationGeometry(rel, nowItems, checks) is string problem)
                {
                    string rb = Undo(ctx, d, name, before);
                    throw new SwException(ErrorCodes.VerifyFailed, $"After '{rel}' the geometry doesn't satisfy it: {problem}. {rb}",
                        "The sketch may not solve with this relation; check sketch_info.");
                }
            }
            var diff = Diff(before, after);
            var related = refs.Where(r => r.Obj is ISketchSegment).Select(r => ((ISketchSegment)r.Obj).GetName())
                .Where(after.Segs.ContainsKey).Distinct().ToList();
            return new SketchRelationResult
            {
                Doc = ctx.Title(d),
                Sketch = name,
                Relation = rel,
                RelationsBefore = before.Relations,
                RelationsAfter = after.Relations,
                Constrained = ConstrainedName(after.Status),
                Segments = related.Count == 0 ? null : related.Select(x => Info(x, after.Segs[x], is3D)).ToList(),
                Changed = NullIfEmpty(diff.Changed.Where(x => !related.Contains(x)).Take(ListMax).Select(x => Info(x, after.Segs[x], is3D, before.Segs[x].Length)).ToList()),
                Checks = checks,
                Resolved = refs.Select(r => r.Info("entities")).ToList(),
            };
        }, RunOptions.Write(doc), ct);
    }

    private static void CheckRelationTargets(string rel, IReadOnlyList<Item> it)
    {
        if (it.Any(i => i.Kind is "external" or "other")) return; // SOLIDWORKS decides for model geometry
        string got = string.Join(" + ", it.Select(i => i.Kind));
        bool lines = it.All(i => i.Kind == "line");
        bool round(Item i) => i.Kind is "arc" or "circle";
        string? problem = rel switch
        {
            "horizontal" or "vertical" => lines || it.All(i => i.Kind == "point") && it.Count >= 2 ? null : "lines, or two or more points",
            "coincident" => it.Any(i => i.Kind == "point") ? null : "a point and a point, line or arc",
            "collinear" or "parallel" or "perpendicular" => lines ? null : "lines",
            "tangent" => it.Any(round) && it.All(i => i.Kind is "line" or "arc" or "circle") ? null : "an arc or circle and a line, arc or circle",
            "concentric" => it.Any(round) && it.All(i => round(i) || i.Kind == "point") ? null : "arcs/circles (or a point and an arc/circle)",
            "equal" => lines || it.All(round) ? null : "all lines, or all arcs/circles",
            "symmetric" => it[^1].Kind == "line" ? null : "two entities and a symmetry line",
            "midpoint" => it.Count(i => i.Kind == "point") == 1 && it.Count(i => i.Kind == "line") == 1 ? null : "a point and a line",
            "intersection" => it.Count(i => i.Kind == "point") == 1 && it.Count(i => i.Kind == "line") == 2 ? null : "a point and two lines",
            "merge" => it.All(i => i.Kind == "point") ? null : "two sketch points",
            _ => null,
        };
        if (problem is not null)
            throw SwException.InvalidArgument($"'{rel}' takes {problem} (got {got}).", "sketch_info shows each segment's kind; points are {at:[x,y]}.");
    }

    /// <summary>Tier-B check of a relation on the solved geometry; returns the problem or null.</summary>
    private static string? RelationGeometry(string rel, IReadOnlyList<Item> it, List<string> checks)
    {
        if (it.Any(i => i.Kind is "external" or "other")) return null;
        bool lines = it.All(i => i.Kind == "line");
        bool points = it.All(i => i.Kind == "point");
        bool round(Item i) => i.Kind is "arc" or "circle";
        string L(Item i) => i.R.Label;
        switch (rel)
        {
            case "horizontal" or "vertical":
            {
                bool h = rel == "horizontal";
                if (lines)
                {
                    foreach (var l in it)
                        if (Math.Abs(h ? l.B.Y - l.A.Y : l.B.X - l.A.X) > Tol) return $"{L(l)} is not {rel}";
                    checks.Add($"{rel}: {it.Count} line(s)");
                }
                else if (points)
                {
                    if (it.Any(p => Math.Abs(h ? p.P.Y - it[0].P.Y : p.P.X - it[0].P.X) > Tol)) return $"the points are not {(h ? "level" : "plumb")}";
                    checks.Add($"{rel}: points aligned");
                }
                return null;
            }
            case "parallel" or "collinear" or "perpendicular" when lines:
            {
                var d0 = (it[0].B - it[0].A).Normalized();
                foreach (var l in it.Skip(1))
                {
                    var di = (l.B - l.A).Normalized();
                    if (rel == "perpendicular" ? Math.Abs(d0.Dot(di)) > AngTol : Math.Abs(Cross2(d0, di)) > AngTol)
                        return $"{L(l)} is not {rel} to {L(it[0])}";
                    if (rel == "collinear" && PointLineDistance(l.A, it[0].A, it[0].B) > Tol) return $"{L(l)} is parallel but not on the line of {L(it[0])}";
                }
                checks.Add(rel);
                return null;
            }
            case "equal":
            {
                double v0 = lines ? it[0].A.DistanceTo(it[0].B) : it[0].Radius;
                foreach (var x in it.Skip(1))
                    if (Math.Abs((lines ? x.A.DistanceTo(x.B) : x.Radius) - v0) > Tol) return $"{L(x)} differs from {L(it[0])}";
                checks.Add(lines ? $"equal length {Units.Mm(v0)} mm" : $"equal radius {Units.Mm(v0)} mm");
                return null;
            }
            case "concentric":
            {
                var c0 = it[0].Anchor;
                if (it.Any(x => x.Anchor.DistanceTo(c0) > Tol)) return "the centers differ";
                checks.Add("common center");
                return null;
            }
            case "coincident" or "midpoint":
            {
                var p = it.First(i => i.Kind == "point");
                var o = it.First(i => !ReferenceEquals(i, p));
                double dist = rel == "midpoint" ? p.P.DistanceTo((o.A + o.B) / 2) : o.Kind switch
                {
                    "point" => p.P.DistanceTo(o.P),
                    "line" => PointLineDistance(p.P, o.A, o.B),
                    "arc" or "circle" => Math.Abs(p.P.DistanceTo(o.C) - o.Radius),
                    _ => 0,
                };
                if (dist > Tol) return $"the point is {Units.Mm(dist)} mm off {(rel == "midpoint" ? "the midpoint of " : "")}{L(o)}";
                checks.Add(rel == "midpoint" ? "point at the midpoint" : "point on the entity");
                return null;
            }
            case "tangent":
            {
                var a = it.First(round);
                var o = it.First(i => !ReferenceEquals(i, a));
                double err = o.Kind == "line"
                    ? Math.Abs(PointLineDistance(a.C, o.A, o.B) - a.Radius)
                    : Math.Min(Math.Abs(a.C.DistanceTo(o.C) - (a.Radius + o.Radius)), Math.Abs(a.C.DistanceTo(o.C) - Math.Abs(a.Radius - o.Radius)));
                if (err > Tol) return $"{L(a)} and {L(o)} are not tangent ({Units.Mm(err)} mm off)";
                checks.Add("tangent");
                return null;
            }
            case "symmetric" when it is [{ Kind: "point" } p1, { Kind: "point" } p2, { Kind: "line" } axis]:
            {
                double err = Reflect(p1.P, axis.A, axis.B).DistanceTo(p2.P);
                if (err > Tol) return $"the points are not mirror images about {L(axis)} ({Units.Mm(err)} mm off)";
                checks.Add("points symmetric");
                return null;
            }
            case "intersection" when it.Count(i => i.Kind == "line") == 2:
            {
                var p = it.First(i => i.Kind == "point");
                var ls = it.Where(i => i.Kind == "line").ToList();
                if (LineIntersection(ls[0].A, ls[0].B, ls[1].A, ls[1].B) is not Vec3 x) return "the lines are parallel";
                if (x.DistanceTo(p.P) > Tol) return $"the point is {Units.Mm(x.DistanceTo(p.P))} mm from the intersection";
                checks.Add("point at the intersection");
                return null;
            }
        }
        return null;
    }

    // ================================================================================================== sketch_trim

    public Task<SketchChangeResult> TrimAsync(string? doc, double[]? at, CancellationToken ct = default)
    {
        CheckPoint(at, "at", required: true);
        return session.RunAsync("sketch_trim", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_trim", twoD: true);
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var p = ToSketch(at!, m2s, "at", ctx);
            var before = SkState.Take(sketch);
            if (before.Segs.Count == 0) throw SwException.InvalidArgument("The sketch has no segments to trim.");
            var nearest = before.Segs.Select(kv => (Name: kv.Key, Dist: DistanceTo(kv.Value, p))).Where(x => double.IsFinite(x.Dist))
                .OrderBy(x => x.Dist).FirstOrDefault();
            // Verified recipe: select the segment, then SketchTrim(closest, x, y, 0) with the pick point in sketch coordinates.
            // Only lines, arcs and circles can be measured here; other kinds (splines, ellipses) are left to SketchTrim's own pick.
            bool measurable = nearest.Name is not null && nearest.Dist <= TrimReach;
            if (!measurable && !before.Segs.Values.Any(g => g.Kind is "ellipse" or "spline" or "parabola" or "other"))
                throw new SwException(ErrorCodes.EntityNotFound,
                    $"No segment within {Units.Mm(TrimReach)} mm of [{TopoQuery.Fmt(P2(p))}]" +
                    (nearest.Name is null ? "." : $"; the nearest is {nearest.Name}, {Units.Mm(nearest.Dist)} mm away."),
                    "Pass a point on the piece to remove (sketch_info lists the segments and their end points).");
            d.ClearSelection2(true);
            if (measurable && FindSegment(sketch, nearest.Name!) is { } target && !target.Select4(false, null))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {nearest.Name} to trim it.");
            bool ok = d.SketchManager.SketchTrim((int)swSketchTrimChoice_e.swSketchTrimClosest, p.X, p.Y, 0); // sketch coordinates (verified)
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            if (diff.None)
                throw new SwException(ErrorCodes.VerifyFailed, $"Nothing was trimmed at [{TopoQuery.Fmt(P2(p))}] (SketchTrim returned {ok}).",
                    "Pick a point on the piece to remove" +
                    (nearest.Name is null ? "." : $" (the nearest segment is {nearest.Name}, {Units.Mm(nearest.Dist)} mm away)."));
            var checks = new List<string> { measurable ? $"trimmed {nearest.Name}" : "geometry changed" };
            if (measurable)
            {
                // The picked segment is gone, shorter, or split into two pieces.
                bool shorter = !after.Segs.TryGetValue(nearest.Name!, out var g1) || g1.Length < before.Segs[nearest.Name!].Length - Tol;
                if (!shorter)
                    ctx.Warn($"{nearest.Name} is not shorter after the trim; check the result with sketch_info.");
            }
            double left = after.Segs.Values.Select(g => DistanceTo(g, p)).Where(double.IsFinite).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (left > 1e-6) checks.Add("the picked piece is gone");
            else ctx.Warn("A segment still passes through the pick point (another entity crosses there?); check the result.");
            return Change(ctx, d, sketch, "trim", before, after, diff, checks);
        }, RunOptions.Write(doc), ct);
    }

    // ================================================================================================ sketch_extend

    public Task<SketchChangeResult> ExtendAsync(string? doc, EntityRef? segment, string? end = "auto", CancellationToken ct = default)
    {
        if (segment is null) throw SwException.InvalidArgument("'segment' is required, e.g. {name:'Line1'}.");
        string which = Lower(end ?? "auto");
        if (which is not ("auto" or "start" or "end")) throw SwException.InvalidArgument($"end '{end}' is not supported.", "Use auto, start or end.");
        return session.RunAsync("sketch_extend", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_extend", twoD: true);
            string sketchName = ((IFeature)sketch).Name;
            var s2m = SketchService.SketchToModel(sketch);
            var m2s = s2m.Inverse();
            var r = Refs(ctx, d, sketch, m2s, [segment], "segment", EntityKind.SketchSegment)[0];
            var seg = (ISketchSegment)r.Obj;
            string name = seg.GetName();
            var before = SkState.Take(sketch);
            var g = before.Segs[name];
            if (g.Kind is not ("line" or "arc"))
                throw SwException.InvalidArgument($"{r.Label} is a {g.Kind}; only lines and arcs can be extended.");
            var others = before.Segs.Where(kv => kv.Key != name).Select(kv => kv.Value).ToList();
            double aheadEnd = Ahead(g, true, others), aheadStart = Ahead(g, false, others);
            bool[] ends = which switch
            {
                "start" => [false],
                "end" => [true],
                _ => aheadEnd <= aheadStart ? [true, false] : [false, true],
            };
            foreach (bool atEnd in ends)
            {
                var pick = PickNear(g, atEnd);
                var places = new List<Vec3> { pick };
                if (!s2m.IsIdentity()) places.Add(s2m.ApplyPoint(pick)); // the coordinate space of SketchExtend is unverified
                foreach (var place in places)
                {
                    d.ClearSelection2(true);
                    if (!SelectOne(d, r, false, 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.");
                    bool ok = d.SketchManager.SketchExtend(place.X, place.Y, place.Z);
                    d.ClearSelection2(true);
                    var after = SkState.Take(sketch);
                    var diff = Diff(before, after);
                    if (after.Segs.TryGetValue(name, out var ng) && ng.Length > g.Length + Tol)
                    {
                        var checks = new List<string>
                        {
                            $"{name} {Units.Mm(g.Length)} → {Units.Mm(ng.Length)} mm (its {(atEnd ? "end" : "start")} extended)",
                        };
                        return Change(ctx, d, sketch, "extend", before, after, diff, checks, [r.Info("segment")]);
                    }
                    if (!diff.None)
                    {
                        string rb = Undo(ctx, d, sketchName, before);
                        throw new SwException(ErrorCodes.VerifyFailed, $"SketchExtend changed the sketch without lengthening {name} (returned {ok}). {rb}");
                    }
                }
            }
            throw new SwException(ErrorCodes.VerifyFailed,
                $"{name} could not be extended: no segment lies ahead of {(which == "auto" ? "either end" : $"its {which}")}.",
                "Extend reaches the next segment along the line (or the arc's circle); draw or move the target first.");
        }, RunOptions.Write(doc), ct);
    }

    /// <summary>A point on the segment close to one end (what a user clicks with the Extend tool).</summary>
    private static Vec3 PickNear(Geo g, bool atEnd)
    {
        if (g.Kind == "line") return atEnd ? g.End + (g.Start - g.End) * 0.1 : g.Start + (g.End - g.Start) * 0.1;
        double a0 = Ang(g.Start - g.Center);
        double sweep = Sweep(g);
        double a = a0 + g.Dir * sweep * (atEnd ? 0.9 : 0.1);
        return g.Center + new Vec3(Math.Cos(a), Math.Sin(a), 0) * g.Radius;
    }

    /// <summary>Distance along the extension of one end to the nearest other segment (∞ when none is ahead).</summary>
    private static double Ahead(Geo g, bool atEnd, IReadOnlyList<Geo> others)
    {
        double best = double.PositiveInfinity;
        if (g.Kind == "line")
        {
            var o = atEnd ? g.End : g.Start;
            var dir = (atEnd ? g.End - g.Start : g.Start - g.End).Normalized();
            foreach (var x in others)
                foreach (var t in RayHits(o, dir, x))
                    best = Math.Min(best, t);
            return best;
        }
        // An arc extends along its circle, away from the end, up to where the arc itself starts again.
        var from = atEnd ? g.End : g.Start;
        int sense = atEnd ? g.Dir : -g.Dir;
        double free = 2 * Math.PI - Sweep(g);
        foreach (var x in others)
            foreach (var p in CircleHits(g.Center, g.Radius, x))
            {
                double da = NormAngle(sense * (Ang(p - g.Center) - Ang(from - g.Center)));
                if (da > 1e-9 && da < free - 1e-9) best = Math.Min(best, da * g.Radius);
            }
        return best;
    }

    // ================================================================================================ sketch_offset

    public Task<SketchChangeResult> OffsetAsync(string? doc, IReadOnlyList<EntityRef>? segments, double distanceMm, string? side = "outward",
        bool chain = false, CancellationToken ct = default)
    {
        double dist = Units.PositiveM(distanceMm, "distanceMm");
        string sd = Lower(side ?? "outward");
        if (sd is not ("outward" or "inward" or "both")) throw SwException.InvalidArgument($"side '{side}' is not supported.", "Use outward, inward or both.");
        if (segments is null || segments.Count == 0)
            throw SwException.InvalidArgument("'segments' must name at least one segment (e.g. the four lines of a rectangle, or one of them with chain=true).");
        return session.RunAsync("sketch_offset", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_offset", twoD: true);
            string sketchName = ((IFeature)sketch).Name;
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var refs = Refs(ctx, d, sketch, m2s, segments, "segments", EntityKind.SketchSegment);
            var before = SkState.Take(sketch);
            var seeds = refs.Select(r => ((ISketchSegment)r.Obj).GetName()).ToList();
            var loop = (chain ? Chain(before, seeds) : seeds).Select(n => before.Segs[n]).ToList();
            bool closed = IsClosedLoop(loop);
            var ob = BoxOf(loop);
            double[] signs = sd switch { "inward" => [-1, 1], "both" => [1], _ => [1, -1] };
            string? lastProblem = null;
            foreach (double sign in signs)
            {
                d.ClearSelection2(true);
                SelectAll(d, refs, 0);
                bool ok = d.SketchManager.SketchOffset2(sign * dist, sd == "both", chain,
                    (int)swSkOffsetCapEndType_e.swSkOffsetNoCaps, (int)swSkOffsetMakeConstructionType_e.swSkOffsetDontMakeConstruction, false);
                d.ClearSelection2(true);
                var after = SkState.Take(sketch);
                var diff = Diff(before, after);
                if (diff.Created.Count == 0)
                {
                    if (!diff.None)
                    {
                        string rb0 = Undo(ctx, d, sketchName, before);
                        throw new SwException(ErrorCodes.VerifyFailed, $"SketchOffset2 changed the sketch but created no offset segments. {rb0}");
                    }
                    lastProblem = $"SketchOffset2 with {Units.Mm(sign * dist)} mm created nothing (returned {ok})";
                    continue;
                }
                var made = diff.Created.Select(n => after.Segs[n]).ToList();
                var nb = BoxOf(made);
                string got = closed ? Classify(ob, nb, made, dist) : "open";
                int matched = made.Count(m => OffsetMatches(m, loop, dist));
                if ((!closed || got == sd) && matched > 0)
                {
                    var checks = new List<string>
                    {
                        closed ? $"{got}: loop {Size(ob)} → {Size(nb)} mm" : "open chain: offset to SOLIDWORKS' side for this sign",
                        $"{matched} of {made.Count} new segments lie {Units.Mm(dist)} mm from the originals",
                    };
                    if (sign < 0) checks.Add("inward = negative distance");
                    if (matched < made.Count) ctx.Warn($"{made.Count - matched} new segment(s) are not at {Units.Mm(dist)} mm from an original (corner arcs or joins?); check sketch_info.");
                    return Change(ctx, d, sketch, "offset", before, after, diff, checks, refs.Select(x => x.Info("segments")).ToList(),
                        side: got, box: [Units.Mm(nb.X0), Units.Mm(nb.Y0), Units.Mm(nb.X1), Units.Mm(nb.Y1)]);
                }
                string rb = Undo(ctx, d, sketchName, before);
                lastProblem = $"with {Units.Mm(sign * dist)} mm the result went {got} (loop {Size(ob)} → {Size(nb)} mm, {matched} of {made.Count} segments at the distance); {rb}";
                if (!rb.StartsWith("Rolled back", StringComparison.Ordinal)) break;
            }
            throw new SwException(ErrorCodes.VerifyFailed, $"No {sd} offset of {Units.Mm(dist)} mm could be verified: {lastProblem}.",
                "Check that the segments form the loop you mean (chain=true takes the whole connected loop); an inward offset larger than the loop fails.");
        }, RunOptions.Write(doc), ct);
    }

    private static string Classify(Box2 ob, Box2 nb, IReadOnlyList<Geo> made, double d)
    {
        double t = d - Tol;
        bool outer = nb.X0 <= ob.X0 - t && nb.X1 >= ob.X1 + t && nb.Y0 <= ob.Y0 - t && nb.Y1 >= ob.Y1 + t;
        bool Inside(Box2 b) => b.X0 >= ob.X0 + t && b.X1 <= ob.X1 - t && b.Y0 >= ob.Y0 + t && b.Y1 <= ob.Y1 - t;
        bool inner = made.Any(m => Inside(BoxOf([m])));
        if (outer && inner) return "both";
        if (outer) return "outward";
        if (Inside(nb)) return "inward";
        return "unclear";
    }

    private static bool OffsetMatches(Geo m, IReadOnlyList<Geo> orig, double d) => m.Kind switch
    {
        "line" => orig.Any(o => o.Kind == "line" && Math.Abs(Cross2((m.End - m.Start).Normalized(), (o.End - o.Start).Normalized())) < AngTol &&
                               Math.Abs(PointLineDistance(m.Start, o.Start, o.End) - d) < Tol),
        "arc" or "circle" =>
            orig.Any(o => o.Kind is "arc" or "circle" && o.Center.DistanceTo(m.Center) < Tol &&
                          (Math.Abs(m.Radius - (o.Radius + d)) < Tol || Math.Abs(m.Radius - (o.Radius - d)) < Tol)) ||
            (Math.Abs(m.Radius - d) < Tol && orig.Any(o => o.HasEnds && (o.Start.DistanceTo(m.Center) < Tol || o.End.DistanceTo(m.Center) < Tol))),
        _ => false,
    };

    /// <summary>The seeds plus every non-construction segment connected to them end to end.</summary>
    private static List<string> Chain(SkState s, IReadOnlyList<string> seeds)
    {
        var names = new List<string>(seeds);
        var seen = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);
        while (queue.Count > 0)
        {
            var g = s.Segs[queue.Dequeue()];
            if (!g.HasEnds) continue;
            foreach (var (n, o) in s.Segs)
            {
                if (seen.Contains(n) || o.Construction || !o.HasEnds) continue;
                if (Touch(g, o)) { seen.Add(n); names.Add(n); queue.Enqueue(n); }
            }
        }
        return names;
    }

    // ================================================================================================ sketch_fillet

    public Task<SketchChangeResult> FilletAsync(string? doc, IReadOnlyList<EntityRef>? segments, double[]? at, double radiusMm, CancellationToken ct = default)
    {
        double r = Units.PositiveM(radiusMm, "radiusMm");
        CheckCorner(segments, at);
        return session.RunAsync("sketch_fillet", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_fillet", twoD: true);
            string sketchName = ((IFeature)sketch).Name;
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var (a, b, _) = CornerOf(ctx, d, sketch, m2s, segments, at);
            string an = ((ISketchSegment)a.Obj).GetName(), bn = ((ISketchSegment)b.Obj).GetName();
            var before = SkState.Take(sketch);
            d.ClearSelection2(true);
            SelectAll(d, [a, b], 0);
            // KeepGeometry: never the interactive (prompting) option for corners that carry relations or dimensions.
            var made = d.SketchManager.CreateFillet(r, (int)swConstrainedCornerAction_e.swConstrainedCornerKeepGeometry);
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            var arcs = diff.Created.Where(n => after.Segs[n].Kind == "arc").ToList();
            if (arcs.Count == 0)
            {
                string rb = diff.None ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no fillet arc of {Units.Mm(r)} mm between {an} and {bn}{(made is null ? "" : " (it returned a segment)")}. {rb}",
                    "The radius may be too large for the segments, or they don't form a corner.");
            }
            var good = arcs.Where(n => Math.Abs(after.Segs[n].Radius - r) < Tol).ToList();
            if (good.Count == 0)
            {
                string rb = Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The fillet arc has radius {string.Join(", ", arcs.Select(n => Units.Mm(after.Segs[n].Radius)))} mm, not {Units.Mm(r)} mm. {rb}");
            }
            var arc = after.Segs[good[0]];
            var checks = new List<string> { $"arc {good[0]} r {Units.Mm(arc.Radius)} mm" };
            if (after.Segs.GetValueOrDefault(an) is { Kind: "line" } la && after.Segs.GetValueOrDefault(bn) is { Kind: "line" } lb)
            {
                bool tangent = Math.Abs(PointLineDistance(arc.Center, la.Start, la.End) - r) < Tol &&
                               Math.Abs(PointLineDistance(arc.Center, lb.Start, lb.End) - r) < Tol;
                if (tangent) checks.Add("tangent to both lines");
                else ctx.Warn("The fillet arc is not tangent to both lines; check sketch_info.");
            }
            return Change(ctx, d, sketch, "fillet", before, after, diff, checks, [a.Info("segments"), b.Info("segments")]);
        }, RunOptions.Write(doc), ct);
    }

    // =============================================================================================== sketch_chamfer

    public Task<SketchChangeResult> ChamferAsync(string? doc, IReadOnlyList<EntityRef>? segments, double[]? at, string? type, double distanceMm,
        double? distance2Mm = null, double? angleDeg = null, CancellationToken ct = default)
    {
        string t = Lower(type ?? "equal");
        double d1 = Units.PositiveM(distanceMm, "distanceMm");
        int apiType;
        double second;
        switch (t)
        {
            case "equal":
                if (distance2Mm is not null || angleDeg is not null)
                    throw SwException.InvalidArgument("type 'equal' takes only distanceMm.", "Use distance_distance or distance_angle for unequal chamfers.");
                apiType = (int)swSketchChamferType_e.swSketchChamfer_DistanceEqual;
                second = d1;
                break;
            case "distance_distance":
                if (distance2Mm is not double dd2) throw SwException.InvalidArgument("type 'distance_distance' needs distance2Mm.");
                apiType = (int)swSketchChamferType_e.swSketchChamfer_DistanceDistance;
                second = Units.PositiveM(dd2, "distance2Mm");
                break;
            case "distance_angle":
                if (angleDeg is not double ad || !double.IsFinite(ad) || ad <= 0 || ad >= 180)
                    throw SwException.InvalidArgument("type 'distance_angle' needs angleDeg between 0 and 180.");
                apiType = (int)swSketchChamferType_e.swSketchChamfer_DistanceAngle;
                second = Units.Rad(ad);
                break;
            default:
                throw SwException.InvalidArgument($"type '{type}' is not supported.", "Use distance_distance, distance_angle or equal.");
        }
        CheckCorner(segments, at);
        return session.RunAsync("sketch_chamfer", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_chamfer", twoD: true);
            string sketchName = ((IFeature)sketch).Name;
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var (a, b, corner) = CornerOf(ctx, d, sketch, m2s, segments, at);
            string an = ((ISketchSegment)a.Obj).GetName(), bn = ((ISketchSegment)b.Obj).GetName();
            var before = SkState.Take(sketch);
            d.ClearSelection2(true);
            SelectAll(d, [a, b], 0);
            var made = d.SketchManager.CreateChamfer(apiType, d1, second);
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            var lines = diff.Created.Where(n => after.Segs[n].Kind == "line").ToList();
            if (lines.Count == 0)
            {
                string rb = diff.None ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no chamfer between {an} and {bn}{(made is null ? "" : " (it returned a segment)")}. {rb}",
                    "The distances may be too large for the segments, or they don't form a corner.");
            }
            var checks = new List<string>();
            var cl = after.Segs[lines[0]];
            if (corner is Vec3 pc)
            {
                double da = cl.Start.DistanceTo(pc), db = cl.End.DistanceTo(pc);
                bool Near(double x, double y) => Math.Abs(x - y) < Tol;
                bool ok = t switch
                {
                    "equal" => Near(da, d1) && Near(db, d1),
                    "distance_distance" => (Near(da, d1) && Near(db, second)) || (Near(da, second) && Near(db, d1)),
                    _ => AngleAt(cl.Start, cl.End, pc, d1, second) || AngleAt(cl.End, cl.Start, pc, d1, second),
                };
                if (!ok)
                {
                    string rb = Undo(ctx, d, sketchName, before);
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"The chamfer line's ends are {Units.Mm(da)} and {Units.Mm(db)} mm from the corner [{TopoQuery.Fmt(P2(pc))}], which doesn't match the request. {rb}");
                }
                checks.Add($"chamfer {lines[0]}: ends {Units.Mm(da)} / {Units.Mm(db)} mm from the corner");
            }
            else
            {
                checks.Add($"chamfer line {lines[0]} created (corner not computable, distances not checked)");
            }
            return Change(ctx, d, sketch, "chamfer", before, after, diff, checks, [a.Info("segments"), b.Info("segments")]);
        }, RunOptions.Write(doc), ct);
    }

    /// <summary>distance-angle check: <paramref name="e1"/> lies <paramref name="dist"/> from the corner and the chamfer makes the angle with that side.</summary>
    private static bool AngleAt(Vec3 e1, Vec3 e2, Vec3 corner, double dist, double angle)
    {
        if (Math.Abs(e1.DistanceTo(corner) - dist) > Tol) return false;
        var u = (corner - e1).Normalized();
        var v = (e2 - e1).Normalized();
        double a = Math.Acos(Math.Clamp(u.Dot(v), -1, 1));
        return Math.Abs(a - angle) < AngTol * 10 || Math.Abs(a - (Math.PI - angle)) < AngTol * 10;
    }

    // ================================================================================================ sketch_pattern

    public Task<SketchChangeResult> PatternAsync(SketchPatternRequest q, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(q);
        string kind = Lower(q.Kind);
        if (q.Entities is null || q.Entities.Count == 0) throw SwException.InvalidArgument("'entities' must name at least one segment or point to copy.");
        switch (kind)
        {
            case "linear":
            {
                int nx = q.CountX ?? 1, ny = q.CountY ?? 1;
                if (nx < 1 || ny < 1) throw SwException.InvalidArgument("countX and countY must be at least 1 (they include the seed).");
                if (nx * ny < 2) throw SwException.InvalidArgument("A pattern needs at least 2 instances (countX × countY, seed included).", "E.g. countX: 3, spacingXMm: 20.");
                if (nx * ny > MaxInstances) throw SwException.InvalidArgument($"At most {MaxInstances} instances per call.");
                if (nx > 1) Units.PositiveM(q.SpacingXMm ?? 0, "spacingXMm");
                if (ny > 1) Units.PositiveM(q.SpacingYMm ?? 0, "spacingYMm");
                if (q.AngleDeg is double ang && !double.IsFinite(ang)) throw SwException.InvalidArgument("angleDeg must be a finite number.");
                break;
            }
            case "circular":
            {
                int n = q.Count ?? q.CountX ?? 0;
                if (n < 2) throw SwException.InvalidArgument("A circular pattern needs count ≥ 2 (the seed included).");
                if (n > MaxInstances) throw SwException.InvalidArgument($"At most {MaxInstances} instances per call.");
                CheckPoint(q.Center, "center", required: true);
                if (!double.IsFinite(q.TotalAngleDeg) || q.TotalAngleDeg <= 0 || q.TotalAngleDeg > 360)
                    throw SwException.InvalidArgument("totalAngleDeg must be in (0, 360].");
                break;
            }
            default:
                throw SwException.InvalidArgument($"kind '{q.Kind}' is not supported.", "Use linear or circular.");
        }
        return session.RunAsync("sketch_pattern", ctx => kind == "linear" ? LinearPattern(ctx, q) : CircularPattern(ctx, q), RunOptions.Write(q.Doc), ct);
    }

    private static SketchChangeResult LinearPattern(SwContext ctx, SketchPatternRequest q)
    {
        var d = DocOf(ctx);
        var sketch = OpenSketch(ctx, d, "sketch_pattern", twoD: true);
        string sketchName = ((IFeature)sketch).Name;
        var m2s = SketchService.SketchToModel(sketch).Inverse();
        var refs = Refs(ctx, d, sketch, m2s, q.Entities!, "entities", SketchKinds);
        var before = SkState.Take(sketch);
        var seeds = SeedGeos(refs, before);
        int pointSeeds = refs.Count(r => r.Obj is ISketchPoint);
        int nx = q.CountX ?? 1, ny = q.CountY ?? 1;
        double dx = nx > 1 ? Units.M(q.SpacingXMm!.Value) : 0, dy = ny > 1 ? Units.M(q.SpacingYMm!.Value) : 0;
        double ax = Units.Rad(q.AngleDeg ?? 0);
        var attempts = new List<(double Ax, double Ay)> { (ax, ax + Math.PI / 2) };
        for (int i = 0; i < attempts.Count && i < 2; i++)
        {
            var (a1, a2) = attempts[i];
            d.ClearSelection2(true);
            SelectAll(d, refs, 0);
            bool ok = d.SketchManager.CreateLinearSketchStepAndRepeat(nx, ny, dx > 0 ? dx : Math.Max(dy, 0.001), dy > 0 ? dy : Math.Max(dx, 0.001),
                a1, a2, "", false, false, false, false, false);
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            if (diff.Created.Count == 0 && after.Points == before.Points)
            {
                string rb0 = diff.None ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no pattern instances (returned {ok}). {rb0}",
                    "Check the entities and spacing (instances may not overlap the seed).");
            }
            var made = Copies(diff, after, seeds);
            Vec3 Dir(double a) => new(Math.Cos(a), Math.Sin(a), 0);
            var u = Dir(a1);
            var v = Dir(a2);
            List<Geo> Predict(Vec3 du, Vec3 dv)
            {
                var list = new List<Geo>();
                foreach (var s in seeds)
                    for (int ix = 0; ix < nx; ix++)
                        for (int iy = 0; iy < ny; iy++)
                            if (ix != 0 || iy != 0) list.Add(s with { Center = s.Anchor + du * (ix * dx) + dv * (iy * dy) });
                return list;
            }
            bool countsOk = seeds.Count > 0 ? made.Count == seeds.Count * (nx * ny - 1)
                                            : after.Points - before.Points == pointSeeds * (nx * ny - 1);
            if (countsOk && (seeds.Count == 0 || MatchAll(made, Predict(u, v))))
            {
                var checks = new List<string>
                {
                    seeds.Count > 0 ? $"{made.Count} new segment(s) at the predicted positions" : $"{after.Points - before.Points} new point(s)",
                };
                if (i > 0) checks.Add("direction angle flipped by 180° to match the requested direction");
                return Change(ctx, d, sketch, "pattern", before, after, diff, checks, refs.Select(r => r.Info("entities")).ToList());
            }
            if (i == 0 && countsOk && seeds.Count > 0)
            {
                foreach (var (su, sv) in new[] { (-1.0, 1.0), (1.0, -1.0), (-1.0, -1.0) })
                    if (MatchAll(made, Predict(u * su, v * sv)))
                    {
                        attempts.Add((su < 0 ? a1 + Math.PI : a1, sv < 0 ? a2 + Math.PI : a2));
                        break;
                    }
            }
            string rb = Undo(ctx, d, sketchName, before);
            if (attempts.Count == i + 1 || !rb.StartsWith("Rolled back", StringComparison.Ordinal))
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"The pattern made {(seeds.Count > 0 ? $"{made.Count} segment(s)" : $"{after.Points - before.Points} point(s)")} " +
                    $"where {(seeds.Count > 0 ? seeds.Count : pointSeeds) * (nx * ny - 1)} were expected at the requested spacing. {rb}");
        }
        throw new SwException(ErrorCodes.VerifyFailed, "The linear pattern could not be verified.");
    }

    private static SketchChangeResult CircularPattern(SwContext ctx, SketchPatternRequest q)
    {
        var d = DocOf(ctx);
        var sketch = OpenSketch(ctx, d, "sketch_pattern", twoD: true);
        string sketchName = ((IFeature)sketch).Name;
        var m2s = SketchService.SketchToModel(sketch).Inverse();
        var refs = Refs(ctx, d, sketch, m2s, q.Entities!, "entities", SketchKinds);
        var center = ToSketch(q.Center!, m2s, "center", ctx);
        int n = q.Count ?? q.CountX ?? 2;
        double total = Units.Rad(q.TotalAngleDeg);
        var before = SkState.Take(sketch);
        var seeds = SeedGeos(refs, before);
        int pointSeeds = refs.Count(r => r.Obj is ISketchPoint);
        // The API takes the center as seed + R·(cos A, sin A) (verified for a circle seed). Which point of a general
        // selection is "the seed" is not verified: try the selection's box center, the first seed's anchor, the anchors' centroid.
        var candidates = new List<(Vec3 P, string What)>();
        void AddCand(Vec3 p, string what) { if (!candidates.Any(c => c.P.DistanceTo(p) < Tol)) candidates.Add((p, what)); }
        var allPoints = refs.Where(r => r.Obj is ISketchPoint).Select(r => SketchPointPos((ISketchPoint)r.Obj)).ToList();
        if (seeds.Count > 0)
        {
            var box = BoxOf(seeds);
            AddCand(new Vec3((box.X0 + box.X1) / 2, (box.Y0 + box.Y1) / 2, 0), "the selection's box center");
            AddCand(seeds[0].Anchor, "the first seed's center");
            AddCand(seeds.Aggregate(Vec3.Zero, (s, g) => s + g.Anchor) / seeds.Count, "the seeds' centroid");
        }
        else
        {
            AddCand(allPoints[0], "the first point");
            AddCand(allPoints.Aggregate(Vec3.Zero, (s, p) => s + p) / allPoints.Count, "the points' centroid");
        }
        string? lastProblem = null;
        foreach (var (seed, what) in candidates)
        {
            double radius = seed.DistanceTo(center);
            if (radius < 1e-9) { lastProblem = $"the center lies on {what}"; continue; }
            double angle = Math.Atan2(center.Y - seed.Y, center.X - seed.X);
            d.ClearSelection2(true);
            SelectAll(d, refs, 0);
            bool ok = d.SketchManager.CreateCircularSketchStepAndRepeat(radius, angle, n, total, true, "", false, false, false);
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            if (diff.Created.Count == 0 && after.Points == before.Points)
            {
                string rb0 = diff.None ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS created no pattern instances (returned {ok}). {rb0}",
                    "Check the entities, count and center.");
            }
            var made = Copies(diff, after, seeds);
            string? layout = null;
            if (seeds.Count > 0)
            {
                // Every copy must be a seed rotated about the requested center by k steps (k = 1 … n-1). A full circle is
                // divided into n steps; for a partial angle the convention (n-1 or n steps, which sense) is unverified, so
                // each is tried and the one that matches is reported.
                bool full = Math.Abs(total - 2 * Math.PI) < 1e-9;
                var steps = full ? new[] { total / n } : new[] { total / (n - 1), total / n };
                foreach (double step in steps)
                    foreach (int sense in new[] { 1, -1 })
                        if (layout is null && MatchAll(made, Rotated(seeds, center, n, sense * step)))
                            layout = $"{made.Count} copies rotated in steps of {Units.Deg(step)}° {(full ? "" : sense > 0 ? "counter-clockwise " : "clockwise ")}about [{TopoQuery.Fmt(P2(center))}]";
            }
            else if (after.Points - before.Points == pointSeeds * (n - 1))
            {
                layout = $"{after.Points - before.Points} new point(s) about [{TopoQuery.Fmt(P2(center))}]";
            }
            if (layout is not null)
            {
                var checks = new List<string> { layout, $"seed reference: {what}" };
                return Change(ctx, d, sketch, "pattern", before, after, diff, checks, refs.Select(r => r.Info("entities")).ToList());
            }
            string rb = Undo(ctx, d, sketchName, before);
            lastProblem = $"with {what} as the seed the copies did not rotate about [{TopoQuery.Fmt(P2(center))}]; {rb}";
            if (!rb.StartsWith("Rolled back", StringComparison.Ordinal)) break;
        }
        throw new SwException(ErrorCodes.VerifyFailed, $"The circular pattern could not be verified: {lastProblem}.",
            "Pattern a single circle or arc (its center is the seed), or report this case.");
    }

    /// <summary>The seed segments' geometry (points are counted separately).</summary>
    private static List<Geo> SeedGeos(IEnumerable<Resolved> refs, SkState s) =>
        refs.Where(r => r.Obj is ISketchSegment).Select(r => s.Segs[((ISketchSegment)r.Obj).GetName()]).Where(g => g.Kind != "text").ToList();

    /// <summary>
    /// The created segments that are copies of the seeds: text is skipped, and so are construction helpers SOLIDWORKS may
    /// add (pattern axes, mirror links) unless a seed is construction geometry itself.
    /// </summary>
    private static List<Geo> Copies(SkDiff diff, SkState after, IReadOnlyList<Geo> seeds)
    {
        bool constructionSeeds = seeds.Any(s => s.Construction);
        return diff.Created.Select(n => after.Segs[n]).Where(g => g.Kind != "text" && (constructionSeeds || !g.Construction)).ToList();
    }

    /// <summary>The expected copies of a circular pattern (anchor in <see cref="Geo.Center"/>, as <see cref="MatchAll"/> wants).</summary>
    private static List<Geo> Rotated(IReadOnlyList<Geo> seeds, Vec3 center, int count, double step)
    {
        var list = new List<Geo>();
        foreach (var s in seeds)
            for (int k = 1; k < count; k++)
            {
                double a = k * step, c = Math.Cos(a), sn = Math.Sin(a);
                var v = s.Anchor - center;
                list.Add(s with { Center = center + new Vec3(v.X * c - v.Y * sn, v.X * sn + v.Y * c, 0) });
            }
        return list;
    }

    /// <summary>True when every created segment matches a distinct expected one (kind, size and anchor, the anchor in <see cref="Geo.Center"/>).</summary>
    private static bool MatchAll(IReadOnlyList<Geo> made, IReadOnlyList<Geo> expected)
    {
        if (made.Count != expected.Count) return false;
        var used = new bool[expected.Count];
        foreach (var m in made)
        {
            int k = -1;
            for (int i = 0; i < expected.Count && k < 0; i++)
            {
                var e = expected[i];
                if (!used[i] && e.Kind == m.Kind && Math.Abs(e.Radius - m.Radius) < Tol && Math.Abs(e.Length - m.Length) < Tol * 10 &&
                    e.Center.DistanceTo(m.Anchor) < Tol * 10)
                    k = i;
            }
            if (k < 0) return false;
            used[k] = true;
        }
        return true;
    }

    // ================================================================================================= sketch_mirror

    public Task<SketchChangeResult> MirrorAsync(string? doc, IReadOnlyList<EntityRef>? entities, EntityRef? about, CancellationToken ct = default)
    {
        if (entities is null || entities.Count == 0) throw SwException.InvalidArgument("'entities' must name at least one segment or point to mirror.");
        if (about is null) throw SwException.InvalidArgument("'about' is required: the line or centerline to mirror about, e.g. {name:'Line5'}.");
        return session.RunAsync("sketch_mirror", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_mirror", twoD: true);
            string sketchName = ((IFeature)sketch).Name;
            var m2s = SketchService.SketchToModel(sketch).Inverse();
            var line = Refs(ctx, d, sketch, m2s, [about], "about", EntityKind.SketchSegment)[0];
            var lg = Read((ISketchSegment)line.Obj);
            if (lg.Kind != "line") throw SwException.InvalidArgument($"'about' must be a line or centerline ({line.Label} is a {lg.Kind}).");
            var refs = Refs(ctx, d, sketch, m2s, entities, "entities", SketchKinds);
            if (refs.Any(r => SameEntity(r, line))) throw SwException.InvalidArgument("'entities' must not contain the mirror line itself.");
            var before = SkState.Take(sketch);
            var seeds = SeedGeos(refs, before);
            var expected = seeds.Select(g => g with { Center = Reflect(g.Anchor, lg.Start, lg.End) }).ToList();
            int pointSeeds = refs.Count(r => r.Obj is ISketchPoint);
            // Selection marks for SketchMirror are unverified: entities on mark 1 and the line on mark 2 (like the
            // PropertyManager boxes), then everything on mark 0 with the line last. A wrong result is undone before the
            // next convention is tried.
            string? problem = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                d.ClearSelection2(true);
                SelectAll(d, refs, attempt == 0 ? 1 : 0);
                if (!SelectOne(d, line, true, attempt == 0 ? 2 : 0)) throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {line.Label}.");
                d.SketchMirror();
                d.ClearSelection2(true);
                var after = SkState.Take(sketch);
                var diff = Diff(before, after);
                if (diff.Created.Count == 0 && after.Points == before.Points)
                {
                    if (!diff.None)
                    {
                        string rb0 = Undo(ctx, d, sketchName, before);
                        throw new SwException(ErrorCodes.VerifyFailed, $"SketchMirror changed the sketch but made no copies. {rb0}");
                    }
                    continue;
                }
                var made = Copies(diff, after, seeds);
                bool ok = seeds.Count > 0 ? MatchAll(made, expected) : after.Points - before.Points >= pointSeeds;
                if (ok)
                {
                    var checks = new List<string>
                    {
                        seeds.Count > 0 ? $"{made.Count} mirror cop{(made.Count == 1 ? "y" : "ies")} at the reflected positions" : $"{after.Points - before.Points} new point(s)",
                    };
                    return Change(ctx, d, sketch, "mirror", before, after, diff, checks, [.. refs.Select(r => r.Info("entities")), line.Info("about")]);
                }
                string rb = Undo(ctx, d, sketchName, before);
                problem = $"SketchMirror made {made.Count} segment(s) that are not the {seeds.Count} mirror image(s) about {line.Label}. {rb}";
                if (!rb.StartsWith("Rolled back", StringComparison.Ordinal)) break;
            }
            if (problem is not null) throw new SwException(ErrorCodes.VerifyFailed, problem, "Mirror about a centerline (sketch_add kind centerline).");
            throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS mirrored nothing about {line.Label}.",
                "Check that 'about' is a straight line or centerline of this sketch.");
        }, RunOptions.Write(doc), ct);
    }

    // ================================================================================================ sketch_convert

    public Task<SketchChangeResult> ConvertAsync(string? doc, IReadOnlyList<EntityRef>? entities, CancellationToken ct = default)
    {
        if (entities is null || entities.Count == 0)
            throw SwException.InvalidArgument("'entities' must name at least one model edge or face, e.g. [{type:'edge', at:[x,y,z]}] or [{id:'E3'}].");
        return session.RunAsync("sketch_convert", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_convert", twoD: false);
            string sketchName = ((IFeature)sketch).Name;
            bool is3D = sketch.Is3D();
            var m2s = is3D ? Xform.Identity : SketchService.SketchToModel(sketch).Inverse();
            // A bare model point means an edge here (the common case).
            var norm = entities.Select(e =>
            {
                if (e is null || e.Type is not null || e.Id is not null || e.Where is not null || e.At is not { Length: 3 }) return e!;
                var c = Clone(e);
                c.Type = "edge";
                return c;
            }).ToList();
            var refs = Refs(ctx, d, sketch, m2s, norm, "entities", EntityKind.Edge, EntityKind.Face, EntityKind.SketchSegment);
            foreach (var r in refs.Where(r => r.Obj is ISketchSegment))
                if ((object?)((ISketchSegment)r.Obj).GetSketch() is IFeature owner && string.Equals(owner.Name, sketchName, StringComparison.OrdinalIgnoreCase))
                    throw SwException.InvalidArgument($"{r.Label} is already in {sketchName}.", "Convert takes model edges, faces, or segments of another sketch ({name:'Line1', sketch:'Sketch1'}).");
            double edgeLength = refs.Where(r => r.Obj is IEdge).Sum(r => Topology.Describe((IEdge)r.Obj).Length);
            bool edgesOnly = refs.All(r => r.Obj is IEdge);
            var before = SkState.Take(sketch);
            d.ClearSelection2(true);
            SelectAll(d, refs, 0);
            bool ok = d.SketchManager.SketchUseEdge3(false, false); // verified
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            if (diff.Created.Count == 0)
            {
                string rb = diff.None ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.FeatureFailed, $"Convert Entities created no segments (returned {ok}). {rb}",
                    "Pass model edges or faces; an edge perpendicular to the sketch plane projects to a point.");
            }
            double made = diff.Created.Sum(n => after.Segs[n].Length);
            var checks = new List<string> { $"{diff.Created.Count} new segment(s), {Units.Mm(made)} mm long in total" };
            if (edgesOnly && edgeLength > 0)
            {
                if (Math.Abs(made - edgeLength) < 1e-6) checks.Add("total length equals the edges' length");
                else checks.Add($"projected: the edges are {Units.Mm(edgeLength)} mm long");
            }
            return Change(ctx, d, sketch, "convert", before, after, diff, checks, refs.Select(r => r.Info("entities")).ToList());
        }, RunOptions.Write(doc), ct);
    }

    // ======================================================================================= sketch_construction_set

    public Task<SketchChangeResult> ConstructionSetAsync(string? doc, IReadOnlyList<EntityRef>? segments, bool construction, CancellationToken ct = default)
    {
        if (segments is null || segments.Count == 0) throw SwException.InvalidArgument("'segments' must name at least one segment.");
        return session.RunAsync("sketch_construction_set", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_construction_set", twoD: false);
            bool is3D = sketch.Is3D();
            var m2s = is3D ? Xform.Identity : SketchService.SketchToModel(sketch).Inverse();
            var refs = Refs(ctx, d, sketch, m2s, segments, "segments", EntityKind.SketchSegment);
            var before = SkState.Take(sketch);
            var changed = new List<ISketchSegment>();
            foreach (var r in refs)
            {
                var seg = (ISketchSegment)r.Obj;
                if (seg.ConstructionGeometry == construction) continue;
                seg.ConstructionGeometry = construction;
                if (seg.ConstructionGeometry != construction)
                {
                    foreach (var c in changed) c.ConstructionGeometry = !construction;
                    throw new SwException(ErrorCodes.VerifyFailed,
                        $"{r.Label} did not become {(construction ? "construction" : "regular")} geometry; the other segments were reverted.",
                        "Text and some converted entities can't change their construction state.");
                }
                changed.Add(seg);
            }
            var after = SkState.Take(sketch);
            var diff = Diff(before, after);
            var checks = new List<string>
            {
                changed.Count == refs.Count ? $"{refs.Count} segment(s) now {(construction ? "construction" : "regular")} geometry"
                    : $"{changed.Count} changed; {refs.Count - changed.Count} already {(construction ? "construction" : "regular")}",
            };
            return Change(ctx, d, sketch, "construction_set", before, after, diff, checks, refs.Select(r => r.Info("segments")).ToList());
        }, RunOptions.Write(doc), ct);
    }

    // ================================================================================================= sketch_delete

    public Task<SketchChangeResult> DeleteAsync(string? doc, IReadOnlyList<EntityRef>? segments, CancellationToken ct = default)
    {
        if (segments is null || segments.Count == 0) throw SwException.InvalidArgument("'segments' must name at least one segment (or point) to delete.");
        return session.RunAsync("sketch_delete", ctx =>
        {
            var d = DocOf(ctx);
            var sketch = OpenSketch(ctx, d, "sketch_delete", twoD: false);
            string sketchName = ((IFeature)sketch).Name;
            bool is3D = sketch.Is3D();
            var m2s = is3D ? Xform.Identity : SketchService.SketchToModel(sketch).Inverse();
            var refs = Refs(ctx, d, sketch, m2s, segments, "segments", SketchKinds);
            var names = refs.Where(r => r.Obj is ISketchSegment).Select(r => ((ISketchSegment)r.Obj).GetName()).ToList();
            int points = refs.Count(r => r.Obj is ISketchPoint);
            var before = SkState.Take(sketch);
            d.ClearSelection2(true);
            SelectAll(d, refs, 0);
            d.EditDelete();
            d.ClearSelection2(true);
            var after = SkState.Take(sketch);
            var left = names.Where(after.Segs.ContainsKey).ToList();
            bool pointsGone = points == 0 || after.Points < before.Points;
            if (left.Count > 0 || !pointsGone)
            {
                var diff0 = Diff(before, after);
                string rb = diff0.None && after.Points == before.Points ? "Nothing changed." : Undo(ctx, d, sketchName, before);
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"Not deleted: {(left.Count > 0 ? string.Join(", ", left) : "the point(s)")}. {rb}",
                    "A point that ends a segment goes away only with the segment.");
            }
            var diff = Diff(before, after);
            var checks = new List<string> { names.Count > 0 ? $"{names.Count} segment(s) gone" : $"{before.Points - after.Points} point(s) gone" };
            if (after.Relations < before.Relations) checks.Add($"relations {before.Relations} → {after.Relations}");
            return Change(ctx, d, sketch, "delete", before, after, diff, checks, refs.Select(r => r.Info("segments")).ToList());
        }, RunOptions.Write(doc), ct);
    }

    // ================================================================================================= shared: state

    /// <summary>Geometry of one segment (sketch coordinates in m; model coordinates in a 3D sketch).</summary>
    private sealed record Geo(string Kind, Vec3 Start, Vec3 End, Vec3 Center, double Radius, double Length, bool Construction,
        bool HasEnds, int Dir = 1, double Major = 0, double Minor = 0, string? Text = null)
    {
        /// <summary>The point that follows the segment through a pattern or mirror: the center of round segments, else the mid point.</summary>
        public Vec3 Anchor => Kind switch
        {
            "arc" or "circle" or "ellipse" => Center,
            _ => HasEnds ? (Start + End) / 2 : Center,
        };
    }

    private static Geo Read(ISketchSegment s)
    {
        bool cons = false;
        try { cons = s.ConstructionGeometry; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* text and some converted entities */ }
        double len = 0;
        try { len = s.GetLength(); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* not measurable */ }
        switch (s.GetType())
        {
            case (int)swSketchSegments_e.swSketchLINE when s is ISketchLine l:
            {
                var a = V(l.SkStart());
                var b = V(l.SkEnd());
                return new Geo("line", a, b, (a + b) / 2, 0, len > 0 ? len : a.DistanceTo(b), cons, true);
            }
            case (int)swSketchSegments_e.swSketchARC when s is ISketchArc arc:
            {
                var c = V(arc.SkCenter());
                double r = arc.GetRadius();
                if (arc.IsCircle() != 0) return new Geo("circle", c, c, c, r, len > 0 ? len : 2 * Math.PI * r, cons, false);
                return new Geo("arc", V(arc.SkStart()), V(arc.SkEnd()), c, r, len, cons, true, arc.GetRotationDir() < 0 ? -1 : 1);
            }
            case (int)swSketchSegments_e.swSketchELLIPSE when s is ISketchEllipse e:
            {
                var c = V(e.SkCenter());
                var st = V(e.SkStart());
                var en = V(e.SkEnd());
                bool partial = st.DistanceTo(en) > 1e-12;
                return new Geo("ellipse", st, en, c, 0, len, cons, partial, e.GetRotationDir() < 0 ? -1 : 1,
                    V(e.SkMajor()).DistanceTo(c), V(e.SkMinor()).DistanceTo(c));
            }
            case (int)swSketchSegments_e.swSketchSPLINE when s is ISketchSpline sp:
            {
                var pts = sp.SkFitPoints();
                var a = pts.Count > 0 ? V(pts[0]) : Vec3.Zero;
                var b = pts.Count > 0 ? V(pts[^1]) : Vec3.Zero;
                return new Geo("spline", a, b, (a + b) / 2, 0, len, cons, pts.Count > 0);
            }
            case (int)swSketchSegments_e.swSketchTEXT:
            {
                string? text = null;
                try { text = (s as ISketchText)?.Text; }
                catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* optional */ }
                return new Geo("text", Vec3.Zero, Vec3.Zero, Vec3.Zero, 0, 0, cons, false, Text: text);
            }
            case (int)swSketchSegments_e.swSketchPARABOLA:
                return new Geo("parabola", Vec3.Zero, Vec3.Zero, Vec3.Zero, 0, len, cons, false);
            default:
                return new Geo("other", Vec3.Zero, Vec3.Zero, Vec3.Zero, 0, len, cons, false);
        }
    }

    private static Vec3 V(ISketchPoint? p) => p is null ? Vec3.Zero : new Vec3(p.X, p.Y, p.Z);

    private static Vec3 SketchPointPos(ISketchPoint p) => new(p.X, p.Y, 0);

    /// <summary>The sketch's segments (by name), point count, relation count and constrained status.</summary>
    private sealed class SkState
    {
        public required Dictionary<string, Geo> Segs { get; init; }
        public int Points { get; init; }
        public int Relations { get; init; }
        public int Status { get; init; }

        public static SkState Take(ISketch s)
        {
            var segs = new Dictionary<string, Geo>(StringComparer.Ordinal);
            foreach (var seg in s.Segments()) segs[seg.GetName()] = Read(seg);
            return new SkState { Segs = segs, Points = s.Points().Count, Relations = RelationCount(s), Status = s.GetConstrainedStatus() };
        }
    }

    private sealed record SkDiff(List<string> Created, List<string> Removed, List<string> Changed)
    {
        public bool None => Created.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
    }

    private static SkDiff Diff(SkState a, SkState b) => new(
        b.Segs.Keys.Where(k => !a.Segs.ContainsKey(k)).ToList(),
        a.Segs.Keys.Where(k => !b.Segs.ContainsKey(k)).ToList(),
        b.Segs.Where(kv => a.Segs.TryGetValue(kv.Key, out var g) && !SameGeo(g, kv.Value)).Select(kv => kv.Key).ToList());

    private static bool SameGeo(Geo a, Geo b) =>
        a.Kind == b.Kind && a.Construction == b.Construction && a.Start.DistanceTo(b.Start) < Tol && a.End.DistanceTo(b.End) < Tol &&
        a.Center.DistanceTo(b.Center) < Tol && Math.Abs(a.Radius - b.Radius) < Tol && Math.Abs(a.Length - b.Length) < Tol;

    /// <summary>
    /// Undoes the last operation (IModelDoc2.EditUndo2(1), unverified inside a sketch) and checks that the sketch is back
    /// to <paramref name="before"/>. Returns a sentence for the error message ("Rolled back (undo)." when it is).
    /// </summary>
    private static string Undo(SwContext ctx, IModelDoc2 d, string sketchName, SkState before)
    {
        try { d.EditUndo2(1); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return $"Undo failed ({ex.Message}); the change was kept (check sketch_info)."; }
        var active = d.SketchManager.ActiveSketch;
        if (active is null || !string.Equals(((IFeature)active).Name, sketchName, StringComparison.OrdinalIgnoreCase))
        {
            ctx.DocEntry.ServerSketch = null;
            return "Undo also closed the sketch: re-open it with sketch_edit and check it with sketch_info.";
        }
        var now = SkState.Take(active);
        return Diff(before, now).None && now.Relations == before.Relations
            ? "Rolled back (undo)."
            : "Rollback incomplete: the sketch still differs from before (check sketch_info).";
    }

    private static SketchChangeResult Change(SwContext ctx, IModelDoc2 d, ISketch sketch, string op, SkState before, SkState after, SkDiff diff,
        List<string> checks, IReadOnlyList<ResolvedInfo>? resolved = null, string? side = null, double[]? box = null)
    {
        bool is3D = sketch.Is3D();
        return new SketchChangeResult
        {
            Doc = ctx.Title(d),
            Sketch = ((IFeature)sketch).Name,
            Operation = op,
            Created = NullIfEmpty(diff.Created.Take(ListMax).Select(n => Info(n, after.Segs[n], is3D)).ToList()),
            CreatedCount = diff.Created.Count > ListMax ? diff.Created.Count : null,
            Changed = ChangedList(diff, before, after, is3D),
            Removed = NullIfEmpty(diff.Removed.Take(ListMax).ToList()),
            Side = side,
            BoxMm = box,
            Checks = checks.Count == 0 ? null : checks,
            Contours = SafeContours(sketch),
            Constrained = ConstrainedName(after.Status),
            Resolved = resolved,
        };
    }

    private static List<SketchSegmentInfo>? ChangedList(SkDiff diff, SkState before, SkState after, bool is3D) =>
        NullIfEmpty(diff.Changed.Take(ListMax).Select(n => Info(n, after.Segs[n], is3D, before.Segs[n].Length)).ToList());

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;

    private static SketchSegmentInfo Info(string name, Geo g, bool is3D, double? lengthBefore = null) => new()
    {
        Name = name,
        Kind = g.Kind,
        Construction = g.Construction ? true : null,
        StartMm = g.HasEnds ? Pt(g.Start, is3D) : null,
        EndMm = g.HasEnds ? Pt(g.End, is3D) : null,
        CenterMm = g.Kind is "arc" or "circle" or "ellipse" ? Pt(g.Center, is3D) : null,
        RadiusMm = g.Kind is "arc" or "circle" ? Units.Mm(g.Radius) : null,
        MajorRadiusMm = g.Kind == "ellipse" ? Units.Mm(g.Major) : null,
        MinorRadiusMm = g.Kind == "ellipse" ? Units.Mm(g.Minor) : null,
        LengthMm = g.Kind == "text" ? null : Units.Mm(g.Length),
        LengthBeforeMm = lengthBefore is double lb && Math.Abs(lb - g.Length) > Tol ? Units.Mm(lb) : null,
        Text = g.Text,
    };

    private static double[] Pt(Vec3 p, bool is3D) => is3D ? Units.Mm(p) : P2(p);

    private static double[] P2(Vec3 p) => [Units.Mm(p.X), Units.Mm(p.Y)];

    // ============================================================================================ shared: references

    private static IModelDoc2 DocOf(SwContext ctx) => ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);

    /// <summary>The open sketch, which must be the one this server opened (sketch_start / sketch_edit).</summary>
    private static ISketch OpenSketch(SwContext ctx, IModelDoc2 d, string tool, bool twoD)
    {
        var sketch = d.SketchManager.ActiveSketch
                     ?? throw new SwException(ErrorCodes.SketchState, "No sketch is open.",
                         $"{tool} works in the open sketch: open one with sketch_start, or an existing one with sketch_edit.");
        string name = ((IFeature)sketch).Name;
        if (ctx.DocEntry.ServerSketch != name)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{name}' is open, but this server didn't open it (the user is editing it).",
                "Ask the user to exit the sketch, then re-open it with sketch_edit.");
        if (twoD && sketch.Is3D())
            throw new SwException(ErrorCodes.NotSupported, $"'{name}' is a 3D sketch; {tool} works in 2D sketches.");
        return sketch;
    }

    /// <summary>
    /// Resolves references for sketch tools. Beyond <see cref="EntityResolver"/>: a bare {at:[x,y]} is a sketch point
    /// (else the segment through it), {at:[x,y,z]} is a model point projected into the sketch, a bare name that is a
    /// segment of the sketch is that segment, and {type:'sketch_segment', at:[...]} picks the segment through the point.
    /// Duplicates are removed.
    /// </summary>
    private static List<Resolved> Refs(SwContext ctx, IModelDoc2 d, ISketch sketch, Xform m2s, IReadOnlyList<EntityRef> refs, string arg,
        params EntityKind[] allowed)
    {
        if (refs.Count == 0) throw SwException.InvalidArgument($"'{arg}' must contain at least one entity reference.");
        var list = new List<Resolved>();
        for (int i = 0; i < refs.Count; i++)
            foreach (var r in Ref(ctx, d, sketch, m2s, refs[i], refs.Count == 1 ? arg : $"{arg}[{i}]", allowed))
                if (!list.Any(x => SameEntity(x, r)))
                    list.Add(r);
        return list;
    }

    private static List<Resolved> Ref(SwContext ctx, IModelDoc2 d, ISketch sketch, Xform m2s, EntityRef? r, string arg, EntityKind[] allowed)
    {
        if (r is null) throw SwException.InvalidArgument($"'{arg}' is null; pass e.g. {{name:'Line1'}} or {{at:[x,y]}}.");
        var n = Clone(r);
        bool segOk = allowed.Contains(EntityKind.SketchSegment), ptOk = allowed.Contains(EntityKind.SketchPoint);
        bool sketchOnly = allowed.All(k => k is EntityKind.SketchSegment or EntityKind.SketchPoint);
        var kind = EntityResolver.ParseKind(n.Type);
        bool plain = string.IsNullOrWhiteSpace(n.Id) && n.Where is null;
        if (n.At is not null && (n.At.Length is not (2 or 3) || n.At.Any(v => !double.IsFinite(v))))
            throw SwException.InvalidArgument($"'{arg}.at' must be [x, y] (sketch mm) or [x, y, z] (model mm).");

        // A model point for a sketch entity: express it in sketch coordinates.
        if (plain && n.At is { Length: 3 } && !sketch.Is3D() && (kind is EntityKind.SketchPoint or EntityKind.SketchSegment || (kind is null && sketchOnly)))
        {
            var p = m2s.ApplyPoint(new Vec3(Units.M(n.At[0]), Units.M(n.At[1]), Units.M(n.At[2])));
            if (Math.Abs(p.Z) > 1e-6) ctx.Warn($"'{arg}.at' lies {Units.Mm(p.Z)} mm off the sketch plane; projected onto it.");
            n.At = P2(p);
        }

        if (plain && kind is null)
        {
            if (n.At is { Length: 2 } && (segOk || ptOk))
            {
                if (ptOk)
                {
                    n.Type = "sketch_point";
                    try { return [EntityResolver.Resolve(ctx, d, n, arg, allowed)]; }
                    catch (SwException ex) when (ex.Code == ErrorCodes.EntityNotFound && segOk) { /* not a point: the segment through it */ }
                }
                return [SegmentAt(ctx, d, sketch, n, arg)];
            }
            // A name that is a segment of the sketch is that segment; a name that is no feature either is reported as a
            // missing segment (with the sketch_info hint) rather than a missing feature.
            if (n.At is null && !string.IsNullOrWhiteSpace(n.Name) && segOk &&
                (FindSegment(SketchOf(ctx, d, sketch, n.Sketch), n.Name!) is not null ||
                 (!n.Name!.Trim().Equals("origin", StringComparison.OrdinalIgnoreCase) &&
                  EntityResolver.StandardPlane(d, n.Name) is null && EntityResolver.FeatureByName(d, n.Name) is null)))
                n.Type = "sketch_segment";
        }
        else if (plain && kind == EntityKind.SketchSegment && n.At is { Length: 2 } && string.IsNullOrWhiteSpace(n.Name))
        {
            if (!segOk) throw SwException.InvalidArgument($"'{arg}' can't be a sketch segment here.");
            return [SegmentAt(ctx, d, sketch, n, arg)];
        }
        if (n.Where is not null) return EntityResolver.ResolveAll(ctx, d, [n], arg, allowed);
        return [EntityResolver.Resolve(ctx, d, n, arg, allowed)];
    }

    private static EntityRef Clone(EntityRef r) => new()
    {
        Id = r.Id, Type = r.Type, At = r.At?.ToArray(), Name = r.Name, Where = r.Where, Expect = r.Expect,
        Component = r.Component, Sketch = r.Sketch, TolMm = r.TolMm, Strict = r.Strict,
    };

    private static ISketch SketchOf(SwContext ctx, IModelDoc2 d, ISketch open, string? name) =>
        string.IsNullOrWhiteSpace(name) ? open : EntityResolver.SketchFor(ctx, d, name);

    private static ISketchSegment? FindSegment(ISketch s, string name) =>
        s.Segments().FirstOrDefault(x => string.Equals(x.GetName(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The segment passing through a sketch point [x,y] mm (lines, arcs and circles).</summary>
    private static Resolved SegmentAt(SwContext ctx, IModelDoc2 d, ISketch open, EntityRef r, string arg)
    {
        var sketch = SketchOf(ctx, d, open, r.Sketch);
        double tol = Units.M(r.TolMm is > 0 ? r.TolMm.Value : EntityResolver.DefaultTolMm);
        var p = new Vec3(Units.M(r.At![0]), Units.M(r.At[1]), 0);
        var hits = sketch.Segments().Select(s => (Seg: s, Dist: DistanceTo(Read(s), p))).Where(h => double.IsFinite(h.Dist))
            .OrderBy(h => h.Dist).ToList();
        string where = $"[{TopoQuery.Fmt(r.At)}]";
        if (hits.Count == 0)
            throw new SwException(ErrorCodes.EntityNotFound, $"'{arg}': the sketch has no line, arc or circle near {where}.", "Pass the segment by name (sketch_info lists them).");
        var best = hits[0];
        var tied = hits.Where(h => h.Dist <= best.Dist + Math.Max(1e-9, 1e-3 * tol)).ToList();
        if (tied.Count > 1 && best.Dist <= tol)
            throw new SwException(ErrorCodes.EntityAmbiguous, $"'{arg}': {tied.Count} segments pass through {where}: {string.Join(", ", tied.Take(6).Select(h => h.Seg.GetName()))}.",
                "Pass one by name, or a point on just one of them.");
        if (best.Dist > tol)
        {
            double runner = hits.Count > 1 ? hits[1].Dist : double.MaxValue;
            if (r.Strict == true || best.Dist > Math.Max(tol, 1e-3) || runner < 2 * best.Dist + 1e-4)
                throw new SwException(ErrorCodes.EntityNotFound,
                    $"'{arg}': no segment within {Units.Mm(tol)} mm of {where}; nearest: {string.Join("; ", hits.Take(3).Select(h => $"{h.Seg.GetName()} at {Units.Mm(h.Dist)} mm"))}.",
                    "Pass the segment by name (sketch_info lists them), or a point on it.");
            ctx.Warn($"{arg}: no segment exactly at {where}; used {best.Seg.GetName()}, {Units.Mm(best.Dist)} mm away.");
        }
        return new Resolved { Obj = best.Seg, Kind = EntityKind.SketchSegment, Label = $"sketch segment {best.Seg.GetName()}", Distance = best.Dist };
    }

    private static bool SameEntity(Resolved a, Resolved b)
    {
        if (ReferenceEquals(a.Obj, b.Obj)) return true;
        if (a.Id is not null && a.Id == b.Id) return true;
        if (a.Obj is ISketchSegment && b.Obj is ISketchSegment && a.Label == b.Label) return true;
        return a.Obj is ISketchPoint p && b.Obj is ISketchPoint q && Math.Abs(p.X - q.X) < 1e-12 && Math.Abs(p.Y - q.Y) < 1e-12 && Math.Abs(p.Z - q.Z) < 1e-12;
    }

    private static void SelectAll(IModelDoc2 d, IEnumerable<Resolved> items, int mark)
    {
        foreach (var r in items)
            if (!SelectOne(d, r, true, mark))
                throw new SwException(ErrorCodes.FeatureFailed, $"Could not select {r.Label}.", "Check that it is visible and not suppressed.");
    }

    private static bool SelectOne(IModelDoc2 d, Resolved r, bool append, int mark)
    {
        if (r.Kind == EntityKind.Origin && r.Obj is IFeature origin)
        {
            // The origin as an external sketch point ("Point1@Origin", unverified), else the origin feature.
            if (d.Extension.SelectByID2($"Point1@{origin.Name}", "EXTSKETCHPOINT", 0, 0, 0, append, mark, null, 0)) return true;
            return origin.Select2(append, mark);
        }
        return r.Select(d, append, mark);
    }

    /// <summary>A referenced entity as dimension/relation geometry (sketch coordinates).</summary>
    private sealed record Item(Resolved R, string Kind, Vec3 P, Vec3 A, Vec3 B, Vec3 C, double Radius, int Dir)
    {
        public Vec3 Anchor => Kind is "arc" or "circle" ? C : Kind == "line" ? (A + B) / 2 : P;
    }

    private static Item ItemOf(Resolved r, Xform m2s)
    {
        switch (r.Obj)
        {
            case ISketchPoint p:
                return new Item(r, "point", new Vec3(p.X, p.Y, 0), default, default, default, 0, 1);
            case ISketchSegment s:
            {
                var g = Read(s);
                string k = g.Kind is "line" or "arc" or "circle" ? g.Kind : "other";
                return new Item(r, k, g.Start, g.Start, g.End, g.Center, g.Radius, g.Dir);
            }
            default:
                if (r.Kind == EntityKind.Origin)
                {
                    var o = m2s.ApplyPoint(Vec3.Zero);
                    return new Item(r, "point", new Vec3(o.X, o.Y, 0), default, default, default, 0, 1);
                }
                return new Item(r, "external", default, default, default, default, 0, 1);
        }
    }

    // ============================================================================================ shared: corners

    private static void CheckCorner(IReadOnlyList<EntityRef>? segments, double[]? at)
    {
        bool hasSegs = segments is { Count: > 0 };
        if (hasSegs == (at is not null))
            throw SwException.InvalidArgument(hasSegs ? "Pass either 'segments' or 'at', not both." : "Pass 'segments' (the two segments of the corner) or 'at' (the corner point).");
        if (hasSegs && segments!.Count != 2) throw SwException.InvalidArgument($"'segments' needs exactly two segments (got {segments.Count}).");
        CheckPoint(at, "at", required: false);
    }

    /// <summary>The two segments of a corner and the corner point (shared end, or the lines' intersection).</summary>
    private static (Resolved A, Resolved B, Vec3? Corner) CornerOf(SwContext ctx, IModelDoc2 d, ISketch sketch, Xform m2s,
        IReadOnlyList<EntityRef>? segments, double[]? at)
    {
        if (segments is { Count: > 0 })
        {
            var refs = Refs(ctx, d, sketch, m2s, segments, "segments", EntityKind.SketchSegment);
            if (refs.Count != 2) throw SwException.InvalidArgument("'segments' needs two different segments that meet at the corner.");
            var ga = Read((ISketchSegment)refs[0].Obj);
            var gb = Read((ISketchSegment)refs[1].Obj);
            Vec3? corner = SharedEnd(ga, gb) ?? (ga.Kind == "line" && gb.Kind == "line" ? LineIntersection(ga.Start, ga.End, gb.Start, gb.End) : null);
            return (refs[0], refs[1], corner);
        }
        var p = ToSketch(at!, m2s, "at", ctx);
        var segs = sketch.Segments().Select(s => (Seg: s, G: Read(s))).Where(x => x.G.HasEnds).ToList();
        var ends = segs.SelectMany(x => new[] { x.G.Start, x.G.End }).ToList();
        if (ends.Count == 0) throw new SwException(ErrorCodes.EntityNotFound, "The sketch has no segment end points (no corners).");
        var nearest = ends.MinBy(e => e.DistanceTo(p));
        double dist = nearest.DistanceTo(p);
        if (dist > Units.M(EntityResolver.DefaultTolMm))
        {
            if (dist > 1e-3)
                throw new SwException(ErrorCodes.EntityNotFound,
                    $"No segment end within 1 mm of [{TopoQuery.Fmt(P2(p))}]; the nearest is [{TopoQuery.Fmt(P2(nearest))}], {Units.Mm(dist)} mm away.",
                    "Pass the corner point (sketch_info lists end points), or 'segments'.");
            ctx.Warn($"'at': used the corner at [{TopoQuery.Fmt(P2(nearest))}], {Units.Mm(dist)} mm away.");
        }
        var touching = segs.Where(x => x.G.Start.DistanceTo(nearest) < JoinTol || x.G.End.DistanceTo(nearest) < JoinTol).ToList();
        var solid = touching.Where(x => !x.G.Construction).ToList();
        var pick = solid.Count == 2 ? solid : touching;
        if (pick.Count != 2)
            throw new SwException(pick.Count < 2 ? ErrorCodes.EntityNotFound : ErrorCodes.EntityAmbiguous,
                $"{pick.Count} segment(s) end at [{TopoQuery.Fmt(P2(nearest))}] ({string.Join(", ", pick.Take(6).Select(x => x.Seg.GetName()))}); a corner needs exactly two.",
                "Pass 'segments' with the two segments of the corner.");
        Resolved R(ISketchSegment s) => new() { Obj = s, Kind = EntityKind.SketchSegment, Label = $"sketch segment {s.GetName()}" };
        return (R(pick[0].Seg), R(pick[1].Seg), nearest);
    }

    private static Vec3? SharedEnd(Geo a, Geo b)
    {
        if (!a.HasEnds || !b.HasEnds) return null;
        foreach (var p in new[] { a.Start, a.End })
            if (p.DistanceTo(b.Start) < JoinTol || p.DistanceTo(b.End) < JoinTol) return p;
        return null;
    }

    private static bool Touch(Geo a, Geo b) => SharedEnd(a, b) is not null;

    // ============================================================================================ shared: geometry

    private static double Cross2(Vec3 a, Vec3 b) => a.X * b.Y - a.Y * b.X;

    private static Vec3 Perp(Vec3 v) => new(-v.Y, v.X, 0);

    private static double Ang(Vec3 v) => Math.Atan2(v.Y, v.X);

    private static double NormAngle(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>The arc's sweep (0, 2π] from its start, in its own direction.</summary>
    private static double Sweep(Geo g)
    {
        double a0 = Ang(g.Start - g.Center), a1 = Ang(g.End - g.Center);
        double s = NormAngle(g.Dir > 0 ? a1 - a0 : a0 - a1);
        return s < 1e-12 ? 2 * Math.PI : s;
    }

    private static double ArcMidAngle(Vec3 start, Vec3 end, Vec3 center, int dir)
    {
        var g = new Geo("arc", start, end, center, start.DistanceTo(center), 0, false, true, dir);
        return Ang(start - center) + dir * Sweep(g) / 2;
    }

    private static bool OnArc(Geo g, Vec3 p)
    {
        if (g.Kind == "circle") return true;
        double a = NormAngle(g.Dir * (Ang(p - g.Center) - Ang(g.Start - g.Center)));
        return a <= Sweep(g) + 1e-9;
    }

    private static double PointLineDistance(Vec3 p, Vec3 a, Vec3 b)
    {
        var d = b - a;
        double l = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        return l < 1e-15 ? Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y)) : Math.Abs(Cross2(d, p - a)) / l;
    }

    private static Vec3 Foot(Vec3 p, Vec3 a, Vec3 b)
    {
        var d = b - a;
        double l2 = d.X * d.X + d.Y * d.Y;
        return l2 < 1e-30 ? a : a + d * (((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / l2);
    }

    private static double PointSegmentDistance(Vec3 p, Vec3 a, Vec3 b)
    {
        var d = b - a;
        double l2 = d.X * d.X + d.Y * d.Y;
        if (l2 < 1e-30) return p.DistanceTo(a);
        double t = Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / l2, 0, 1);
        return p.DistanceTo(a + d * t);
    }

    /// <summary>Distance from a sketch point to a segment (∞ for kinds this tool can't measure).</summary>
    private static double DistanceTo(Geo g, Vec3 p) => g.Kind switch
    {
        "line" => PointSegmentDistance(p, g.Start, g.End),
        "circle" => Math.Abs(p.DistanceTo(g.Center) - g.Radius),
        "arc" => OnArc(g, p) ? Math.Abs(p.DistanceTo(g.Center) - g.Radius) : Math.Min(p.DistanceTo(g.Start), p.DistanceTo(g.End)),
        _ => double.PositiveInfinity,
    };

    private static Vec3? LineIntersection(Vec3 a1, Vec3 b1, Vec3 a2, Vec3 b2)
    {
        var d1 = b1 - a1;
        var d2 = b2 - a2;
        double den = Cross2(d1, d2);
        if (Math.Abs(den) < 1e-18) return null;
        double t = Cross2(a2 - a1, d2) / den;
        return a1 + d1 * t;
    }

    private static Vec3 Reflect(Vec3 p, Vec3 a, Vec3 b)
    {
        var f = Foot(p, a, b);
        return new Vec3(2 * f.X - p.X, 2 * f.Y - p.Y, 0);
    }

    private static bool Parallel(Item a, Item b) =>
        Math.Abs(Cross2((a.B - a.A).Normalized(), (b.B - b.A).Normalized())) < AngTol;

    private static bool IsHorizontal(Item l) => Math.Abs(l.B.Y - l.A.Y) < Tol;

    private static bool IsVertical(Item l) => Math.Abs(l.B.X - l.A.X) < Tol;

    /// <summary>Intersection of two lines and the unit directions from it toward each line's segment.</summary>
    private static (Vec3 P, Vec3 U1, Vec3 U2) Rays(Item l1, Item l2)
    {
        var p = LineIntersection(l1.A, l1.B, l2.A, l2.B) ?? l1.A;
        static Vec3 Toward(Vec3 p, Item l)
        {
            var mid = (l.A + l.B) / 2;
            var v = mid - p;
            if (v.Length < 1e-9) v = l.B - p;
            if (v.Length < 1e-9) v = l.A - p;
            return v.Normalized();
        }
        return (p, Toward(p, l1), Toward(p, l2));
    }

    private static double Reach(Item l, Vec3 p) => ((l.A + l.B) / 2).DistanceTo(p);

    /// <summary>Ray (unit direction) parameters where it meets a segment, t &gt; 0.</summary>
    private static IEnumerable<double> RayHits(Vec3 o, Vec3 dir, Geo g)
    {
        switch (g.Kind)
        {
            case "line":
            {
                var e = g.End - g.Start;
                double den = Cross2(dir, e);
                if (Math.Abs(den) < 1e-15) yield break;
                var w = g.Start - o;
                double t = Cross2(w, e) / den, u = Cross2(w, dir) / den;
                if (t > 1e-9 && u >= -1e-9 && u <= 1 + 1e-9) yield return t;
                yield break;
            }
            case "arc" or "circle":
            {
                var f = o - g.Center;
                double b = f.X * dir.X + f.Y * dir.Y;
                double c = f.X * f.X + f.Y * f.Y - g.Radius * g.Radius;
                double disc = b * b - c;
                if (disc < 0) yield break;
                double s = Math.Sqrt(disc);
                foreach (var t in new[] { -b - s, -b + s })
                    if (t > 1e-9 && OnArc(g, o + dir * t)) yield return t;
                yield break;
            }
        }
    }

    /// <summary>Points where a circle meets a line, arc or circle segment.</summary>
    private static IEnumerable<Vec3> CircleHits(Vec3 c, double r, Geo g)
    {
        switch (g.Kind)
        {
            case "line":
            {
                var d = g.End - g.Start;
                double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (len < 1e-15) yield break;
                var u = d / len;
                foreach (var t in RayHits(g.Start - u * 1e-6, u, g with { Kind = "circle", Center = c, Radius = r }))
                {
                    double along = t - 1e-6;
                    if (along >= -1e-9 && along <= len + 1e-9) yield return g.Start + u * along;
                }
                yield break;
            }
            case "arc" or "circle":
            {
                double dist = c.DistanceTo(g.Center);
                if (dist < 1e-12 || dist > r + g.Radius || dist < Math.Abs(r - g.Radius)) yield break;
                double a = (r * r - g.Radius * g.Radius + dist * dist) / (2 * dist);
                double h = Math.Sqrt(Math.Max(0, r * r - a * a));
                var ex = (g.Center - c) / dist;
                var mid = c + ex * a;
                foreach (var p in new[] { mid + Perp(ex) * h, mid - Perp(ex) * h })
                    if (OnArc(g, p)) yield return p;
                yield break;
            }
        }
    }

    private readonly record struct Box2(double X0, double Y0, double X1, double Y1);

    private static Box2 BoxOf(IEnumerable<Geo> gs)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(Vec3 p)
        {
            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
        }
        foreach (var g in gs)
        {
            switch (g.Kind)
            {
                case "circle":
                    Add(g.Center - new Vec3(g.Radius, g.Radius, 0));
                    Add(g.Center + new Vec3(g.Radius, g.Radius, 0));
                    break;
                case "arc":
                    Add(g.Start);
                    Add(g.End);
                    for (int k = 0; k < 4; k++)
                    {
                        var p = g.Center + new Vec3(Math.Cos(k * Math.PI / 2), Math.Sin(k * Math.PI / 2), 0) * g.Radius;
                        if (OnArc(g, p)) Add(p);
                    }
                    break;
                case "ellipse":
                    Add(g.Center - new Vec3(g.Major, g.Major, 0));
                    Add(g.Center + new Vec3(g.Major, g.Major, 0));
                    break;
                default:
                    if (g.HasEnds) { Add(g.Start); Add(g.End); }
                    break;
            }
        }
        return x0 > x1 ? new Box2(0, 0, 0, 0) : new Box2(x0, y0, x1, y1);
    }

    private static string Size(Box2 b) => $"{Units.Mm(b.X1 - b.X0)} x {Units.Mm(b.Y1 - b.Y0)}";

    /// <summary>True when every end point meets exactly one other (or all segments are closed curves).</summary>
    private static bool IsClosedLoop(IReadOnlyList<Geo> gs)
    {
        if (gs.Count == 0) return false;
        var ends = gs.Where(g => g.HasEnds).SelectMany(g => new[] { g.Start, g.End }).ToList();
        foreach (var e in ends)
            if (ends.Count(o => o.DistanceTo(e) < JoinTol) != 2) return false;
        return true;
    }

    // ============================================================================================== shared: misc

    private static string Lower(string? s) => (s ?? "").Trim().ToLowerInvariant();

    private static void CheckPoint(double[]? p, string arg, bool required)
    {
        if (p is null)
        {
            if (required) throw SwException.InvalidArgument($"'{arg}' is required: [x, y] sketch mm or [x, y, z] model mm.");
            return;
        }
        if (p.Length is not (2 or 3) || p.Any(v => !double.IsFinite(v)))
            throw SwException.InvalidArgument($"'{arg}' must be [x, y] (sketch mm) or [x, y, z] (model mm).");
    }

    /// <summary>[x,y] sketch mm or [x,y,z] model mm (projected onto the sketch plane) → sketch coordinates in m.</summary>
    private static Vec3 ToSketch(double[] p, Xform m2s, string arg, SwContext ctx)
    {
        if (p.Length == 2) return new Vec3(Units.M(p[0]), Units.M(p[1]), 0);
        var s = m2s.ApplyPoint(new Vec3(Units.M(p[0]), Units.M(p[1]), Units.M(p[2])));
        if (Math.Abs(s.Z) > 1e-6) ctx.Warn($"'{arg}' lies {Units.Mm(s.Z)} mm off the sketch plane; projected onto it.");
        return new Vec3(s.X, s.Y, 0);
    }

    private static int RelationCount(ISketch s)
    {
        try { return s.RelationManager?.GetRelationsCount((int)swSketchRelationFilterType_e.swAll) ?? 0; }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { return 0; }
    }

    private static bool BecameOverDefined(ISketch s, int before)
    {
        static bool Bad(int x) => x is (int)swConstrainedStatus_e.swOverConstrained or (int)swConstrainedStatus_e.swNoSolution
            or (int)swConstrainedStatus_e.swInvalidSolution;
        return Bad(s.GetConstrainedStatus()) && !Bad(before);
    }

    private static ContourStatus SafeContours(ISketch s)
    {
        try { return SketchService.Contours(s); }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex))
        {
            return new ContourStatus { Status = "unknown", Segments = s.Segments().Count(x => !x.ConstructionGeometry), Problem = ex.Message };
        }
    }

    private static string ConstrainedName(int s) => s switch
    {
        (int)swConstrainedStatus_e.swUnderConstrained => "under",
        (int)swConstrainedStatus_e.swFullyConstrained => "fully",
        (int)swConstrainedStatus_e.swOverConstrained => "over",
        (int)swConstrainedStatus_e.swNoSolution => "no_solution",
        (int)swConstrainedStatus_e.swInvalidSolution => "invalid",
        (int)swConstrainedStatus_e.swAutosolveOff => "autosolve_off",
        _ => "unknown",
    };

    private static string RelationTypeName(int t)
    {
        if (!Enum.IsDefined(typeof(swConstraintType_e), t)) return $"type_{t}";
        string raw = ((swConstraintType_e)t).ToString().Replace("swConstraintType_", "").ToLowerInvariant();
        return raw switch
        {
            "samelength" => "equal",
            "atmiddle" => "midpoint",
            "atintersect" => "intersection",
            "colinear" => "collinear",
            "fixed" => "fix",
            "atpierce" => "pierce",
            "mergepoints" => "merge",
            "horizpoints" => "horizontal_points",
            "vertpoints" => "vertical_points",
            _ => raw,
        };
    }
}
