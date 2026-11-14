using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// The log reader (spec PIP §5.4). A replay covers the sequences up to the last one present when it started; when the
/// broker breaks off, the replay resumes after the last record it yielded, so nothing is skipped or repeated.
/// </summary>
internal sealed class EventLogReader(
    ILogBroker broker, PipelineOptions options, TimeProvider timeProvider, ILogger<EventLogReader> logger) : IEventLogReader
{
    private readonly LogNaming _naming = new(options);

    public IAsyncEnumerable<LogRecord> ReadPartitionAsync(int partition, long fromSequence, CancellationToken ct) =>
        ReadAsync(LogStream.Log, [_naming.PartitionFilter(partition)], fromSequence, ct);

    public IAsyncEnumerable<LogRecord> ReadPartitionsAsync(IReadOnlySet<int> partitions, long fromSequence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        return partitions.Count == 0
            ? AsyncEnumerable.Empty<LogRecord>()
            : ReadAsync(LogStream.Log, _naming.PartitionFilters(partitions), fromSequence, ct);
    }

    public IAsyncEnumerable<LogRecord> ReadGlobalAsync(LogStream stream, long fromSequence, CancellationToken ct) =>
        stream is LogStream.Cfg or LogStream.Sys
            ? ReadAsync(stream, [_naming.StreamSubjects(stream)], fromSequence, ct)
            : throw new ArgumentOutOfRangeException(nameof(stream), stream, "ReadGlobalAsync reads WP_CFG or WP_SYS only.");

    public async ValueTask<long> GetLastSequenceAsync(CancellationToken ct) =>
        (await StateAsync(LogStream.Log, ct).ConfigureAwait(false)).LastSequence;

    public async ValueTask<long> GetFirstAvailableSequenceAsync(CancellationToken ct)
    {
        var state = await StateAsync(LogStream.Log, ct).ConfigureAwait(false);
        return state.Messages > 0 ? state.FirstSequence : state.LastSequence + 1;
    }

    private ValueTask<BrokerStreamState> StateAsync(LogStream stream, CancellationToken ct) =>
        BrokerRetry.RunAsync(token => broker.GetStateAsync(_naming.StreamName(stream), token), options, timeProvider, logger, ct);

    private async IAsyncEnumerable<LogRecord> ReadAsync(
        LogStream stream, IReadOnlyList<string> filters, long fromSequence, [EnumeratorCancellation] CancellationToken ct)
    {
        var streamName = _naming.StreamName(stream);
        var end = (await StateAsync(stream, ct).ConfigureAwait(false)).LastSequence;
        var next = Math.Max(fromSequence, 1);
        var delay = options.RetryDelayMin;
        var failingSince = (long?)null;
        var lastLog = long.MinValue;
        while (next <= end)
        {
            var enumerator = broker.ReadAsync(streamName, filters, next, end, ct).GetAsyncEnumerator(ct);
            var completed = false;
            try
            {
                while (true)
                {
                    BrokerDelivery delivery;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            completed = true;
                            break;
                        }

                        delivery = enumerator.Current;
                    }
                    catch (BrokerUnavailableException ex)
                    {
                        failingSince ??= timeProvider.GetTimestamp();
                        if (timeProvider.GetElapsedTime(failingSince.Value) >= options.AppendTimeout)
                        {
                            throw new PipelineException(PipelineErrors.LogUnavailable(ex.Message), ex);
                        }

                        BrokerRetry.LogThrottled(logger, timeProvider, ref lastLog, ex.Message);
                        break;
                    }

                    if (delivery.Sequence < next)
                    {
                        continue;
                    }

                    next = delivery.Sequence + 1;
                    failingSince = null;
                    delay = options.RetryDelayMin;
                    if (LogMessageCodec.Decode(delivery, out var reason) is { } record)
                    {
                        yield return record;
                    }
                    else
                    {
                        PipelineLog.RecordUndecodable(logger, delivery.Sequence, streamName, reason!);
                    }
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (completed)
            {
                yield break;
            }

            await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
            delay = BrokerRetry.Next(delay, options);
        }
    }
}
