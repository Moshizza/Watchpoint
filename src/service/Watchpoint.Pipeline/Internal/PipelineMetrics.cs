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
    private readonly Counter<long> _outboxRetries;
    private int _outboxPending;

    public PipelineMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _records = _meter.CreateCounter<long>("watchpoint.pipeline.records", "{records}", "Records appended to the event log.");
        _appendDuration = _meter.CreateHistogram<double>("watchpoint.pipeline.append.duration", "ms", "Duration of an append call until all records are acknowledged.");
        _outboxRetries = _meter.CreateCounter<long>("watchpoint.pipeline.outbox.retries", "{attempts}", "Failed attempts to append a registry outbox record to the log.");
        _meter.CreateObservableGauge("watchpoint.pipeline.outbox.pending", () => Volatile.Read(ref _outboxPending), "{records}", "Registry outbox records waiting to be published, as last seen by the publisher.");
    }

    /// <summary>The activity source used when records are processed by consumers.</summary>
    public static ActivitySource ActivitySource { get; } = new(MeterName);

    public void RecordAppended(string kind) => _records.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void AppendCompleted(TimeSpan duration) => _appendDuration.Record(duration.TotalMilliseconds);

    public void OutboxPending(int count) => Volatile.Write(ref _outboxPending, count);

    public void OutboxRetried() => _outboxRetries.Add(1);

    public void Dispose() => _meter.Dispose();
}
