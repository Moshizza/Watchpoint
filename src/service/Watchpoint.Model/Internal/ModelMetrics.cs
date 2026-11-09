using System.Diagnostics.Metrics;

namespace Watchpoint.Model.Internal;

/// <summary>Metrics instruments for Watchpoint.Model (spec MOD §10).</summary>
internal sealed class ModelMetrics
{
    public const string MeterName = "Watchpoint.Model";

    private readonly Meter _meter;

    public ModelMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge("watchpoint.model.catalog.revision", () => Revision);
        _meter.CreateObservableGauge("watchpoint.model.types", () =>
        {
            var counts = TypeCounts;
            var measurements = new List<Measurement<int>>(counts.Count);
            foreach (var (source, count) in counts)
            {
                measurements.Add(new Measurement<int>(count, new KeyValuePair<string, object?>("source", source)));
            }

            return measurements;
        });
    }

    public long Revision { get; set; }

    public IReadOnlyDictionary<string, int> TypeCounts { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
}
