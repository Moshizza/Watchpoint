using System.Buffers.Binary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Log;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>
/// Publishes the registry's transactional outbox to <c>WP_CFG</c> as <c>registry.changed</c> records (spec PIP §5.5,
/// REG §6). Records are sent one at a time in revision order; the record id, and so the JetStream dedup id, is derived
/// from the revision, so a publish repeated after a crash between the acknowledgement and the mark is stored once. A record
/// is marked sent only after the log acknowledged it. While the log is down the publisher retries with back-off (the
/// callers of the registry are not affected); after a restart it resumes from the oldest unsent record. Sent records
/// older than <see cref="PipelineOptions.OutboxSentRetention"/> are deleted once per <see cref="PipelineOptions.OutboxPruneInterval"/>.
/// </summary>
/// <remarks>
/// Exactly one publisher runs per node. With several nodes sharing the database (CLU, M9) the loop must run on one node
/// only: the seam is <see cref="RunAsync"/>, to be started by the holder of a <c>registry-outbox</c> lease; until then the
/// revision-based dedup id keeps a double publish harmless within the duplicate window.
/// </remarks>
internal sealed class RegistryOutboxPublisher(
    IRegistryOutbox outbox,
    IEventLogWriter writer,
    ILogRecordTypeRegistry registry,
    PipelineOptions options,
    TimeProvider timeProvider,
    PipelineMetrics metrics,
    ILogger<RegistryOutboxPublisher> logger) : BackgroundService
{
    // "REGCHG" marks the ids of registry.changed records; the revision is in the last eight bytes.
    private static readonly byte[] _idPrefix = [0x52, 0x45, 0x47, 0x43, 0x48, 0x47];

    private long _lastPrune = long.MinValue;

    /// <summary>
    /// The id of the <c>registry.changed</c> record of a revision: a UUID v8 (RFC 9562) built from the revision, the same
    /// every time, so a repeated publish carries the same <c>Nats-Msg-Id</c>.
    /// </summary>
    public static Guid RecordId(long revision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        Span<byte> bytes = stackalloc byte[16];
        _idPrefix.CopyTo(bytes);
        bytes[6] = 0x80; // version 8
        bytes[7] = 0x00;
        BinaryPrimitives.WriteInt64BigEndian(bytes[8..], revision);
        bytes[8] = (byte)(0x80 | (bytes[8] & 0x3F)); // variant 10; revisions stay below 2^61, so the top bits are free
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The envelope of a record (the body is PIP §5.3 <c>registry.changed</c>).</summary>
    public LogEnvelope Envelope(RegistryOutboxRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var body = new RegistryChangedBody(
            record.Revision,
            new RegistryChangedContext(record.Context.Actor?.Value.ToString(), record.Context.Origin, record.Context.ChangeSet?.Value.ToString()),
            [.. record.Changes.Select(c => new RegistryChangedEntity(c.Entity, c.Id, KindName(c.Kind)))]);
        var origin = record.Context.Actor.HasValue ? new RecordOrigin(RecordOriginType.User, record.Context.Actor.Value.Value) : RecordOrigin.System;
        return new LogEnvelope(
            RecordId(record.Revision),
            RecordKinds.RegistryChanged,
            record.CreatedAt,
            Src: null,
            P: 0,
            Dev: null,
            Srv: null,
            origin,
            RecordCorrelation.None,
            registry.SerializeBody(RecordKinds.RegistryChanged, body));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>The publisher loop; returns when <paramref name="ct"/> is cancelled.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        var delay = options.OutboxRetryDelayMin;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<RegistryOutboxRecord> pending;
                try
                {
                    pending = await outbox.ReadUnsentAsync(options.OutboxBatchSize, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PipelineLog.OutboxDatabaseFailed(logger, ex, ex.Message, delay);
                    await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                    delay = BrokerRetry.Next(delay, options.OutboxRetryDelayMax);
                    continue;
                }

                delay = options.OutboxRetryDelayMin;
                metrics.OutboxPending(pending.Count);
                if (pending.Count == 0)
                {
                    await PruneAsync(ct).ConfigureAwait(false);
                    await WaitForWorkAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var attempts = 0;
                for (var i = 0; i < pending.Count; i++)
                {
                    attempts += await PublishAsync(pending[i], ct).ConfigureAwait(false);
                    await MarkSentAsync(pending[i].Revision, ct).ConfigureAwait(false);
                    metrics.OutboxPending(pending.Count - 1 - i);
                }

                if (attempts > 0 || pending.Count > 1)
                {
                    PipelineLog.OutboxCaughtUp(logger, pending[0].Revision, pending[^1].Revision, pending.Count, attempts);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Appends one record, retrying while the log is unavailable; returns the number of failed attempts.</summary>
    private async Task<int> PublishAsync(RegistryOutboxRecord record, CancellationToken ct)
    {
        var envelope = Envelope(record);
        var delay = options.OutboxRetryDelayMin;
        var attempts = 0;
        while (true)
        {
            try
            {
                await writer.AppendGlobalAsync(LogStream.Cfg, envelope, ct).ConfigureAwait(false);
                return attempts;
            }
            catch (PipelineException ex) when (ex.Error.Code.Value == PipelineErrors.LogUnavailableCode)
            {
                attempts++;
                metrics.OutboxRetried();
                PipelineLog.OutboxPublishRetried(logger, record.Revision, ex.Message, delay);
                await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                delay = BrokerRetry.Next(delay, options.OutboxRetryDelayMax);
            }
            catch (PipelineException ex)
            {
                // The log refuses the record itself (too large for a batch of thousands of changes): it cannot be stored, so it
                // is skipped and marked sent; consumers see the gap in the revisions and read the registry again.
                PipelineLog.OutboxRecordSkipped(logger, ex, record.Revision, ex.Error.Code.Value);
                return attempts;
            }
        }
    }

    private async Task MarkSentAsync(long revision, CancellationToken ct)
    {
        var delay = options.OutboxRetryDelayMin;
        while (true)
        {
            try
            {
                await outbox.MarkSentAsync(revision, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The record is in the log; if the node stops before the mark is stored, the record is published again with
                // the same id after the restart (at least once).
                PipelineLog.OutboxDatabaseFailed(logger, ex, ex.Message, delay);
                await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                delay = BrokerRetry.Next(delay, options.OutboxRetryDelayMax);
            }
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var now = timeProvider.GetTimestamp();
        if (_lastPrune != long.MinValue && timeProvider.GetElapsedTime(_lastPrune, now) < options.OutboxPruneInterval)
        {
            return;
        }

        _lastPrune = now;
        try
        {
            await outbox.DeleteSentBeforeAsync(timeProvider.GetUtcNow() - options.OutboxSentRetention, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PipelineLog.OutboxDatabaseFailed(logger, ex, ex.Message, options.OutboxPruneInterval);
        }
    }

    /// <summary>Waits for the registry's signal or the poll interval (records of other nodes, a missed signal).</summary>
    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = outbox.WaitForRecordsAsync(poll.Token);
        var timer = Task.Delay(options.OutboxPollInterval, timeProvider, poll.Token);
        var first = await Task.WhenAny(signal, timer).ConfigureAwait(false);
        poll.Cancel();
        try
        {
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The other one finished first.
        }

        try
        {
            await Task.WhenAll(signal, timer).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loser was cancelled.
        }

        ct.ThrowIfCancellationRequested();
    }

    private static string KindName(ChangeKind kind) => kind switch
    {
        ChangeKind.Created => "created",
        ChangeKind.Updated => "updated",
        ChangeKind.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown change kind."),
    };
}
