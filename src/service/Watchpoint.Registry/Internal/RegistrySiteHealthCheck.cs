using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// Readiness of the registry (spec REG §4 R1, §12): unhealthy with reason <c>siteId</c> while the stored site row's
/// id differs from the installation's configured <c>Site:Id</c> (2026-11 architecture review, D12). The node then
/// serves no traffic and, in a cluster, does not join (CLU-U-081) until the operator corrects <c>Site:Id</c> and
/// restarts.
/// </summary>
internal sealed class RegistrySiteHealthCheck(IRegistryReader reader, SiteId configuredSiteId) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (reader.Site.Id != configuredSiteId)
        {
            var data = new Dictionary<string, object> { ["reason"] = "siteId" };
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Registry site id {reader.Site.Id} differs from the configured Site:Id {configuredSiteId}.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }
}
