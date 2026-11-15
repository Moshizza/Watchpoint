using Microsoft.Extensions.Logging;

namespace Watchpoint.Pipeline;

/// <summary>Log events of the pipeline (spec PIP §12, event ids 1600–1699). Bodies are never logged, only ids and kinds.</summary>
internal static partial class PipelineLog
{
    [LoggerMessage(EventId = 1601, Level = LogLevel.Information, Message = "Streams ready: {Streams}")]
    public static partial void StreamsReady(ILogger logger, string streams);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Warning, Message = "Event log unavailable, retrying: {Reason}")]
    public static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(EventId = 1603, Level = LogLevel.Information, Message = "Stream {Stream} configuration updated: {Changes}")]
    public static partial void StreamUpdated(ILogger logger, string stream, string changes);

    [LoggerMessage(EventId = 1604, Level = LogLevel.Warning, Message = "Stream {Stream} limited to {Bytes} bytes instead of {Wanted}: JetStream has no more storage to reserve")]
    public static partial void StreamReduced(ILogger logger, string stream, long bytes, long wanted);

    [LoggerMessage(EventId = 1605, Level = LogLevel.Error, Message = "Record at sequence {Sequence} of {Stream} cannot be decoded and is skipped: {Reason}")]
    public static partial void RecordUndecodable(ILogger logger, long sequence, string stream, string reason);

    [LoggerMessage(EventId = 1606, Level = LogLevel.Debug, Message = "Record {RecordId} at sequence {Sequence} skipped by {Consumer}: kind {Kind} (schema {Schema}) is not known to this node")]
    public static partial void UnknownKindSkipped(ILogger logger, Guid recordId, long sequence, string consumer, string kind, int schema);

    [LoggerMessage(EventId = 1607, Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on record {RecordId} at sequence {Sequence} (delivery {Delivery}); the batch is delivered again")]
    public static partial void ConsumerRecordFailed(ILogger logger, Exception exception, string consumer, Guid recordId, long sequence, int delivery);

    [LoggerMessage(EventId = 1608, Level = LogLevel.Error, Message = "Consumer {Consumer} dropped record {RecordId} of kind {Kind} at sequence {Sequence} after {Deliveries} deliveries")]
    public static partial void ConsumerRecordDropped(ILogger logger, Exception? exception, string consumer, Guid recordId, string kind, long sequence, int deliveries);

    [LoggerMessage(EventId = 1609, Level = LogLevel.Error, Message = "Consumer {Consumer} dropped an unacknowledged batch of {Records} records, sequences {First} to {Last}, after {Deliveries} deliveries")]
    public static partial void ConsumerBatchDropped(ILogger logger, string consumer, int records, long first, long last, int deliveries);

    [LoggerMessage(EventId = 1611, Level = LogLevel.Warning, Message = "registry.changed revision {Revision} could not be appended to the log: {Reason}; the outbox retries in {Delay}")]
    public static partial void OutboxPublishRetried(ILogger logger, long revision, string reason, TimeSpan delay);

    [LoggerMessage(EventId = 1612, Level = LogLevel.Information, Message = "Registry outbox published revisions {First} to {Last} ({Records} records) after {FailedAttempts} failed attempts")]
    public static partial void OutboxCaughtUp(ILogger logger, long first, long last, int records, int failedAttempts);

    [LoggerMessage(EventId = 1613, Level = LogLevel.Error, Message = "registry.changed revision {Revision} was refused by the log ({Code}) and is skipped; log consumers must resynchronise")]
    public static partial void OutboxRecordSkipped(ILogger logger, Exception exception, long revision, string code);

    [LoggerMessage(EventId = 1614, Level = LogLevel.Warning, Message = "Registry outbox database call failed: {Reason}; retrying in {Delay}")]
    public static partial void OutboxDatabaseFailed(ILogger logger, Exception exception, string reason, TimeSpan delay);
}
