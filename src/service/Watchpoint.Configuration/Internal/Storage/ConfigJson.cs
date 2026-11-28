using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Storage;

/// <summary>The JSON of the <c>wp_config</c> columns: camelCase, enums as text.</summary>
internal static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = ConfigJsonContext.Default.Options;

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

    internal sealed record FixJson(string Code, IReadOnlyDictionary<string, object?> Args, JsonElement? Patch);

    internal sealed record FixJsonRead(string Code, Dictionary<string, JsonElement> Args, JsonElement? Patch);
}

/// <summary>Source-generated JSON of the <c>wp_config</c> columns and of <c>config.published</c>.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConfigPublishedBody))]
[JsonSerializable(typeof(ApprovalInfo))]
[JsonSerializable(typeof(List<DraftEntry>))]
[JsonSerializable(typeof(List<DiffEntry>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, object?>))]
[JsonSerializable(typeof(List<ConfigJson.FixJson>))]
[JsonSerializable(typeof(List<ConfigJson.FixJsonRead>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(List<object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
