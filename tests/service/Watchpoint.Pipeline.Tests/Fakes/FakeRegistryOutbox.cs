using System.Threading.Channels;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Tests.Fakes;

/// <summary>An in-memory registry outbox with the semantics of the real one (REG §10), plus an injectable database fault.</summary>
internal sealed class FakeRegistryOutbox : IRegistryOutbox
{
    private readonly object _lock = new();
    private readonly List<RegistryOutboxRecord> _records = [];
    private readonly Dictionary<long, DateTimeOffset> _sent = [];
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly TimeProvider _time;

    public FakeRegistryOutbox(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Thrown by every call while set (the database is unreachable).</summary>
    public Exception? Fault { get; set; }

    public List<DateTimeOffset> PruneThresholds { get; } = [];

    public int Reads { get; private set; }

    public IReadOnlyList<long> SentRevisions
    {
        get
        {
            lock (_lock)
            {
                return _sent.Keys.Order().ToList();
            }
        }
    }

    public IReadOnlyList<long> Revisions
    {
        get
        {
            lock (_lock)
            {
                return _records.Select(r => r.Revision).Order().ToList();
            }
        }
    }

    /// <summary>Adds a record as the registry writer would (committed, then signalled).</summary>
    public RegistryOutboxRecord Add(long revision, bool sent = false, int changes = 1, string? user = null)
    {
        var record = new RegistryOutboxRecord(
            revision,
            _time.GetUtcNow(),
            new ChangeContext(user, "unit-test", "corr-" + revision),
            [.. Enumerable.Range(0, changes).Select(i => new EntityChange("device", Kernel.Testing.TestIds.Uuid(revision * 100_000 + i), ChangeKind.Updated))]);
        lock (_lock)
        {
            _records.Add(record);
            if (sent)
            {
                _sent[revision] = _time.GetUtcNow();
            }
        }

        _signal.Writer.TryWrite(true);
        return record;
    }

    /// <summary>Forgets that a record was sent (a node crashed between the publish and the mark).</summary>
    public void Unmark(long revision)
    {
        lock (_lock)
        {
            _sent.Remove(revision);
        }
    }

    public Task<IReadOnlyList<RegistryOutboxRecord>> ReadUnsentAsync(int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        lock (_lock)
        {
            Reads++;
            IReadOnlyList<RegistryOutboxRecord> result = _records.Where(r => !_sent.ContainsKey(r.Revision)).OrderBy(r => r.Revision).Take(limit).ToList();
            return Task.FromResult(result);
        }
    }

    public Task MarkSentAsync(long revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        lock (_lock)
        {
            if (_records.Any(r => r.Revision == revision))
            {
                _sent.TryAdd(revision, _time.GetUtcNow());
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteSentBeforeAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Throw();
        lock (_lock)
        {
            PruneThresholds.Add(threshold);
            var expired = _sent.Where(s => s.Value < threshold).Select(s => s.Key).ToList();
            foreach (var revision in expired)
            {
                _sent.Remove(revision);
                _records.RemoveAll(r => r.Revision == revision);
            }

            return Task.FromResult(expired.Count);
        }
    }

    public async Task WaitForRecordsAsync(CancellationToken ct) => await _signal.Reader.ReadAsync(ct);

    private void Throw()
    {
        if (Fault is { } fault)
        {
            throw fault;
        }
    }
}
