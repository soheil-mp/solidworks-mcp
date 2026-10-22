using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SolidWorksMcp.Core;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>
/// One CallTool filter around every tool call (DESIGN §3.5). It validates argument names, treats null as "use the
/// default", and turns every exception — from the tool, argument binding or result serialization — into
/// "CODE: message Hint: hint" with isError. Without it the SDK reports "An error occurred invoking 'x'." for everything
/// except McpException and silently ignores misspelled arguments.
/// </summary>
public static class ToolErrorFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create() => next => async (ctx, ct) =>
    {
        var tool = ctx.MatchedPrimitive as McpServerTool;
        var args = ctx.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        if (tool is not null)
        {
            DropNullsForDefaultedParameters(tool, args);
            if (CheckArgumentNames(tool, args) is { } problem)
                return Error(ErrorCodes.InvalidArgument, problem);
        }

        var log = ctx.Services?.GetService<ILoggerFactory>()?.CreateLogger("SolidWorksMcp.Tools");
        var started = DateTime.UtcNow;
        try
        {
            var result = await next(ctx, ct).ConfigureAwait(false);
            log?.LogInformation("{Tool} ok in {Ms} ms", ctx.Params?.Name, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            return result;
        }
        catch (McpProtocolException)
        {
            throw; // e.g. "Unknown tool": stays a JSON-RPC error
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the client sent notifications/cancelled; the SDK sends no response
        }
        catch (SwException ex)
        {
            log?.LogInformation("{Tool} failed in {Ms} ms: {Error}", ctx.Params?.Name, (int)(DateTime.UtcNow - started).TotalMilliseconds, ex.ToAgentText());
            return Error(ex.Code, ex.Message, ex.Hint);
        }
        catch (McpException ex)
        {
            return Text(ex.Message);
        }
        catch (JsonException ex) when (tool is not null)
        {
            return Error(ErrorCodes.InvalidArgument, DescribeBindingError(tool, args) ?? ex.Message);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "Tool {Tool} failed", ctx.Params?.Name);
            return Error(ErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    };

    private static CallToolResult Error(string code, string message, string? hint = null) =>
        Text(Clip($"{code}: {message}" + (hint is null ? "" : $" Hint: {hint}"), 4000));

    private static CallToolResult Text(string text) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    /// <summary>Models often send null for an optional argument: treat it as "use the declared default".</summary>
    private static void DropNullsForDefaultedParameters(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        if (args.IsReadOnly || tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } method) return;
        foreach (var p in method.GetParameters())
            if (p.HasDefaultValue && p.Name is { } n && args.TryGetValue(n, out var v) && v.ValueKind == JsonValueKind.Null)
                args.Remove(n);
    }

    /// <summary>Unknown (typo'd) names are silently ignored by the SDK; null reaches non-nullable parameters.</summary>
    private static string? CheckArgumentNames(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        var schema = tool.ProtocolTool.InputSchema;
        var props = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        var known = props.ValueKind == JsonValueKind.Object ? props.EnumerateObject().Select(x => x.Name).ToList() : [];

        foreach (var (key, value) in args)
        {
            if (!known.Contains(key, StringComparer.Ordinal))
            {
                var near = known.FirstOrDefault(k =>
                    string.Equals(k.Replace("_", ""), key.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                return $"unknown argument '{key}' for tool '{tool.ProtocolTool.Name}'." +
                       (near is null ? "" : $" Did you mean '{near}'?") + $" Valid arguments: {string.Join(", ", known)}.";
            }
            if (value.ValueKind == JsonValueKind.Null && props.GetProperty(key).TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String)
                return $"argument '{key}' must not be null (expected {type.GetString()}).";
        }

        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            var missing = req.EnumerateArray().Select(r => r.GetString()!).Where(r => !args.ContainsKey(r)).ToList();
            if (missing.Count > 0) return $"missing required argument(s): {string.Join(", ", missing)}.";
        }
        return null;
    }

    /// <summary>System.Text.Json does not say which parameter failed; re-bind each one to find out.</summary>
    private static string? DescribeBindingError(McpServerTool tool, IDictionary<string, JsonElement> args)
    {
        if (tool.Metadata.OfType<MethodInfo>().FirstOrDefault() is not { } method) return null;
        var props = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var p in method.GetParameters())
        {
            if (p.Name is null || !args.TryGetValue(p.Name, out var value) || !props.TryGetProperty(p.Name, out var schema))
                continue;
            try
            {
                JsonSerializer.Deserialize(value, p.ParameterType, McpJson.Options);
            }
            catch (JsonException ex)
            {
                var (got, expected, path) = Locate(value, schema, ex.Path);
                return $"argument '{p.Name}{path}' is invalid: got {Clip(got.GetRawText(), 120)}; expected {Describe(expected)}.";
            }
        }
        return null;
    }

    private static (JsonElement Value, JsonElement Schema, string Path) Locate(JsonElement value, JsonElement schema, string? path)
    {
        var shown = "";
        foreach (Match m in Regex.Matches(path ?? "", @"\[(\d+)\]|\.([^.\[]+)"))
        {
            if (m.Groups[1].Success && value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items)
                && int.Parse(m.Groups[1].Value) is var i && i < value.GetArrayLength())
                (value, schema) = (value[i], items);
            else if (m.Groups[2].Success && value.ValueKind == JsonValueKind.Object
                     && value.TryGetProperty(m.Groups[2].Value, out var v) && schema.TryGetProperty("properties", out var ps)
                     && ps.TryGetProperty(m.Groups[2].Value, out var s))
                (value, schema) = (v, s);
            else
                break;
            shown += m.Value;
        }
        return (value, schema, shown);
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Describe(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return schema.GetRawText();
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node.Remove("description");
        node.Remove("default");
        return Clip(node.ToJsonString(Readable), 300);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
