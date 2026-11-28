using System.Text.Json;
using Watchpoint.Audit.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Replaces the identity module's log-only sink: its records go to the audit trail (callers do not change).</summary>
internal sealed class IdentityAuditAdapter(IAuditSink sink) : IIdentityAuditSink
{
    public ValueTask RecordAsync(IdentityAuditRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        var actor = record.Actor is { } user ? AuditActor.User(user.Value, "user") : AuditActor.System("identity");
        IReadOnlyList<AuditObjectRef> objects = record.Subject is { } subject ? [new AuditObjectRef("user", subject.Value)] : [];
        var details = record.Details.Count == 0 ? (JsonElement?)null : JsonSerializer.SerializeToElement(record.Details);
        var action = record.Action.Contains('.', StringComparison.Ordinal) ? record.Action : "identity." + record.Action;
        return sink.WriteAsync(
            new AuditRecord(
                new AuditAction(action), (AuditOutcome)(int)record.Outcome, actor, objects, record.Reason, details, (AuditSeverity)(int)record.Severity, null, record.At),
            ct);
    }
}

/// <summary>Replaces the secrets module's log-only sink.</summary>
internal sealed class SecretsAuditAdapter(IAuditSink sink) : ISecretsAuditSink
{
    public void Record(SecretAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var action = record.Action.Contains('.', StringComparison.Ordinal) ? record.Action : "secrets." + record.Action;
        var severity = action is "secrets.readDenied" ? AuditSeverity.Sensitive : AuditSeverity.Normal;
        var task = sink.WriteAsync(
            new AuditRecord(
                new AuditAction(action), AuditOutcome.Success, AuditActor.System(record.Module), [new AuditObjectRef("secret", record.SecretId.Value)], null,
                JsonSerializer.SerializeToElement(new { module = record.Module, entityId = record.EntityId, count = record.Count }), severity, null, record.At));
        if (!task.IsCompleted)
        {
            // The sink completes when the record is queued; the secrets vault cannot await here.
            _ = task.AsTask();
        }
    }
}
