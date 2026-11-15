using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Watchpoint.Pipeline.Internal;

/// <summary>Metrics and traces of the event log (spec PIP §12): meter and activity source <c>Watchpoint.Pipeline</c>.</summary>
internal sealed class PipelineMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.Pipeline";

    private readonly Meter _meter;
    private readonly Counter<long> _records;
    private readonly Histogram<double> _appendDuration;

    public PipelineMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _records = _meter.CreateCounter<long>("watchpoint.pipeline.records", "{records}", "Records appended to the event log.");
        _appendDuration = _meter.CreateHistogram<double>("watchpoint.pipeline.append.duration", "ms", "Duration of an append call until all records are acknowledged.");
    }

    /// <summary>The activity source used when records are processed by consumers.</summary>
    public static ActivitySource ActivitySource { get; } = new(MeterName);

    public void RecordAppended(string kind) => _records.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void AppendCompleted(TimeSpan duration) => _appendDuration.Record(duration.TotalMilliseconds);

    public void Dispose() => _meter.Dispose();
}
