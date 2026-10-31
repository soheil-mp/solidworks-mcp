using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Geometry;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;

namespace SolidWorksMcp.Core.Services;

/// <summary>One finding of model_review.</summary>
public sealed class ReviewIssue
{
    /// <summary>error | warning | info.</summary>
    public required string Severity { get; init; }
    /// <summary>rebuild_error | rebuild_warning | sketch_over_defined | sketch_under_defined | suppressed | no_material | geometry |
    /// needs_rebuild | component_missing | component_lightweight | component_suppressed | component_floating | mate_error | bodies.</summary>
    public required string Kind { get; init; }
    public required string Where { get; init; }
    public required string Message { get; init; }
}

public sealed class ReviewResult : ToolResult
{
    /// <summary>part | assembly.</summary>
    public required string Type { get; init; }
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public required IReadOnlyList<ReviewIssue> Issues { get; init; }
    /// <summary>What was looked at (features, sketches, bodies, components, mates), with counts.</summary>
    public required IReadOnlyList<string> Checked { get; init; }
    public bool? Truncated { get; init; }
}

/// <summary>
/// model_review: a health report in one call, read-only. Parts: features with rebuild errors or warnings
/// (IFeature.GetErrorCode2), sketches that are over- or under-defined (ISketch.GetConstrainedStatus), suppressed features,
/// a missing material, faulty geometry (IBody2.Check3) and a pending rebuild (IModelDocExtension.NeedsRebuild2).
/// Assemblies: those of the top document, plus missing, unloaded and suppressed components, floating (under-defined)
/// components and mates with errors.
/// </summary>
public sealed class ReviewService(SwSession session)
{
    private const int MaxIssues = 200;

    public Task<ReviewResult> ReviewAsync(string? doc, CancellationToken ct = default) =>
        session.RunAsync("model_review", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocPART, swDocumentTypes_e.swDocASSEMBLY);
            bool assembly = d.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
            var issues = new List<ReviewIssue>();
            var checkedList = new List<string>();
            void Add(string sev, string kind, string where, string msg) => issues.Add(new ReviewIssue { Severity = sev, Kind = kind, Where = where, Message = msg });

