using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Verify;

/// <summary>What a feature tool must have changed in the volume.</summary>
internal enum VolumeRule
{
    /// <summary>Boss/revolve/sweep/loft (merged): ΔV &gt; 0, or one more body.</summary>
    Increase,
    /// <summary>Cut/hole: ΔV &lt; 0.</summary>
    Decrease,
    /// <summary>Fillet/chamfer/draft/shell: |ΔV| &gt; ε or the face count changed (sign reported, not asserted).</summary>
    Change,
    /// <summary>Surfaces, reference geometry, sheet-metal flatten: no volume rule.</summary>
    Any,
}

/// <summary>The type signature and rules of one feature tool (Tier A).</summary>
internal sealed record FeatureSpec(string Tool, string[] Types, VolumeRule Volume, bool RequireFaces = true);

/// <summary>A verified feature creation.</summary>
internal sealed record FeatureOutcome(IFeature Main, IReadOnlyList<IFeature> Created, Snapshot Before, Snapshot After,
    IReadOnlyList<string> Checks);

/// <summary>
/// Tier-A verification and rollback (DESIGN §3.12). New features are identified by the set difference of feature IDs,
/// so void APIs, multi-node features and a rolled-back tree are all handled. Any failure after side effects deletes
/// what the call created (absorbed sketches that existed before are kept) and says so in the error.
/// </summary>
internal static class FeatureVerifier
{
    private const double VolEps = 1e-13; // m³ (0.0001 mm³)

    /// <summary>
    /// Checks the change since <paramref name="before"/>. <paramref name="intent"/> adds Tier-B checks: it returns
    /// failure messages (empty = ok) and may append passed-check descriptions to the list it gets.
    /// </summary>
    public static FeatureOutcome Check(SwContext ctx, IModelDoc2 doc, Snapshot before, FeatureSpec spec,
        Func<IFeature, Snapshot, List<string>, IEnumerable<string>>? intent = null, bool keepOnFailure = false,
        object? apiResult = null)
    {
        var after = Snapshot.Take(doc);
        var created = after.Features.Where(kv => !before.Features.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        if (created.Count == 0)
            throw new SwException(ErrorCodes.FeatureFailed,
                $"{spec.Tool}: SOLIDWORKS created no feature{(apiResult is null ? " (the API returned nothing)" : "")}.",
                Hint(spec));

        var main = created.FirstOrDefault(f => spec.Types.Contains(f.GetTypeName2()));
        if (main is null)
            Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.FeatureFailed,
                $"{spec.Tool}: expected a {string.Join("/", spec.Types)} feature but got {string.Join(", ", created.Select(f => $"{f.Name} ({f.GetTypeName2()})"))}.",
                Hint(spec));

        int err = main!.GetErrorCode2(out bool isWarning);
        if (err != 0 && !isWarning)
            Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.RebuildError,
                $"{spec.Tool}: '{main.Name}' has rebuild error {err} ({ErrorName(err)}).", Hint(spec));
        if (err != 0) ctx.Warn($"'{main.Name}' has rebuild warning {err} ({ErrorName(err)}).");

