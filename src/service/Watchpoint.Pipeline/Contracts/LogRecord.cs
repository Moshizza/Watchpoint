using System.Diagnostics;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>A record read from the event log (spec PIP §5.2).</summary>
/// <param name="Sequence">The stream sequence of the record (the checkpoint value).</param>
/// <param name="Envelope">The envelope.</param>
public sealed record LogRecord(long Sequence, LogEnvelope Envelope)
{
    /// <summary>Gets the body schema version the record was written with (<c>Wp-Schema</c>).</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Gets the W3C <c>traceparent</c> of the activity that appended the record, if any.</summary>
    public string? TraceParent { get; init; }

    /// <summary>Returns the trace context the record was appended in, to continue the trace when the record is processed.</summary>
    /// <returns>The context, or <see langword="null"/> when the record carries none.</returns>
    public ActivityContext? GetTraceContext() =>
        TraceParent is not null && ActivityContext.TryParse(TraceParent, null, isRemote: true, out var context) ? context : null;

    /// <summary>Deserialises the body with the type registered for the record's kind.</summary>
    /// <typeparam name="T">The body type registered for the kind.</typeparam>
    /// <param name="registry">The record kind registry.</param>
    /// <returns>The body.</returns>
    /// <exception cref="InvalidOperationException">The kind is not registered, or registered with another type.</exception>
    /// <exception cref="System.Text.Json.JsonException">The body does not match the type.</exception>
    public T BodyAs<T>(ILogRecordTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.DeserializeBody<T>(Envelope.Kind, Envelope.Body);
    }
}
