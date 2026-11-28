using Microsoft.Extensions.Logging;

namespace Watchpoint.State;

/// <summary>Log events of the state module (spec STA §11, event ids 1700–1799).</summary>
internal static partial class StateLog
{
    [LoggerMessage(EventId = 1701, Level = LogLevel.Information, Message = "State recovered: {Entities} entities from snapshot, {Replayed} records replayed in {DurationMs} ms")]
    public static partial void StateRecovered(ILogger logger, int entities, long replayed, long durationMs);

    [LoggerMessage(EventId = 1710, Level = LogLevel.Debug, Message = "Snapshot partition {Partition}: {Count} entities at sequence {Sequence}")]
    public static partial void SnapshotWritten(ILogger logger, int partition, int count, long sequence);

    [LoggerMessage(EventId = 1720, Level = LogLevel.Warning, Message = "Snapshot write failed for partition {Partition}; will retry")]
    public static partial void SnapshotWriteFailed(ILogger logger, int partition, Exception exception);

    [LoggerMessage(EventId = 1730, Level = LogLevel.Error, Message = "Aggregate drift detected for a scope: {Details}; corrected")]
    public static partial void AggregateDriftCorrected(ILogger logger, string details);

    [LoggerMessage(EventId = 1740, Level = LogLevel.Warning, Message = "Listener {Listener} overflowed")]
    public static partial void ListenerOverflowed(ILogger logger, string listener);
}
