using Microsoft.Extensions.Logging;

namespace Watchpoint.Audit.Contracts;

/// <summary>Writes audit records as structured log events (EventId 2890). Used until the audit module registers the real sink.</summary>
public sealed partial class LoggingAuditSink(ILogger<LoggingAuditSink> logger) : IAuditSink
{
    /// <inheritdoc />
    public ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Written(logger, record.Action.Value, record.Actor.DisplayName, record.Outcome);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 2890, Level = LogLevel.Information, Message = "{Action} by {Actor} {Outcome}")]
    private static partial void Written(ILogger logger, string action, string actor, AuditOutcome outcome);
}
