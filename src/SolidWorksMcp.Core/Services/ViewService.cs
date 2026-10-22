using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Imaging;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed class ScreenshotResult : ToolResult
{
    public required string View { get; init; }
    public required string DisplayMode { get; init; }
    public int WidthPx { get; init; }
    public int HeightPx { get; init; }
    public string? SavedTo { get; init; }
    public string? BackupPath { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public byte[] Png { get; init; } = [];
}

/// <summary>
/// Screenshots that leave no trace: the view orientation, zoom, display mode, plane/sketch visibility, selection and
/// active document are restored after the capture.
/// </summary>
public sealed class ViewService(SwSession session)
{
    private static readonly Dictionary<string, int> Views = new(StringComparer.OrdinalIgnoreCase)
    {
        ["front"] = (int)swStandardViews_e.swFrontView,
        ["back"] = (int)swStandardViews_e.swBackView,
        ["left"] = (int)swStandardViews_e.swLeftView,
        ["right"] = (int)swStandardViews_e.swRightView,
        ["top"] = (int)swStandardViews_e.swTopView,
        ["bottom"] = (int)swStandardViews_e.swBottomView,
        ["iso"] = (int)swStandardViews_e.swIsometricView,
        ["isometric"] = (int)swStandardViews_e.swIsometricView,
        ["trimetric"] = (int)swStandardViews_e.swTrimetricView,
        ["dimetric"] = (int)swStandardViews_e.swDimetricView,
    };

    private static readonly Dictionary<string, int> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shaded_edges"] = (int)swViewDisplayMode_e.swViewDisplayMode_ShadedWithEdges,
        ["shaded"] = (int)swViewDisplayMode_e.swViewDisplayMode_Shaded,
        ["wireframe"] = (int)swViewDisplayMode_e.swViewDisplayMode_Wireframe,
        ["hidden_removed"] = (int)swViewDisplayMode_e.swViewDisplayMode_HiddenLinesRemoved,
        ["hidden_grayed"] = (int)swViewDisplayMode_e.swViewDisplayMode_HiddenLinesGrayed,
    };

    public Task<ScreenshotResult> ScreenshotAsync(string? doc, string view, bool zoomToFit, string displayMode, bool hidePlanes,
        bool hideSketches, int maxEdgePx, string? file, bool overwrite, CancellationToken ct = default) =>
        session.RunAsync("view_screenshot", ctx =>
        {
            string v = (view ?? "iso").Trim().ToLowerInvariant();
            if (v != "current" && !Views.ContainsKey(v))
                throw SwException.InvalidArgument($"Unknown view '{view}'.", "Use current, iso, front, back, left, right, top, bottom, trimetric or dimetric.");
            string mode = (displayMode ?? "shaded_edges").Trim().ToLowerInvariant();
            if (mode != "current" && !Modes.ContainsKey(mode))
                throw SwException.InvalidArgument($"Unknown displayMode '{displayMode}'.", "Use current, shaded_edges, shaded, wireframe, hidden_removed or hidden_grayed.");
            int edge = Math.Clamp(maxEdgePx, 64, 2048);
            string? saveTo = null, backup = null;
            if (!string.IsNullOrWhiteSpace(file))
            {
                string f = file!.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? file : file + ".png";
                saveTo = ctx.Paths.ForWrite(f, overwrite, out backup, "file");
            }

            var d = ctx.Doc;
            var previous = ctx.ActiveDocOrNull;
            bool switched = false;
            if (previous is null || !DocLocator.SameDoc(previous, d))
            {
                int err = 0;
                ctx.App.ActivateDoc3(ctx.Title(d), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
                switched = true;
            }
            var mv = d.View() ?? throw new SwException(ErrorCodes.NotSupported, $"'{ctx.Title(d)}' has no graphics view.");
            var sel = d.Sel();

            // Save everything we may change.
            var orientation = mv.Orientation3;
            double scale = mv.Scale2;
            var translation = mv.Translation3;
            int oldMode = mv.DisplayMode;
            bool planes = d.Extension.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes, 0);
            bool sketches = d.Extension.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches, 0);
            string tmp = Path.Combine(Path.GetTempPath(), $"swmcp_{Guid.NewGuid():N}.bmp");
            bool suspended = false;
            try
            {
                try { sel.SuspendSelectionList(); suspended = true; } catch { /* nothing selected */ }
                if (hidePlanes && planes) d.Extension.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes, 0, false);
                if (hideSketches && sketches) d.Extension.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches, 0, false);
                if (mode != "current") mv.DisplayMode = Modes[mode];
                if (v != "current") d.ShowNamedView2("", Views[v]);
                if (zoomToFit) d.ViewZoomtofit2();
                d.GraphicsRedraw2();
                if (!d.SaveBMP(tmp, 0, 0) || !File.Exists(tmp))
                    throw new SwException(ErrorCodes.IoError, "SOLIDWORKS could not render the view (SaveBMP failed).",
                        "The window may be minimized or the session locked; ask the user to restore the SOLIDWORKS window.");
                var png = Png.FromBmp(File.ReadAllBytes(tmp), edge, out int w, out int h);
                if (saveTo is not null)
                {
                    File.WriteAllBytes(saveTo, png);
                    ctx.Paths.RecordWritten(saveTo);
                }
                return new ScreenshotResult
                {
                    Doc = ctx.Title(d), View = v, DisplayMode = mode, WidthPx = w, HeightPx = h, Png = png,
                    SavedTo = saveTo, BackupPath = backup,
                };
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* temp */ }
                try
                {
                    if (hidePlanes && planes) d.Extension.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayPlanes, 0, true);
                    if (hideSketches && sketches) d.Extension.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplaySketches, 0, true);
                    if (mode != "current") mv.DisplayMode = oldMode;
                    if (v != "current" || zoomToFit)
                    {
                        if (orientation is not null) mv.Orientation3 = orientation;
                        if (translation is not null) mv.Translation3 = translation;
                        mv.Scale2 = scale;
                    }
                    if (suspended) sel.ResumeSelectionList();
                    d.GraphicsRedraw2();
                    if (switched && previous is not null)
                    {
                        int err = 0;
                        ctx.App.ActivateDoc3(ctx.Title(previous), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref err);
                    }
                }
                catch (Exception ex) { ctx.Warn($"Could not fully restore the view: {ex.Message}"); }
            }
        }, RunOptions.Read(doc), ct);
}
