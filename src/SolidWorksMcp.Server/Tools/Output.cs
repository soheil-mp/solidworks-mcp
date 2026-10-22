using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Server.Tools;

/// <summary>Result serialization and progress heartbeats shared by all tool classes.</summary>
internal static class Output
{
    /// <summary>Awaits a service call, reporting progress every 5 s while it runs, and returns its compact JSON.</summary>
    public static async Task<string> Json<T>(Task<T> task, IProgress<ProgressNotificationValue>? progress, string operation)
        where T : ToolResult
    {
        await Heartbeat(task, progress, operation).ConfigureAwait(false);
        return ResultJson.Serialize(await task.ConfigureAwait(false));
    }

    public static async Task Heartbeat(Task task, IProgress<ProgressNotificationValue>? progress, string operation)
    {
        if (progress is null) return;
        var started = DateTime.UtcNow;
        while (!task.IsCompleted)
        {
            var done = await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false);
            if (done == task) break;
            double s = (DateTime.UtcNow - started).TotalSeconds;
            progress.Report(new ProgressNotificationValue { Progress = (float)s, Message = $"{operation}: running for {s:0} s" });
        }
    }
}
