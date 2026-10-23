namespace SolidWorksMcp.Core.Win32;

/// <summary>The dialog auto-answers the server knows (design §3.5). Each is only active for operations that list it.</summary>
public static class DialogRules
{
    /// <summary>VBA run-time error box (Continue/End/Debug/Help): capture the text and press End.</summary>
    public static readonly DialogRule VbaRuntimeError =
        new("^Microsoft Visual Basic", null, "End", "VBA run-time error", IsError: true);

    /// <summary>The Modify dimension-value box (owner-drawn): accept it. Normally prevented by switching option 10 off.</summary>
    public static readonly DialogRule ModifyDimension = new("^Modify$", null, "ok", "dimension Modify box");

    /// <summary>"Do you want to save Simulation data?" when closing a document with studies: do not save.</summary>
    public static readonly DialogRule SimulationSaveNo = new("Simulation", "save", "No", "Simulation save prompt (not saving)");

    /// <summary>"Do you want to save Simulation data?" when closing a document with studies: save.</summary>
    public static readonly DialogRule SimulationSaveYes = new("Simulation", "save", "Yes", "Simulation save prompt (saving)");

    /// <summary>
    /// "Make Dimension Driven?" (a new sketch dimension would over-define the sketch; shown while system option 100
    /// "prompt to set driven state" is on): cancel, so the dimension is not added. sketch_dimension_add then reports
    /// the refusal. Title unverified live.
    /// </summary>
    public static readonly DialogRule OverdefiningDimension =
        new("^Make Dimension Driven", null, "cancel", "over-defining dimension prompt (dimension not added)");

    /// <summary>
    /// FeatureWorks' "proceed with feature recognition?" after an import (no preference prevents it): decline, keeping the
    /// imported body as it is. doc_open imports only. The text is not verified live yet.
    /// </summary>
    public static readonly DialogRule ImportFeatureRecognitionNo =
        new("SOLIDWORKS|FeatureWorks", "feature recognition", "No", "FeatureWorks feature-recognition prompt after an import (declined)");

    /// <summary>
    /// "Run Import Diagnostics?" after an import, normally prevented by options 291/690 being off during doc_open: decline.
    /// The text is not verified live yet.
    /// </summary>
    public static readonly DialogRule ImportDiagnosticsNo =
        new("SOLIDWORKS", @"Import\s*Diagnos", "No", "Import Diagnostics prompt after an import (declined)");

    // ---- Simulation (sim_static, sim_delete). Meshing and solving are single blocking COM calls that show progress
    // windows. A rule with an empty Button only TOLERATES its window: the watchdog never presses anything, and the
    // dispatcher doesn't end the call early with DIALOG_BLOCKING because of it. A window whose title or text reads like
    // a question, a warning or an error is never tolerated, so real prompts still surface. Titles and texts of the
    // progress windows are not verified live yet.

    /// <summary>Matches a title or text that contains no question mark and no warning or failure words.</summary>
    private const string NotAPrompt =
        @"^(?!.*(\?|fail|error|warning|unstable|excessive|insufficient|invalid|cannot|could\s*not|unable|license)).*";

    /// <summary>The meshing progress window ("Mesh Progress", "Meshing…").</summary>
    public static readonly DialogRule SimulationMeshProgress =
        new(NotAPrompt + @"\bmesh", NotAPrompt, "", "Simulation mesh progress (tolerated)");

    /// <summary>
    /// The solver status window (elapsed time, memory usage, solver or progress readouts), whatever its title. Progress
    /// text is required, so a plain information box (which needs an answer) is never tolerated into a long stall.
    /// </summary>
    public static readonly DialogRule SimulationSolverProgress =
        new(NotAPrompt, @"^(?=.*(elapsed\s*time|memory\s*usage|solver\s*:|solving|in\s+progress|iteration|\d+\s*%))(?!.*(\?|fail|error|warning|unstable|excessive|insufficient|invalid|cannot|could\s*not|unable|license)).*",
            "", "Simulation solver progress (tolerated)");

    /// <summary>A progress window titled after the running study, e.g. "MCP Static 1a2b (-Default-)".</summary>
    public static DialogRule SimulationStudyProgress(string study) =>
        new(NotAPrompt + System.Text.RegularExpressions.Regex.Escape(study), NotAPrompt, "", $"Simulation progress of '{study}' (tolerated)");

    /// <summary>
    /// "Do you want to save Simulation data?" (title "Simulation", VERIFIED on close), should unloading the add-in ask
    /// it about a study the call just deleted: do not save. Stricter than <see cref="SimulationSaveNo"/>, so a solver
    /// question such as "save the results of this solution?" is never answered.
    /// </summary>
    public static readonly DialogRule SimulationDataSaveNo =
        new("Simulation", @"save\s+(the\s+)?Simulation\s+data", "No", "Simulation data save prompt (not saving)");

    /// <summary>sim_static: the progress windows of a static study run, and the save prompt at add-in unload.</summary>
    public static IReadOnlyList<DialogRule> SimulationRun(string study) =>
        [SimulationStudyProgress(study), SimulationMeshProgress, SimulationSolverProgress, SimulationDataSaveNo];

    /// <summary>sim_delete: the save prompt at add-in unload.</summary>
    public static IReadOnlyList<DialogRule> SimulationCleanup => [SimulationDataSaveNo];
}
