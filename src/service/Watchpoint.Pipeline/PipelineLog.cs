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

    [LoggerMessage(EventId = 1615, Level = LogLevel.Information, Message = "Registry outbox publisher lost the lease {Lease}; publishing stops until it is held again")]
    public static partial void OutboxLeaseLost(ILogger logger, string lease);

    [LoggerMessage(EventId = 1610, Level = LogLevel.Information, Message = "Partition {Partition} recovered from sequence {From} to {To} ({Records} records) in {DurationMs} ms")]
    public static partial void PartitionRecovered(ILogger logger, int partition, long from, long to, long records, long durationMs);

    [LoggerMessage(EventId = 1620, Level = LogLevel.Warning, Message = "Connection {ConnectionId} rejected {Count} observations with {Code} in the last minute; sample {Sample}")]
    public static partial void ObservationsRejected(ILogger logger, Guid connectionId, long count, string code, Guid sample);

    [LoggerMessage(EventId = 1630, Level = LogLevel.Warning, Message = "Connection {ConnectionId} over quota; coalescing active")]
    public static partial void OverQuota(ILogger logger, Guid connectionId);

    [LoggerMessage(EventId = 1631, Level = LogLevel.Error, Message = "Connection {ConnectionId} flooding: {Pending} pending events")]
    public static partial void Flooding(ILogger logger, Guid connectionId, int pending);

    [LoggerMessage(EventId = 1632, Level = LogLevel.Information, Message = "Connection {ConnectionId} back within quota after {Coalesced} coalesced observations")]
    public static partial void WithinQuota(ILogger logger, Guid connectionId, long coalesced);

    [LoggerMessage(EventId = 1633, Level = LogLevel.Error, Message = "Flooding listener {Listener} failed")]
    public static partial void FloodingListenerFailed(ILogger logger, Exception exception, string listener);

    [LoggerMessage(EventId = 1640, Level = LogLevel.Error, Message = "Follow-up depth exceeded in partition {Partition} for record {RecordId}")]
    public static partial void FollowUpDepthExceeded(ILogger logger, int partition, Guid recordId);

    [LoggerMessage(EventId = 1641, Level = LogLevel.Error, Message = "Subscriber {Subscriber} failed on record {RecordId}")]
    public static partial void SubscriberFailed(ILogger logger, Exception exception, string subscriber, Guid recordId);

    [LoggerMessage(EventId = 1642, Level = LogLevel.Error, Message = "Follow-up record {RecordId} of kind {Kind} in partition {Partition} cannot be appended and is dropped: {Reason}")]
    public static partial void FollowUpDropped(ILogger logger, Guid recordId, string kind, int partition, string reason);

    [LoggerMessage(EventId = 1643, Level = LogLevel.Error, Message = "Worker of partition {Partition} failed; {Count} observations are rejected")]
    public static partial void WorkerFailed(ILogger logger, Exception exception, int partition, int count);

    [LoggerMessage(EventId = 1644, Level = LogLevel.Warning, Message = "Pipeline stopped before {Count} queued observations were processed; they are rejected")]
    public static partial void StoppedWithBacklog(ILogger logger, int count);

    [LoggerMessage(EventId = 1645, Level = LogLevel.Error, Message = "Posted work failed in partition {Partition}")]
    public static partial void PostedWorkFailed(ILogger logger, Exception exception, int partition);

    [LoggerMessage(EventId = 1650, Level = LogLevel.Error, Message = "Replay for partition {Partition} starts after retention: wanted {Wanted}, first available {First}")]
    public static partial void ReplayAfterRetention(ILogger logger, int partition, long wanted, long first);

    [LoggerMessage(EventId = 1660, Level = LogLevel.Warning, Message = "Side effect {Kind} ({Class}) failed after {Attempts} attempts")]
    public static partial void SideEffectFailed(ILogger logger, Exception? exception, string kind, string @class, int attempts);

    [LoggerMessage(EventId = 1661, Level = LogLevel.Warning, Message = "Side-effect queue {Class} is full or stopped; {Count} effects were refused, last {Kind}")]
    public static partial void SideEffectRefused(ILogger logger, string @class, long count, string kind);
}
