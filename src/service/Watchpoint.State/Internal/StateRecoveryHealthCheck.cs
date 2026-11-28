using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Watchpoint.State.Internal;

/// <summary>Health check <c>ready</c> of the state module (spec STA §11): healthy once the recovery completed.</summary>
internal sealed class StateRecoveryHealthCheck(StateRecovery recovery) : IHealthCheck
{
    public const string Name = "state-recovery";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(recovery.Recovered
            ? HealthCheckResult.Healthy("State recovered.")
            : HealthCheckResult.Unhealthy("State recovery not completed."));
}
