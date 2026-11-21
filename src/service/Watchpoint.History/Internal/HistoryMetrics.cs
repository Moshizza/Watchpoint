using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Watchpoint.History.Internal;

/// <summary>Metrics of the history module (spec HIS §13): meter <c>Watchpoint.History</c>.</summary>
internal sealed class HistoryMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.History";

    private readonly Meter _meter;
    private readonly Counter<long> _written;
    private readonly Histogram<double> _batchDuration;
    private readonly ConcurrentDictionary<string, double> _lag = new(StringComparer.Ordinal);

    public HistoryMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _written = _meter.CreateCounter<long>("watchpoint.history.written", "{rows}", "Rows written to history, by table.");
        _batchDuration = _meter.CreateHistogram<double>("watchpoint.history.batch.duration", "ms", "Duration of one batch write, by consumer.");
        _meter.CreateObservableGauge("watchpoint.history.lag", () => _lag.Select(p => new Measurement<double>(p.Value, new KeyValuePair<string, object?>("consumer", p.Key))), "s", "Age of the oldest record not yet written, by consumer.");
    }

    public void Written(string table, int rows) => _written.Add(rows, new KeyValuePair<string, object?>("table", table));

    public void BatchDuration(string consumer, TimeSpan duration) =>
        _batchDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("consumer", consumer));

    public void SetLag(string consumer, TimeSpan lag) => _lag[consumer] = Math.Max(0, lag.TotalSeconds);

    public void Dispose() => _meter.Dispose();
}
