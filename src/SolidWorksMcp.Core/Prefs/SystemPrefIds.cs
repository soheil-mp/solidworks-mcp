namespace SolidWorksMcp.Core.Prefs;

/// <summary>
/// The only SOLIDWORKS <em>system</em> preferences the server may change (always temporarily, inside a
/// <see cref="PreferenceScope"/>). <c>swUserPreferenceToggle_e</c> mixes system and document ids, so writing an
/// unlisted id at system level is refused. Add an id here, with the reason, when a tool genuinely needs it.
/// </summary>
public static class SystemPrefIds
{
    // Toggles (swUserPreferenceToggle_e)
    /// <summary>10: "Input dimension value" — opens the modal Modify box on every new dimension.</summary>
    public const int InputDimValOnCreate = 10;
    /// <summary>69: STL output binary (true) / ASCII (false).</summary>
    public const int StlBinary = 69;
    /// <summary>70: show the STL info box on save (modal) — kept off during exports.</summary>
    public const int StlShowInfoOnSave = 70;
    /// <summary>72: write all components of an assembly into one STL file.</summary>
    public const int StlComponentsIntoOneFile = 72;

    // Integers (swUserPreferenceIntegerValue_e)
    /// <summary>75: STEP application protocol (203, 214, 242).</summary>
    public const int StepAp = 75;
    /// <summary>78: STL quality (1 coarse, 2 fine, 3 custom).</summary>
    public const int StlQuality = 78;

    // Doubles (swUserPreferenceDoubleValue_e)
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
        (PrefKind.Integer, StepAp),
        (PrefKind.Integer, StlQuality),
        (PrefKind.Double, StlDeviation),
        (PrefKind.Double, StlAngleTolerance),
    ];

    public static bool IsAllowed(PrefKind kind, int pref) => Allowed.Contains((kind, pref));

    /// <summary>Every allow-listed id, for the preference audit in sw_status and tests.</summary>
    public static IReadOnlyCollection<(PrefKind Kind, int Pref)> All => Allowed;
}
