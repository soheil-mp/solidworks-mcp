namespace SolidWorksMcp.Core.Prefs;

/// <summary>
/// The only SOLIDWORKS <em>system</em> preferences the server may change (always temporarily, inside a
/// <see cref="PreferenceScope"/>). <c>swUserPreferenceToggle_e</c> mixes system and document ids, so writing an
/// unlisted id at system level is refused. Add an id here, with the reason, when a tool genuinely needs it.
/// </summary>
public static class SystemPrefIds
{
    // ---- Toggles (swUserPreferenceToggle_e)
    /// <summary>10: "Input dimension value" — opens the modal Modify box on every new dimension.</summary>
    public const int InputDimValOnCreate = 10;
    /// <summary>69: STL output binary (true) / ASCII (false).</summary>
    public const int StlBinary = 69;
    /// <summary>70: show the STL info box on save (modal) — kept off during exports.</summary>
    public const int StlShowInfoOnSave = 70;
    /// <summary>72: write all components of an assembly into one STL file.</summary>
    public const int StlComponentsIntoOneFile = 72;
    /// <summary>77: show the What's Wrong dialog at every rebuild (modal) — off during rebuilding tools.</summary>
    public const int ShowErrorsEveryRebuild = 77;
    /// <summary>291: run Import Diagnostics automatically on import (prompts).</summary>
    public const int ImportAutoRunDiagnostics = 291;
    /// <summary>353: show the sheet-format dialog when adding a drawing sheet (modal).</summary>
    public const int DrawingShowSheetFormatDialog = 353;
    /// <summary>690: run diagnostics on neutral-format imports (prompts).</summary>
    public const int ImportNeutralRunDiagnostics = 690;
    /// <summary>691: 3D Interconnect (changes what LoadFile4 produces for third-party formats).</summary>
    public const int Enable3DInterconnect = 691;

    // ---- Integers (swUserPreferenceIntegerValue_e)
    /// <summary>75: STEP application protocol (203, 214, 242).</summary>
    public const int StepAp = 75;
    /// <summary>78: STL quality (1 coarse, 2 fine, 3 custom).</summary>
    public const int StlQuality = 78;
    /// <summary>208: what an STL/VRML import creates (0 graphics, 1 surface, 2 solid bodies); solid during doc_open.</summary>
    public const int ImportStlModelType = 208;
    /// <summary>210: the length unit an STL/VRML import assumes (swLengthUnit_e); mm during doc_open.</summary>
    public const int ImportStlUnits = 210;
    /// <summary>211: STL export units.</summary>
    public const int StlUnits = 211;
    /// <summary>230: rebuild on activation (0 = ask the user: a prompt) — "don't rebuild" during activations/opens.</summary>
    public const int RebuildOnActivation = 230;

    // ---- Doubles (swUserPreferenceDoubleValue_e)
    /// <summary>2: STL deviation (custom quality).</summary>
    public const int StlDeviation = 2;
    /// <summary>3: STL angle tolerance (custom quality).</summary>
    public const int StlAngleTolerance = 3;

    private static readonly HashSet<(PrefKind, int)> Allowed =
    [
        (PrefKind.Toggle, InputDimValOnCreate),
        (PrefKind.Toggle, StlBinary),
        (PrefKind.Toggle, StlShowInfoOnSave),
        (PrefKind.Toggle, StlComponentsIntoOneFile),
        (PrefKind.Toggle, ShowErrorsEveryRebuild),
        (PrefKind.Toggle, ImportAutoRunDiagnostics),
        (PrefKind.Toggle, DrawingShowSheetFormatDialog),
        (PrefKind.Toggle, ImportNeutralRunDiagnostics),
        (PrefKind.Toggle, Enable3DInterconnect),
        (PrefKind.Integer, StepAp),
        (PrefKind.Integer, StlQuality),
        (PrefKind.Integer, ImportStlModelType),
        (PrefKind.Integer, ImportStlUnits),
        (PrefKind.Integer, StlUnits),
        (PrefKind.Integer, RebuildOnActivation),
        (PrefKind.Double, StlDeviation),
        (PrefKind.Double, StlAngleTolerance),
    ];

    public static bool IsAllowed(PrefKind kind, int pref) => Allowed.Contains((kind, pref));

    /// <summary>
    /// Preferences SOLIDWORKS keeps consistent with each other, listed in restore order. The STL quality is derived
    /// from the deviation and the angle: setting coarse or fine overwrites both with that preset, non-preset values
    /// switch the quality to custom by themselves, and custom doesn't stick while both hold a preset (verified live).
    /// A <see cref="PreferenceScope"/> snapshots, journals and restores a group as one unit.
    /// </summary>
    public static readonly IReadOnlyList<IReadOnlyList<(PrefKind Kind, int Pref)>> Coupled =
    [
        [(PrefKind.Double, StlDeviation), (PrefKind.Double, StlAngleTolerance), (PrefKind.Integer, StlQuality)],
    ];

    /// <summary>The coupled group a preference belongs to (in restore order), or null.</summary>
    public static IReadOnlyList<(PrefKind Kind, int Pref)>? GroupOf(PrefKind kind, int pref) =>
        Coupled.FirstOrDefault(g => g.Contains((kind, pref)));

    /// <summary>Every allow-listed id, for the preference audit in sw_status and tests.</summary>
    public static IReadOnlyCollection<(PrefKind Kind, int Pref)> All => Allowed;
}
