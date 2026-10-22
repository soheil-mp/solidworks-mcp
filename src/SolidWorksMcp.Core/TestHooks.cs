using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Prefs;

namespace SolidWorksMcp.Core;

/// <summary>
/// Raw-API helpers for the live tests (which can't use COM types: they are embedded in this assembly only).
/// Internal; visible to SolidWorksMcp.LiveTests.
/// </summary>
internal static class TestHooks
{
    /// <summary>The allow-listed system preferences, read directly.</summary>
    public static Task<Dictionary<string, string>> PreferencesAsync(SwSession s) =>
        s.RunAsync("test_prefs", ctx =>
            SystemPrefIds.All.ToDictionary(p => $"{p.Kind}{p.Pref}", p => PreferenceScope.Read(ctx.App, p.Kind, p.Pref)));

    /// <summary>Creates a part through the raw API, bypassing the registry, so the session treats it as the user's.</summary>
    public static Task<string> NewForeignPartAsync(SwSession s) =>
        s.RunAsync("test_foreign_part", ctx =>
        {
            string tpl = ctx.App.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            var d = (object?)ctx.App.NewDocument(tpl, 0, 0, 0) as SolidWorks.Interop.sldworks.IModelDoc2
                    ?? throw new SwException(ErrorCodes.FeatureFailed, "NewDocument failed.");
            return ctx.Title(d);
        });

    /// <summary>Closes a document by title through the raw API (test cleanup only).</summary>
    public static Task CloseRawAsync(SwSession s, string title) =>
        s.RunAsync("test_close_raw", ctx =>
        {
            ctx.App.CloseDoc(title);
            return 0;
        });

    /// <summary>The titles of all open documents.</summary>
    public static Task<List<string>> OpenTitlesAsync(SwSession s) =>
        s.RunAsync("test_titles", ctx => ctx.App.Documents().Select(ctx.Title).ToList());
}
