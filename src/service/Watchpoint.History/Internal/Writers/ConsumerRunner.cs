using Microsoft.Extensions.Logging;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal.Lag;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>
/// Runs one durable consumer (spec HIS §5): reads a batch of at most <see cref="HistorySettings.BatchSize"/> records (or what arrived within
/// <see cref="HistorySettings.MaxWait"/>), writes it in one transaction, and acknowledges it only after the commit. A failing write is
/// repeated with back-off, the batch kept, for as long as it takes: the consumer reads nothing more meanwhile, so a slow or
/// down database holds back the log without any buffer growing, and the log keeps what it did not deliver.
/// </summary>
internal sealed class ConsumerRunner(
    ILogConsumerFactory consumers,
    ILogRecordTypeRegistry registry,
    HistorySettings settings,
    TimeProvider time,
    HistoryMetrics metrics,
    LagMonitor lag,
    ILogger<ConsumerRunner> logger)
{
    /// <summary>Runs the consumer until cancelled; a consumer that ends or fails is started again after a delay.</summary>
    /// <param name="definition">The consumer.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when <paramref name="ct"/> is cancelled.</returns>
    public async Task RunAsync(HistoryConsumerDefinition definition, CancellationToken ct)
    {
        var delay = settings.RetryDelayMin;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(definition, ct).ConfigureAwait(false);
                delay = settings.RetryDelayMin;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A consumer must outlive any failure of the log connection: it is restarted.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HistoryLog.ConsumerRestarted(logger, definition.Name, ex.GetType().Name);
            }

            if (!await DelayAsync(delay, ct).ConfigureAwait(false))
            {
                return;
            }

            delay = Next(delay);
        }
    }

    private async Task RunOnceAsync(HistoryConsumerDefinition definition, CancellationToken ct)
    {
        var options = new LogConsumerOptions(settings.BatchSize, settings.MaxWait, TimeSpan.FromMinutes(10));
        await using var consumer = consumers.CreateDurable(definition.Name, definition.Stream, definition.Kinds, options);
        var started = false;
        await foreach (var batch in consumer.ReadBatchesAsync(ct).ConfigureAwait(false))
        {
            if (!started)
            {
                started = true;
                HistoryLog.ConsumerStarted(logger, definition.Name, batch.Records[0].Sequence);
            }

            await ProcessAsync(definition, batch, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(HistoryConsumerDefinition definition, LogRecordBatch batch, CancellationToken ct)
    {
        List<LogRecord> readable = new(batch.Records.Count);
        foreach (var record in batch.Records)
        {
            if (!definition.RequiresKnownKind || registry.CanRead(record))
            {
                readable.Add(record);
            }
            else
            {
                HistoryLog.UnknownKind(logger, definition.Name, record.Envelope.Id, record.Envelope.Kind, record.SchemaVersion);
            }
        }

        var started = time.GetTimestamp();
        var retry = settings.RetryDelayMin;
        await lag.ObserveAsync(definition.Name, batch.Records[0].Envelope.Ts, ct).ConfigureAwait(false);
        while (true)
        {
            try
            {
                var rows = readable.Count == 0 ? 0 : await definition.Write(readable, ct).ConfigureAwait(false);
                metrics.Written(definition.Table, rows);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // Anything the database or the mapping throws is retried: the batch is not acknowledged until it is stored.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HistoryLog.WriteFailed(logger, definition.Name, ex.GetType().Name);
                await lag.ObserveAsync(definition.Name, batch.Records[0].Envelope.Ts, ct).ConfigureAwait(false);
                await Task.Delay(retry, time, ct).ConfigureAwait(false);
                retry = Next(retry);
            }
        }

        await AckAsync(batch, ct).ConfigureAwait(false);
        metrics.BatchDuration(definition.Name, time.GetElapsedTime(started));
        lag.Processed(definition.Name, batch.Records[^1].Envelope.Ts);
    }

    private async Task AckAsync(LogRecordBatch batch, CancellationToken ct)
    {
        var delay = settings.RetryDelayMin;
        while (true)
        {
            try
            {
                await batch.AckAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (PipelineException)
            {
                // The log is unreachable: the batch is stored, so only the acknowledgement is repeated (a re-delivery would write nothing new).
            }

            await Task.Delay(delay, time, ct).ConfigureAwait(false);
            delay = Next(delay);
        }
    }

    private TimeSpan Next(TimeSpan delay) => delay + delay < settings.RetryDelayMax ? delay + delay : settings.RetryDelayMax;

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
