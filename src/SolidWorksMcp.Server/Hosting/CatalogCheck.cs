using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>Tool-catalog invariants the SDK doesn't enforce (DESIGN §4 conventions, §6.2). Used by --check-catalog and the tests.</summary>
public static class CatalogCheck
{
    private const int ClientPrefix = 17; // "mcp__solidworks__"; Claude Code drops tools whose full name exceeds 128
    private static readonly string[] UnitSuffixes = ["Mm", "Deg", "Kg", "Mpa", "N", "Px", "S", "Mm2", "Mm3"];

    public static List<string> Problems(out int toolCount, out int listBytes)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        ServerSetup.Configure(builder, fileLog: false);
        using var host = builder.Build(); // building does not start the stdio transport

        var tools = host.Services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
        toolCount = tools.Count;
        listBytes = JsonSerializer.SerializeToUtf8Bytes(tools, McpJson.Options).Length;
        var problems = new List<string>();
        foreach (var g in tools.GroupBy(t => t.Name).Where(g => g.Count() > 1))
            problems.Add($"duplicate tool name '{g.Key}' x{g.Count()}");
        if (Instructions.Text.Length > 4096) problems.Add($"instructions are {Instructions.Text.Length} chars (> 4096)");

        foreach (var t in tools)
        {
            if (!Regex.IsMatch(t.Name, "^[a-z][a-z0-9_]*$") || t.Name.Length > 128 - ClientPrefix)
                problems.Add($"{t.Name}: name must be snake_case and at most {128 - ClientPrefix} chars");
            if (string.IsNullOrWhiteSpace(t.Description))
                problems.Add($"{t.Name}: missing [Description]");
            else if (t.Description.Length > 4096)
                problems.Add($"{t.Name}: description over 4,096 chars");
            if (string.IsNullOrWhiteSpace(t.Title)) problems.Add($"{t.Name}: missing Title");

            var a = t.Annotations;
            if (a?.ReadOnlyHint is null || a.OpenWorldHint is null || a.DestructiveHint is null || a.IdempotentHint is null)
                problems.Add($"{t.Name}: set ReadOnly, Destructive, Idempotent and OpenWorld explicitly");
            else
            {
                if (a.ReadOnlyHint == true && a.DestructiveHint == true) problems.Add($"{t.Name}: read-only tools can't be destructive");
                bool vba = t.Name is "vba_run" or "macro_run";
                if (a.OpenWorldHint == true && !vba) problems.Add($"{t.Name}: OpenWorld must be false");
            }

            int bytes = JsonSerializer.SerializeToUtf8Bytes(t, McpJson.Options).Length;
            if (bytes > 6144) problems.Add($"{t.Name}: schema+description is {bytes} bytes (budget 6 KB)");

            foreach (var p in t.InputSchema.GetProperty("properties").EnumerateObject())
            {
                if (!Regex.IsMatch(p.Name, "^[a-z][A-Za-z0-9]{0,63}$"))
                    problems.Add($"{t.Name}.{p.Name}: parameter names are camelCase ASCII, at most 64 chars");
                if (!p.Value.TryGetProperty("description", out var d) || string.IsNullOrWhiteSpace(d.GetString()))
                    problems.Add($"{t.Name}.{p.Name}: missing [Description]");
            }
        }
        if (listBytes > 120 * 1024) problems.Add($"tools/list is {listBytes / 1024} KB (budget 120 KB)");
        return problems;
    }

    public static int Run()
    {
        var problems = Problems(out int count, out int bytes);
        foreach (var p in problems) Console.Error.WriteLine("CATALOG: " + p);
        Console.Error.WriteLine($"CATALOG: {count} tools, tools/list {bytes / 1024.0:0.0} KB");
        Console.Error.WriteLine(problems.Count == 0 ? "CATALOG: ok" : $"CATALOG: {problems.Count} problem(s)");
        return problems.Count == 0 ? 0 : 1;
    }
}
