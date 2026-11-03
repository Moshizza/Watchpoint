using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Watchpoint.Kernel.Ids;

/// <summary>Shared implementation of the string-based ids.</summary>
internal static class StringIdHelper
{
    public static bool IsValid([NotNullWhen(true)] string? text, int maxLength, Regex pattern) =>
        text is not null && text.Length <= maxLength && pattern.IsMatch(text);
}

/// <summary>Serialises a string-based id as a JSON string; invalid values throw <see cref="JsonException"/>.</summary>
/// <typeparam name="TId">The id type.</typeparam>
internal sealed class StringIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IStringId<TId>
{
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? Parse(reader.GetString())
            : throw new JsonException($"common.value.invalid_format: expected a {typeof(TId).Name} string.");

    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);

    private static TId Parse(string? text) =>
        TId.Parse(text).TryGetValue(out var id, out _)
            ? id
            : throw new JsonException($"common.value.invalid_format: '{text}' is not a valid {typeof(TId).Name}.");
}
