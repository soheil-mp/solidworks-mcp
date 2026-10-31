using System.Runtime.InteropServices;
using SolidWorks.Interop.fworks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Prefs;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

public sealed class RecognizeResult : ToolResult
{
    /// <summary>What FeatureWorks reported recognizing.</summary>
    public int Recognized { get; init; }
    /// <summary>The features now in the tree (name, type), the imported body's replacement.</summary>
    public required IReadOnlyList<FeatureRef> Features { get; init; }
    public required double VolumeMm3 { get; init; }
    public required double VolumeBeforeMm3 { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

/// <summary>
/// feature_recognize: FeatureWorks (GetAddInObject("FeatureWorks.FeatureWorksApp") → IFeatureWorksApp) turns an imported
/// body into features: RecognizeFeatureAutomatic with the chosen kinds, then CreateFeatures. Verified: new features in the
/// tree, the volume unchanged, no rebuild errors; else the document is put back.
/// </summary>
public sealed class RecognitionService(SwSession session)
{
    private static readonly Dictionary<string, int> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["extrude"] = (int)fwAutomaticRecognitionOptions_e.fwExtrudeOption,
        ["volume"] = (int)fwAutomaticRecognitionOptions_e.fwVolume,
        ["revolve"] = (int)fwAutomaticRecognitionOptions_e.fwRevolve,
        ["holes"] = (int)fwAutomaticRecognitionOptions_e.fwHoles,
        ["fillets"] = (int)fwAutomaticRecognitionOptions_e.fwChamfils,
        ["ribs"] = (int)fwAutomaticRecognitionOptions_e.fwRibs,
        ["sheetmetal"] = (int)(fwAutomaticRecognitionOptions_e.fwBaseFlange | fwAutomaticRecognitionOptions_e.fwSketchedBend
                               | fwAutomaticRecognitionOptions_e.fwAutoEdgeFlange | fwAutomaticRecognitionOptions_e.fwAutoHemFlange),
    };

    public Task<RecognizeResult> RecognizeAsync(string? doc, IReadOnlyList<string>? kinds, bool constrainSketches, CancellationToken ct = default) =>
        session.RunAsync("feature_recognize", ctx =>
        {
            int options = 0;
            foreach (var k in kinds is { Count: > 0 } ? kinds : ["extrude", "revolve", "holes", "fillets", "volume"])
                options |= Kinds.TryGetValue(k.Trim(), out int v) ? v
                    : throw SwException.InvalidArgument($"Unknown kind '{k}'.", $"Kinds: {string.Join(", ", Kinds.Keys)}.");
            var part = ctx.Part();
            var d = (IModelDoc2)part;
            if (d.SketchManager.ActiveSketch is not null) throw new SwException(ErrorCodes.SketchState, "A sketch is open; finish it first.");
            var imported = Tree.WalkAll(d).Where(f => f.GetTypeName2() == "BaseBody").ToList();
            if (imported.Count == 0)
                throw SwException.InvalidArgument("The part has no imported body to recognize (FeatureWorks works on imported STEP/IGES/Parasolid bodies).",
                    "Open the file with doc_open first.");
            var fw = Addin(ctx);
            using var prefs = ctx.SystemPrefs();
            prefs.Toggle(SystemPrefIds.ShowErrorsEveryRebuild, false);
            var before = Snapshot.Take(d);
            var names = Tree.WalkAll(d).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            // FeatureWorks works on the active document.
            int err = 0;
            ctx.App.ActivateDoc3(ctx.Title(d), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
            d.ClearSelection2(true);
            int n = fw.RecognizeFeatureAutomatic((short)options);
            if (n <= 0)
            {
                // Then with the imported body selected.
                imported[0].Select2(false, 0);
                n = fw.RecognizeFeatureAutomatic((short)options);
                if (n <= 0) ctx.Warn("FeatureWorks recognized nothing, also with the imported body selected.");
            }
            bool made = n > 0 && fw.CreateFeatures((short)(constrainSketches ? (int)fwFeatureCreationOptions_e.fwAddConstraintsToSketch : 0));
            d.EditRebuild3();
            var after = Snapshot.Take(d);
            var news = Tree.WalkAll(d).Where(f => !names.Contains(f.Name) && !Tree.SystemTypes.Contains(f.GetTypeName2())
                                                   && f.GetTypeName2() is not ("ProfileFeature" or "3DProfileFeature")).ToList();
            var errors = d.Extension.WhatsWrong().Where(x => !x.Warning).Select(x => x.Feature.Name).ToList();
            string? problem = n <= 0 ? "FeatureWorks recognized no features"
                : !made ? "FeatureWorks recognized features but created none"
                : news.Count == 0 ? "no new features appeared"
                : Math.Abs(after.Volume - before.Volume) > 1e-6 * Math.Max(before.Volume, 1e-12)
                    ? $"the volume changed from {Units.Mm3(before.Volume)} to {Units.Mm3(after.Volume)} mm³"
                : errors.Count > 0 ? $"rebuild errors in {string.Join(", ", errors.Take(5))}" : null;
            if (problem is not null)
            {
                string undone = FeatureVerifier.Rollback(ctx, d, before);
                throw new SwException(n <= 0 ? ErrorCodes.FeatureFailed : ErrorCodes.VerifyFailed, $"{problem}. {undone}",
                    "Try fewer kinds (e.g. ['extrude']) or a simpler body; FeatureWorks recognizes prismatic and turned shapes best.");
            }
            return new RecognizeResult
            {
                Doc = ctx.Title(d),
                Recognized = n,
                Features = news.Select(f => new FeatureRef { Name = f.Name, Type = f.GetTypeName2() }).ToList(),
                VolumeMm3 = Units.Mm3(after.Volume),
                VolumeBeforeMm3 = Units.Mm3(before.Volume),
                Checks =
                [
                    $"{news.Count} feature(s) made from the imported body",
                    $"volume unchanged ({Units.Mm3(after.Volume)} mm³), no rebuild errors",
                ],
            };
        }, RunOptions.Write(doc) with { Timeout = TimeSpan.FromSeconds(300) }, ct);

    private static IFeatureWorksApp Addin(SwContext ctx)
    {
        if ((object?)ctx.App.GetAddInObject("FeatureWorks.FeatureWorksApp") is IFeatureWorksApp loaded) return loaded;
        string? exe = null;
        try { exe = ctx.App.GetExecutablePath(); } catch (COMException ex) when (!ComArgs.IsDisconnect(ex)) { /* not available */ }
        string dir = string.IsNullOrWhiteSpace(exe) ? "" : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(exe) ?? exe : exe;
        string path = Path.Combine(dir, "fworks", "fworks.dll");
        if (!File.Exists(path)) throw new SwException(ErrorCodes.AddinUnavailable, "FeatureWorks is not installed (no fworks\\fworks.dll).");
        int r = ctx.App.LoadAddIn(path);
        if (r != (int)swLoadAddinError_e.swSuccess && r != (int)swLoadAddinError_e.swAddinAlreadyLoaded)
            throw new SwException(ErrorCodes.AddinUnavailable, $"Loading FeatureWorks failed ({r}).", "Ask the user to enable FeatureWorks under Tools > Add-Ins.");
        return (object?)ctx.App.GetAddInObject("FeatureWorks.FeatureWorksApp") as IFeatureWorksApp
               ?? throw new SwException(ErrorCodes.AddinUnavailable, "FeatureWorks loaded but offers no API object.");
    }
}
