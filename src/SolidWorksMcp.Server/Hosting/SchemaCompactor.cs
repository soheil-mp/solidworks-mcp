using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using SolidWorksMcp.Core.Model;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>
/// Shrinks the generated input schemas once at startup (DESIGN §6.2 budget). The EntityRef object schema is inlined
/// wherever a tool takes geometry (30+ times, about 40% of tools/list), so its field descriptions are dropped there:
/// the server instructions (rule 4) and sw_help explain the fields once. "default": null is dropped too (an optional
/// parameter is already "not required"). Field names, types and additionalProperties stay, so nothing a client
/// validates against changes.
/// </summary>
public static class SchemaCompactor
{
    /// <summary>EntityRef's JSON field names (camelCase, as McpJson writes them).</summary>
    public static readonly IReadOnlySet<string> EntityRefFields = typeof(EntityRef)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
        .ToHashSet(StringComparer.Ordinal);

    public static void Apply(Tool tool)
    {
        var root = JsonNode.Parse(tool.InputSchema.GetRawText());
        Walk(root);
        using var doc = JsonDocument.Parse(root!.ToJsonString());
        tool.InputSchema = doc.RootElement.Clone();
    }

    /// <summary>True when <paramref name="schema"/> is an object schema with exactly EntityRef's fields.</summary>
    public static bool IsEntityRef(JsonObject schema) =>
        schema["properties"] is JsonObject props && props.Count == EntityRefFields.Count && props.All(p => EntityRefFields.Contains(p.Key));

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o.ContainsKey("default") && o["default"] is null) o.Remove("default");
                if (IsEntityRef(o))
                    foreach (var p in o["properties"]!.AsObject())
                        (p.Value as JsonObject)?.Remove("description");
                foreach (var p in o.ToList()) Walk(p.Value);
                break;
            case JsonArray a:
                foreach (var v in a) Walk(v);
                break;
        }
    }
}
