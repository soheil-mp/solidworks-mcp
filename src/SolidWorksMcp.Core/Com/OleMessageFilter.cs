using System.Runtime.InteropServices;
using SolidWorksMcp.Core.Win32;

namespace SolidWorksMcp.Core.Com;

/// <summary>
/// Retries COM calls that SOLIDWORKS rejects with SERVERCALL_RETRYLATER while it is busy (rebuilding, loading, showing
/// a progress bar). Without a filter such calls fail at once with RPC_E_CALL_REJECTED. Registered on the STA thread
/// (CoRegisterMessageFilter fails on MTA threads). Retrying stops when the running operation's deadline passes or its
/// caller gave up, so a rejected call never outlives its request.
/// </summary>
[ComVisible(true)]
internal sealed class OleMessageFilter : IOleMessageFilter
{
    private const int SERVERCALL_ISHANDLED = 0;
    private const int SERVERCALL_RETRYLATER = 2;
    private const int PENDINGMSG_WAITDEFPROCESS = 2;

    /// <summary>Upper bound for retrying one rejected call (milliseconds).</summary>
    public static int RetryWindowMs { get; set; } = 30_000;

    /// <summary>Set by the dispatcher per operation: returns true when retrying should stop.</summary>
    [ThreadStatic] public static Func<bool>? ShouldStop;

    public static void Register()
    {
        int hr = NativeMethods.CoRegisterMessageFilter(new OleMessageFilter(), out _);
        if (hr != 0) throw new COMException("CoRegisterMessageFilter failed", hr);
    }

    public static void Revoke() => NativeMethods.CoRegisterMessageFilter(null, out _);

    // Incoming calls (e.g. RCW releases marshalled back into this STA) must be accepted.
    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) =>
        SERVERCALL_ISHANDLED;

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        // >= 0 and < 100: retry immediately; >= 100: wait that many ms and retry; -1: cancel the call.
        if (dwRejectType != SERVERCALL_RETRYLATER || dwTickCount >= RetryWindowMs) return -1;
        if (ShouldStop?.Invoke() == true) return -1;
        return 150;
    }

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) =>
        PENDINGMSG_WAITDEFPROCESS;
}
