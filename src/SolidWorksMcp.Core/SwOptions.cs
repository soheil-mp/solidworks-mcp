using System.Globalization;

namespace SolidWorksMcp.Core;

/// <summary>Server configuration (from environment variables; see README "Configuration").</summary>
public sealed record SwOptions
{
    /// <summary>
    /// Default per-call time limit, counted from when the call starts running (SWMCP_TIMEOUT_S, default 50 s — below
    /// the 60 s request timeout many MCP clients use). Long tools (simulation, big exports) set their own.
    /// </summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(50);

    /// <summary>Longest wait for a turn on the SOLIDWORKS thread before BUSY (SWMCP_QUEUE_WAIT_S, default 20 s).</summary>
    public TimeSpan MaxQueueWait { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Launch SOLIDWORKS on the first call if it isn't running (SWMCP_AUTO_LAUNCH=1). Off by default.</summary>
    public bool AutoLaunch { get; init; }

    /// <summary>Enable vba_run / macro_run (SWMCP_VBA=on). Off by default: VBA is unrestricted code execution.</summary>
    public bool EnableVba { get; init; }

    /// <summary>Folders macro_run may load macros from (SWMCP_MACRO_DIRS, ';'-separated). Empty = any local folder.</summary>
    public IReadOnlyList<string> MacroDirs { get; init; } = [];

    /// <summary>Folder for journals, registry, backups and scratch files (SWMCP_DATA_DIR, default %LOCALAPPDATA%\solidworks-mcp).</summary>
    public string DataDir { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "solidworks-mcp");

    public static SwOptions FromEnvironment()
    {
        var o = new SwOptions();
        if (Seconds("SWMCP_TIMEOUT_S") is { } t) o = o with { DefaultTimeout = t };
        if (Seconds("SWMCP_QUEUE_WAIT_S") is { } q) o = o with { MaxQueueWait = q };
        if (IsOn("SWMCP_AUTO_LAUNCH")) o = o with { AutoLaunch = true };
        if (IsOn("SWMCP_VBA")) o = o with { EnableVba = true };
        if (Environment.GetEnvironmentVariable("SWMCP_MACRO_DIRS") is { Length: > 0 } dirs)
            o = o with { MacroDirs = dirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        if (Environment.GetEnvironmentVariable("SWMCP_DATA_DIR") is { Length: > 0 } dir) o = o with { DataDir = dir };
        return o;
    }

    private static TimeSpan? Seconds(string name) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0
            ? TimeSpan.FromSeconds(s) : null;

    private static bool IsOn(string name) => Environment.GetEnvironmentVariable(name) is { } v &&
        (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));
}
