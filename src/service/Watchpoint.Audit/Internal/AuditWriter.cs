using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchpoint.Audit.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>
/// The audit writer (spec AUD §5): a bounded channel drained by one task that assigns sequences, chains hashes and stores
/// batches in one transaction. A record is never dropped: when the channel stays full, or on shutdown, it goes to the fallback file.
/// </summary>
internal sealed class AuditWriter : IAuditSink, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly IAuditStore _store;
    private readonly AuditSettings _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<AuditWriter> _logger;
    private readonly AuditMetrics _metrics;
    private readonly FallbackFile _fallback;
    private readonly Channel<Pending> _channel;
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _loop = Task.CompletedTask;
    private AuditChainHead? _head;
    private int _disposed;

    public AuditWriter(IAuditStore store, AuditSettings settings, TimeProvider clock, ILogger<AuditWriter> logger, AuditMetrics metrics)
    {
        _store = store;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _metrics = metrics;
        _fallback = new FallbackFile(settings.FallbackDirectory, clock, logger);
        _channel = Channel.CreateBounded<Pending>(new BoundedChannelOptions(settings.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        metrics.BindQueueDepth(() => _channel.Reader.CanCount ? _channel.Reader.Count : 0);
    }

    /// <summary>Completes when the schema exists, the head is loaded and the fallback files are ingested.</summary>
    public Task Started => _started.Task;

    public async ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var prepared = AuditRecordPreparer.Prepare(record, _clock.GetUtcNow(), _settings.MaxDetailsBytes, out var normalised);
        if (normalised)
        {
            AuditLog.ActionInvalid(_logger, record.Action.Value);
        }

        var critical = record.Severity == AuditSeverity.Critical;
        TaskCompletionSource? done = critical ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var pending = new Pending(prepared, done);
        if (critical)
        {
            AuditLog.CriticalRecord(_logger, prepared.Action, record.Actor.DisplayName);
        }

        if (!await TryEnqueueAsync(pending, ct).ConfigureAwait(false))
        {
            AppendFallback(prepared);
            return;
        }

        if (done is not null)
        {
            try
            {
                await done.Task.WaitAsync(_settings.ChannelFullWait, _clock, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The record stays queued; the writer stores it as soon as the store answers.
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(() => RunAsync(_abort.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(_settings.ShutdownTimeout, _clock, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(false);
        }

        while (_channel.Reader.TryRead(out var rest))
        {
            AppendFallback(rest.Record);
            rest.Done?.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _abort.Cancel();
            _abort.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
            _abort.Dispose();
        }
    }

    private async ValueTask<bool> TryEnqueueAsync(Pending pending, CancellationToken ct)
    {
        if (_channel.Writer.TryWrite(pending))
        {
            return true;
        }

        using var timeout = new CancellationTokenSource(_settings.ChannelFullWait, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct);
        try
        {
            return await _channel.Writer.WaitToWriteAsync(linked.Token).ConfigureAwait(false) && _channel.Writer.TryWrite(pending);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private void AppendFallback(PreparedRecord record)
    {
        _fallback.Append(record);
        _metrics.Fallback();
        AuditLog.FallbackUsed(_logger);
    }

    private async Task RunAsync(CancellationToken abort)
    {
        List<Pending> batch = [];
        try
        {
            await InitialiseAsync(abort).ConfigureAwait(false);
            _started.TrySetResult();
            while (await _channel.Reader.WaitToReadAsync(abort).ConfigureAwait(false))
            {
                batch = await CollectAsync(abort).ConfigureAwait(false);
                await WriteBatchAsync(batch, abort).ConfigureAwait(false);
                batch = [];
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var pending in batch)
            {
                AppendFallback(pending.Record);
                pending.Done?.TrySetResult();
            }
        }
    }

    private async Task InitialiseAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _store.EnsureSchemaAsync(ct).ConfigureAwait(false);
                _head = await _store.GetHeadAsync(_settings.ChainId, ct).ConfigureAwait(false);
                var count = await IngestFallbackAsync(ct).ConfigureAwait(false);
                AuditLog.WriterStarted(_logger, count);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AuditLog.BatchFailed(_logger, ex.GetType().Name);
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task<int> IngestFallbackAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var (path, records) in _fallback.ReadAll().ToList())
        {
            for (var i = 0; i < records.Count; i += _settings.BatchSize)
            {
                var chunk = records.Skip(i).Take(_settings.BatchSize).Select(r => new Pending(r, null)).ToList();
                await WriteBatchAsync(chunk, ct).ConfigureAwait(false);
                total += chunk.Count;
            }

            FallbackFile.Delete(path);
        }

        return total;
    }

    private async Task<List<Pending>> CollectAsync(CancellationToken ct)
    {
        List<Pending> batch = [];
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? timer = null;
        while (batch.Count < _settings.BatchSize)
        {
            if (_channel.Reader.TryRead(out var next))
            {
                batch.Add(next);
                if (next.Done is not null)
                {
                    break;
                }

                continue;
            }

            if (batch.Count == 0)
            {
                break;
            }

            timer ??= Task.Delay(_settings.BatchDelay, _clock, window.Token);
            var more = _channel.Reader.WaitToReadAsync(window.Token).AsTask();
            var first = await Task.WhenAny(more, timer).ConfigureAwait(false);
            if (first == timer || !await more.ConfigureAwait(false))
            {
                break;
            }
        }

        await window.CancelAsync().ConfigureAwait(false);
        return batch;
    }

    private async Task WriteBatchAsync(List<Pending> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var started = _clock.GetTimestamp();
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            var recordedAt = HashChain.Truncate(_clock.GetUtcNow());
            var rows = Seal(batch, recordedAt);
            try
            {
                await _store.AppendAsync(rows, ct).ConfigureAwait(false);
                _head = new AuditChainHead(rows[^1].Sequence, rows[^1].Hash);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AuditLog.BatchFailed(_logger, ex.GetType().Name);
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
                var head = await _store.GetHeadAsync(_settings.ChainId, ct).ConfigureAwait(false);
                if (head is not null && head.Sequence >= rows[^1].Sequence && head.Hash.AsSpan().SequenceEqual(rows[^1].Hash))
                {
                    // The failed attempt had committed: the batch is stored.
                    _head = head;
                    break;
                }

                _head = head;
            }
        }

        foreach (var pending in batch)
        {
            _metrics.Written(pending.Record.Action, pending.Record.Outcome);
            pending.Done?.TrySetResult();
        }

        var elapsedMs = (long)_clock.GetElapsedTime(started).TotalMilliseconds;
        AuditLog.BatchWritten(_logger, batch.Count, elapsedMs);
    }

    private List<AuditRow> Seal(List<Pending> batch, DateTimeOffset recordedAt)
    {
        var prev = _head?.Hash ?? HashChain.Genesis(_settings.ChainId);
        var sequence = _head?.Sequence ?? 0;
        List<AuditRow> rows = new(batch.Count);
        foreach (var pending in batch)
        {
            var r = pending.Record;
            var row = new AuditRow(
                0, _settings.ChainId, ++sequence, r.OccurredAt, recordedAt, r.Action, r.Outcome, r.Severity, r.ActorJson, r.ActorId,
                r.ObjectsJson, r.ObjectIds, r.Reason, r.DetailsJson, r.CorrelationId, prev, prev);
            row = HashChain.Seal(row, prev);
            rows.Add(row);
            prev = row.Hash;
        }

        return rows;
    }

    private sealed record Pending(PreparedRecord Record, TaskCompletionSource? Done);
}
