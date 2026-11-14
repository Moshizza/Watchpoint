using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// A durable consumer (spec PIP §5.4). Order is kept by never asking the broker for more while a batch is
/// unacknowledged: such a batch is kept in memory and yielded again. When the consumer is opened, the broker rewinds it
/// to its last acknowledged message, so a batch a previous process did not acknowledge comes first again. Messages
/// the broker delivers again although they were acknowledged (a lost acknowledgement) are dropped here.
/// </summary>
internal sealed class LogConsumer(
    ILogBroker broker,
    string streamName,
    BrokerConsumerSettings settings,
    LogConsumerOptions consumerOptions,
    PipelineOptions options,
    TimeProvider timeProvider,
    ILogger logger) : ILogConsumer
{
    private readonly TimeSpan _maxWait = consumerOptions.MaxWait ?? TimeSpan.FromSeconds(1);
    private IBrokerConsumer? _consumer;
    private Unsettled? _unsettled;
    private long _lastAcked;
    private int _reading;
    private long _lastLog = long.MinValue;

    public async IAsyncEnumerable<LogRecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _reading, 1) != 0)
        {
            throw new InvalidOperationException($"Consumer '{settings.Name}' is already being read.");
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_unsettled is { Acked: false } pending)
                {
                    if (pending.Delivery >= consumerOptions.MaxDeliver)
                    {
                        // Dead letter: the batch is reported and acknowledged so that the consumer can go on.
                        PipelineLog.ConsumerBatchDropped(
                            logger, settings.Name, pending.Records.Count, pending.Records[0].Sequence, pending.Records[^1].Sequence, pending.Delivery);
                        if (!await TryAckAsync(pending, ct).ConfigureAwait(false))
                        {
                            yield break;
                        }

                        continue;
                    }

                    pending.Delivery++;
                    yield return ToBatch(pending);
                    continue;
                }

                var fetch = await FetchAsync(ct).ConfigureAwait(false);
                if (fetch is null)
                {
                    yield break;
                }

                if (fetch.Messages.Count == 0)
                {
                    continue;
                }

                var records = new List<LogRecord>(fetch.Messages.Count);
                foreach (var delivery in fetch.Messages)
                {
                    if (delivery.Sequence <= _lastAcked)
                    {
                        continue;
                    }

                    if (LogMessageCodec.Decode(delivery, out var reason) is { } record)
                    {
                        records.Add(record);
                    }
                    else
                    {
                        PipelineLog.RecordUndecodable(logger, delivery.Sequence, streamName, reason!);
                    }
                }

                var last = fetch.Messages[^1].Sequence;
                _unsettled = new Unsettled(records, fetch, last) { Delivery = 1 };
                if (records.Count == 0)
                {
                    // Nothing to hand out (undecodable or already acknowledged): acknowledge and go on.
                    if (!await TryAckAsync(_unsettled, ct).ConfigureAwait(false))
                    {
                        yield break;
                    }

                    continue;
                }

                yield return ToBatch(_unsettled);
            }
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _consumer, null) is { } consumer)
        {
            await consumer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private LogRecordBatch ToBatch(Unsettled batch) => new(batch.Records, token => AckAsync(batch, token))
    {
        Delivery = batch.Delivery,
        IsLastDelivery = batch.Delivery >= consumerOptions.MaxDeliver,
    };

    private async ValueTask AckAsync(Unsettled batch, CancellationToken ct)
    {
        if (batch.Acked)
        {
            return;
        }

        try
        {
            await batch.Fetch.AckAsync(ct).ConfigureAwait(false);
        }
        catch (BrokerUnavailableException ex)
        {
            throw new PipelineException(PipelineErrors.LogUnavailable(ex.Message), ex);
        }

        batch.Acked = true;
        _lastAcked = Math.Max(_lastAcked, batch.LastSequence);
    }

    /// <summary>Acknowledges, retrying while the broker is unavailable; <see langword="false"/> when cancelled.</summary>
    private async ValueTask<bool> TryAckAsync(Unsettled batch, CancellationToken ct)
    {
        var delay = options.RetryDelayMin;
        while (true)
        {
            try
            {
                await AckAsync(batch, ct).ConfigureAwait(false);
                return true;
            }
            catch (PipelineException ex)
            {
                BrokerRetry.LogThrottled(logger, timeProvider, ref _lastLog, ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }

            if (!await DelayAsync(delay, ct).ConfigureAwait(false))
            {
                return false;
            }

            delay = BrokerRetry.Next(delay, options);
        }
    }

    /// <summary>Fetches the next messages, opening the consumer again when needed; <see langword="null"/> when cancelled.</summary>
    private async ValueTask<BrokerFetch?> FetchAsync(CancellationToken ct)
    {
        var delay = options.RetryDelayMin;
        while (true)
        {
            try
            {
                _consumer ??= await broker.OpenConsumerAsync(streamName, settings, ct).ConfigureAwait(false);
                return await _consumer.FetchAsync(consumerOptions.MaxBatch, _maxWait, ct).ConfigureAwait(false);
            }
            catch (BrokerUnavailableException ex)
            {
                BrokerRetry.LogThrottled(logger, timeProvider, ref _lastLog, ex.Message);
                if (Interlocked.Exchange(ref _consumer, null) is { } broken)
                {
                    await broken.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }

            if (!await DelayAsync(delay, ct).ConfigureAwait(false))
            {
                return null;
            }

            delay = BrokerRetry.Next(delay, options);
        }
    }

    private async ValueTask<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class Unsettled(IReadOnlyList<LogRecord> records, BrokerFetch fetch, long lastSequence)
    {
        public IReadOnlyList<LogRecord> Records { get; } = records;

        public BrokerFetch Fetch { get; } = fetch;

        public long LastSequence { get; } = lastSequence;

        public int Delivery { get; set; }

        public bool Acked { get; set; }
    }
}
