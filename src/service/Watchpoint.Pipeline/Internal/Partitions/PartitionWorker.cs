using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Ingest;

namespace Watchpoint.Pipeline.Internal.Partitions;

/// <summary>What a partition worker needs; shared by the 64 workers.</summary>
internal sealed record PartitionServices(
    IngestOptions Options,
    PipelineOptions LogOptions,
    IEventLogWriter Writer,
    IEventLogReader Reader,
    ILogRecordTypeRegistry Registry,
    IReadOnlyList<IPartitionSubscriber> Subscribers,
    ISideEffectQueue SideEffects,
    TimeProvider Time,
    PipelineMetrics Metrics,
    ILogger Logger)
{
    /// <summary>The soft budget of subscriber calls, shared by every worker (spec PIP §7.3, log 1646).</summary>
    public SubscriberBudget Budget { get; } = new(Options.SubscriberBudget, Time);
}

/// <summary>
/// The worker of one partition (spec PIP §7.2, §7.3): reads validated observations from its bounded channel, detects
/// changes, appends the records and only then calls the subscribers, in order. Everything the worker owns (the last
/// value cache, the partition context, its sequence) is touched by its loop only — one call at a time — so no state is
/// shared between workers; a failure in one worker never reaches another.
/// </summary>
internal sealed class PartitionWorker : IAsyncDisposable
{
    private readonly PartitionServices _services;
    private readonly IngestOptions _options;
    private readonly Channel<IngestItem> _input;
    private readonly Channel<ControlItem> _control;
    private readonly CancellationTokenSource _abort = new();
    private readonly PartitionContext _context;
    private readonly List<IngestItem> _batch;
    private readonly bool[] _isRecord;
    private readonly List<LogEnvelope> _envelopes = [];
    private readonly List<int> _envelopeItems = [];
    private readonly Dictionary<(Guid Target, PropertyPath Property), (WpValue Value, Quality Quality)> _stagedProperties = [];
    private readonly Dictionary<Guid, HealthRecordBody> _stagedHealth = [];
    private TaskCompletionSource _tickSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<LogEnvelope>? _recoveryFollowUps;
    private ITimer? _timer;
    private Task? _loop;
    private int _tickDue;

