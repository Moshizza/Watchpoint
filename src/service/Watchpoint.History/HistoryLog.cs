using Microsoft.Extensions.Logging;

namespace Watchpoint.History;

/// <summary>Log events of the history module (spec HIS §13, event ids 3200–3249). Bodies are never logged, only ids and kinds.</summary>
internal static partial class HistoryLog
{
    [LoggerMessage(EventId = 3201, Level = LogLevel.Information, Message = "History consumer {Consumer} started at sequence {Sequence}")]
    public static partial void ConsumerStarted(ILogger logger, string consumer, long sequence);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "History lag {LagSeconds} s for {Consumer}")]
    public static partial void Lag(ILogger logger, long lagSeconds, string consumer);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Error, Message = "History write failed for {Consumer}: {Error}; retrying")]
    public static partial void WriteFailed(ILogger logger, string consumer, string error);

    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning, Message = "History consumer {Consumer} skipped record {RecordId} of kind {Kind}: {Reason}")]
    public static partial void RecordSkipped(ILogger logger, string consumer, Guid recordId, string kind, string reason);

    [LoggerMessage(EventId = 3205, Level = LogLevel.Debug, Message = "History consumer {Consumer} skipped record {RecordId}: kind {Kind} (schema {Schema}) is not known to this node")]
    public static partial void UnknownKind(ILogger logger, string consumer, Guid recordId, string kind, int schema);

    [LoggerMessage(EventId = 3206, Level = LogLevel.Warning, Message = "History consumer {Consumer} stopped unexpectedly ({Error}); restarting")]
    public static partial void ConsumerRestarted(ILogger logger, string consumer, string error);

    [LoggerMessage(EventId = 3207, Level = LogLevel.Warning, Message = "History schema upkeep failed ({Error}); retrying")]
    public static partial void SchemaFailed(ILogger logger, string error);
}
