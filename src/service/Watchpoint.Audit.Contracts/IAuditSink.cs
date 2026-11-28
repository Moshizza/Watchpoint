namespace Watchpoint.Audit.Contracts;

/// <summary>Every module writes its security-relevant actions through this sink. The audit module replaces the log-only default.</summary>
public interface IAuditSink
{
    /// <summary>
    /// Records an action. Never throws for business reasons and never blocks a caller for more than the bounded channel wait
    /// (spec AUD §5); a record is never dropped.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completed when the record is queued (or stored for critical records).</returns>
    ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default);
}