        if (spec.RequireFaces && main.GetFaceCount() == 0)
            Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.VerifyFailed,
                $"{spec.Tool}: '{main.Name}' was created but has no faces, so it changed no geometry.", Hint(spec));

        double dv = after.Volume - before.Volume;
        var checks = new List<string>();
        switch (spec.Volume)
        {
            case VolumeRule.Increase when !(dv > VolEps || after.SolidBodies > before.SolidBodies):
                Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.VerifyFailed,
                    $"{spec.Tool}: '{main.Name}' added no material (ΔV = {Units.Mm3(dv)} mm³).",
                    "The new material may lie entirely inside the existing body; check the sketch position and direction.");
                break;
            case VolumeRule.Decrease when !(dv < -VolEps):
                Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.VerifyFailed,
                    $"{spec.Tool}: '{main.Name}' removed no material (ΔV = {Units.Mm3(dv)} mm³).",
                    "The cut probably points away from the body: use direction 'auto' (default), 'reverse', or through_all_both.");
                break;
            case VolumeRule.Change when !(Math.Abs(dv) > VolEps || after.Faces != before.Faces):
                Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.VerifyFailed,
                    $"{spec.Tool}: '{main.Name}' changed neither volume nor faces.", Hint(spec));
                break;
        }
        if (spec.Volume != VolumeRule.Any) checks.Add("volume");

        if (intent is not null)
        {
            var problems = intent(main, after, checks).ToList();
            if (problems.Count > 0)
                Fail(ctx, doc, before, created, keepOnFailure, ErrorCodes.VerifyFailed,
                    $"{spec.Tool}: '{main.Name}' doesn't match the request: {string.Join("; ", problems)}.", Hint(spec));
        }
        return new FeatureOutcome(main, created, before, after, checks);
    }

    /// <summary>Rolls back (unless <paramref name="keep"/>) and throws <paramref name="code"/>.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Fail(SwContext ctx, IModelDoc2 doc, Snapshot before, IReadOnlyList<IFeature> created, bool keep,
        string code, string message, string? hint)
    {
        if (keep)
            throw new SwException(code, message + " The result was kept (keepOnFailure).", hint);
        string rb = Rollback(ctx, doc, before);
        throw new SwException(code, message + " " + rb, hint);
    }

    /// <summary>Deletes every feature created since <paramref name="before"/>; returns a one-line report.</summary>
    public static string Rollback(SwContext ctx, IModelDoc2 doc, Snapshot before)
    {
        var removed = new List<string>();
        for (int pass = 0; pass < 3; pass++)
        {
            var now = ComArgs.Objects<IFeature>((object?)doc.FeatureManager.GetFeatures(true));
            var extra = now.Where(f => { try { return !before.Features.ContainsKey(f.GetID()); } catch { return false; } }).ToList();
            if (extra.Count == 0) break;
            doc.ClearSelection2(true);
            foreach (var f in extra)
            {
                removed.Add(f.Name);
                f.Select2(true, 0);
            }
            // Feature only: a sketch that existed before (absorbed by the new feature) comes back for a retry.
            doc.Extension.DeleteSelection2(0);
            doc.ClearSelection2(true);
        }
        var left = Snapshot.Take(doc).Features.Keys.Where(id => !before.Features.ContainsKey(id)).ToList();
        if (left.Count == 0)
            return removed.Count == 0 ? "Nothing was left behind." : $"Rolled back: deleted {string.Join(", ", removed.Distinct())}.";
        return $"Rollback incomplete: {left.Count} new feature(s) remain in the tree (feature_list shows them).";
    }

    public static string ErrorName(int code) =>
        Enum.IsDefined(typeof(swFeatureError_e), code) ? ((swFeatureError_e)code).ToString() : code.ToString();

    private static string? Hint(FeatureSpec spec) => spec.Tool switch
    {
        "feature_extrude" => "For a cut, the direction may point away from the material (use direction 'auto', the default, or " +
                             "through_all_both); for a boss, check that the profile is closed and the depth positive.",
        "feature_fillet" => "A radius too large for the adjacent faces fails; try a smaller radius or fewer edges.",
        _ => null,
    };

    /// <summary>Builds the standard result of a feature tool.</summary>
    public static FeatureResult Result(SwContext ctx, IModelDoc2 doc, FeatureOutcome o, string? consumedSketch = null,
        Vec3? direction = null, IReadOnlyList<ResolvedInfo>? resolved = null)
    {
        double dv = o.After.Volume - o.Before.Volume;
        var faces = Describe.FeatureFaces(ctx, doc, o.Main, 12, out int faceTotal);
        int err = o.Main.GetErrorCode2(out bool warn);
        var also = o.Created.Where(f => !ReferenceEquals(f, o.Main) && !IsSubFeatureOf(f, o.Main))
            .Select(f => new FeatureRef { Name = f.Name, Type = f.GetTypeName2() }).ToList();
        return new FeatureResult
        {
            Doc = ctx.Title(doc),
            Feature = new FeatureRef { Name = o.Main.Name, Type = o.Main.GetTypeName2(), Error = err == 0 ? null : err, Warning = warn ? true : null },
            AlsoCreated = also.Count == 0 ? null : also,
            ConsumedSketch = consumedSketch,
            DirectionModel = direction is Vec3 d ? Units.Unit(d) : null,
            Verified = new Verified
            {
                VolumeDeltaMm3 = Units.Mm3(dv),
                AreaDeltaMm2 = Units.Mm2(o.After.Area - o.Before.Area),
                BodyCountDelta = o.After.Bodies - o.Before.Bodies,
                FaceCountDelta = o.After.Faces - o.Before.Faces,
                Material = dv > VolEps ? "added" : dv < -VolEps ? "removed" : "none",
                Checks = o.Checks.Count == 0 ? null : o.Checks,
            },
            Model = o.After.ToState(),
            NewFaces = faces,
            NewFaceCount = faceTotal,
            Dimensions = Dimensions(o.Main),
            Resolved = resolved,
        };
    }

    private static bool IsSubFeatureOf(IFeature f, IFeature parent)
    {
        foreach (var c in Tree.Children(parent))
            if (c.GetID() == f.GetID()) return true;
        return false;
    }

    /// <summary>The feature's dimensions (D1@Boss-Extrude1 …) with values in mm / deg / count.</summary>
    public static List<DimensionInfo> Dimensions(IFeature f)
    {
        var list = new List<DimensionInfo>();
        object? dd = f.GetFirstDisplayDimension();
        int guard = 0;
        while (dd is IDisplayDimension disp && guard++ < 200)
        {
            var dim = disp.GetDimension2(0);
            if (dim is not null) list.Add(Dimension(dim));
            dd = f.GetNextDisplayDimension(disp);
        }
        return list;
    }

    public static DimensionInfo Dimension(IDimension dim)
    {
        int type = dim.GetType();
        double v = dim.SystemValue;
        (double value, string unit) = type switch
        {
            (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular => (Units.Deg(v), "deg"),
            (int)swDimensionParamType_e.swDimensionParamTypeInteger => (Math.Round(v), "count"),
            _ => (Units.Mm(v), "mm"),
        };
        var tol = Services.FeatureManagementService.ReadTolerance(dim, type);
        return new DimensionInfo
        {
            Name = ShortName(dim.FullName),
            Value = value,
            Unit = unit,
            Driven = dim.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven ? true : null,
            Tolerance = tol is { Type: not "none" } ? tol : null,
        };
    }

    /// <summary>"D1@Boss-Extrude1@Part1.Part" → "D1@Boss-Extrude1" (the name IModelDoc2.Parameter and dimension_set take).</summary>
    public static string ShortName(string fullName)
    {
        var parts = fullName.Split('@');
        if (parts.Length >= 3 && (parts[^1].EndsWith(".Part", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^1].EndsWith(".Assembly", StringComparison.OrdinalIgnoreCase) ||
                                  parts[^1].EndsWith(".Drawing", StringComparison.OrdinalIgnoreCase)))
            return string.Join('@', parts[..^1]);
        return fullName;
    }
}
