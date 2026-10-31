using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Core.Services;

public sealed record AsmStructureRequest
{
    public string? Doc { get; init; }
    /// <summary>form | dissolve.</summary>
    public string Kind { get; init; } = "";
    /// <summary>form: top-level components to move into a new subassembly; dissolve: the subassembly.</summary>
    public IReadOnlyList<string>? Components { get; init; }
    /// <summary>form: save the new (virtual) subassembly to this file.</summary>
    public string? SaveTo { get; init; }
}

public sealed class AsmStructureResult : ToolResult
{
    public required string Kind { get; init; }
    public required string Subassembly { get; init; }
    /// <summary>The components moved (form: now inside the subassembly; dissolve: now at the top level).</summary>
    public required IReadOnlyList<string> Moved { get; init; }
    public required IReadOnlyList<string> TopLevel { get; init; }
    /// <summary>Part file → quantity in the whole tree, the same before and after.</summary>
    public required IReadOnlyDictionary<string, int> PartQuantities { get; init; }
    public string? SavedTo { get; init; }
    public required IReadOnlyList<string> Checks { get; init; }
}

public sealed partial class AssemblyService
{
    /// <summary>
    /// asm_structure. form: IAssemblyDoc.InsertNewVirtualAssembly, then ReorganizeComponents moves the components into it
    /// (optionally saved to a file with SaveVirtualComponent). dissolve: the subassembly selected, DissolveSubAssembly.
    /// Verified: the top level and the subassembly hold the expected components, and every part's quantity in the whole
    /// tree is unchanged.
    /// </summary>
    public Task<AsmStructureResult> StructureAsync(AsmStructureRequest q, CancellationToken ct = default) =>
        session.RunAsync("asm_structure", ctx =>
        {
            var d = ctx.DocOfType(swDocumentTypes_e.swDocASSEMBLY);
            var asm = (IAssemblyDoc)d;
            string kind = (q.Kind ?? "").Trim().ToLowerInvariant();
            if (kind is not ("form" or "dissolve")) throw SwException.InvalidArgument($"kind must be form or dissolve (got '{q.Kind}').");
            if (q.Components is not { Count: > 0 }) throw SwException.InvalidArgument("'components' must name at least one component.");
            var comps = q.Components.Select(n => (object?)asm.GetComponentByName(n.Trim()) as IComponent2
                                                ?? throw new SwException(ErrorCodes.EntityNotFound, $"No top-level component named '{n}'.",
                                                    "asm_component_list shows the names, e.g. bracket-1.")).ToList();
            if (comps.Any(c => (object?)c.GetParent() is IComponent2))
                throw SwException.InvalidArgument("Give top-level components (not ones inside a subassembly).");
            var before = Quantities(d);
            var checks = new List<string>();
            string sub;
            List<string> moved;
            string? saved = null;
            if (kind == "form")
            {
                int st = asm.InsertNewVirtualAssembly(out Component2 made);
                var newSub = (object?)made as IComponent2
                             ?? throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS made no subassembly (InsertNewVirtualAssembly status {st}).");
                sub = newSub.Name2;
                moved = comps.Select(c => c.Name2).ToList();
                // The source array's flavour matters to some members (typed arrays worked where object[] didn't): try each.
                var tried = new List<string>();
                bool moved0 = false;
                foreach (var (how, arr) in new (string, object)[]
                         {
                             ("Component2[]", comps.Cast<Component2>().ToArray()),
                             ("dispatch array", ComArgs.DispatchArray(comps)),
                             ("object[]", comps.Cast<object>().ToArray()),
                         })
                {
                    moved0 = asm.ReorganizeComponents(arr, newSub);
                    tried.Add($"{how}: {moved0}");
                    if (moved0) break;
                }
                if (!moved0) ctx.Warn($"ReorganizeComponents: {string.Join("; ", tried)}");
                if (!moved0)
                {
                    TryDelete(d, newSub);
                    throw new SwException(ErrorCodes.FeatureFailed, "SOLIDWORKS did not move the components into the new subassembly.",
                        "Components that share mates with ones staying behind may need those mates first.");
                }
                newSub = (object?)asm.GetComponentByName(sub) as IComponent2 ?? newSub;
                var inside = ComArgs.Objects<IComponent2>((object?)newSub.GetChildren()).Select(c => Leaf(c.Name2)).ToList();
                var missing = moved.Where(m => !inside.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0) throw new SwException(ErrorCodes.VerifyFailed, $"Not in the subassembly afterwards: {string.Join(", ", missing)}.");
                checks.Add($"{moved.Count} component(s) moved into {sub} ({tried[^1].Split(':')[0]})");
                if (!string.IsNullOrWhiteSpace(q.SaveTo))
                {
                    string path = ctx.Paths.ForWrite(q.SaveTo!.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase) ? q.SaveTo : q.SaveTo + ".sldasm",
                        false, out _, "saveTo");
                    if (!newSub.SaveVirtualComponent(path) || !File.Exists(path))
                        throw new SwException(ErrorCodes.IoError, $"The subassembly was not saved to '{path}'.");
                    ctx.Paths.RecordWritten(path);
                    saved = path;
                    sub = ((object?)asm.GetComponentByName(Path.GetFileNameWithoutExtension(path) + "-1") as IComponent2)?.Name2 ?? sub;
                    checks.Add($"saved as {Path.GetFileName(path)}");
                }
            }
            else
            {
                if (comps.Count != 1) throw SwException.InvalidArgument("dissolve takes one subassembly.");
                var s = comps[0];
                if ((object?)s.GetModelDoc2() is not IModelDoc2 sm || sm.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
                    throw SwException.InvalidArgument($"'{s.Name2}' is not a subassembly.");
                sub = s.Name2;
                moved = ComArgs.Objects<IComponent2>((object?)s.GetChildren()).Select(c => Leaf(c.Name2)).ToList();
                d.ClearSelection2(true);
                s.Select4(false, null, false);
                if (!asm.DissolveSubAssembly())
                    throw new SwException(ErrorCodes.FeatureFailed, $"SOLIDWORKS did not dissolve '{sub}'.");
                d.ClearSelection2(true);
                if ((object?)asm.GetComponentByName(sub) is IComponent2)
                    throw new SwException(ErrorCodes.VerifyFailed, $"'{sub}' is still in the assembly.");
                checks.Add($"{sub} dissolved; its {moved.Count} component(s) are at the top level");
            }
            d.EditRebuild3();
            var after = Quantities(d);
            var diff = before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k)).ToList();
            if (diff.Count > 0)
                throw new SwException(ErrorCodes.VerifyFailed,
                    $"Part quantities changed: {string.Join(", ", diff.Select(k => $"{k} {before.GetValueOrDefault(k)} → {after.GetValueOrDefault(k)}"))}.");
            checks.Add($"part quantities unchanged ({after.Values.Sum()} part(s))");
            var top = ComArgs.Objects<IComponent2>((object?)((IConfiguration)d.ConfigurationManager.ActiveConfiguration).GetRootComponent3(true).GetChildren())
                .Select(c => c.Name2).ToList();
            return new AsmStructureResult
            {
                Doc = ctx.Title(d), Kind = kind, Subassembly = sub, Moved = moved, TopLevel = top, PartQuantities = after, SavedTo = saved, Checks = checks,
            };
        }, RunOptions.Write(q.Doc) with { Timeout = TimeSpan.FromSeconds(300) }, ct);

    /// <summary>"Assem2^asm-1/bracket-1" → "bracket-1".</summary>
    private static string Leaf(string name) => name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;

    /// <summary>Every resolved part in the tree by file name, with its quantity.</summary>
    private static Dictionary<string, int> Quantities(IModelDoc2 d)
    {
        var q = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var root = (IComponent2)((IConfiguration)d.ConfigurationManager.ActiveConfiguration).GetRootComponent3(true);
        void Walk(IComponent2 c)
        {
            foreach (var ch in ComArgs.Objects<IComponent2>((object?)c.GetChildren()))
            {
                if (ch.IsSuppressed()) continue;
                string path = ch.GetPathName() ?? "";
                if (path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase))
                {
                    string k = Path.GetFileName(path);
                    q[k] = q.GetValueOrDefault(k) + 1;
                }
                else Walk(ch);
            }
        }
        Walk(root);
        return q;
    }

    private static void TryDelete(IModelDoc2 d, IComponent2 c)
    {
        try
        {
            d.ClearSelection2(true);
            c.Select4(false, null, false);
            d.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
        }
        catch (Exception ex) when (!ComArgs.IsDisconnect(ex)) { /* best effort */ }
    }
}
