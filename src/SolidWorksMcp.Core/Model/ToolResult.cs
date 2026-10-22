using System.Text.Json;
using System.Text.Json.Serialization;

namespace SolidWorksMcp.Core.Model;

/// <summary>
/// Base of every service result. <see cref="SwSession.RunAsync{T}"/> fills <see cref="Warnings"/> and
/// <see cref="DialogsHandled"/> from the operation context, so services only set their own data and <see cref="Doc"/>.
/// </summary>
public abstract class ToolResult
{
    /// <summary>The document the tool acted on (title).</summary>
    [JsonPropertyOrder(-100)]
    public string? Doc { get; set; }

    /// <summary>Non-fatal issues (rebuild warnings, restore problems, ...).</summary>
    [JsonPropertyOrder(1000)]
    public IReadOnlyList<string>? Warnings { get; set; }

    /// <summary>Dialogs the server answered automatically during the call.</summary>
    [JsonPropertyOrder(1001)]
    public IReadOnlyList<string>? DialogsHandled { get; set; }
}

/// <summary>JSON conventions for tool results: camelCase, no nulls, compact, enums as camelCase strings.</summary>
public static class ResultJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, value?.GetType() ?? typeof(T), Options);
}
