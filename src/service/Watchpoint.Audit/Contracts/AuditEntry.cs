using System.Text.Json;

namespace Watchpoint.Audit.Contracts;

/// <summary>One stored audit record as returned by a query.</summary>
/// <param name="Id">Store id.</param>
/// <param name="ChainId">The chain.</param>
/// <param name="Sequence">Position in the chain.</param>
/// <param name="OccurredAt">When the action happened.</param>
/// <param name="RecordedAt">When it was stored.</param>
/// <param name="Action">The action.</param>
/// <param name="ActionLabel">The localised action label.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Objects">The affected objects, with labels resolved at query time.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Details">The details.</param>
/// <param name="CorrelationId">The correlation id.</param>
public sealed record AuditEntry(
    long Id,
    string ChainId,
    long Sequence,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    AuditAction Action,
    string ActionLabel,
    AuditOutcome Outcome,
    AuditSeverity Severity,
    AuditActor Actor,
    IReadOnlyList<AuditObjectRef> Objects,
    string? Reason,
    JsonElement? Details,
    Guid? CorrelationId);