            // Features: rebuild errors and warnings; suppressed; sketches and their definition.
            // WalkAll meets an absorbed sketch twice (in the tree and under its feature): each name is looked at once.
            int features = 0, sketches = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Tree.WalkAll(d))
            {
                string type = Safe(() => f.GetTypeName2(), "") ?? "";
                if (Tree.SystemTypes.Contains(type) && type != "MateGroup") continue;
                string name = Safe(() => f.Name, "?") ?? "?";
                if (!seen.Add(type + "|" + name)) continue;
                features++;
                int code = 0;
                bool warn = false;
                try { code = f.GetErrorCode2(out warn); }
                catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* unreadable: skipped */ }
                bool mate = type.StartsWith("Mate", StringComparison.Ordinal) && type != "MateGroup";
                if (code != 0)
                {
                    string error = FeatureVerifier.ErrorName(code);
                    if (mate) Add(warn ? "warning" : "error", "mate_error", name, $"mate {(warn ? "warning" : "error")} {error}");
                    else Add(warn ? "warning" : "error", warn ? "rebuild_warning" : "rebuild_error", name, $"{type}: {error}");
                }
                if (Safe(() => f.IsSuppressed(), false) && !mate) Add("info", "suppressed", name, $"{type} is suppressed");
                if (type is "ProfileFeature" or "3DProfileFeature" && (object?)Safe<object?>(() => f.GetSpecificFeature2(), null) is ISketch sk)
                {
                    sketches++;
                    int status = Safe(() => sk.GetConstrainedStatus(), 0);
                    if (status == (int)swConstrainedStatus_e.swOverConstrained)
                        Add("error", "sketch_over_defined", name, "the sketch is over defined (conflicting relations or dimensions)");
                    else if (status is (int)swConstrainedStatus_e.swNoSolution or (int)swConstrainedStatus_e.swInvalidSolution)
                        Add("error", "sketch_over_defined", name, "the sketch has no valid solution");
                    else if (status == (int)swConstrainedStatus_e.swUnderConstrained)
                        Add("info", "sketch_under_defined", name, "the sketch is under defined (it can move or change size)");
                }
            }
            checkedList.Add($"{features} feature(s), {sketches} sketch(es)");

            int needs = Safe(() => d.Extension.NeedsRebuild2, 0);
            if (needs != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
                Add("warning", "needs_rebuild", ctx.Title(d), "the model needs a rebuild (doc_rebuild)");

            if (!assembly)
            {
                var part = (IPartDoc)d;
                if ((Safe(() => part.GetMaterialPropertyName2("", out _), "") ?? "").Length == 0)
                    Add("warning", "no_material", ctx.Title(d), "no material: mass, cost and simulation need one (material_set)");
                var solids = part.Bodies(swBodyType_e.swSolidBody, visibleOnly: false);
                var surfaces = part.Bodies(swBodyType_e.swSheetBody, visibleOnly: false);
                if (solids.Count > 1) Add("info", "bodies", ctx.Title(d), $"{solids.Count} solid bodies (a multibody part)");
                if (surfaces.Count > 0) Add("info", "bodies", ctx.Title(d), $"{surfaces.Count} surface bod(ies)");
                int faults = 0;
                foreach (var b in solids)
                    try { faults += b.FaultList().Count; }
                    catch (COMException e) when (!ComArgs.IsDisconnect(e)) { /* not checkable */ }
                if (faults > 0) Add("error", "geometry", ctx.Title(d), $"{faults} geometry fault(s) (model_check lists them)");
                checkedList.Add($"{solids.Count} solid and {surfaces.Count} surface bod(ies), geometry checked");
            }
            else
            {
                var comps = ((IAssemblyDoc)d).Components(false);
                int floating = 0;
                foreach (var c in comps)
                {
                    string cn = Safe(() => c.Name2, "?") ?? "?";
                    int supp = Safe(() => c.GetSuppression2(), -1);
                    string path = Safe(() => c.GetPathName(), "") ?? "";
                    if (supp == (int)swComponentSuppressionState_e.swComponentSuppressed) { Add("info", "component_suppressed", cn, "suppressed"); continue; }
                    if (path.Length > 0 && !File.Exists(path)) Add("error", "component_missing", cn, $"its file '{path}' is missing");
                    else if ((object?)c.GetModelDoc2() is null)
                        Add(supp == (int)swComponentSuppressionState_e.swComponentLightweight ? "info" : "warning",
                            supp == (int)swComponentSuppressionState_e.swComponentLightweight ? "component_lightweight" : "component_missing", cn,
                            supp == (int)swComponentSuppressionState_e.swComponentLightweight ? "lightweight (resolve it to check it)" : "not loaded");
                    if (Safe(() => c.IsFixed(), false)) continue;
                    if (Safe(() => c.GetConstrainedStatus(), 0) == (int)swConstrainedStatus_e.swUnderConstrained) floating++;
                }
                if (floating > 0) Add("info", "component_floating", ctx.Title(d), $"{floating} component(s) not fully positioned by mates (they can move)");
                checkedList.Add($"{comps.Count} component(s) and the mates");
            }
            var ordered = issues.OrderBy(i => i.Severity switch { "error" => 0, "warning" => 1, _ => 2 }).ToList();
            return new ReviewResult
            {
                Doc = ctx.Title(d),
                Type = assembly ? "assembly" : "part",
                Errors = issues.Count(i => i.Severity == "error"),
                Warnings = issues.Count(i => i.Severity == "warning"),
                Issues = ordered.Take(MaxIssues).ToList(),
                Checked = checkedList,
                Truncated = ordered.Count > MaxIssues ? true : null,
            };
        }, RunOptions.Read(doc), ct);

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (COMException e) when (!ComArgs.IsDisconnect(e)) { return fallback; }
    }
}
