using System.ComponentModel;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core.Services;

/// <summary>One step of an exploded view: components moved together along an assembly axis.</summary>
[Description("{components:['bolt-1'], direction:'+y', distanceMm:50}")]
public sealed class AsmExplodeStep
{
    [Description("Components moved in this step, e.g. [\"bolt-1\",\"washer-1\"].")]
    public required string[] Components { get; set; }

    [Description("+x | -x | +y | -y | +z | -z (assembly axes).")]
    public required string Direction { get; set; }

    [Description("How far they move in mm.")]
    public double DistanceMm { get; set; }
}

public sealed record AsmExplodeRequest
{
    public string? Doc { get; init; }
    public IReadOnlyList<AsmExplodeStep>? Steps { get; init; }
    /// <summary>Show the assembly collapsed again afterwards (the exploded view is kept).</summary>
    public bool Collapse { get; init; }
}

public sealed class AsmExplodedComponent
{
    public required string Name { get; init; }
    public required double[] CollapsedOriginMm { get; init; }
    public required double[] ExplodedOriginMm { get; init; }
    public required double[] OffsetMm { get; init; }
}

public sealed class AsmExplodeResult : ToolResult
{
    /// <summary>The exploded view (in the active configuration), e.g. "ExplView1".</summary>
    public required string View { get; init; }
    public required IReadOnlyList<string> StepNames { get; init; }
    public required IReadOnlyList<AsmExplodedComponent> Components { get; init; }
    /// <summary>True when the assembly is left shown exploded.</summary>
    public bool Exploded { get; init; }
    public required IReadOnlyList<string> Verified { get; init; }
}

public sealed partial class AssemblyService
{
    // ================================================================== asm_explode

