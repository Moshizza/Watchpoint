using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Pipeline.Internal.Log;
using Watchpoint.Pipeline.Internal.Nats;

namespace Watchpoint.Pipeline.Internal;

/// <summary>Readiness of the event log (spec PIP §12): NATS connected and streams present.</summary>
internal sealed class PipelineLogHealthCheck(ILogBroker broker, StreamProvisioner provisioner) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!provisioner.Ready)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The event log streams are not provisioned."));
        }

        return Task.FromResult(broker.IsConnected
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("NATS is not connected."));
    }
}
