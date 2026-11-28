using System.Text.Json;

namespace Watchpoint.Audit.Contracts;

/// <summary>A security-relevant action to record (spec AUD §4).</summary>
/// <param name="Action">The action.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Actor">Who acted.</param>
/// <param name="Objects">What was affected (may be empty).</param>
/// <param name="Reason">A user-entered reason when one is required.</param>
/// <param name="Details">Action-specific payload (diffs, arguments), at most 64 KB; secrets are redacted by the writer.</param>
/// <param name="Severity">The severity.</param>
/// <param name="CorrelationId">Command id, config version, copy operation id or request id.</param>
/// <param name="OccurredAt">When it happened; the writer's clock when <see langword="null"/>.</param>
public sealed record AuditRecord(
    AuditAction Action,
    AuditOutcome Outcome,
    AuditActor Actor,
    IReadOnlyList<AuditObjectRef> Objects,
    string? Reason,
    JsonElement? Details,
    AuditSeverity Severity = AuditSeverity.Normal,
    Guid? CorrelationId = null,
    DateTimeOffset? OccurredAt = null);
