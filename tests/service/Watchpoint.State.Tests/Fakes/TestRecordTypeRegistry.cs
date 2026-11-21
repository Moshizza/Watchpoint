using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.State.Tests.Fakes;

/// <summary>
/// A plain-reflection <see cref="ILogRecordTypeRegistry"/> for tests: builds and reads the bodies of synthetic
/// <see cref="LogRecord"/>s without depending on <c>Watchpoint.Pipeline</c>'s internal registry. The real registry's
/// camelCase, source-generated JSON is a production (no-reflection-at-runtime) concern, irrelevant to whether the
/// state module applies a body correctly.
/// </summary>
internal sealed class TestRecordTypeRegistry : ILogRecordTypeRegistry
{
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Dictionary<string, (Type Body, int SchemaVersion)> _kinds = [];

    public void Register<TBody>(string kind, int schemaVersion, JsonTypeInfo<TBody> typeInfo) => _kinds[kind] = (typeof(TBody), schemaVersion);

    public bool TryGetSchemaVersion(string kind, out int schemaVersion)
    {
        if (_kinds.TryGetValue(kind, out var entry))
        {
            schemaVersion = entry.SchemaVersion;
            return true;
        }

        schemaVersion = 0;
        return false;
    }

    public bool CanRead(LogRecord record) => _kinds.ContainsKey(record.Envelope.Kind);

    public JsonElement SerializeBody<TBody>(string kind, TBody body) => JsonSerializer.SerializeToElement(body, _options);

    public TBody DeserializeBody<TBody>(string kind, JsonElement body) => JsonSerializer.Deserialize<TBody>(body, _options)!;
}
