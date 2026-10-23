using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SolidWorks.Interop.cosworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

// ====================================================================== DTOs

/// <summary>The finite-element mesh a study was solved on.</summary>
public sealed class SimMeshInfo
{
    public int Nodes { get; init; }
    public int Elements { get; init; }
    /// <summary>Global element size used (mm).</summary>
    public double ElementSizeMm { get; init; }
    public double ToleranceMm { get; init; }
    /// <summary>"high": second-order (10-node) tetrahedra.</summary>
    public required string Quality { get; init; }
    /// <summary>True when the size is SOLIDWORKS' default for the part (no meshSizeMm given).</summary>
    public bool DefaultSize { get; init; }
}

/// <summary>Wall-clock seconds per phase.</summary>
public sealed class SimTimes
{
    /// <summary>Add-in, material, study, fixtures and loads.</summary>
    public double SetupS { get; init; }
    public double MeshS { get; init; }
    public double SolveS { get; init; }
    public double TotalS { get; init; }
}

/// <summary>A load as applied (values read back from the study).</summary>
public sealed class SimLoadInfo
{
    /// <summary>pressure | force.</summary>
    public required string Kind { get; init; }
    public int Faces { get; init; }
    public double? ValueMpa { get; init; }
    public double? ValueN { get; init; }
    /// <summary>"normal", or the model direction "x,y,z" of a force.</summary>
    public required string Direction { get; init; }
    /// <summary>Pressure: the faces' total area (mm²) and pressure × area (N).</summary>
    public double? AreaMm2 { get; init; }
    public double? ForceN { get; init; }
}

/// <summary>[min, max] of the displacement components along the model axes (mm).</summary>
public sealed class SimDisplacementRanges
{
    public double[]? Ux { get; init; }
    public double[]? Uy { get; init; }
    public double[]? Uz { get; init; }
}

/// <summary>The fixture reactions against the applied loads (equilibrium check).</summary>
public sealed class SimReactions
{
    /// <summary>Sum of the reaction forces over the model (N), as SOLIDWORKS reports them.</summary>
    public required double[] TotalN { get; init; }
    /// <summary>The loads' resultant (N) from their values, directions and planar faces; null when a load face is curved.</summary>
    public double[]? LoadsN { get; init; }
    /// <summary>True when the reactions cancel the loads within 2 % (null: not compared).</summary>
    public bool? Balanced { get; init; }
}

/// <summary>What the call did with the SOLIDWORKS Simulation add-in.</summary>
public sealed class SimAddinInfo
{
    /// <summary>This call loaded the add-in (it was not loaded before).</summary>
    public bool LoadedByCall { get; init; }
    /// <summary>The call unloaded it again at the end.</summary>
    public bool Unloaded { get; init; }
    /// <summary>Why it was left loaded although the server loaded it (studies remain).</summary>
    public string? LeftLoaded { get; init; }
}

