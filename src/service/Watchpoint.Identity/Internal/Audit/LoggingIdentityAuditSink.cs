using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;

namespace Watchpoint.Identity.Internal.Audit;

/// <summary>Writes audit records as structured log events (2790–2792) until the audit module (AUD, T3.06) takes over.</summary>
internal sealed class LoggingIdentityAuditSink(ILogger<LoggingIdentityAuditSink> logger) : IIdentityAuditSink
{
    public ValueTask RecordAsync(IdentityAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        IdentityLog.AuditRecord(logger, record);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds audit records and system events; a sink that is not registered is a no-op.</summary>
internal sealed class IdentityAuditor(TimeProvider clock, IIdentityAuditSink? audit = null, ISystemEventSink? events = null)
{
    public ValueTask RecordAsync(string action, IdentityAuditOutcome outcome, Watchpoint.Kernel.Ids.UserId? actor, Watchpoint.Kernel.Ids.UserId? subject,
        IdentityAuditSeverity severity, CancellationToken ct, string? reason = null, params (string Key, string? Value)[] details)
    {
        if (audit is null)
        {
            return ValueTask.CompletedTask;
        }

        var map = new Dictionary<string, string?>(details.Length, StringComparer.Ordinal);
        foreach (var (key, value) in details)
        {
            map[key] = value;
        }

        return audit.RecordAsync(new IdentityAuditRecord(action, outcome, actor, subject, reason, severity, map, clock.GetUtcNow()), ct);
    }

    public ValueTask PublishAsync(string kind, IdentitySystemEventSeverity severity, CancellationToken ct, params (string Key, string? Value)[] payload)
    {
        if (events is null)
        {
            return ValueTask.CompletedTask;
        }

        var map = new Dictionary<string, string?>(payload.Length, StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            map[key] = value;
        }

        return events.PublishAsync(new IdentitySystemEvent(kind, severity, map, clock.GetUtcNow()), ct);
    }
}
