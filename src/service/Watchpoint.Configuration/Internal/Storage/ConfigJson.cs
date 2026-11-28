using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Storage;

/// <summary>The JSON of the <c>wp_config</c> columns: camelCase, enums as text.</summary>
internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    public static string? Document(JsonElement? element) => element?.GetRawText();

    public static JsonElement? Document(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Problem arguments as written: text, numbers (int when it fits, else long or double), booleans, lists and maps.</summary>
    public static IReadOnlyDictionary<string, object?> Args(string json) =>
        Read<Dictionary<string, JsonElement>>(json).ToDictionary(a => a.Key, a => Value(a.Value), StringComparer.Ordinal);

    public static string WriteProblemFixes(IReadOnlyList<FixSuggestion> fixes) =>
        Write(fixes.Select(f => new FixJson(f.Code, f.Args, f.Patch)).ToList());

    public static IReadOnlyList<FixSuggestion> ReadProblemFixes(string json) =>
        [.. Read<List<FixJsonRead>>(json).Select(f => new FixSuggestion(f.Code, f.Args.ToDictionary(a => a.Key, a => Value(a.Value), StringComparer.Ordinal), f.Patch))];

    private static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt32(out var i) => i,
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(Value).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value), StringComparer.Ordinal),
        _ => null,
    };

    private sealed record FixJson(string Code, IReadOnlyDictionary<string, object?> Args, JsonElement? Patch);

    private sealed record FixJsonRead(string Code, Dictionary<string, JsonElement> Args, JsonElement? Patch);
}
