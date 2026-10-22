using System.ComponentModel;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;
using SolidWorksMcp.Core.Model;
using SolidWorksMcp.Core.Services;
using SolidWorksMcp.Server.Hosting;

namespace SolidWorksMcp.Server.Tools;

[McpServerToolType, Toolset("session")]
public sealed class SessionTools(SwSession session)
{
    private readonly SessionService _svc = new(session);

    [McpServerTool(Name = "sw_status", Title = "SOLIDWORKS status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [McpMeta("anthropic/alwaysLoad", true)]
    [Description("Status of the SOLIDWORKS connection: version, open documents (handle, owned/adopted/foreign, dirty), the target document, " +
                 "open sketch, visible dialogs, whether a modal dialog blocks SOLIDWORKS, the running operation and recentOps (what happened " +
                 "to calls that timed out). Never hangs: works even while SOLIDWORKS is busy. Call it first, and after any TIMEOUT.")]
    public async Task<string> SwStatus(CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.StatusAsync(cancellationToken));

    [McpServerTool(Name = "sw_connect", Title = "Connect to SOLIDWORKS", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Attach to the running SOLIDWORKS (normally automatic), or start it with launch=true (takes a license seat and up to " +
                 "3 minutes). Never starts a second instance. Reports the revision and any preferences recovered from a crashed session.")]
    public Task<string> SwConnect(
        IProgress<ModelContextProtocol.ProgressNotificationValue> progress,
        [Description("Start SOLIDWORKS if it is not running. Only when the user asked for it.")] bool launch = false,
        CancellationToken cancellationToken = default) =>
        Output.Json(_svc.ConnectAsync(launch, cancellationToken), progress, "sw_connect");

    [McpServerTool(Name = "dialog_list", Title = "List SOLIDWORKS dialogs", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists visible SOLIDWORKS dialogs (handle, title, text, buttons) and whether one blocks SOLIDWORKS. Works while SOLIDWORKS " +
                 "is blocked. Use it after DIALOG_BLOCKING or TIMEOUT, and tell the user what the dialog says.")]
    public string DialogList() => ResultJson.Serialize(_svc.ListDialogs());

    [McpServerTool(Name = "dialog_respond", Title = "Answer a SOLIDWORKS dialog", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Presses a button on a SOLIDWORKS dialog by its handle (from dialog_list). Use it only for dialogs your own call caused, " +
                 "or when the user told you how to answer. Answers that can discard work (Don't Save, Discard, Delete, Replace, No on a save " +
                 "prompt) need confirm=true, which requires the user's approval. Works while SOLIDWORKS is blocked.")]
    public async Task<string> DialogRespond(
        [Description("Dialog handle from dialog_list, e.g. \"0x1A2B3C\".")] string handle,
        [Description("Button caption as shown (e.g. \"OK\", \"Cancel\", \"End\", \"Yes\"); \"ok\"/\"cancel\" also work on dialogs without that caption.")] string button,
        [Description("Required for answers that can discard work; only with the user's approval.")] bool confirm = false,
        CancellationToken cancellationToken = default) =>
        ResultJson.Serialize(await _svc.RespondAsync(handle, button, confirm, cancellationToken));
}