    public PartitionWorker(int partition, PartitionServices services)
    {
        Partition = partition;
        _services = services;
        _options = services.Options;
        _input = Channel.CreateBounded<IngestItem>(new BoundedChannelOptions(_options.PartitionQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        _control = Channel.CreateBounded<ControlItem>(new BoundedChannelOptions(_options.ControlQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });
        _context = new PartitionContext(partition, services.SideEffects);
        _batch = new List<IngestItem>(_options.MaxBatch);
        _isRecord = new bool[_options.MaxBatch];
    }

    public int Partition { get; }

    public LastValueCache Cache { get; } = new();

    /// <summary>The sequence of the last record of this partition that was appended or replayed.</summary>
    public long Sequence { get; private set; }

    public int Queued => _input.Reader.Count;

    public ChannelWriter<IngestItem> Input => _input.Writer;

    public ChannelWriter<ControlItem> Control => _control.Writer;

    // ---- Recovery (spec PIP §7.4): called by the router before the worker starts, one call at a time. ----

    public void BeginRecovery() => Invoke(null, Guid.Empty, s => s.OnRecoveryStarting(Partition));

    /// <summary>
    /// Applies replayed records: the cache follows the log, and each subscriber receives the records after its
    /// checkpoint. Follow-up records emitted meanwhile are kept until the end of the replay.
    /// </summary>
    public void Replay(IReadOnlyList<LogRecord> records, IReadOnlyList<long?> checkpoints)
    {
        _context.IsRecovering = true;
        _context.Now = _services.Time.GetUtcNow();
        _context.CurrentSequence = Sequence = records[^1].Sequence;
        foreach (var record in records)
        {
            Cache.Apply(record, _services.Registry);
        }

        // A follow-up emitted earlier in the replay that the log already holds was appended before the node stopped.
        if (_recoveryFollowUps is { Count: > 0 })
        {
            RemoveReplayed(_recoveryFollowUps, records);
        }

        for (var i = 0; i < _services.Subscribers.Count; i++)
        {
            if (checkpoints[i] is not { } checkpoint)
            {
                continue;
            }

            var first = 0;
            while (first < records.Count && records[first].Sequence <= checkpoint)
            {
                first++;
            }

            if (first == records.Count)
            {
                continue;
            }

            var slice = first == 0 ? records : records.Skip(first).ToList();
            Invoke(_services.Subscribers[i], slice[0].Envelope.Id, s => s.OnRecords(_context, slice));
        }

        if (_context.TakeFollowUps() is { } emitted)
        {
            RemoveReplayed(emitted, records);
            (_recoveryFollowUps ??= []).AddRange(emitted);
        }
    }

    /// <summary>Ends the recovery: current values from the state module, <c>OnRecoveryCompleted</c>, then the follow-ups kept during the replay.</summary>
    public async Task CompleteRecoveryAsync(long lastSequence, IEnumerable<ILastValueSource> sources, CancellationToken ct)
    {
        _context.IsRecovering = false;
        foreach (var source in sources)
        {
            source.ProvideLastValues(Partition, Cache);
        }

        Sequence = Math.Max(Sequence, lastSequence);
        _context.CurrentSequence = Sequence;
        Invoke(null, Guid.Empty, s => s.OnRecoveryCompleted(Partition, lastSequence));
        if (_recoveryFollowUps is { Count: > 0 } followUps)
        {
            _recoveryFollowUps = null;
            await AppendFollowUpsAsync(followUps, 1, ct).ConfigureAwait(false);
        }
    }

    // ---- Lifecycle ----

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _timer = _services.Time.CreateTimer(
            _ =>
            {
                Volatile.Write(ref _tickDue, 1);
                Volatile.Read(ref _tickSignal).TrySetResult();
            },
            null,
            _options.TickInterval,
            _options.TickInterval);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>No more input: the worker finishes what is queued, then ends.</summary>
    public void CompleteInput()
    {
        _input.Writer.TryComplete();
        _control.Writer.TryComplete();
    }

    /// <summary>Stops the worker at once; what is still queued is rejected.</summary>
    public void Abort() => _abort.Cancel();

    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>
    /// Rejects what is still queued (after the worker was stopped, or when it never started). Safe while the loop is
    /// still inside a subscriber: the loop rejects the batch it holds itself when it ends.
    /// </summary>
    public int RejectBacklog()
    {
        var count = 0;
        var error = PipelineErrors.LogUnavailable("The pipeline stopped before the observation was processed.");
        while (_input.Reader.TryRead(out var item))
        {
            item.Reject(error);
            count++;
        }

        while (_control.Reader.TryRead(out var work))
        {
            work.Cancel();
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        CompleteInput();
        Abort();
        RejectBacklog();
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        // A loop held by a subscriber that never returns is not waited for: it ends when the subscriber does.
        if (_loop is null || _loop.IsCompleted)
        {
            _abort.Dispose();
        }
    }

    // ---- The loop ----

    private async Task RunAsync()
    {
        var ct = _abort.Token;
        Task<bool>? inputWait = null;
        Task<bool>? controlWait = null;
        var inputOpen = true;
        var controlOpen = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Posted work first: the control queue has priority over the input.
                while (_control.Reader.TryRead(out var work))
                {
                    await RunPostedAsync(work, ct).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _tickSignal).Task.IsCompleted)
                {
                    Volatile.Write(ref _tickSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                if (Interlocked.Exchange(ref _tickDue, 0) == 1)
                {
                    await TickAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (_input.Reader.TryRead(out var first))
                {
                    _batch.Add(first);
                    await FillBatchAsync(ct).ConfigureAwait(false);
                    await ProcessBatchGuardedAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (inputWait is { IsCompleted: true })
                {
                    inputOpen = await inputWait.ConfigureAwait(false);
                    inputWait = null;
                    continue;
                }

                if (controlWait is { IsCompleted: true })
                {
                    controlOpen = await controlWait.ConfigureAwait(false);
                    controlWait = null;
                    continue;
                }

                if (!inputOpen && !controlOpen)
                {
                    break;
                }

                if (inputOpen)
                {
                    inputWait ??= _input.Reader.WaitToReadAsync(ct).AsTask();
                }

                if (controlOpen)
                {
                    controlWait ??= _control.Reader.WaitToReadAsync(ct).AsTask();
                }

                var tick = Volatile.Read(ref _tickSignal).Task;
                if (inputWait is not null && controlWait is not null)
                {
                    await Task.WhenAny(inputWait, controlWait, tick).ConfigureAwait(false);
                }
                else
                {
                    await Task.WhenAny(inputWait ?? controlWait!, tick).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Aborted: what is queued is rejected by the owner.
        }
        finally
        {
            var error = PipelineErrors.LogUnavailable("The pipeline stopped before the observation was processed.");
            foreach (var item in _batch)
            {
                item.Reject(error);
            }

            _batch.Clear();
        }
    }

    /// <summary>Reads up to <c>Ingest:MaxBatch</c> observations, waiting at most <c>Ingest:Linger</c> after the first.</summary>
    private async ValueTask FillBatchAsync(CancellationToken ct)
    {
        var max = _options.MaxBatch;
        while (_batch.Count < max && _input.Reader.TryRead(out var item))
        {
            _batch.Add(item);
        }

        if (_batch.Count >= max || _options.Linger <= TimeSpan.Zero)
        {
            return;
        }

        using var lingerTime = new CancellationTokenSource(_options.Linger, _services.Time);
        using var linger = CancellationTokenSource.CreateLinkedTokenSource(ct, lingerTime.Token);
        try
        {
            while (_batch.Count < max && await _input.Reader.WaitToReadAsync(linger.Token).ConfigureAwait(false))
            {
                while (_batch.Count < max && _input.Reader.TryRead(out var item))
                {
                    _batch.Add(item);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linger time is over.
        }
    }

    private async ValueTask ProcessBatchGuardedAsync(CancellationToken ct)
    {
        try
        {
            await ProcessBatchAsync(ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A bug in one batch must not stop the partition: the batch is rejected and the worker goes on.
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            var pending = 0;
            var error = Error.Internal("The partition worker failed on the batch: " + ex.GetType().Name);
            foreach (var item in _batch)
            {
                if (!item.Settled)
                {
                    item.Reject(error);
                    pending++;
                }
            }

            _context.TakeFollowUps();
            PipelineLog.WorkerFailed(_services.Logger, ex, Partition, pending);
        }

        if (!ct.IsCancellationRequested)
        {
            _batch.Clear();
        }
    }

    /// <summary>
    /// Change detection, append, subscribers, then the callers (spec PIP §7.3). When the log does not take a record,
    /// what was stored before it is settled, the record is rejected, and the rest of the batch is planned again against
    /// the cache — so the cache, the subscribers and the callers always agree with the log.
    /// </summary>
    private async ValueTask ProcessBatchAsync(CancellationToken ct)
    {
        var items = _batch;
        var start = 0;
        while (start < items.Count)
        {
            _envelopes.Clear();
            _envelopeItems.Clear();
            _stagedProperties.Clear();
            _stagedHealth.Clear();
            for (var i = start; i < items.Count; i++)
            {
                Plan(items[i], i);
            }

            IReadOnlyList<long> sequences = [];
            var stored = 0;
            Error? failure = null;
            if (_envelopes.Count > 0)
            {
                (sequences, stored, failure) = await AppendInputAsync(_envelopes, ct).ConfigureAwait(false);
            }

            var end = failure is null ? items.Count : _envelopeItems[stored];
            await SettleAsync(items, start, end, sequences, ct).ConfigureAwait(false);
            if (failure is null)
            {
                return;
            }

            items[end].Reject(failure);
            if (IsUnavailable(failure))
            {
                // The log is not reachable: nothing after the failed record was appended either.
                for (var i = end + 1; i < items.Count; i++)
                {
                    items[i].Reject(failure);
                }

                return;
            }

            start = end + 1;
        }
    }

    /// <summary>Decides whether an observation changes something and, if so, builds its record (spec PIP §7.2).</summary>
    private void Plan(IngestItem item, int index)
    {
        string kind;
        System.Text.Json.JsonElement body;
        switch (item.Kind)
        {
            case ObservationKind.Property:
                var key = (item.Target, item.Property);
                bool known;
                WpValue previous;
                Quality previousQuality;
                if (_stagedProperties.TryGetValue(key, out var staged))
                {
                    known = true;
                    (previous, previousQuality) = staged;
                }
                else
                {
                    known = Cache.TryGetProperty(item.Target, item.Property, out previous, out previousQuality);
                }

                if (known && previousQuality == item.Quality && previous.Equals(item.Value))
                {
                    _isRecord[index] = false;
                    return;
                }

                _stagedProperties[key] = (item.Value, item.Quality);
                kind = RecordKinds.Property;
                body = _services.Registry.SerializeBody(kind, new PropertyRecordBody(item.Property.Value, item.Value, item.Quality, known ? previous : null));
                break;

            case ObservationKind.Health:
                var current = _stagedHealth.GetValueOrDefault(item.Target) ?? Cache.GetHealth(item.Target);
                if (current is not null && LastValueCache.SameHealth(current, item.Health!))
                {
                    _isRecord[index] = false;
                    return;
                }

                _stagedHealth[item.Target] = item.Health!;
                kind = RecordKinds.Health;
                body = _services.Registry.SerializeBody(kind, item.Health!);
                break;

            default:
                // Events are facts: never compared with earlier ones (a resend is deduplicated by its id in the log).
                kind = RecordKinds.Event;
                body = _services.Registry.SerializeBody(kind, new EventRecordBody(item.Event.Value, item.Payload!, item.Severity));
                break;
        }

        _isRecord[index] = true;
        _envelopeItems.Add(index);
        _envelopes.Add(new LogEnvelope(
            item.Id,
            kind,
            item.ReceivedTime,
            item.SourceTime,
            Partition,
            item.Device,
            item.Server,
            new RecordOrigin(RecordOriginType.Connection, item.Connection.Value),
            item.Command is { } command ? new RecordCorrelation(Command: command) : RecordCorrelation.None,
            body));
    }

    /// <summary>Items [start, end) are final: their records are in the log. Cache, subscribers, then the callers.</summary>
    private async ValueTask SettleAsync(List<IngestItem> items, int start, int end, IReadOnlyList<long> sequences, CancellationToken ct)
    {
        if (end == start)
        {
            return;
        }

        List<LogRecord>? records = null;
        var next = 0;
        for (var i = start; i < end; i++)
        {
            if (!_isRecord[i])
            {
                continue;
            }

            var item = items[i];
            var envelope = _envelopes[next];
            (records ??= []).Add(Record(sequences[next], envelope));
            next++;
            if (item.Kind == ObservationKind.Property)
            {
                Cache.SetProperty(item.Target, item.Property, item.Value, item.Quality);
            }
            else if (item.Kind == ObservationKind.Health)
            {
                Cache.SetHealth(item.Target, item.Health!);
            }
        }

        if (records is not null)
        {
            await DispatchAsync(records, 0, ct).ConfigureAwait(false);
        }

        _context.Now = _services.Time.GetUtcNow();
        for (var i = start; i < end; i++)
        {
            var item = items[i];
            if (!_isRecord[i])
            {
                var touch = new TouchInfo(item.Device, item.Server, item.Kind == ObservationKind.Property ? item.Property : null, item.ReceivedTime, item.SourceTime);
                Invoke(null, item.Id, s => s.OnTouched(_context, touch));
            }

            if (item.DriftedSourceTime is { } drifted)
            {
                var hint = new ClockDriftHint(item.Device, item.Server, item.Connection, drifted, item.ReceivedTime);
                Invoke(null, item.Id, s => s.OnClockDrift(_context, hint));
            }
        }

        await FlushFollowUpsAsync(ct).ConfigureAwait(false);
        var now = _services.Time.GetTimestamp();
        for (var i = start; i < end; i++)
        {
            var item = items[i];
            _services.Metrics.IngestCompleted(_services.Time.GetElapsedTime(item.Call.StartedTimestamp, now));
            item.Settle(_isRecord[i] ? IngestOutcome.Accepted : IngestOutcome.Unchanged);
        }
    }

    /// <summary>
    /// Appends the records of observations. Returns how many leading records the log holds and why the next one is
    /// not in it. While the log cannot even be read the worker waits here: the partition does not move on with an
    /// unknown log state.
    /// </summary>
    private async ValueTask<(IReadOnlyList<long> Sequences, int Stored, Error? Failure)> AppendInputAsync(List<LogEnvelope> envelopes, CancellationToken ct)
    {
        try
        {
            var sequences = await _services.Writer.AppendAsync(envelopes, ct).ConfigureAwait(false);
            return (sequences, envelopes.Count, null);
        }
        catch (PipelineException ex) when (IsUnavailable(ex.Error))
        {
            // Per partition a prefix of the records is stored: read it back instead of guessing.
            var found = await FindStoredAsync(envelopes, ct).ConfigureAwait(false);
            var stored = 0;
            while (stored < found.Length && found[stored] > 0)
            {
                stored++;
            }

            return (found, stored, stored == found.Length ? null : ex.Error);
        }
        catch (PipelineException ex)
        {
            // Invalid or too large: nothing of the batch was appended. Find the record by appending one at a time.
            if (envelopes.Count == 1)
            {
                return ([], 0, ex.Error);
            }

            var sequences = new long[envelopes.Count];
            for (var i = 0; i < envelopes.Count; i++)
            {
                var (one, stored, failure) = await AppendInputAsync([envelopes[i]], ct).ConfigureAwait(false);
                if (stored == 0)
                {
                    return (sequences, i, failure);
                }

                sequences[i] = one[0];
            }

            return (sequences, envelopes.Count, null);
        }
    }

    /// <summary>Looks for records in the log after the last known sequence; 0 for a record the log does not hold.</summary>
#pragma warning disable CA1859 // Called with lists and arrays.
    private async ValueTask<long[]> FindStoredAsync(IReadOnlyList<LogEnvelope> envelopes, CancellationToken ct)
#pragma warning restore CA1859
    {
        var index = new Dictionary<Guid, int>(envelopes.Count);
        for (var i = 0; i < envelopes.Count; i++)
        {
            index[envelopes[i].Id] = i;
        }

        var delay = _services.LogOptions.RetryDelayMin;
        while (true)
        {
            var found = new long[envelopes.Count];
            try
            {
                await foreach (var record in _services.Reader.ReadPartitionAsync(Partition, Sequence + 1, ct).ConfigureAwait(false))
                {
                    if (index.TryGetValue(record.Envelope.Id, out var i))
                    {
                        found[i] = record.Sequence;
                    }
                }

                return found;
            }
            catch (PipelineException)
            {
                await Task.Delay(delay, _services.Time, ct).ConfigureAwait(false);
                delay = delay + delay < _services.LogOptions.RetryDelayMax ? delay + delay : _services.LogOptions.RetryDelayMax;
            }
        }
    }

    /// <summary>Calls the subscribers with durable records, then appends what they emitted before anything else happens.</summary>
    private async ValueTask DispatchAsync(List<LogRecord> records, int depth, CancellationToken ct)
    {
        Sequence = records[^1].Sequence;
        _context.CurrentSequence = Sequence;
        _context.Now = _services.Time.GetUtcNow();
        Invoke(null, records[0].Envelope.Id, s => s.OnRecords(_context, records));
        if (_context.TakeFollowUps() is { } followUps)
        {
            await AppendFollowUpsAsync(followUps, depth + 1, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask FlushFollowUpsAsync(CancellationToken ct)
    {
        if (_context.TakeFollowUps() is { } followUps)
        {
            await AppendFollowUpsAsync(followUps, 1, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends follow-up records as a new batch and dispatches them (spec PIP §7.3). Subscribers already changed their
    /// state when they emitted them, so the worker does not give up while the log is unreachable.
    /// </summary>
    private async ValueTask AppendFollowUpsAsync(List<LogEnvelope> followUps, int depth, CancellationToken ct)
    {
        if (depth > _options.MaxFollowUpDepth)
        {
            PipelineLog.FollowUpDepthExceeded(_services.Logger, Partition, followUps[0].Id);
            return;
        }

        var records = new List<LogRecord>(followUps.Count);
        try
        {
            var sequences = await _services.Writer.AppendAsync(followUps, ct).ConfigureAwait(false);
            for (var i = 0; i < followUps.Count; i++)
            {
                records.Add(Record(sequences[i], followUps[i]));
            }
        }
        catch (PipelineException)
        {
            // One at a time: an invalid record is dropped alone, an unreachable log is waited for.
            foreach (var followUp in followUps)
            {
                var sequence = await AppendOneAsync(followUp, ct).ConfigureAwait(false);
                if (sequence > 0)
                {
                    records.Add(Record(sequence, followUp));
                }
            }
        }

        if (records.Count > 0)
        {
            await DispatchAsync(records, depth, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask<long> AppendOneAsync(LogEnvelope envelope, CancellationToken ct)
    {
        LogEnvelope[] one = [envelope];
        while (true)
        {
            // An earlier attempt may have stored it although its acknowledgement was lost.
            var found = await FindStoredAsync(one, ct).ConfigureAwait(false);
            if (found[0] > 0)
            {
                return found[0];
            }

            try
            {
                return (await _services.Writer.AppendAsync(one, ct).ConfigureAwait(false))[0];
            }
            catch (PipelineException ex) when (IsUnavailable(ex.Error))
            {
                // Try again: the writer already waited for Log:AppendTimeout.
            }
            catch (PipelineException ex)
            {
                PipelineLog.FollowUpDropped(_services.Logger, envelope.Id, envelope.Kind, Partition, ex.Error.Code.Value);
                return 0;
            }
        }
    }

    private async ValueTask RunPostedAsync(ControlItem work, CancellationToken ct)
    {
        _context.Now = _services.Time.GetUtcNow();
        try
        {
            await work.RunAsync(_context).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The exception belongs to the poster (it is set on its task); the worker goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PipelineLog.PostedWorkFailed(_services.Logger, ex, Partition);
        }

        await FlushFollowUpsAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask TickAsync(CancellationToken ct)
    {
        _context.Now = _services.Time.GetUtcNow();
        Invoke(null, Guid.Empty, s => s.OnTick(_context));
        await FlushFollowUpsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Calls one subscriber, or all of them in order; a subscriber that throws is logged and skipped (spec PIP §7.3).</summary>
    private void Invoke(IPartitionSubscriber? only, Guid recordId, Action<IPartitionSubscriber> call)
    {
        if (only is not null)
        {
            InvokeOne(only, recordId, call);
            return;
        }

        foreach (var subscriber in _services.Subscribers)
        {
            InvokeOne(subscriber, recordId, call);
        }
    }

    private void InvokeOne(IPartitionSubscriber subscriber, Guid recordId, Action<IPartitionSubscriber> call)
    {
        var started = _services.Time.GetTimestamp();
        try
        {
            call(subscriber);
        }
#pragma warning disable CA1031 // One faulty module must not stop supervision (spec PIP §7.3).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PipelineLog.SubscriberFailed(_services.Logger, ex, subscriber.Name, recordId);
            _services.Metrics.SubscriberFailed(subscriber.Name);
        }

        var elapsed = _services.Time.GetElapsedTime(started);
        _services.Metrics.SubscriberCalled(subscriber.Name, elapsed);
        if (_services.Budget.ShouldLog(subscriber.Name, elapsed))
        {
            PipelineLog.SubscriberOverBudget(_services.Logger, subscriber.Name, elapsed.TotalMilliseconds, Partition, _services.Budget.Limit.TotalMilliseconds);
        }
    }

    private LogRecord Record(long sequence, LogEnvelope envelope) =>
        new(sequence, envelope)
        {
            SchemaVersion = _services.Registry.TryGetSchemaVersion(envelope.Kind, out var version) ? version : 1,
        };

    private static void RemoveReplayed(List<LogEnvelope> followUps, IReadOnlyList<LogRecord> records)
    {
        var replayed = new HashSet<Guid>(records.Count);
        foreach (var record in records)
        {
            replayed.Add(record.Envelope.Id);
        }

        followUps.RemoveAll(f => replayed.Contains(f.Id));
    }

    private static bool IsUnavailable(Error error) => error.Code.Value == PipelineErrors.LogUnavailableCode;
}

/// <summary>Work posted to a partition worker (spec PIP §7.3, <see cref="IPartitionDispatcher"/>).</summary>
internal abstract class ControlItem
{
    public abstract ValueTask RunAsync(PartitionContext context);

    public abstract void Cancel();
}

/// <summary>Posted work with a result.</summary>
internal sealed class ControlItem<T>(Func<PartitionContext, ValueTask<T>> work) : ControlItem
{
    private readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Completion => _done.Task;

    public override async ValueTask RunAsync(PartitionContext context)
    {
        try
        {
            _done.TrySetResult(await work(context).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _done.TrySetException(ex);
            throw;
        }
    }

    public override void Cancel() => _done.TrySetCanceled();
}