public sealed class SimStaticResult : ToolResult
{
    public required string Study { get; init; }
    /// <summary>The study (with its results) stays in the document (keepStudy, or it could not be deleted).</summary>
    public bool StudyKept { get; init; }
    /// <summary>The part material the study used.</summary>
    public required string Material { get; init; }
    /// <summary>True when this call assigned 'material'; <see cref="PreviousMaterial"/> is what the part had before.</summary>
    public bool? MaterialAssigned { get; init; }
    public string? PreviousMaterial { get; init; }
    public double MaxVonMisesMpa { get; init; }
    /// <summary>Where the largest von Mises stress is (mesh node, model mm).</summary>
    public double[]? MaxVonMisesAtMm { get; init; }
    /// <summary>Largest resultant displacement (URES).</summary>
    public double MaxDisplacementMm { get; init; }
    public double[]? MaxDisplacementAtMm { get; init; }
    public SimDisplacementRanges? DisplacementMm { get; init; }
    public double? YieldStrengthMpa { get; init; }
    /// <summary>Yield strength / largest von Mises stress.</summary>
    public double? MinFactorOfSafety { get; init; }
    public SimReactions? Reactions { get; init; }
    public required SimMeshInfo Mesh { get; init; }
    public required SimTimes Time { get; init; }
    public int FixedFaces { get; init; }
    public required IReadOnlyList<SimLoadInfo> Loads { get; init; }
    public required SimAddinInfo Addin { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
    public IReadOnlyList<ResolvedInfo>? Resolved { get; init; }
}

/// <summary>One Simulation study of a document.</summary>
public sealed class SimStudyRow
{
    public required string Name { get; init; }
    /// <summary>static | frequency | buckling | thermal | nonlinear | drop_test | fatigue | dynamic | …</summary>
    public required string Type { get; init; }
    public string? Configuration { get; init; }
    /// <summary>none | current | outdated | failed | interrupted.</summary>
    public required string Mesh { get; init; }
    public int? Nodes { get; init; }
    public int? Elements { get; init; }
    /// <summary>Static studies: whether results can be read.</summary>
    public bool? Results { get; init; }
    public double? MaxDisplacementMm { get; init; }
    public bool? Active { get; init; }
    /// <summary>Created by this server in this session (sim_static keepStudy): sim_delete removes it by default.</summary>
    public bool ServerCreated { get; init; }
}

public sealed class SimListResult : ToolResult
{
    public bool AddinLoaded { get; init; }
    public required IReadOnlyList<SimStudyRow> Studies { get; init; }
    public string? Message { get; init; }
}

public sealed class SimDeleteResult : ToolResult
{
    public required IReadOnlyList<string> Deleted { get; init; }
    /// <summary>The studies left in the document (null when the add-in was not consulted).</summary>
    public IReadOnlyList<string>? Remaining { get; init; }
    public SimAddinInfo? Addin { get; init; }
    public string? Message { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

// ====================================================================== service

/// <summary>
/// Linear static FEA through the SOLIDWORKS Simulation API (docs/solidworks-api-notes.md §14): load the add-in if
/// needed, create a static study, fix faces, apply pressures and forces, mesh, solve, read von Mises stress,
/// displacement and the fixture reactions, then delete the study (unless kept) and unload the add-in again if this call
/// loaded it. Every step's status is checked and the loads are read back; on failure the study is removed and the
/// error says what was cleaned up.
/// </summary>
public sealed class SimulationService(SwSession session)
{
    private const int Mm = (int)swsLinearUnit_e.swsLinearUnitMillimeters;           // 0: mesh sizes, displacements
    private const int NPerM2 = (int)swsPressureUnit_e.swsPressureUnit_NewtonsPerMeterSquared; // 0: pressure (Pa)
    private const int Newton = (int)swsForceUnit_e.swsForceUnitNOrNm;                // 0: forces, reactions
    private const int NPerMm2 = (int)swsStrengthUnit_e.swsStrengthUnitNewtonPerSquareMillimeter; // 3: stresses (MPa)
    private const int SI = (int)swsUnitSystem_e.swsUnitSystemSI;                     // 0: material properties

    /// <summary>Per SOLIDWORKS session: did this server load the add-in (and leave it loaded for kept studies)?</summary>
    private sealed class SimState
    {
        public bool AddinLoadedByServer;
        public string? AddinPath;
    }

    private static readonly ConditionalWeakTable<SwSession, SimState> States = new();

    /// <summary>The studies this server created and kept, per document (dies with the registry entry when it closes).</summary>
    private static readonly ConditionalWeakTable<DocumentRegistry.Entry, HashSet<string>> Created = new();

    private SimState State => States.GetValue(session, _ => new SimState());

    private static HashSet<string> Tracked(DocumentRegistry.Entry entry) =>
        Created.GetValue(entry, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private sealed record Addin(CosmosWorks Cw, bool LoadedByCall, string? Path);

    /// <summary>The entity-array flavour that the Simulation API accepted last (tried first next time).</summary>
    private sealed class Flavor
    {
        public SwExt.SimArrayFlavor Preferred = SwExt.SimArrayFlavor.Variant;
    }

    // ------------------------------------------------------------------ sim_static

    /// <summary>Runs a linear static study; <paramref name="phase"/> receives "meshing", "solving", … (any thread may read it).</summary>
    public Task<SimStaticResult> StaticAsync(SimStaticRequest q, Action<string>? phase = null, CancellationToken ct = default)
    {
        // Everything that needs no COM is checked before the call is queued.
        if (q.FixedFaces is not { Count: > 0 } && !q.AllowNoFixtures)
            throw SwException.InvalidArgument("'fixedFaces' is empty: a static study needs at least one fixed face, or the part floats away.",
                "Pass the faces that hold the part, e.g. fixedFaces:[{at:[0,5,5]}] (face_list shows ids and pick points).");
        var loads = SimulationMath.ParseLoads(q.Loads);
        if (q.MeshSizeMm is double ms && (!double.IsFinite(ms) || ms <= 0))
            throw SwException.InvalidArgument($"'meshSizeMm' must be a positive length in mm (got {ms}).", "Omit it for SOLIDWORKS' default size.");
        string study = SimulationMath.StudyName(q.StudyName);
        string? material = string.IsNullOrWhiteSpace(q.Material) ? null : q.Material.Trim();
        // Meshing and solving are single blocking COM calls: the whole run gets its own, long limit.
        var timeout = TimeSpan.FromSeconds(Math.Max(SimulationMath.TimeoutS, session.Options.DefaultTimeout.TotalSeconds));
        return session.RunAsync("sim_static", ctx => Static(ctx, q, loads, study, material, phase),
            RunOptions.Write(q.Doc) with { Timeout = timeout, DialogRules = DialogRules.SimulationRun(study) }, ct);
    }

    private SimStaticResult Static(SwContext ctx, SimStaticRequest q, IReadOnlyList<SimLoadSpec> loads, string study, string? material,
        Action<string>? phase)
    {
        var total = Stopwatch.StartNew();
        var d = ctx.Doc;
        var part = ctx.Part();
        var entry = ctx.DocEntry;
        string title = ctx.Title(d);
        if (d.SketchManager.ActiveSketch is ISketch open)
            throw new SwException(ErrorCodes.SketchState, $"Sketch '{((IFeature)open).Name}' is open.", "Finish it with sketch_finish first.");

        var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
        if (solids.Count == 0)
            throw SwException.InvalidArgument($"'{title}' has no solid body to analyze.", "Model a solid first (feature_extrude, …).");
        var (volume, box) = VolumeAndBox(solids);
        SimulationMath.CheckMeshSize(q.MeshSizeMm, volume);

        // Geometry first: a bad reference fails before anything changes.
        var fixedFaces = q.FixedFaces is { Count: > 0 } ff ? EntityResolver.ResolveAll(ctx, d, ff, "fixedFaces", EntityKind.Face) : [];
        var loadFaces = loads.Select(l => EntityResolver.ResolveAll(ctx, d, l.Faces, $"{l.Arg}.faces", EntityKind.Face)).ToList();
        var resolved = fixedFaces.Select(r => r.Info("fixedFaces"))
            .Concat(loads.SelectMany((l, i) => loadFaces[i].Select(r => r.Info($"{l.Arg}.faces")))).ToList();
        var loadResultant = LoadResultant(loads, loadFaces);
        var checks = new List<string>();

        // The material must be explicit: the given one (through material_set's checked logic), else the part's own.
        string currentMaterial = part.GetMaterialPropertyName2("", out _) ?? "";
        string? previousMaterial = null;
        bool assigned = false;
        if (material is not null)
        {
            phase?.Invoke("assigning the material");
            // Runs inline on this STA thread (the dispatcher is re-entrant), so the task is already complete.
            var set = new PropertiesService(session).MaterialSetAsync(entry.Handle, material).GetAwaiter().GetResult();
            previousMaterial = set.Previous;
            currentMaterial = set.Material;
            assigned = true;
            checks.Add($"material '{set.Material}' assigned and read back (was {(set.Previous is null ? "none" : $"'{set.Previous}'")})");
        }
        else if (currentMaterial.Length == 0)
            throw SwException.InvalidArgument($"'{title}' has no material, so its stiffness and strength are unknown.",
                "Pass material (e.g. \"AISI 1020\", \"6061 Alloy\"; material_list shows the names), or call material_set first.");
        else checks.Add($"part material '{currentMaterial}'");

        var st = State;
        Addin? addin = null;
        CWStudyManager? manager = null;
        string? created = null;
        int previousActive = -1;
        Outcome? outcome = null;
        Exception? failure = null;
        try
        {
            phase?.Invoke("loading the Simulation add-in");
            addin = EnsureAddin(ctx, st);
            manager = StudyManagerOf(addin, title);
            var names = StudyNames(manager);
            if (names.Contains(study, StringComparer.OrdinalIgnoreCase))
                throw SwException.InvalidArgument($"'{title}' already has a study named '{study}'.",
                    "Pass another studyName, or remove the old one with sim_delete.");
            if (names.Count > 0) previousActive = SafeActive(manager);

            phase?.Invoke("creating the study");
            var s = manager.CreateNewStudy3(study, (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeStatic,
                (int)swsMeshType_e.swsMeshTypeSolid, out int err);
            // Whatever the status, a study of that name may exist now: the cleanup must know.
            if (s is not null || StudyNames(manager).Contains(study, StringComparer.OrdinalIgnoreCase)) created = study;
            if (s is null || err != 0)
                throw new SwException(ErrorCodes.FeatureFailed, $"Simulation could not create the study '{study}' ({Name<swsStudyError_e>(err)}).",
                    "Check that the part has a solid body and that the study name is unused (sim_list).");
            created = study;
            checks.Add($"static study '{study}' created (solid mesh)");
            outcome = Run(ctx, s, fixedFaces, loads, loadFaces, loadResultant, q.MeshSizeMm, box, checks, phase, total);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        phase?.Invoke("cleaning up");
        var clean = Cleanup(ctx, st, entry, addin, manager, created, q.KeepStudy && failure is null, previousActive);
        if (failure is not null)
        {
            string note = clean.Note + (assigned ? $" The material stays '{currentMaterial}' (it was {(previousMaterial is null ? "none" : $"'{previousMaterial}'")})." : "");
            if (failure is SwException se) throw new SwException(se.Code, se.Message + note, se.Hint, se);
            if (ComArgs.IsDisconnect(failure) || ComArgs.IsRejected(failure) || ComArgs.IsServerFault(failure))
                ExceptionDispatchInfo.Capture(failure).Throw(); // the session maps these (NOT_CONNECTED, BUSY, INTERNAL)
            throw new SwException(ErrorCodes.Internal, $"{failure.GetType().Name}: {failure.Message}.{note}",
                "Check the document (sim_list, feature_list) before retrying.", failure);
        }

        var o = outcome!;
        if (clean.Kept && !q.KeepStudy) checks.Add($"the study '{study}' could not be deleted (see warnings)");
        else if (clean.Deleted) checks.Add($"study '{study}' deleted again");
        return new SimStaticResult
        {
            Doc = title,
            Study = study,
            StudyKept = clean.Kept,
            Material = currentMaterial,
            MaterialAssigned = assigned ? true : null,
            PreviousMaterial = previousMaterial,
            MaxVonMisesMpa = SimulationMath.Sig(o.VonMises.Max),
            MaxVonMisesAtMm = o.VonMisesAt,
            MaxDisplacementMm = SimulationMath.Sig(o.Displacement.Max),
            MaxDisplacementAtMm = o.DisplacementAt,
            DisplacementMm = o.Components,
            YieldStrengthMpa = o.YieldMpa is double y ? SimulationMath.Sig(y) : null,
            MinFactorOfSafety = SimulationMath.FactorOfSafety(o.YieldMpa, o.VonMises.Max),
            Reactions = o.Reactions,
            Mesh = o.Mesh,
            Time = new SimTimes
            {
                SetupS = Math.Round(o.SetupS, 2), MeshS = Math.Round(o.MeshS, 2), SolveS = Math.Round(o.SolveS, 2),
                TotalS = Math.Round(total.Elapsed.TotalSeconds, 2),
            },
            FixedFaces = fixedFaces.Count,
            Loads = o.Loads,
            Addin = new SimAddinInfo { LoadedByCall = addin?.LoadedByCall ?? false, Unloaded = clean.Unloaded, LeftLoaded = clean.LeftLoaded },
            Checks = checks,
            Resolved = resolved,
        };
    }

    private sealed record Outcome(SimRange VonMises, double[]? VonMisesAt, SimRange Displacement, double[]? DisplacementAt,
        SimDisplacementRanges? Components, double? YieldMpa, SimReactions? Reactions, SimMeshInfo Mesh, double SetupS, double MeshS,
        double SolveS, IReadOnlyList<SimLoadInfo> Loads);

    /// <summary>Fixture, loads, mesh, solve and results for a freshly created study.</summary>
    private static Outcome Run(SwContext ctx, CWStudy s, List<Resolved> fixedFaces, IReadOnlyList<SimLoadSpec> loads,
        List<List<Resolved>> loadFaces, Vec3? loadResultant, double? meshSizeMm, double[]? box, List<string> checks, Action<string>? phase,
        Stopwatch total)
    {
        var lr = s.LoadsAndRestraintsManager
                 ?? throw new SwException(ErrorCodes.FeatureFailed, "The new study has no loads-and-restraints manager.");
        var flavor = new Flavor();

        phase?.Invoke("applying fixtures and loads");
        var fixObjs = fixedFaces.Select(r => r.Obj).ToList();
        if (fixObjs.Count > 0)
        {
            var (restraint, rerr, how) = AddOnEntities(lr, fixObjs, flavor, a =>
            {
                var r = lr.AddRestraint((int)swsRestraintType_e.swsRestraintTypeFixed, a, null, out int e);
                return (r, e);
            });
            if (restraint is null)
                throw new SwException(ErrorCodes.FeatureFailed,
                    $"Simulation did not accept the fixture on {fixObjs.Count} face(s) ({Name<swsRestraintError_e>(rerr)}{how}).",
                    "fixedFaces must be faces of the part's solid body (face_list shows them).");
            int type = restraint.RestraintType;
            if (type != (int)swsRestraintType_e.swsRestraintTypeFixed)
                throw new SwException(ErrorCodes.VerifyFailed, $"The fixture reads back as restraint type {Name<swsRestraintType_e>(type)} instead of fixed.");
            checks.Add($"fixed fixture on {fixObjs.Count} face(s) accepted{how}{EntitiesNote(lr, fixObjs.Count)}");
        }
        else ctx.Warn("No fixture was applied (test mode).");

        var applied = new List<SimLoadInfo>();
        for (int i = 0; i < loads.Count; i++)
            applied.Add(ApplyLoad(ctx, lr, loads[i], loadFaces[i], flavor, checks));
        double setupS = total.Elapsed.TotalSeconds;

        // Mesh: high quality (second order), SOLIDWORKS' default size unless the caller gave one. Lengths in mm (unit 0).
        phase?.Invoke("meshing");
        var mesh = s.Mesh ?? throw new SwException(ErrorCodes.FeatureFailed, "The new study has no mesh object.");
        mesh.Quality = (int)swsMeshQuality_e.swsMeshQualityHigh;
        mesh.GetDefaultElementSizeAndTolerance(Mm, out double defSize, out double defTol);
        double size = meshSizeMm ?? defSize;
        double tol = meshSizeMm is null ? defTol : SimulationMath.Tolerance(size, defSize, defTol);
        if (!(size > 0) || !(tol > 0))
            throw new SwException(ErrorCodes.FeatureFailed, $"Simulation reported no usable default element size ({SimulationMath.F(defSize)} mm).",
                "Pass meshSizeMm explicitly.");
        var meshClock = Stopwatch.StartNew();
        int meshErr = s.CreateMesh(Mm, size, tol);
        meshClock.Stop();
        int state = mesh.MeshState, nodes = mesh.NodeCount, elements = mesh.ElementCount;
        if (meshErr != 0 || state != (int)swsMeshState_e.swsMeshStateExistsAndCurrent || nodes <= 0 || elements <= 0)
            throw new SwException(ErrorCodes.FeatureFailed,
                $"Meshing failed ({Name<swsStudyMeshError_e>(meshErr)}; mesh {SimulationMath.MeshState(state)}, {nodes} nodes, {elements} elements, " +
                $"element size {SimulationMath.F(SimulationMath.Sig(size, 4))} mm).",
                meshSizeMm is null ? "Check the part with model_check, or pass a smaller meshSizeMm." : "Try another meshSizeMm, or omit it for the default size.");
        int quality = mesh.Quality;
        checks.Add($"mesh current: {nodes} nodes, {elements} elements ({(quality == (int)swsMeshQuality_e.swsMeshQualityHigh ? "high" : "draft")} " +
                   $"quality, {SimulationMath.F(SimulationMath.Sig(size, 4))} mm)");
        var meshInfo = new SimMeshInfo
        {
            Nodes = nodes, Elements = elements, ElementSizeMm = SimulationMath.Sig(size, 6), ToleranceMm = SimulationMath.Sig(tol, 6),
            Quality = quality == (int)swsMeshQuality_e.swsMeshQualityHigh ? "high" : "draft", DefaultSize = meshSizeMm is null,
        };

        phase?.Invoke("solving");
        var solveClock = Stopwatch.StartNew();
        int run = s.RunAnalysis();
        solveClock.Stop();
        if (run != 0)
            throw new SwException(ErrorCodes.FeatureFailed, $"The solver did not finish: RunAnalysis returned {run} ({Name<swsRunAnalysisError_e>(run)}).",
                SimulationMath.RunHint(run));
        checks.Add("solver status 0");

        phase?.Invoke("reading results");
        var res = s.Results ?? throw new SwException(ErrorCodes.VerifyFailed, "The solver reported success but the study has no results.");
        var vm = SimulationMath.Range(res.StressRange((int)swsStressComponent_e.swsStressComponentVON, NPerMm2, out int vmErr));
        if (vm is null || vmErr != 0)
            throw new SwException(ErrorCodes.VerifyFailed, $"The von Mises stresses could not be read ({Name<swsResultsError_e>(vmErr)}).");
        var ures = SimulationMath.Range(res.DisplacementRange((int)swsDisplacementComponent_e.swsDisplacementComponentURES, Mm, out int uErr));
        if (ures is null || uErr != 0)
            throw new SwException(ErrorCodes.VerifyFailed, $"The displacements could not be read ({Name<swsResultsError_e>(uErr)}).");
        if (vm.Max < 0 || vm.Min < -1e-9 || vm.Max < vm.Min || ures.Max < 0 || ures.Max < ures.Min)
            throw new SwException(ErrorCodes.VerifyFailed,
                $"The results are inconsistent: von Mises {SimulationMath.F(vm.Min)}..{SimulationMath.F(vm.Max)} MPa, URES {SimulationMath.F(ures.Min)}..{SimulationMath.F(ures.Max)} mm.");
        checks.Add("von Mises stress and resultant displacement read");

        var comps = new SimDisplacementRanges
        {
            Ux = Component(res, swsDisplacementComponent_e.swsDisplacementComponentUX),
            Uy = Component(res, swsDisplacementComponent_e.swsDisplacementComponentUY),
            Uz = Component(res, swsDisplacementComponent_e.swsDisplacementComponentUZ),
        };
        CheckForceDirection(ctx, loads, comps, ures.Max, checks);
        var reactions = Reactions(ctx, res, nodes, fixObjs, flavor, loads, loadResultant, checks);

        var vmAt = NodeAt(mesh, vm.MaxNode, box);
        var uAt = NodeAt(mesh, ures.MaxNode, box);
        if (vmAt is null || uAt is null) ctx.Warn("The location of the largest stress or displacement could not be read (mesh node coordinates).");

        var (yieldMpa, studyMaterial, mixed) = StudyYield(ctx, s);
        if (yieldMpa is null) ctx.Warn("The yield strength could not be read from the study material, so no factor of safety is given.");
        else checks.Add($"yield strength {SimulationMath.F(SimulationMath.Sig(yieldMpa.Value, 6))} MPa from the study material" +
                        (studyMaterial is { Length: > 0 } ? $" '{studyMaterial}'" : "") + (mixed ? " (lowest of several materials)" : ""));
        return new Outcome(vm, vmAt, ures, uAt, comps, yieldMpa, reactions, meshInfo, setupS, meshClock.Elapsed.TotalSeconds,
            solveClock.Elapsed.TotalSeconds, applied);
    }

    /// <summary>Adds one pressure or force and reads its value back.</summary>
    private static SimLoadInfo ApplyLoad(SwContext ctx, CWLoadsAndRestraintsManager lr, SimLoadSpec load, List<Resolved> faces, Flavor flavor,
        List<string> checks)
    {
        var objs = faces.Select(r => r.Obj).ToList();
        if (load.Kind == SimLoadKind.Pressure)
        {
            // VERIFIED recipe: AddPressure(normal), then Unit = 0 (N/m²) and Value in Pa inside Begin/EndEdit.
            var (p, perr, how) = AddOnEntities(lr, objs, flavor, a =>
            {
                var x = lr.AddPressure((int)swsPressureType_e.swsPressureTypeNormal, a, null, out int e);
                return (x, e);
            });
            if (p is null)
                throw new SwException(ErrorCodes.FeatureFailed, $"Simulation did not accept the pressure of {load.Arg} ({Name<swsPressureError_e>(perr)}{how}).",
                    "A pressure needs faces of the part's solid body.");
            p.PressureBeginEdit();
            p.Unit = NPerM2;
            p.Value = load.Value;
            int end = p.PressureEndEdit();
            if (end != 0)
                throw new SwException(ErrorCodes.FeatureFailed, $"Simulation refused the pressure value of {load.Arg} ({Name<swsPressureEndEditError_e>(end)}).");
            double back = p.Value;
            int unit = p.Unit, ptype = p.PressureType;
            if (unit != NPerM2 || Math.Abs(back - load.Value) > 1e-6 * load.Value || ptype != (int)swsPressureType_e.swsPressureTypeNormal)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"{load.Arg}: the pressure reads back {SimulationMath.F(back)} (unit {unit}, type {ptype}) instead of {SimulationMath.F(load.Value)} Pa normal to the faces.");
            double area = faces.Sum(f => f.Obj is IFace2 face ? face.GetArea() : 0);
            checks.Add($"{load.Arg}: pressure {SimulationMath.F(load.Value / SimulationMath.PaPerMpa)} MPa on {objs.Count} face(s) read back{how}{EntitiesNote(lr, objs.Count)}");
            return new SimLoadInfo
            {
                Kind = load.KindName, Faces = objs.Count, ValueMpa = load.Value / SimulationMath.PaPerMpa, Direction = "normal",
                AreaMm2 = Units.Mm2(area), ForceN = SimulationMath.Sig(load.Value * area),
            };
        }

        // Forces (GUESS: not verified live yet). AddForce3 is the current API; AddForce2 (obsolete) is the fallback. The
        // value is then set again with Unit = N inside Force Begin/EndEdit and read back.
        bool normal = load.Direction is null;
        double[] c = [0, 0, 0];
        bool[] use = [false, false, false];
        int ucode = 0;
        double[] comps = [1, 1, 1, 1, 1, 1]; // normal force: six ones, Ucode 0 (API remarks)
        IFeature? plane = null;
        if (load.Direction is Vec3 dir)
        {
            // A direction is given along a reference plane's axes (direction 1, direction 2, normal): the Front Plane.
            var (front, xAxis, yAxis, n) = ForceReference(ctx.Doc);
            plane = front;
            c = SimulationMath.Components(dir * load.Value, xAxis, yAxis, n);
            (use, ucode, comps) = SimulationMath.ForceComps(c, load.Value);
        }
        int forceType = normal ? (int)swsForceType_e.swsForceTypeNormal : (int)swsForceType_e.swsForceTypeForceOrMoment;
        const int faceSelection = (int)swsSelectionType_e.swsSelectionFaceEdgeVertexPoint;
        int refDirType = normal ? -1 : 2; // -1: no direction reference; 2: a face or plane
        string api = "AddForce3";
        var (f, ferr, fhow) = AddOnEntities(lr, objs, flavor, a =>
        {
            var x = lr.AddForce3(forceType, faceSelection, refDirType, 0, 0, 0, ComArgs.Empty, ComArgs.Empty, false, false, 0, 0, ucode, load.Value,
                comps, false, false, a, plane, false, out int e);
            return (x, e);
        });
        if (f is null)
        {
            api = "AddForce2";
            string first = $"AddForce3: {Name<swsForceError_e>(ferr)}{fhow}";
            (f, ferr, fhow) = AddOnEntities(lr, objs, flavor, a =>
            {
                var x = lr.AddForce2(forceType, faceSelection, a, plane, out int e);
                return (x, e);
            });
            if (f is null)
                throw new SwException(ErrorCodes.FeatureFailed,
                    $"Simulation did not accept the {(normal ? "normal" : "directional")} force of {load.Arg} ({first}; " +
                    $"AddForce2: {Name<swsForceError_e>(ferr)}{fhow}).",
                    normal ? "A normal force needs faces of the part's solid body." : "Try direction 'normal', or check that the faces belong to the solid body.");
        }
        f.ForceBeginEdit();
        f.Unit = Newton;
        if (normal) f.NormalForceOrTorqueValue = load.Value;
        else f.SetForceComponentValues2(use[0], use[1], use[2], use[0] ? c[0] : 0, use[1] ? c[1] : 0, use[2] ? c[2] : 0);
        int fend = f.ForceEndEdit();
        if (fend != 0)
            throw new SwException(ErrorCodes.FeatureFailed, $"Simulation refused the force value of {load.Arg} ({Name<swsForceEndEditError_e>(fend)}).");
        int funit = f.Unit, ftype = f.ForceType;
        if (funit != Newton || ftype != forceType)
            throw new SwException(ErrorCodes.VerifyFailed, $"{load.Arg}: the force reads back as type {ftype}, unit {funit} (expected type {forceType}, newtons).");
        if (normal)
        {
            double back = f.NormalForceOrTorqueValue;
            if (Math.Abs(back - load.Value) > 1e-6 * load.Value)
                throw new SwException(ErrorCodes.VerifyFailed, $"{load.Arg}: the force reads back {SimulationMath.F(back)} N instead of {SimulationMath.F(load.Value)} N.");
            checks.Add($"{load.Arg}: normal force {SimulationMath.F(load.Value)} N on {objs.Count} face(s) read back ({api}{fhow}){EntitiesNote(lr, objs.Count)}");
            return new SimLoadInfo { Kind = load.KindName, Faces = objs.Count, ValueN = load.Value, Direction = "normal" };
        }
        f.GetForceComponentValues2(out bool b1, out bool b2, out bool b3, out double d1, out double d2, out double d3);
        bool[] usedBack = [b1, b2, b3];
        double[] back3 = [d1, d2, d3];
        for (int k = 0; k < 3; k++)
            if (use[k] && (!usedBack[k] || Math.Abs(back3[k] - c[k]) > 1e-6 * load.Value))
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"{load.Arg}: the force components read back [{string.Join(", ", back3.Select(SimulationMath.F))}] N instead of " +
                    $"[{string.Join(", ", c.Select(v => SimulationMath.F(SimulationMath.Sig(v))))}] N.");
        checks.Add($"{load.Arg}: force {SimulationMath.F(load.Value)} N along [{SimulationMath.Vector(load.Direction!.Value)}] on {objs.Count} face(s) read back " +
                   $"({api}{fhow}, components on '{plane!.Name}'){EntitiesNote(lr, objs.Count)}");
        return new SimLoadInfo { Kind = load.KindName, Faces = objs.Count, ValueN = load.Value, Direction = SimulationMath.Vector(load.Direction.Value) };
    }

    /// <summary>Tier B for a lone axis-aligned force: the model moves the way the force pushes.</summary>
    private static void CheckForceDirection(SwContext ctx, IReadOnlyList<SimLoadSpec> loads, SimDisplacementRanges comps, double uresMax, List<string> checks)
    {
        if (loads.Count != 1 || loads[0].Direction is not Vec3 dir || uresMax <= 0) return;
        double[] a = [Math.Abs(dir.X), Math.Abs(dir.Y), Math.Abs(dir.Z)];
        int axis = Array.IndexOf(a, a.Max());
        if (a[axis] < 1 - 1e-9) return; // not along a model axis
        var range = axis switch { 0 => comps.Ux, 1 => comps.Uy, _ => comps.Uz };
        if (range is not { Length: 2 }) return;
        double sign = (axis switch { 0 => dir.X, 1 => dir.Y, _ => dir.Z }) > 0 ? 1 : -1;
        double along = sign > 0 ? range[1] : -range[0];
        string name = "xyz"[axis].ToString();
        if (along >= 0.5 * uresMax) checks.Add($"the largest displacement is along the force ({(sign > 0 ? "+" : "-")}{name})");
        else ctx.Warn($"The largest displacement ({SimulationMath.F(SimulationMath.Sig(uresMax))} mm) is not mainly along the force " +
                      $"({(sign > 0 ? "+" : "-")}{name}: {SimulationMath.F(SimulationMath.Sig(along))} mm): check the direction and the fixtures.");
    }

    /// <summary>
    /// Tier B (GUESS: the reading is not verified live yet): the fixture reactions must cancel the applied loads. Read
    /// with GetReactionForcesAndMomentsWithSelections on the fixed faces; a mismatch is a warning, not a failure.
    /// </summary>
    private static SimReactions? Reactions(SwContext ctx, CWResults res, int nodes, IReadOnlyList<object> fixObjs, Flavor flavor,
        IReadOnlyList<SimLoadSpec> loads, Vec3? load, List<string> checks)
    {
        if (fixObjs.Count == 0) return null;
        if (nodes > SimulationMath.MaxNodesForReactions)
        {
            checks.Add($"fixture reactions not read (mesh of {nodes} nodes)");
            return null;
        }
        Vec3? total = null;
        int err = -1;
        string? problem = null;
        foreach (var fl in SwExt.SimFlavors(flavor.Preferred))
        {
            try
            {
                var summary = res.SimReactionSummary(SwExt.SimEntityArray(fixObjs, fl), Newton, out err);
                total = err == 0 ? SimulationMath.ReactionTotal(summary) : null;
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex) && !ComArgs.IsRejected(ex))
            {
                problem = ex.Message;
            }
            if (total is not null) break;
        }
        if (total is not Vec3 r)
        {
            ctx.Warn($"The fixture reactions could not be read ({(problem ?? Name<swsResultsError_e>(err))}), so the load balance was not checked.");
            return null;
        }
        if (load is not Vec3 l)
        {
            checks.Add($"fixture reactions {SimulationMath.Newtons(r)} (a load face is curved: not compared with the loads)");
            return new SimReactions { TotalN = SimulationMath.Sig(r, 6) };
        }
        var b = SimulationMath.Balance(r, l);
        if (b.Balanced)
            checks.Add($"fixture reactions {SimulationMath.Newtons(r)} balance the loads {SimulationMath.Newtons(l)}" +
                       (b.LoadSign ? " (SOLIDWORKS reports them as the force on the fixtures)" : ""));
        else
        {
            bool perFace = loads.Any(x => x.Kind == SimLoadKind.Force && x.Faces.Count > 1);
            ctx.Warn($"The fixture reactions {SimulationMath.Newtons(r)} do not balance the applied loads {SimulationMath.Newtons(l)}: " +
                     "check the load values, units and directions" +
                     (perFace ? " (a force on several faces may have been applied to each face)." : "."));
        }
        return new SimReactions { TotalN = SimulationMath.Sig(r, 6), LoadsN = SimulationMath.Sig(l, 6), Balanced = b.Balanced };
    }

    /// <summary>The applied loads' resultant (N), or null when a pressure or normal force sits on a curved face.</summary>
    private static Vec3? LoadResultant(IReadOnlyList<SimLoadSpec> loads, List<List<Resolved>> faces)
    {
        var sum = Vec3.Zero;
        for (int i = 0; i < loads.Count; i++)
        {
            var l = loads[i];
            if (l.Kind == SimLoadKind.Force && l.Direction is Vec3 dir)
            {
                sum += dir * l.Value;
                continue;
            }
            var planar = new List<SimFace>();
            foreach (var r in faces[i])
            {
                if (r.Obj is not IFace2 f || Topology.SurfaceKind(f.Surface()) != "plane") return null;
                var n = f.NormalVec();
                if (n.Length < 3) return null;
                planar.Add(new SimFace(new Vec3(n[0], n[1], n[2]).Normalized(), f.GetArea()));
            }
            sum += l.Kind == SimLoadKind.Pressure ? SimulationMath.PressureResultant(l.Value, planar) : SimulationMath.NormalForceResultant(l.Value, planar);
        }
        return sum;
    }

    // ------------------------------------------------------------------ sim_list

    public Task<SimListResult> ListAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("sim_list", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            string title = ctx.Title(d);
            var tracked = Tracked(ctx.DocEntry);
            var cb = ctx.App.SimulationAddin();
            if (cb is null)
                return new SimListResult
                {
                    Doc = title, AddinLoaded = false, Studies = [],
                    Message = "The SOLIDWORKS Simulation add-in is not loaded, so no study is active (studies saved in the file appear " +
                              "once it is loaded; sim_static loads it).",
                };
            var rows = OnActive(ctx, d, () =>
            {
                var cw = cb.CosmosWorks ?? throw new SwException(ErrorCodes.AddinUnavailable, "The Simulation add-in returned no API object.");
                var manager = WaitForDoc(cw)?.StudyManager
                              ?? throw new SwException(ErrorCodes.AddinUnavailable, $"Simulation has no data for '{title}'.",
                                  "Activate the document (doc_activate) and retry.");
                int active = SafeActive(manager);
                var list = new List<SimStudyRow>();
                foreach (var (index, s) in Studies(manager))
                {
                    string name = s.Name ?? "";
                    try
                    {
                        list.Add(Row(s, index == active, tracked.Contains(name)));
                    }
                    catch (COMException ex) when (!ComArgs.IsDisconnect(ex) && !ComArgs.IsRejected(ex))
                    {
                        ctx.Warn($"The study '{name}' could not be read completely: {ex.Message}");
                        list.Add(new SimStudyRow { Name = name, Type = "unknown", Mesh = "unknown", ServerCreated = tracked.Contains(name) });
                    }
                }
                return list;
            });
            return new SimListResult
            {
                Doc = title, AddinLoaded = true, Studies = rows,
                Message = rows.Count == 0 ? $"'{title}' has no Simulation studies." : null,
            };
        }, RunOptions.Read(doc) with { Timeout = TimeSpan.FromSeconds(Math.Max(120, session.Options.DefaultTimeout.TotalSeconds)) }, ct);

    private static SimStudyRow Row(CWStudy s, bool active, bool serverCreated)
    {
        int type = s.AnalysisType;
        var mesh = s.Mesh;
        int state = mesh?.MeshState ?? 0;
        bool meshed = state is (int)swsMeshState_e.swsMeshStateExistsAndCurrent or (int)swsMeshState_e.swsMeshStateExistsAndNotCurrent;
        bool? results = null;
        double? maxU = null;
        if (type == (int)swsAnalysisStudyType_e.swsAnalysisStudyTypeStatic)
        {
            results = false;
            try
            {
                var res = s.Results;
                if (res is not null && meshed)
                {
                    var u = SimulationMath.Range(res.DisplacementRange((int)swsDisplacementComponent_e.swsDisplacementComponentURES, Mm, out int e));
                    if (u is not null && e == 0)
                    {
                        results = true;
                        maxU = SimulationMath.Sig(u.Max);
                    }
                }
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* no results database */ }
        }
        string? config = null;
        try { config = s.ConfigurationName; } catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* optional */ }
        return new SimStudyRow
        {
            Name = s.Name ?? "",
            Type = SimulationMath.StudyType(type),
            Configuration = string.IsNullOrEmpty(config) ? null : config,
            Mesh = SimulationMath.MeshState(state),
            Nodes = meshed ? mesh!.NodeCount : null,
            Elements = meshed ? mesh!.ElementCount : null,
            Results = results,
            MaxDisplacementMm = maxU,
            Active = active ? true : null,
            ServerCreated = serverCreated,
        };
    }

