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

    /// <summary>
    /// "The section line does not completely cut through … make this a partial section?" when a section line ends inside
    /// the part (drw_view_section validates the line first, so this is a safety net): decline, so no partial section is
    /// made. Title and text are not verified live yet.
    /// </summary>
    public static readonly DialogRule PartialSectionNo =
        new("SOLIDWORKS|Section", "partial section", "No", "partial section-view prompt (declined)");
}
