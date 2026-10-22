using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorksMcp.Core.Com;
using SolidWorksMcp.Core.Documents;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Verify;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Services;

public sealed class VbaResult : ToolResult
{
    /// <summary>False when the macro never started: a syntax/compile error (SOLIDWORKS reports nothing for those).</summary>
    public bool Compiled { get; init; }
    public bool Completed { get; init; }
    public IReadOnlyList<string>? Output { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string>? FeaturesAdded { get; init; }
    public bool? DocDirty { get; init; }
}

/// <summary>
/// vba_run: SolidWorks BASIC (.swb) with error trapping and output capture. Off unless SWMCP_VBA=on. The prelude gives
/// <c>swApp</c>, <c>swDoc</c> (the target document), <c>Echo(text)</c>, <c>MM(mm)</c> → m and <c>DEG(deg)</c> → rad.
/// </summary>
public sealed class AutomationService(SwSession session)
{
    public Task<VbaResult> VbaRunAsync(string? doc, string code, int timeoutS, CancellationToken ct = default)
    {
        if (!session.Options.EnableVba)
            throw new SwException(ErrorCodes.Disabled, "vba_run is turned off (it runs arbitrary code with the user's rights).",
                "Prefer the dedicated tools. The user can enable it by setting SWMCP_VBA=on for this server.");
        if (string.IsNullOrWhiteSpace(code)) throw SwException.InvalidArgument("'code' is empty.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutS, 5, 600));
        return session.RunAsync("vba_run", ctx =>
        {
            string dir = Path.Combine(session.Options.DataDir, "tmp");
            Directory.CreateDirectory(dir);
            string id = Guid.NewGuid().ToString("N")[..12];
            string module = "swmcp_" + id;
            string macro = Path.Combine(dir, module + ".swb");
            string outFile = Path.Combine(dir, module + ".out");
            IModelDoc2? target = ctx.DocOrNull;
            Snapshot? before = target is not null && target.GetType() == 1 ? Snapshot.Take(target) : null;
            try
            {
                string docName = target is null ? "" : (DocumentRegistry.SafePath(target) is { Length: > 0 } p ? p : ctx.Title(target));
                File.WriteAllText(macro, VbaText.Wrap(code, outFile, docName), Encoding.ASCII);
                int err = 0;
                bool ok = ctx.App.RunMacro2(macro, module, "main", 0, out err);
                var lines = File.Exists(outFile) ? File.ReadAllLines(outFile, Encoding.Latin1) : [];
                bool compiled = lines.Length > 0 && lines[0] == "started";
                var output = lines.Skip(1).Where(l => l.StartsWith("O|")).Select(l => VbaText.Unescape(l[2..])).ToList();
                string? trapped = lines.LastOrDefault(l => l.StartsWith("ERR "));
                bool completed = lines.Contains("OK");
                var dlg = ctx.Op.FirstErrorDialog;
                string? error = trapped?[4..]
                                ?? (dlg is not null ? $"Runtime error (dialog answered with End): {dlg.Dialog.Text}" : null)
                                ?? (!compiled ? $"The macro did not run: probably a syntax error (RunMacro2 returned {ok}, error {err})." : null)
                                ?? (!completed ? "The macro stopped before the end (End statement or an unhandled error)." : null);
                List<string>? added = null;
                if (before is not null && target is not null)
                {
                    var after = Snapshot.Take(target);
                    added = after.Features.Where(kv => !before.Features.ContainsKey(kv.Key)).Select(kv => kv.Value.Name).ToList();
                }
                var result = new VbaResult
                {
                    Doc = target is null ? null : ctx.Title(target),
                    Compiled = compiled,
                    Completed = completed && error is null,
                    Output = output.Count == 0 ? null : output,
                    Error = error,
                    FeaturesAdded = added is { Count: > 0 } ? added : null,
                    DocDirty = target?.GetSaveFlag(),
                };
                if (error is not null)
                    throw new SwException(ErrorCodes.VbaError, error + (output.Count > 0 ? $" Output so far: {string.Join(" | ", output.Take(20))}" : ""),
                        compiled ? "Fix the code; remember the API inside VBA uses meters and radians (MM(), DEG())." :
                                   "Check the syntax: SolidWorks BASIC needs Dim x As <type>, no reserved names (Log, Imp, Eqv, Mod).");
                return result;
            }
            finally
            {
                try { File.Delete(macro); } catch { /* temp */ }
                try { File.Delete(outFile); } catch { /* temp */ }
            }
        }, RunOptions.Read(doc) with { Timeout = timeout, DialogRules = [DialogRules.VbaRuntimeError], OptionalDoc = true }, ct);
    }
}