    // ------------------------------------------------------------------ sim_delete

    /// <summary>
    /// Deletes the named studies (only in documents this server owns, unless the server created them), or by default the
    /// studies this server created in the document.
    /// </summary>
    public Task<SimDeleteResult> DeleteAsync(string? doc, IReadOnlyList<string>? studies, CancellationToken ct = default)
    {
        var names = studies?.Select(s => s?.Trim() ?? "").ToList();
        if (names is not null && names.Any(n => n.Length == 0))
            throw SwException.InvalidArgument("'studies' contains an empty name.", "sim_list shows the study names.");
        return session.RunAsync("sim_delete", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            var entry = ctx.DocEntry;
            string title = ctx.Title(d);
            var tracked = Tracked(entry);
            bool named = names is { Count: > 0 };
            var targets = named ? names!.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : tracked.ToList();
            if (targets.Count == 0)
                return new SimDeleteResult
                {
                    Doc = title, Deleted = [], Checks = [],
                    Message = $"This server created no studies in '{title}' in this session; nothing was deleted. Pass 'studies' to delete " +
                              "others (only in documents this server owns; sim_list shows the names).",
                };
            var foreignStudies = targets.Where(t => !tracked.Contains(t)).ToList();
            if (foreignStudies.Count > 0 && entry.Class != DocClass.Owned)
                throw new SwException(ErrorCodes.NotOwned,
                    $"'{title}' is the user's document and this server did not create {Quote(foreignStudies)}, so it does not delete them.",
                    "Ask the user to delete them in SOLIDWORKS, or pass only studies this server created (sim_list: serverCreated).");

            var st = State;
            var addin = EnsureAddin(ctx, st);
            var deleted = new List<string>();
            var checks = new List<string>();
            List<string>? remaining = null;
            SimAddinInfo? addinInfo = null;
            CWStudyManager? manager = null;
            try
            {
                manager = StudyManagerOf(addin, title);
                var existing = StudyNames(manager);
                var missing = targets.Where(t => !existing.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (named && missing.Count > 0)
                    throw new SwException(ErrorCodes.EntityNotFound, $"'{title}' has no study named {Quote(missing)}.",
                        existing.Count == 0 ? "It has no studies at all." : $"Studies: {Quote(existing)}.");
                foreach (var m in missing)
                {
                    tracked.Remove(m);
                    ctx.Warn($"The study '{m}' is already gone.");
                }
                foreach (var t in targets.Except(missing, StringComparer.OrdinalIgnoreCase))
                {
                    string actual = existing.First(e => e.Equals(t, StringComparison.OrdinalIgnoreCase));
                    manager.DeleteStudy(actual);
                    if (StudyNames(manager).Contains(actual, StringComparer.OrdinalIgnoreCase))
                        throw new SwException(ErrorCodes.VerifyFailed, $"The study '{actual}' is still there after DeleteStudy." +
                                                                       (deleted.Count > 0 ? $" Deleted before it: {Quote(deleted)}." : ""),
                            "Delete it in the SOLIDWORKS Simulation tree, or check dialog_list for a prompt.");
                    deleted.Add(actual);
                    tracked.Remove(actual);
                }
                remaining = StudyNames(manager);
                if (deleted.Count > 0) checks.Add($"{deleted.Count} stud{(deleted.Count == 1 ? "y" : "ies")} gone from the study list");
            }
            finally
            {
                // Unload when this call loaded the add-in, or an earlier sim_static did and left it loaded for a kept study.
                bool ours = addin.LoadedByCall || st.AddinLoadedByServer;
                if (ours)
                {
                    // After an error the list may not have been read yet: count now (no manager at all = nothing changed).
                    int count = remaining?.Count ?? (manager is not null ? SafeCount(manager) : addin.LoadedByCall ? 0 : -1);
                    var (unloaded, left) = MaybeUnload(ctx, st, entry, addin.Path, count);
                    addinInfo = new SimAddinInfo { LoadedByCall = addin.LoadedByCall, Unloaded = unloaded, LeftLoaded = left };
                }
            }
            return new SimDeleteResult
            {
                Doc = title, Deleted = deleted, Remaining = remaining, Addin = addinInfo, Checks = checks,
                Message = deleted.Count == 0 ? "Nothing was deleted (the studies were already gone)." : null,
            };
        }, RunOptions.Write(doc) with
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(120, session.Options.DefaultTimeout.TotalSeconds)),
            DialogRules = DialogRules.SimulationCleanup,
        }, ct);
    }

    // ------------------------------------------------------------------ cleanup and add-in

    private sealed record CleanupResult(bool Deleted, bool Kept, bool Unloaded, string? LeftLoaded, string Note);

    /// <summary>
    /// Deletes the study (unless kept), restores the previously active study, and unloads the add-in when this call loaded
    /// it and no study is left. Never throws: problems become warnings and are summed up in <see cref="CleanupResult.Note"/>.
    /// </summary>
    private static CleanupResult Cleanup(SwContext ctx, SimState st, DocumentRegistry.Entry entry, Addin? addin, CWStudyManager? manager,
        string? created, bool keep, int previousActive)
    {
        bool deleted = false, kept = false;
        var notes = new List<string>();
        if (created is not null && manager is not null)
        {
            if (keep)
            {
                Tracked(entry).Add(created);
                kept = true;
                notes.Add($"The study '{created}' was kept (keepStudy).");
            }
            else
            {
                try
                {
                    manager.DeleteStudy(created);
                    deleted = !StudyNames(manager).Contains(created, StringComparer.OrdinalIgnoreCase);
                }
                catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
                {
                    ctx.Warn($"Deleting the study '{created}' failed: {ex.Message}");
                }
                if (deleted) notes.Add($"The study '{created}' was deleted.");
                else
                {
                    Tracked(entry).Add(created);
                    kept = true;
                    ctx.Warn($"The study '{created}' could not be deleted; remove it with sim_delete (closing the document would ask to save Simulation data).");
                    notes.Add($"The study '{created}' could NOT be deleted (sim_delete removes it).");
                }
                if (deleted && previousActive >= 0)
                {
                    try
                    {
                        if (manager.StudyCount > previousActive) manager.ActiveStudy = previousActive;
                    }
                    catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* cosmetic */ }
                }
            }
        }

        bool unloaded = false;
        string? left = null;
        if (addin is { LoadedByCall: true })
        {
            int remaining = manager is null ? 0 : SafeCount(manager);
            (unloaded, left) = MaybeUnload(ctx, st, entry, addin.Path, remaining);
            if (unloaded) notes.Add("The Simulation add-in was unloaded again.");
        }
        return new CleanupResult(deleted, kept, unloaded, left, notes.Count == 0 ? "" : " " + string.Join(" ", notes));
    }

    /// <summary>
    /// Unloads the add-in when no study is left in this document (<paramref name="remaining"/>; -1 = unknown) and no other
    /// open document holds a study this server created. Otherwise remembers that the server loaded it.
    /// </summary>
    private static (bool Unloaded, string? LeftLoaded) MaybeUnload(SwContext ctx, SimState st, DocumentRegistry.Entry entry, string? path, int remaining)
    {
        string? why = remaining < 0 ? "the study list could not be read"
            : remaining > 0 ? $"the document still has {remaining} stud{(remaining == 1 ? "y" : "ies")}"
            : OtherDocsWithStudies(ctx, entry) is { Count: > 0 } others ? $"studies created by this server remain in {Quote(others)}"
            : path is null ? "the add-in's file is unknown"
            : null;
        if (why is not null)
        {
            st.AddinLoadedByServer = true;
            return (false, why);
        }
        try
        {
            int r = ctx.App.UnloadAddIn(path!);
            bool gone = ctx.App.SimulationAddin() is null;
            if (gone)
            {
                st.AddinLoadedByServer = false;
                return (true, null);
            }
            st.AddinLoadedByServer = true;
            ctx.Warn($"UnloadAddIn returned {r} but the Simulation add-in is still loaded.");
            return (false, "unloading did not take effect");
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
        {
            st.AddinLoadedByServer = true;
            ctx.Warn($"Unloading the Simulation add-in failed: {ex.Message}");
            return (false, "unloading failed");
        }
    }

    private static List<string> OtherDocsWithStudies(SwContext ctx, DocumentRegistry.Entry entry) =>
        ctx.Documents.Entries.Where(e => !ReferenceEquals(e, entry) && Created.TryGetValue(e, out var set) && set.Count > 0)
            .Select(e => e.Title).ToList();

    /// <summary>The Simulation API, loading the add-in from the SOLIDWORKS folder when it isn't loaded.</summary>
    private static Addin EnsureAddin(SwContext ctx, SimState st)
    {
        var cb = ctx.App.SimulationAddin();
        if (cb is not null) return new Addin(Cosmos(cb), false, st.AddinPath ?? CosworksPath(ctx.App));

        string path = CosworksPath(ctx.App) ?? throw new SwException(ErrorCodes.AddinUnavailable,
            "SOLIDWORKS Simulation is not installed: there is no Simulation\\cosworks.dll in the SOLIDWORKS folder.",
            "Static studies need SOLIDWORKS Premium or Professional with Simulation installed.");
        int r = ctx.App.LoadAddIn(path);
        if (r != (int)swLoadAddinError_e.swSuccess && r != (int)swLoadAddinError_e.swAddinAlreadyLoaded)
            throw new SwException(ErrorCodes.AddinUnavailable, $"Loading the Simulation add-in failed ({Name<swLoadAddinError_e>(r)}, '{path}').",
                r switch
                {
                    (int)swLoadAddinError_e.swLicenseError => "No SOLIDWORKS Simulation license is available; ask the user.",
                    (int)swLoadAddinError_e.swAddinsDisabled => "Add-ins are disabled in this SOLIDWORKS session (started with add-ins off?).",
                    _ => "Ask the user to enable SOLIDWORKS Simulation under Tools > Add-Ins, then retry.",
                });
        st.AddinPath = path;
        cb = ctx.App.SimulationAddin();
        if (cb is null)
        {
            if (r == (int)swLoadAddinError_e.swSuccess)
                try { ctx.App.UnloadAddIn(path); } catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* best effort */ }
            throw new SwException(ErrorCodes.AddinUnavailable, "The Simulation add-in loaded but offers no API object (GetAddInObject returned nothing).",
                "Ask the user to check SOLIDWORKS Simulation in Tools > Add-Ins.");
        }
        try
        {
            return new Addin(Cosmos(cb), r == (int)swLoadAddinError_e.swSuccess, path);
        }
        catch (SwException)
        {
            if (r == (int)swLoadAddinError_e.swSuccess)
                try { ctx.App.UnloadAddIn(path); } catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* best effort */ }
            throw;
        }
    }

    private static CosmosWorks Cosmos(CwAddincallback cb) =>
        cb.CosmosWorks ?? throw new SwException(ErrorCodes.AddinUnavailable, "The Simulation add-in returned no CosmosWorks object.");

    /// <summary>The active document's study manager (the add-in may need a moment after loading to attach to it).</summary>
    private static CWStudyManager StudyManagerOf(Addin addin, string title) =>
        WaitForDoc(addin.Cw)?.StudyManager
        ?? throw new SwException(ErrorCodes.AddinUnavailable, $"Simulation offers no study manager for '{title}'.",
            "Make sure the part is the active document (doc_activate) and retry.");

    private static CWModelDoc? WaitForDoc(CosmosWorks cw)
    {
        for (int i = 0; i < 20; i++)
        {
            var doc = cw.ActiveDoc;
            if (doc is not null) return doc;
            Thread.Sleep(250);
        }
        return null;
    }

    /// <summary>&lt;SOLIDWORKS folder&gt;\Simulation\cosworks.dll, from ISldWorks.GetExecutablePath or the registry.</summary>
    private static string? CosworksPath(ISldWorks app)
    {
        foreach (var dir in InstallFolders(app))
        {
            string p = Path.Combine(dir, "Simulation", "cosworks.dll");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IEnumerable<string> InstallFolders(ISldWorks app)
    {
        string? exe = null;
        try { exe = app.GetExecutablePath(); } catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* registry next */ }
        if (!string.IsNullOrWhiteSpace(exe))
            yield return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(exe) ?? exe : exe;
        string? folder = null;
        try
        {
            // Revision 33 = SOLIDWORKS 2025.
            if (int.TryParse((app.RevisionNumber() ?? "").Split('.')[0], out int major) && major > 0)
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey($@"SOFTWARE\SolidWorks\SOLIDWORKS {1992 + major}\Setup");
                folder = key?.GetValue("SolidWorks Folder") as string;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or COMException) { /* none */ }
        if (!string.IsNullOrWhiteSpace(folder)) yield return folder;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Adds a fixture or load on entities, trying the by-value array flavours (the last accepted one first); a refused
    /// try is removed again. Returns the item, the last error, and " (… array)" naming the flavour that worked.
    /// </summary>
    private static (T? Item, int Error, string How) AddOnEntities<T>(CWLoadsAndRestraintsManager lr, IReadOnlyList<object> entities,
        Flavor flavor, Func<object, (T? Item, int Error)> add) where T : class
    {
        int last = -1;
        string? thrown = null;
        foreach (var fl in SwExt.SimFlavors(flavor.Preferred))
        {
            int before = SafeCount(lr);
            T? item = null;
            int err;
            try
            {
                (item, err) = add(SwExt.SimEntityArray(entities, fl));
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex) && !ComArgs.IsRejected(ex))
            {
                // A VARIANT flavour the member can't take may raise instead of returning an error: try the next one.
                (item, err) = (null, -1);
                thrown = ex.Message;
            }
            if (item is not null && err == 0)
            {
                flavor.Preferred = fl;
                return (item, 0, $", {(fl == SwExt.SimArrayFlavor.Variant ? "variant" : "dispatch")} array");
            }
            last = err;
            RemoveNewItems(lr, before);
        }
        return (null, last, thrown is null ? "" : $"; {thrown}");
    }

    /// <summary>Deletes loads/fixtures added after index <paramref name="before"/> (a half-made item from a refused try).</summary>
    private static void RemoveNewItems(CWLoadsAndRestraintsManager lr, int before)
    {
        if (before < 0) return;
        for (int guard = 0; guard < 5; guard++)
        {
            try
            {
                int n = lr.Count;
                if (n <= before) return;
                var item = lr.GetLoadsAndRestraints(n - 1, out _);
                if (item?.Name is not { Length: > 0 } name) return;
                lr.DeleteLoadsAndRestraints(name);
            }
            catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
            {
                return; // the study is deleted at the end anyway
            }
        }
    }

    /// <summary>", n entities read back" when the newest item reports the expected entity count.</summary>
    private static string EntitiesNote(CWLoadsAndRestraintsManager lr, int expected)
    {
        try
        {
            int n = lr.Count;
            if (n <= 0) return "";
            var item = lr.GetLoadsAndRestraints(n - 1, out int e);
            if (item is null || e != 0) return "";
            int count = item.EntityCount;
            return count == expected ? $", {count} entit{(count == 1 ? "y" : "ies")} read back" : $" (SOLIDWORKS lists {count} entities)";
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return ""; }
    }

    /// <summary>The standard Front Plane and its axes (transform rows): a directional force is given in its direction 1, 2 and normal.</summary>
    private static (IFeature Plane, Vec3 X, Vec3 Y, Vec3 Normal) ForceReference(IModelDoc2 d)
    {
        var plane = EntityResolver.StandardPlane(d, "front")
                    ?? throw new SwException(ErrorCodes.FeatureFailed, "The part has no Front Plane to orient a directional force.");
        var t = plane.Specific() is IRefPlane rp ? rp.RefPlaneXform() : [];
        return t.Length >= 9
            ? (plane, new Vec3(t[0], t[1], t[2]), new Vec3(t[3], t[4], t[5]), new Vec3(t[6], t[7], t[8]))
            : (plane, Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ);
    }

    private static double[]? Component(CWResults res, swsDisplacementComponent_e component)
    {
        try
        {
            var r = SimulationMath.Range(res.DisplacementRange((int)component, Mm, out int e));
            return r is null || e != 0 ? null : [SimulationMath.Sig(r.Min), SimulationMath.Sig(r.Max)];
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    private static double[]? NodeAt(CWMesh mesh, int node, double[]? box)
    {
        if (node <= 0) return null;
        try
        {
            int e = mesh.GetNodeLocation(node, out double x, out double y, out double z);
            return e == 0 ? SimulationMath.LocationMm(x, y, z, box) : null;
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return null; }
    }

    /// <summary>The lowest yield strength (MPa) among the study's solid bodies, the first material name, and whether they differ.</summary>
    private static (double? YieldMpa, string? Material, bool Mixed) StudyYield(SwContext ctx, CWStudy s)
    {
        try
        {
            var solids = s.SolidManager;
            if (solids is null) return (null, null, false);
            var yields = new List<double>();
            string? name = null;
            bool mixed = false;
            for (int c = 0; c < solids.ComponentCount; c++)
            {
                var comp = solids.GetComponentAt(c, out _);
                if (comp is null) continue;
                for (int b = 0; b < comp.SolidBodyCount; b++)
                {
                    var mat = comp.GetSolidBodyAt(b, out _)?.GetSolidBodyMaterial();
                    if (mat is null) continue;
                    string mn = mat.MaterialName ?? "";
                    if (name is null) name = mn;
                    else if (!string.Equals(name, mn, StringComparison.Ordinal)) mixed = true;
                    double y = mat.GetPropertyByName2(SI, "SIGYLD", out _); // N/m²
                    if (double.IsFinite(y) && y > 0) yields.Add(y / SimulationMath.PaPerMpa);
                }
            }
            return (yields.Count > 0 ? yields.Min() : null, name, mixed);
        }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex))
        {
            ctx.Warn($"Reading the study material failed: {ex.Message}");
            return (null, null, false);
        }
    }

    private static (double Volume, double[]? Box) VolumeAndBox(List<IBody2> solids)
    {
        double vol = 0;
        double[]? box = null;
        foreach (var b in solids)
        {
            var mp = b.MassOfBody(); // [cx, cy, cz, volume, area, mass, …]
            if (mp.Length >= 4) vol += mp[3];
            var bb = b.Box();
            if (bb.Length == 6)
                box = box is null ? bb : [Math.Min(box[0], bb[0]), Math.Min(box[1], bb[1]), Math.Min(box[2], bb[2]),
                    Math.Max(box[3], bb[3]), Math.Max(box[4], bb[4]), Math.Max(box[5], bb[5])];
        }
        return (vol, box);
    }

    private static IEnumerable<(int Index, CWStudy Study)> Studies(CWStudyManager manager)
    {
        int n = manager.StudyCount;
        for (int i = 0; i < n; i++)
            if (manager.GetStudy(i) is CWStudy s) yield return (i, s);
    }

    private static List<string> StudyNames(CWStudyManager manager) =>
        Studies(manager).Select(x => x.Study.Name ?? "").Where(n => n.Length > 0).ToList();

    private static int SafeCount(CWStudyManager manager)
    {
        try { return manager.StudyCount; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return -1; }
    }

    private static int SafeCount(CWLoadsAndRestraintsManager lr)
    {
        try { return lr.Count; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return -1; }
    }

    private static int SafeActive(CWStudyManager manager)
    {
        try { return manager.ActiveStudy; }
        catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { return -1; }
    }

    /// <summary>Runs <paramref name="read"/> with <paramref name="d"/> active (Simulation only sees the active document), then re-activates the previous one.</summary>
    private static T OnActive<T>(SwContext ctx, IModelDoc2 d, Func<T> read)
    {
        var active = ctx.ActiveDocOrNull;
        if (active is not null && DocLocator.SameDoc(active, d)) return read();
        string title = ctx.Title(d);
        int err = 0;
        ctx.App.ActivateDoc3(title, false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
        var now = ctx.ActiveDocOrNull;
        if (now is null || !DocLocator.SameDoc(now, d))
            throw new SwException(ErrorCodes.VerifyFailed, $"Could not activate '{title}' to read its studies (error {err}).",
                "Activate it with doc_activate, then retry.");
        try
        {
            return read();
        }
        finally
        {
            if (active is not null)
            {
                int e2 = 0;
                try { ctx.App.ActivateDoc3(ctx.Title(active), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref e2); }
                catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* reported below */ }
                ctx.Warn($"Activated '{title}' to read its studies, then re-activated '{ctx.Title(active)}'.");
            }
        }
    }

    /// <summary>The name of an API status code (e.g. swsRunAnalysisErrorInvalidLBC), or "code n".</summary>
    private static string Name<TEnum>(int code) where TEnum : struct, Enum =>
        Enum.GetName(typeof(TEnum), code) ?? $"code {code}";

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));
}
