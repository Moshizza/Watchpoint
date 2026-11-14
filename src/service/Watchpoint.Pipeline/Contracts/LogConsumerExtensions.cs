using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Internal;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>Handles one record of a durable consumer. Throwing makes the record come again (see <see cref="LogConsumerExtensions.ProcessAsync"/>).</summary>
/// <param name="record">The record; its kind is registered on this node.</param>
/// <param name="ct">The cancellation token.</param>
/// <returns>A task completing when the record was applied.</returns>
public delegate ValueTask LogRecordHandler(LogRecord record, CancellationToken ct);

/// <summary>Options of <see cref="LogConsumerExtensions.ProcessAsync"/>.</summary>
/// <param name="Name">The consumer name, for logs.</param>
/// <param name="Registry">The record kind registry: records it cannot read are skipped.</param>
/// <param name="Logger">The logger.</param>
public sealed record LogProcessingOptions(string Name, ILogRecordTypeRegistry Registry, ILogger Logger)
{
    /// <summary>
    /// Gets the sequence of the last record the handler applied in an earlier run (0 for none). A handler that stores
    /// <see cref="LogRecord.Sequence"/> together with its effect and passes that value here never applies a record twice.
    /// </summary>
    public long LastAppliedSequence { get; init; }

    /// <summary>Gets the time provider used for the delays between attempts.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets the delay before a failed batch is processed again; doubled per delivery up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets the longest delay between two attempts.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Helpers for durable consumers (spec PIP §5.4): at-least-once delivery turned into in-order, once-per-record handling.</summary>
public static class LogConsumerExtensions
{
    /// <summary>
    /// Reads the consumer until cancelled and calls <paramref name="handler"/> for every record, in sequence order:
    /// <list type="bullet">
    /// <item>a record is handed to the handler once: records at or below the last applied sequence (redeliveries, or
    /// <see cref="LogProcessingOptions.LastAppliedSequence"/> after a restart) are skipped;</item>
    /// <item>records of kinds or schema versions this node does not know are skipped with a debug log;</item>
    /// <item>when the handler throws, the batch is not acknowledged and is processed again after a delay, starting
    /// at the failed record; later records are never handled before it;</item>
    /// <item>a record that still fails on the last delivery (<see cref="LogConsumerOptions.MaxDeliver"/>) is logged as an
    /// error and skipped;</item>
    /// <item>a batch is acknowledged only after all its records were handled.</item>
    /// </list>
    /// </summary>
    /// <param name="consumer">The consumer.</param>
    /// <param name="options">The processing options.</param>
    /// <param name="handler">The record handler.</param>
    /// <param name="ct">The cancellation token; cancelling ends the processing without an exception.</param>
    /// <returns>A task completing when <paramref name="ct"/> is cancelled.</returns>
    public static async Task ProcessAsync(this ILogConsumer consumer, LogProcessingOptions options, LogRecordHandler handler, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handler);
        var lastApplied = options.LastAppliedSequence;
        await foreach (var batch in consumer.ReadBatchesAsync(ct).ConfigureAwait(false))
        {
            var failed = false;
            foreach (var record in batch.Records)
            {
                if (record.Sequence <= lastApplied)
                {
                    continue;
                }

                if (!options.Registry.CanRead(record))
                {
                    PipelineLog.UnknownKindSkipped(
                        options.Logger, record.Envelope.Id, record.Sequence, options.Name, record.Envelope.Kind, record.SchemaVersion);
                    lastApplied = record.Sequence;
                    continue;
                }

                try
                {
                    using var activity = record.GetTraceContext() is { } parent
                        ? PipelineMetrics.ActivitySource.StartActivity("log.process", ActivityKind.Consumer, parent)
                        : null;
                    await handler(record, ct).ConfigureAwait(false);
                    lastApplied = record.Sequence;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // A handler failure of any kind must not stop the consumer: the record is retried, then dropped.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    if (batch.IsLastDelivery)
                    {
                        PipelineLog.ConsumerRecordDropped(
                            options.Logger, ex, options.Name, record.Envelope.Id, record.Envelope.Kind, record.Sequence, batch.Delivery);
                        lastApplied = record.Sequence;
                        continue;
                    }

                    PipelineLog.ConsumerRecordFailed(options.Logger, ex, options.Name, record.Envelope.Id, record.Sequence, batch.Delivery);
                    failed = true;
                    break;
                }
            }

            try
            {
                if (failed)
                {
                    // Not acknowledged: the consumer yields the same batch again; applied records are skipped above.
                    await Task.Delay(Backoff(options, batch.Delivery), options.TimeProvider, ct).ConfigureAwait(false);
                }
                else
                {
                    await AckUntilConfirmedAsync(batch, options, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>The records were handled: the acknowledgement is repeated until the log confirms it.</summary>
    private static async ValueTask AckUntilConfirmedAsync(LogRecordBatch batch, LogProcessingOptions options, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await batch.AckAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (PipelineException ex)
            {
                PipelineLog.LogUnavailable(options.Logger, ex.Message);
            }

            await Task.Delay(Backoff(options, attempt), options.TimeProvider, ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(LogProcessingOptions options, int delivery)
    {
        var delay = options.RetryDelay;
        for (var i = 1; i < delivery && delay < options.MaxRetryDelay; i++)
        {
            delay += delay;
        }

        return delay < options.MaxRetryDelay ? delay : options.MaxRetryDelay;
    }
}
