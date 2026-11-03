using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchpoint.Kernel.Ids;

/// <summary>
/// Serialises a UUID-based id as a plain lowercase UUID string. Reading accepts upper and lower case
/// and rejects any other format with a <see cref="JsonException"/> (<c>common.value.invalid_format</c>).
/// </summary>
/// <typeparam name="TId">The id type.</typeparam>
public sealed class EntityIdJsonConverter<TId> : JsonConverter<TId>
    where TId : struct, IEntityId<TId>
{
    /// <inheritdoc />
    public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? Parse(reader.GetString())
            : throw new JsonException($"common.value.invalid_format: expected a {TId.Prefix} id (UUID string).");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(EntityIdHelper.Format(value.Value));
    }

    /// <inheritdoc />
    public override TId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Parse(reader.GetString());

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(EntityIdHelper.Format(value.Value));
    }

    private static TId Parse(string? text) =>
        TId.TryParse(text, out var id)
            ? id
            : throw new JsonException($"common.value.invalid_format: expected a {TId.Prefix} id (UUID string).");
}
