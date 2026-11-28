using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Audit.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>The integrity state shown by the AUD health check and the <c>auditIntegrity</c> live message.</summary>
internal sealed class AuditIntegrityState
{
    private readonly Lock _gate = new();
    private bool _ok = true;
    private DateTimeOffset? _lastCheckedAt;
    private string? _issue;

    public (bool Ok, DateTimeOffset? LastCheckedAt, string? Issue) Current
    {
        get
        {
            lock (_gate)
            {
                return (_ok, _lastCheckedAt, _issue);
            }
        }
    }

    public void Set(bool ok, DateTimeOffset at, string? issue)
    {
        lock (_gate)
        {
            _ok = ok;
            _lastCheckedAt = at;
            _issue = issue;
        }
    }
}

/// <summary>Health check AUD: degraded while a verification has found an integrity issue.</summary>
internal sealed class AuditHealthCheck(AuditIntegrityState state) : IHealthCheck
{
    public const string Name = "audit";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var (ok, _, issue) = state.Current;
        return Task.FromResult(ok ? HealthCheckResult.Healthy("Audit chain intact.") : HealthCheckResult.Degraded("Audit integrity failure: " + issue));
    }
}

/// <summary>Default integrity publisher: nothing beyond the log and the health state (the live and system-event bridges come with the API live channel).</summary>
internal sealed class NullAuditIntegrityPublisher : IAuditIntegrityPublisher
{
    public ValueTask PublishAsync(bool ok, DateTimeOffset lastCheckedAt, (string ChainId, long Sequence, VerificationIssueKind Kind)? issue, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>Default label resolver: no object labels, the action value as label.</summary>
internal sealed class DefaultAuditLabelResolver : IAuditLabelResolver
{
    public ValueTask<IReadOnlyDictionary<(string Type, Guid Id), string>> ResolveAsync(IReadOnlyCollection<(string Type, Guid Id)> objects, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyDictionary<(string Type, Guid Id), string>>(new Dictionary<(string Type, Guid Id), string>());

    public string ActionLabel(AuditAction action, string language) => action.Value;
}
