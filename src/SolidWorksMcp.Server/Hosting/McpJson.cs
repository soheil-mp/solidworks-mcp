using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;

namespace SolidWorksMcp.Server.Hosting;

/// <summary>Serializer options for tool arguments and input schemas (one instance, read-only). See docs/mcp-sdk-notes.md §1.</summary>
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        // The SDK defaults: Web (camelCase, case-insensitive properties), WhenWritingNull, AllowReadingFromString,
        // a JsonStringEnumConverter and the SDK's type-info resolver chain.
        var o = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        // Enums as snake_case strings in schemas and inputs; integers rejected. Insert(0) so it wins over the default.
        o.Converters.Insert(0, new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        // Unknown fields inside DTOs (EntityFilter, SketchEntity, …) are an error instead of being silently ignored.
        o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        o.RespectRequiredConstructorParameters = true;
        o.RespectNullableAnnotations = true;
        o.MakeReadOnly();
        return o;
    }
}
