using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// The log writer (spec PIP §5.4). Each partition (and each global stream) is a lane: at most one publish of a lane is
/// in flight, and the next record of the lane is sent only after the previous one was acknowledged. Lanes run
/// concurrently, so at most 66 publishes are in flight. This is what keeps the order of a partition when a publish
/// must be repeated: with several unacknowledged publishes of one partition in flight, a lost first publish and a
/// stored second one could never be put back in order. A repeated publish carries the same message id, so JetStream
/// stores the record once. Nothing is queued inside the writer: callers wait for their lane.
/// </summary>
internal sealed class EventLogWriter : IEventLogWriter, IDisposable
{
    private const int GlobalLanes = 2;

    private readonly ILogBroker _broker;
    private readonly ILogRecordTypeRegistry _registry;
    private readonly PipelineOptions _options;
    private readonly LogNaming _naming;
    private readonly TimeProvider _timeProvider;
    private readonly PipelineMetrics _metrics;
    private readonly ILogger<EventLogWriter> _logger;
    private readonly SemaphoreSlim[] _lanes;
    private long _lastUnavailableLog = long.MinValue;

    public EventLogWriter(
        ILogBroker broker,
        ILogRecordTypeRegistry registry,
        PipelineOptions options,
        TimeProvider timeProvider,
        PipelineMetrics metrics,
        ILogger<EventLogWriter> logger)
    {
        _broker = broker;
        _registry = registry;
        _options = options;
        _naming = new LogNaming(options);
        _timeProvider = timeProvider;
        _metrics = metrics;
        _logger = logger;
        _lanes = new SemaphoreSlim[Partitioning.PartitionCount + GlobalLanes];
        for (var i = 0; i < _lanes.Length; i++)
        {
            _lanes[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<LogEnvelope> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        // Validate and encode everything first: an invalid record must not leave a part of the batch in the log.
        var started = _timeProvider.GetTimestamp();
        var stream = _naming.StreamName(LogStream.Log);
        var messages = new BrokerMessage[records.Count];
        var byLane = new Dictionary<int, List<int>>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i] ?? throw new ArgumentException("A record is null.", nameof(records));
            messages[i] = Prepare(LogStream.Log, stream, record);
            if (!byLane.TryGetValue(record.P, out var indexes))
            {
                byLane[record.P] = indexes = [];
            }

            indexes.Add(i);
        }

        var sequences = new long[records.Count];
        using var deadline = new CancellationTokenSource(_options.AppendTimeout, _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        if (byLane.Count == 1)
        {
            await AppendLaneAsync(records[0].P, byLane[records[0].P], messages, sequences, budget.Token, ct).ConfigureAwait(false);
        }
        else
        {
            var lanes = new List<Task>(byLane.Count);
            foreach (var (lane, indexes) in byLane)
            {
                lanes.Add(AppendLaneAsync(lane, indexes, messages, sequences, budget.Token, ct));
            }

            try
            {
                await Task.WhenAll(lanes).ConfigureAwait(false);
            }
            catch (Exception) when (lanes.Exists(t => t.IsFaulted))
            {
                // Report a log failure rather than the cancellation of another lane.
                var failed = lanes.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).ToList();
                throw failed.Find(e => e is PipelineException) ?? failed[0];
            }
        }

        foreach (var record in records)
        {
            _metrics.RecordAppended(record.Kind);
        }

        _metrics.AppendCompleted(_timeProvider.GetElapsedTime(started));
        return sequences;
    }

    public async ValueTask<long> AppendGlobalAsync(LogStream stream, LogEnvelope record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (stream is not (LogStream.Cfg or LogStream.Sys))
        {
            throw new ArgumentOutOfRangeException(nameof(stream), stream, "AppendGlobalAsync appends to WP_CFG or WP_SYS only.");
        }

        var started = _timeProvider.GetTimestamp();
        BrokerMessage[] messages = [Prepare(stream, _naming.StreamName(stream), record)];
        var sequences = new long[1];
        using var deadline = new CancellationTokenSource(_options.AppendTimeout, _timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var lane = Partitioning.PartitionCount + (stream == LogStream.Cfg ? 0 : 1);
        await AppendLaneAsync(lane, [0], messages, sequences, budget.Token, ct).ConfigureAwait(false);
        _metrics.RecordAppended(record.Kind);
        _metrics.AppendCompleted(_timeProvider.GetElapsedTime(started));
        return sequences[0];
    }

    public void Dispose()
    {
        foreach (var lane in _lanes)
        {
            lane.Dispose();
        }
    }

    private BrokerMessage Prepare(LogStream stream, string streamName, LogEnvelope record)
    {
        var valid = record.Validate(stream);
        if (!valid.IsSuccess)
        {
            throw new PipelineException(valid.Error);
        }

        if (!_registry.TryGetSchemaVersion(record.Kind, out var schema))
        {
            throw new PipelineException(PipelineErrors.RecordInvalid(record.Id, $"kind '{record.Kind}' is not registered"));
        }

        var encoded = LogMessageCodec.Encode(record, schema, Activity.Current);
        if (encoded.Size > _options.MaxMessageSize)
        {
            throw new PipelineException(PipelineErrors.RecordTooLarge(record.Id, encoded.Size, _options.MaxMessageSize));
        }

        return new BrokerMessage(streamName, _naming.Subject(stream, record.P, record.Kind), encoded.MessageId, encoded.Headers, encoded.Payload);
    }

    private async Task AppendLaneAsync(
        int lane, List<int> indexes, BrokerMessage[] messages, long[] sequences, CancellationToken budget, CancellationToken ct)
    {
        var gate = _lanes[lane];
        try
        {
            await gate.WaitAsync(budget).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PipelineException(PipelineErrors.LogUnavailable("timed out waiting for earlier appends of the partition"));
        }

        try
        {
            foreach (var index in indexes)
            {
                sequences[index] = await PublishUntilAckedAsync(messages[index], budget, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<long> PublishUntilAckedAsync(BrokerMessage message, CancellationToken budget, CancellationToken ct)
    {
        var delay = _options.RetryDelayMin;
        var reason = "no acknowledgement";
        Exception? last = null;
        while (true)
        {
            using var attempt = new CancellationTokenSource(_options.AttemptTimeout, _timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget, attempt.Token);
            try
            {
                var ack = await _broker.PublishAsync(message, linked.Token).ConfigureAwait(false);
                return ack.Sequence;
            }
            catch (BrokerRejectedException ex)
            {
                var id = Guid.Parse(message.MessageId);
                throw new PipelineException(
                    ex.TooLarge
                        ? PipelineErrors.RecordTooLarge(id, message.Payload.Length, _options.MaxMessageSize)
                        : PipelineErrors.RecordInvalid(id, ex.Message),
                    ex);
            }
            catch (BrokerUnavailableException ex)
            {
                last = ex;
                reason = ex.Message;
            }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested)
            {
                reason = "the acknowledgement timed out";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PipelineException(PipelineErrors.LogUnavailable(reason), last);
            }

            // The same message (same id) is sent again: if the lost attempt was stored, JetStream answers "duplicate".
            BrokerRetry.LogThrottled(_logger, _timeProvider, ref _lastUnavailableLog, reason);
            try
            {
                await Task.Delay(delay, _timeProvider, budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PipelineException(PipelineErrors.LogUnavailable(reason), last);
            }

            delay = BrokerRetry.Next(delay, _options);
        }
    }
}
