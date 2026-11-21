using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Internal;

/// <summary>JSON used by the history writers and readers (Log profile, source generated).</summary>
internal static class HistoryJson
{
    public static JsonSerializerOptions Options { get; } = WatchpointJson.Create(JsonProfile.Log, HistoryJsonContext.Default);

    public static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Info<T>());

    public static string Serialize(IReadOnlyDictionary<string, WpValue> fields) =>
        JsonSerializer.Serialize(fields as Dictionary<string, WpValue> ?? new Dictionary<string, WpValue>(fields, StringComparer.Ordinal), Info<Dictionary<string, WpValue>>());

    public static string Serialize(WpValue value) => JsonSerializer.Serialize(value, Info<WpValue>());

    /// <summary>The JSON text of an optional element; <paramref name="fallback"/> when absent or JSON null.</summary>
    public static string? Text(JsonElement? element, string? fallback) =>
        element is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } e ? e.GetRawText() : fallback;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement? ParseOrNull(string? json) => json is null ? null : Parse(json);

    /// <summary>Registers the kinds history reads that are not the pipeline's own (idempotent, see <see cref="HistoryRecordKinds"/>).</summary>
    public static void RegisterKinds(ILogRecordTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(HistoryRecordKinds.SysEvent, 1, Info<SysEventBody>());
        registry.Register(HistoryRecordKinds.OperatorNote, 1, Info<OperatorNoteBody>());
        foreach (var kind in HistoryRecordKinds.AlarmKinds)
        {
            registry.Register(kind, 1, Info<AlarmTransitionBody>());
        }

        foreach (var kind in HistoryRecordKinds.CommandKinds)
        {
            registry.Register(kind, 1, Info<CommandTransitionBody>());
        }
    }

    public static string? CorrelationJson(RecordCorrelation correlation) =>
        correlation.Command is null && correlation.Alarm is null && correlation.AutomationRun is null
            ? null
            : Serialize(new HistoryCorrelation(correlation.Command?.Value, correlation.Alarm?.Value, correlation.AutomationRun?.Value));
}
