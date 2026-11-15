using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>Thread-safe record kind registry (spec PIP §5.3).</summary>
internal sealed class LogRecordTypeRegistry : ILogRecordTypeRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _kinds = new(StringComparer.Ordinal);

    public void Register<TBody>(string kind, int schemaVersion, JsonTypeInfo<TBody> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!LogEnvelope.IsValidKind(kind))
        {
            throw new ArgumentException($"'{kind}' is not a valid record kind.", nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        var entry = new Entry(typeof(TBody), schemaVersion, typeInfo);
        var registered = _kinds.GetOrAdd(kind, entry);
        if (registered.BodyType != entry.BodyType || registered.SchemaVersion != entry.SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Record kind '{kind}' is already registered with body {registered.BodyType.Name} v{registered.SchemaVersion}.");
        }
    }

    public bool TryGetSchemaVersion(string kind, out int schemaVersion)
    {
        if (kind is not null && _kinds.TryGetValue(kind, out var entry))
        {
            schemaVersion = entry.SchemaVersion;
            return true;
        }

        schemaVersion = 0;
        return false;
    }

    public bool CanRead(LogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _kinds.TryGetValue(record.Envelope.Kind, out var entry) && record.SchemaVersion <= entry.SchemaVersion;
    }

    public JsonElement SerializeBody<TBody>(string kind, TBody body) => JsonSerializer.SerializeToElement(body, TypeInfo<TBody>(kind));

    public TBody DeserializeBody<TBody>(string kind, JsonElement body) =>
        body.Deserialize(TypeInfo<TBody>(kind)) ?? throw new JsonException($"The body of a '{kind}' record is null.");

    private JsonTypeInfo<TBody> TypeInfo<TBody>(string kind)
    {
        if (kind is null || !_kinds.TryGetValue(kind, out var entry))
        {
            throw new InvalidOperationException($"Record kind '{kind}' is not registered.");
        }

        return entry.TypeInfo as JsonTypeInfo<TBody>
            ?? throw new InvalidOperationException($"Record kind '{kind}' is registered with body {entry.BodyType.Name}, not {typeof(TBody).Name}.");
    }

    private sealed record Entry(Type BodyType, int SchemaVersion, JsonTypeInfo TypeInfo);
}
