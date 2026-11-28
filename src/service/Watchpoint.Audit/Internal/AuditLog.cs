using Microsoft.Extensions.Logging;
using Watchpoint.Audit.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Log events of the audit module (spec AUD §12, event ids 2850–2899).</summary>
internal static partial class AuditLog
{
    [LoggerMessage(EventId = 2850, Level = LogLevel.Information, Message = "Audit writer started; ingested {Count} fallback records")]
    public static partial void WriterStarted(ILogger logger, int count);

    [LoggerMessage(EventId = 2851, Level = LogLevel.Debug, Message = "Audit batch {Count} written in {ElapsedMs} ms")]
    public static partial void BatchWritten(ILogger logger, int count, long elapsedMs);

    [LoggerMessage(EventId = 2860, Level = LogLevel.Warning, Message = "Audit channel full; record written to fallback file")]
    public static partial void FallbackUsed(ILogger logger);

    [LoggerMessage(EventId = 2861, Level = LogLevel.Error, Message = "Audit batch write failed ({Error}); retrying")]
    public static partial void BatchFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2862, Level = LogLevel.Warning, Message = "Audit fallback file {File} line {Line} is not readable and was skipped")]
    public static partial void FallbackLineSkipped(ILogger logger, string file, int line);

    [LoggerMessage(EventId = 2863, Level = LogLevel.Warning, Message = "Critical audit record {Action} by {Actor}")]
    public static partial void CriticalRecord(ILogger logger, string action, string actor);

    [LoggerMessage(EventId = 2864, Level = LogLevel.Error, Message = "Audit action '{Action}' is not valid and was recorded as unknown.invalid")]
    public static partial void ActionInvalid(ILogger logger, string action);

    [LoggerMessage(EventId = 2870, Level = LogLevel.Information, Message = "Checkpoint {ChainId}:{Sequence} created")]
    public static partial void CheckpointCreated(ILogger logger, string chainId, long sequence);

    [LoggerMessage(EventId = 2871, Level = LogLevel.Critical, Message = "Audit chain {ChainId} verification failed at {Sequence}: {Kind}")]
    public static partial void VerificationFailed(ILogger logger, string chainId, long sequence, VerificationIssueKind kind);

    [LoggerMessage(EventId = 2872, Level = LogLevel.Warning, Message = "Audit checkpoint or verification run failed ({Error}); retrying at the next interval")]
    public static partial void UpkeepFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 2880, Level = LogLevel.Information, Message = "Audit retention purged {Count} records up to {Sequence}")]
    public static partial void RetentionPurged(ILogger logger, long count, long sequence);
}
