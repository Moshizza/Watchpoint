using System.Diagnostics.Metrics;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;

namespace Watchpoint.Integrations.Internal;

/// <summary>Metrics of the integration runtime (spec IRT §13), meter <c>Watchpoint.Integrations</c>.</summary>
internal sealed class IntegrationsMetrics
{
    public const string MeterName = "Watchpoint.Integrations";

    public IntegrationsMetrics(PackageCatalog catalog, IMeterFactory? meterFactory = null)
    {
        var meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        meter.CreateObservableGauge("watchpoint.integrations.packages",
            () => catalog.CountByState().Select(c => new Measurement<int>(c.Value, new KeyValuePair<string, object?>("state", PackageJson.StateName(c.Key)))),
            unit: "{packages}", description: "Packages by state.");
    }
}
