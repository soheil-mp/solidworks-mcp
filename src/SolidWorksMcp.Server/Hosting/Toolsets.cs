using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>The toolset a tool class belongs to (SWMCP_TOOLSETS selects which are exposed).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolsetAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Exposes only the selected toolsets (SWMCP_TOOLSETS=session,documents,...; default all) and, with SWMCP_READ_ONLY=1,
/// only read-only tools. Some clients cap the number of tools (VS Code: 128), so users can trim the list.
/// </summary>
public static class Toolsets
{
    public static readonly string[] All =
        ["session", "documents", "inspect", "sketch", "features", "reference", "bodies", "sheetmetal", "weldments", "assembly", "drawing", "export", "properties", "simulation", "automation"];

    /// <summary>tool name → (toolset, read-only).</summary>
    public static IReadOnlyDictionary<string, (string Toolset, bool ReadOnly)> Catalog { get; } = Build();

    private static Dictionary<string, (string, bool)> Build()
    {
        var map = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
        foreach (var t in typeof(Toolsets).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            string set = t.GetCustomAttribute<ToolsetAttribute>()?.Name ?? "misc";
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                if (m.GetCustomAttribute<McpServerToolAttribute>() is { } a && a.Name is { } name)
                    map[name] = (set, a.ReadOnly);
        }
        return map;
    }

    public static HashSet<string>? Selected()
    {
        var env = Environment.GetEnvironmentVariable("SWMCP_TOOLSETS");
        if (string.IsNullOrWhiteSpace(env) || env.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var sets = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        sets.Add("session"); // always needed for status and dialog recovery
        return sets;
    }

    public static bool ReadOnlyMode =>
        Environment.GetEnvironmentVariable("SWMCP_READ_ONLY") is { } v && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    public static bool IsEnabled(string tool, HashSet<string>? sets, bool readOnly)
    {
        if (!Catalog.TryGetValue(tool, out var info)) return true;
        if (sets is not null && !sets.Contains(info.Toolset)) return false;
        if (readOnly && !info.ReadOnly) return false;
        return true;
    }

    public static McpRequestFilter<ListToolsRequestParams, ListToolsResult> ListFilter(HashSet<string>? sets, bool readOnly) => next => async (ctx, ct) =>
    {
        var result = await next(ctx, ct).ConfigureAwait(false);
        if (sets is null && !readOnly) return result;
        result.Tools = result.Tools.Where(t => IsEnabled(t.Name, sets, readOnly)).ToList();
        return result;
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallFilter(HashSet<string>? sets, bool readOnly) => next => async (ctx, ct) =>
    {
        string name = ctx.Params?.Name ?? "";
        if (!IsEnabled(name, sets, readOnly))
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"{ErrorCodes.Disabled}: '{name}' is not enabled on this server (SWMCP_TOOLSETS / SWMCP_READ_ONLY)." }],
            };
        return await next(ctx, ct).ConfigureAwait(false);
    };
}
