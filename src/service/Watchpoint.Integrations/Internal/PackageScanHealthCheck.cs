using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Integrations.Internal.Packages;

namespace Watchpoint.Integrations.Internal;

/// <summary>Readiness: the package catalogue scan has completed (spec IRT §13).</summary>
internal sealed class PackageScanHealthCheck(PackageCatalog catalog) : IHealthCheck
{
    public const string Name = "integrations.packages";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(catalog.ScanCompleted
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Package catalogue scan not completed."));
}
