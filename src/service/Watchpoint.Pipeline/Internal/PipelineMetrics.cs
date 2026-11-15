using System.Diagnostics;
using System.Diagnostics.Metrics;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Ingest;

namespace Watchpoint.Pipeline.Internal;

/// <summary>Metrics and traces of the event log (spec PIP §12): meter and activity source <c>Watchpoint.Pipeline</c>.</summary>
internal sealed class PipelineMetrics : IDisposable
{
    public const string MeterName = "Watchpoint.Pipeline";

    private readonly Meter _meter;
    private readonly Counter<long> _records;
    private readonly Histogram<double> _appendDuration;
    private readonly Counter<long> _observations;
    private readonly Histogram<double> _ingestLatency;
    private readonly Histogram<double> _subscriberDuration;
    private readonly Counter<long> _subscriberErrors;
    private readonly Counter<long> _sideEffectsFailed;
    private Func<IEnumerable<Measurement<int>>>? _partitionQueues;
    private Func<IEnumerable<Measurement<int>>>? _sideEffectQueues;

    public PipelineMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _records = _meter.CreateCounter<long>("watchpoint.pipeline.records", "{records}", "Records appended to the event log.");
        _appendDuration = _meter.CreateHistogram<double>("watchpoint.pipeline.append.duration", "ms", "Duration of an append call until all records are acknowledged.");
        _observations = _meter.CreateCounter<long>("watchpoint.pipeline.observations", "{observations}", "Observations ingested, by outcome.");
        _ingestLatency = _meter.CreateHistogram<double>("watchpoint.pipeline.ingest.latency", "ms", "Time from reception of an observation until the subscribers are done.");
        _subscriberDuration = _meter.CreateHistogram<double>("watchpoint.pipeline.subscriber.duration", "ms", "Duration of one subscriber call.");
        _subscriberErrors = _meter.CreateCounter<long>("watchpoint.pipeline.subscriber.errors", "{errors}", "Subscriber calls that threw.");
        _sideEffectsFailed = _meter.CreateCounter<long>("watchpoint.pipeline.sideeffects.failed", "{effects}", "Side effects that failed on their last attempt.");
        _meter.CreateObservableGauge("watchpoint.pipeline.partition.queue", () => _partitionQueues?.Invoke() ?? [], "{items}", "Observations waiting per partition.");
        _meter.CreateObservableGauge("watchpoint.pipeline.sideeffects.queue", () => _sideEffectQueues?.Invoke() ?? [], "{items}", "Side effects waiting per class.");
    }

    /// <summary>The activity source used when records are processed by consumers.</summary>
    public static ActivitySource ActivitySource { get; } = new(MeterName);

    public void RecordAppended(string kind) => _records.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void AppendCompleted(TimeSpan duration) => _appendDuration.Record(duration.TotalMilliseconds);

    public void Observation(string connection, IngestOutcome outcome, string? code = null)
    {
        var name = outcome switch
        {
            IngestOutcome.Accepted => "accepted",
            IngestOutcome.Unchanged => "unchanged",
            IngestOutcome.Coalesced => "coalesced",
            IngestOutcome.Rejected => "rejected",
            _ => "disabled",
        };
        if (code is null)
        {
            _observations.Add(1, new KeyValuePair<string, object?>("connection", connection), new KeyValuePair<string, object?>("outcome", name));
        }
        else
        {
            _observations.Add(
                1,
                new KeyValuePair<string, object?>("connection", connection),
                new KeyValuePair<string, object?>("outcome", name),
                new KeyValuePair<string, object?>("code", code));
        }
    }

    public void IngestCompleted(TimeSpan latency) => _ingestLatency.Record(latency.TotalMilliseconds);

    public void SubscriberCalled(string subscriber, TimeSpan duration) =>
        _subscriberDuration.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>("subscriber", subscriber));

    public void SubscriberFailed(string subscriber) => _subscriberErrors.Add(1, new KeyValuePair<string, object?>("subscriber", subscriber));

    public void SideEffectFailed(SideEffectClass effectClass, string kind) =>
        _sideEffectsFailed.Add(1, new KeyValuePair<string, object?>("class", effectClass.ToString()), new KeyValuePair<string, object?>("kind", kind));

    public void ObservePartitionQueues(Func<IEnumerable<Measurement<int>>> read) => _partitionQueues = read;

    public void ObserveSideEffectQueues(Func<IEnumerable<Measurement<int>>> read) => _sideEffectQueues = read;

    public void Dispose() => _meter.Dispose();
}