    public Task<AsmExplodeResult> ExplodeAsync(AsmExplodeRequest q, CancellationToken ct = default)
    {
        if (q.Steps is not { Count: > 0 })
            throw SwException.InvalidArgument("'steps' is required: e.g. [{components:['lid-1'], direction:'+y', distanceMm:60}].");
        if (q.Steps.Count > 50) throw SwException.InvalidArgument($"At most 50 steps per call (got {q.Steps.Count}).");
        var plan = new List<(string[] Comps, int Axis, bool Reverse, Vec3 U, double Dist)>();
        for (int i = 0; i < q.Steps.Count; i++)
        {
            var s = q.Steps[i];
            if (s.Components is not { Length: > 0 } || s.Components.Any(string.IsNullOrWhiteSpace))
                throw SwException.InvalidArgument($"steps[{i}].components must name the components to move.");
            var u = AxisSpec.Parse(s.Direction ?? "", $"steps[{i}].direction");
            int axis = Math.Abs(u.X) > 0.5 ? (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis
                : Math.Abs(u.Y) > 0.5 ? (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_YAxis
                : (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_ZAxis;
            if (Math.Abs(Math.Abs(u.X) + Math.Abs(u.Y) + Math.Abs(u.Z) - 1) > 1e-9)
                throw SwException.InvalidArgument($"steps[{i}].direction must be an assembly axis (+x, -x, +y, -y, +z, -z).");
            double dist = Units.PositiveM(s.DistanceMm, $"steps[{i}].distanceMm");
            plan.Add((s.Components.Select(c => c.Trim()).ToArray(), axis, u.X + u.Y + u.Z < 0, u, dist));
        }
        return session.RunAsync("asm_explode", ctx => Explode(ctx, q, plan), RunOptions.Write(q.Doc) with { Timeout = Long(120) }, ct);
    }

    private static AsmExplodeResult Explode(SwContext ctx, AsmExplodeRequest q, List<(string[] Comps, int Axis, bool Reverse, Vec3 U, double Dist)> plan)
    {
        var asm = ctx.Assembly();
        var d = (IModelDoc2)asm;
        NoOpenSketch(d);
        var cfg = (object?)d.ConfigurationManager.ActiveConfiguration as IConfiguration
                  ?? throw new SwException(ErrorCodes.Internal, "The assembly has no active configuration.");
        var comps = new Dictionary<string, IComponent2>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in plan.SelectMany(p => p.Comps).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var c = EntityResolver.Component(asm, name);
            if (Safe(() => c.GetSuppression2(), -1) == (int)swComponentSuppressionState_e.swComponentSuppressed)
                throw SwException.InvalidArgument($"'{c.Name2}' is suppressed; it can't be exploded.");
            comps[name] = c;
        }
        // Where each component sits collapsed (read before the new view, with the assembly shown collapsed), and where
        // the steps should take it. GetSpecificTransform(true) was 40.7 mm off in z for a cube that never moved in z.
        if (Safe(() => d.IsExploded(), false)) asm.ViewCollapseAssembly();
        var collapsed = comps.ToDictionary(kv => kv.Key, kv => FrameOf(ctx, kv.Value).Origin, StringComparer.OrdinalIgnoreCase);
        var expected = comps.Keys.ToDictionary(k => k, _ => Vec3.Zero, StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan)
            foreach (var n in p.Comps)
                expected[n] += p.U * p.Dist;

        using var prefs = ctx.SystemPrefs();
        prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
        var viewsBefore = ComArgs.Strings((object?)asm.GetExplodedViewNames()).ToHashSet(StringComparer.Ordinal);
        d.ClearSelection2(true);
        if (!asm.CreateExplodedView())
            throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS created no exploded view.", "Close the Explode PropertyManager if it is open in SOLIDWORKS.");
        string view = ComArgs.Strings((object?)asm.GetExplodedViewNames()).FirstOrDefault(n => !viewsBefore.Contains(n))
                      ?? throw new SwException(ErrorCodes.FeatureFailed, "The new exploded view could not be found.");
        var checks = new List<string> { $"exploded view '{view}' created" };
        var steps = new List<string>();
        string? problem = null;
        string code = ErrorCodes.VerifyFailed;
        try
        {
            asm.ShowExploded2(true, view);
            // A new view can come with steps SOLIDWORKS made on its own: they are removed, the view is the caller's steps only.
            var own = Enumerable.Range(0, Math.Max(0, cfg.GetNumberOfExplodeSteps()))
                .Select(i => (object?)cfg.GetExplodeStep(i) as IExplodeStep).OfType<IExplodeStep>().Select(st => st.Name).ToList();
            foreach (var n in own) cfg.DeleteExplodeStep(n);
            if (own.Count > 0) checks.Add($"removed {own.Count} step(s) SOLIDWORKS added to the new view ({string.Join(", ", own)})");
            for (int i = 0; i < plan.Count && problem is null; i++)
            {
                var p = plan[i];
                d.ClearSelection2(true);
                foreach (var n in p.Comps)
                    if (!SelectComponent(d, comps[n], true, 1))
                        throw new SwException(ErrorCodes.FeatureFailed, $"Could not select '{comps[n].Name2}' for step {i + 1}.");
                var step = (object?)cfg.AddExplodeStep2(p.Dist, p.Axis, p.Reverse, 0, (int)swRotationAxisIndex_e.swRotationAxisIndex_Unknown, false, true, false, out int err) as IExplodeStep;
                d.ClearSelection2(true);
                if (step is null || err != (int)swCreateExplodeStepError_e.swCreateExplodeStepError_Successful)
                {
                    problem = $"step {i + 1} was not added (error {err}: {ExplodeError(err)})";
                    code = ErrorCodes.FeatureFailed;
                    break;
                }
                steps.Add(step.Name);
            }
            d.EditRebuild3();
            if (problem is null)
            {
                // Tier B: each component moved by the sum of its steps.
                foreach (var (name, c) in comps)
                {
                    var now = FrameOf(ctx, c).Origin;
                    var off = now - collapsed[name];
                    if (off.DistanceTo(expected[name]) > PosTol)
                    {
                        problem = $"'{c.Name2}' moved by [{Pt(off)}] mm, not [{Pt(expected[name])}] mm";
                        break;
                    }
                }
                if (problem is null)
                    checks.Add($"{steps.Count} step(s); every component moved by the sum of its steps (within {Units.Mm(PosTol)} mm)");
            }
        }
        catch (Exception ex) when (ex is SwException || (ex is COMException && !ComArgs.IsDisconnect(ex)))
        {
            problem = $"{(ex is SwException ? "" : ex.GetType().Name + ": ")}{ex.Message}";
            code = (ex as SwException)?.Code ?? ErrorCodes.Internal;
        }
        if (problem is not null)
        {
            string rb = DeleteExplodedView(d, asm, cfg, view, steps);
            throw new SwException(code, $"asm_explode: {problem}. {rb}",
                "Steps move whole components along assembly axes; check the component names (asm_component_list).");
        }

        var rows = comps.Select(kv =>
        {
            var exploded = FrameOf(ctx, kv.Value).Origin;
            return new AsmExplodedComponent
            {
                Name = kv.Value.Name2,
                CollapsedOriginMm = Units.Mm(collapsed[kv.Key]),
                ExplodedOriginMm = Units.Mm(exploded),
                OffsetMm = Units.Mm(exploded - collapsed[kv.Key]),
            };
        }).ToList();
        if (q.Collapse)
        {
            asm.ShowExploded2(false, view);
            checks.Add("shown collapsed again (the exploded view is kept)");
        }
        d.GraphicsRedraw2();
        return new AsmExplodeResult
        {
            Doc = ctx.Title(d),
            View = view,
            StepNames = steps,
            Components = rows,
            Exploded = !q.Collapse,
            Verified = checks,
        };
    }

    private static string ExplodeError(int e) => e switch
    {
        (int)swCreateExplodeStepError_e.swCreateExplodeStepError_NoExplodeView => "no active exploded view",
        (int)swCreateExplodeStepError_e.swCreateExplodeStepError_NoComponents => "no components selected",
        (int)swCreateExplodeStepError_e.swCreateExplodeStepError_OpenExplodePMP => "the Explode PropertyManager is open",
        (int)swCreateExplodeStepError_e.swCreateExplodeStepError_EditingComponentInContext => "a component is being edited in context",
        _ => "generic",
    };

    /// <summary>Removes a failed exploded view: its steps, then the view (selected as EXPLODEDVIEWS).</summary>
    private static string DeleteExplodedView(IModelDoc2 d, IAssemblyDoc asm, IConfiguration cfg, string view, List<string> steps)
    {
        Safe(() => asm.ShowExploded2(false, view), false);
        foreach (var s in steps) Safe(() => cfg.DeleteExplodeStep(s), false);
        d.ClearSelection2(true);
        // swSelEXPLVIEWS is "EXPLODEDVIEWS" (swconst help); the name may need the configuration after an '@'.
        bool selected = Safe(() => d.Extension.SelectByID2(view, "EXPLODEDVIEWS", 0, 0, 0, false, 0, null, 0), false) ||
                        Safe(() => d.Extension.SelectByID2($"{view}@{cfg.Name}", "EXPLODEDVIEWS", 0, 0, 0, false, 0, null, 0), false);
        if (selected) Safe(() => d.Extension.DeleteSelection2(0), false);
        d.ClearSelection2(true);
        Safe(() => d.EditRebuild3(), false);
        bool gone = !ComArgs.Strings((object?)asm.GetExplodedViewNames()).Contains(view);
        return gone ? $"Rolled back: deleted '{view}'." : $"The exploded view '{view}' could not be deleted (its steps were removed).";
    }
}
