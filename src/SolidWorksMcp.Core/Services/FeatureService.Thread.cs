using System.Globalization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>feature_thread options: a modelled (helical) ISO metric thread.</summary>
public sealed record ThreadRequest
{
    public string? Doc { get; init; }
    /// <summary>The circular edge where the thread starts: a shaft's end (external) or a hole's rim (internal).</summary>
    public EntityRef? Edge { get; init; }
    /// <summary>M6 (coarse pitch) or M6x0.75.</summary>
    public string? Size { get; init; }
    public double DepthMm { get; init; }
    /// <summary>cut (default: the groove is cut) | extrude (the thread is added).</summary>
    public string? Method { get; init; }
    public bool LeftHanded { get; init; }
    public string? Name { get; init; }
    public bool KeepOnFailure { get; init; }
}

public sealed partial class FeatureService
{
    // Type name not verified before the first live run; both are accepted.
    private static readonly FeatureSpec ThreadCutSpec = new("feature_thread", ["SweepThread", "Thread"], VolumeRule.Decrease);
    private static readonly FeatureSpec ThreadExtrudeSpec = new("feature_thread", ["SweepThread", "Thread"], VolumeRule.Increase);

    public Task<FeatureResult> ThreadAsync(ThreadRequest q, CancellationToken ct = default)
    {
        var (d0, pitch) = MetricSize(q.Size);
        double depth = Units.PositiveM(q.DepthMm, "depthMm");
        string method = (q.Method ?? "cut").Trim().ToLowerInvariant();
        if (method is not ("cut" or "extrude")) throw SwException.InvalidArgument($"method '{q.Method}' is not supported.", "Use cut or extrude.");
        string size = $"M{d0.ToString("0.##", CultureInfo.InvariantCulture)}x{pitch.ToString("0.0#", CultureInfo.InvariantCulture)}";
        return session.RunAsync("feature_thread", ctx =>
        {
            var d = (IModelDoc2)ctx.Part();
            if (d.SketchManager.ActiveSketch is ISketch open)
                throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");
            var edge = EntityResolver.Resolve(ctx, d, q.Edge, "edge", EntityKind.Edge);
            var e = (IEdge)edge.Obj;
            if (Topology.Describe(e) is not { Curve: "circle", Radius: double r })
                throw SwException.InvalidArgument($"{edge.Label} is not circular; a thread starts at a shaft's end or a hole's rim.");
            var cylinder = e.AdjacentFaces().FirstOrDefault(f => Topology.Describe(f).Surface == "cylinder")
                           ?? throw SwException.InvalidArgument($"{edge.Label} is not on a cylindrical face.");
            bool internalThread = Topology.IsHole(cylinder, Topology.Describe(cylinder));
            // ISO 68-1: the external minor diameter is d − 1.22687 P, the internal one D − 1.08253 P.
            double major = Units.M(d0), p = Units.M(pitch);
            double minor = internalThread ? major - 1.08253 * p : major - 1.22687 * p;
            double here = 2 * r;
            if (internalThread ? Math.Abs(here - minor) > 0.1 * major : Math.Abs(here - major) > 0.05 * major)
                ctx.Warn($"The {(internalThread ? "hole" : "shaft")} is Ø{Units.Mm(here)} mm; an {size} {(internalThread ? "tapped hole wants about Ø" + Units.Mm(minor) : "external thread wants Ø" + Units.Mm(major))} mm.");
            var before = Snapshot.Take(d);
            var data = (object?)d.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmSweepThread) as IThreadFeatureData
                       ?? throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS gave no thread definition.");
            data.InitializeThreadData();
            data.Type = internalThread ? "Metric Tap" : "Metric Die";
            data.Size = size;
            data.ThreadMethod = method == "cut" ? (int)swThreadMethod_e.swThreadMethod_Cut : (int)swThreadMethod_e.swThreadMethod_Extrude;
            data.EndCondition = (int)swThreadEndCondition_e.swThreadEndCondition_Blind;
            data.BlindDepth = depth;
            data.RightHanded = !q.LeftHanded;
            data.Edge = (Edge)e;
            d.ClearSelection2(true);
            var api = (object?)d.FeatureManager.CreateFeature(data);
            d.ClearSelection2(true);
            double annulus = Math.PI / 4 * (major * major - minor * minor) * depth;
            var o = FeatureVerifier.Check(ctx, d, before, method == "cut" ? ThreadCutSpec : ThreadExtrudeSpec, (main, after, checks) =>
            {
                double moved = Math.Abs(after.Volume - before.Volume);
                // The thread's material lies between the minor and major diameters over its length; its ISO profile fills
                // a good part of that annulus, never all of it.
                if (moved < 0.2 * annulus || moved > annulus)
                    return [$"the thread {(method == "cut" ? "removed" : "added")} {Units.Mm3(moved)} mm³, outside 20–100 % of the {Units.Mm3(annulus)} mm³ between Ø{Units.Mm(minor)} and Ø{Units.Mm(major)}"];
                checks.Add($"{size} {(internalThread ? "internal (Metric Tap)" : "external (Metric Die)")}, {q.DepthMm} mm, {(q.LeftHanded ? "left" : "right")}-handed: " +
                           $"{Units.Mm3(moved)} mm³ = {Math.Round(100 * moved / annulus)} % of the annulus between Ø{Units.Mm(minor)} and Ø{Units.Mm(major)}");
                return [];
            }, q.KeepOnFailure, api);
            Rename(ctx, o.Main, q.Name);
            return FeatureVerifier.Result(ctx, d, o, resolved: [edge.Info("edge")]);
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(session.Options.DefaultTimeout.TotalSeconds, 120)) }, ct);
    }

    /// <summary>"M6" (ISO coarse pitch) or "M6x0.75": the diameter and pitch, mm.</summary>
    internal static (double D, double Pitch) MetricSize(string? size)
    {
        string s = (size ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        if (!s.StartsWith('M')) throw SwException.InvalidArgument($"size '{size}' is not a metric size.", "Use e.g. M6 (coarse pitch) or M6x0.75.");
        var parts = s[1..].Split('X');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double dia) || dia <= 0)
            throw SwException.InvalidArgument($"size '{size}' has no diameter.");
        if (parts.Length == 2)
            return double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pitch) && pitch > 0 && pitch < dia
                ? (dia, pitch)
                : throw SwException.InvalidArgument($"size '{size}' has no valid pitch.");
        var coarse = HardwareCatalog.Standard("ISO 4032").Sizes.FirstOrDefault(x => Math.Abs(x.D - dia) < 1e-9)
                     ?? throw SwException.InvalidArgument($"No coarse pitch known for M{parts[0]}.", "Give the pitch, e.g. M7x1.");
        return (dia, coarse.Pitch);
    }
}
