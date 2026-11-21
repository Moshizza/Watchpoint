using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Log;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// The log's record limit for <c>registry.changed</c> records (spec REG §6, PIP §5.5): measures the exact message the
/// outbox publisher appends — same envelope (<see cref="RegistryOutboxPublisher.Envelope(RegistryOutboxRecord, ILogRecordTypeRegistry)"/>),
/// same codec — with the largest header block the publisher can send (a <c>traceparent</c> header included), against
/// <c>Log:MaxMessageSize</c>, so the registry refuses before commit a batch the log would refuse.
/// </summary>
internal sealed class RegistryRecordLimit(ILogRecordTypeRegistry registry, PipelineOptions options) : IRegistryRecordLimit
{
    public int MaxBytes => options.MaxMessageSize;

    public int Measure(RegistryOutboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var envelope = RegistryOutboxPublisher.Envelope(record, registry);
        if (!registry.TryGetSchemaVersion(envelope.Kind, out var schema))
        {
            throw new InvalidOperationException($"Record kind '{envelope.Kind}' is not registered.");
        }

        return LogMessageCodec.Encode(envelope, schema, activity: null).Size + LogMessageCodec.TraceParentHeaderSize;
    }
}
