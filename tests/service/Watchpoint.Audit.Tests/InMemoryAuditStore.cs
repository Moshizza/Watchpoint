using System.Text.Json;
using Watchpoint.Audit.Internal;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Tests;

/// <summary>An in-memory <see cref="IAuditStore"/> whose rows a test can tamper with, with the same filter and keyset semantics as the PostgreSQL store.</summary>
internal sealed class InMemoryAuditStore : IAuditStore
{
    private readonly Lock _gate = new();
    private long _nextId = 1;

    public List<AuditRow> Rows { get; } = [];

    public List<AuditCheckpointRow> Checkpoints { get; } = [];

    public Dictionary<string, AuditVerificationState> States { get; } = [];

    public int AppendCalls { get; private set; }

    public List<int> BatchSizes { get; } = [];

    public int FailAppends { get; set; }

    public bool CommitBeforeFailing { get; set; }

    public AuditRowFilter? LastFilter { get; private set; }

    public Task EnsureSchemaAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<AuditChainHead?> GetHeadAsync(string chainId, CancellationToken ct)
    {
        lock (_gate)
        {
            var last = Rows.Where(r => r.ChainId == chainId).OrderBy(r => r.Sequence).LastOrDefault();
            return Task.FromResult(last is null ? null : new AuditChainHead(last.Sequence, last.Hash));
        }
    }

    public Task AppendAsync(IReadOnlyList<AuditRow> rows, CancellationToken ct)
    {
        lock (_gate)
        {
            AppendCalls++;
            if (FailAppends > 0)
            {
                FailAppends--;
                if (CommitBeforeFailing)
                {
                    Commit(rows);
                }

                throw new InvalidOperationException("store unavailable");
            }

            foreach (var row in rows)
            {
                if (Rows.Any(r => r.ChainId == row.ChainId && r.Sequence == row.Sequence))
                {
                    throw new InvalidOperationException("duplicate (chain, sequence)");
                }
            }

            Commit(rows);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AuditRow>> ReadChainAsync(string chainId, long fromSequence, long? toSequence, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditRow> result = [.. Rows.Where(r => r.ChainId == chainId && r.Sequence >= fromSequence && (toSequence is null || r.Sequence <= toSequence)).OrderBy(r => r.Sequence).Take(limit)];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<string> chains = [.. Rows.Select(r => r.ChainId).Concat(Checkpoints.Select(c => c.ChainId)).Distinct().Order(StringComparer.Ordinal)];
            return Task.FromResult(chains);
        }
    }

    public Task<IReadOnlyList<AuditRow>> QueryAsync(AuditRowFilter filter, AuditCursorPosition? after, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            LastFilter = filter;
            var query = Rows.Where(r => r.OccurredAt >= filter.From && r.OccurredAt < filter.To);
            if (filter.Actors is { Count: > 0 })
            {
                query = query.Where(r => r.ActorId is { } a && filter.Actors.Contains(a));
            }

            if (filter.ActionPrefixes is { Count: > 0 })
            {
                query = query.Where(r => filter.ActionPrefixes.Any(p => r.Action.StartsWith(p, StringComparison.Ordinal)));
            }

            if (filter.ObjectType is not null || filter.ObjectId is not null)
            {
                query = query.Where(r => JsonSerializer.Deserialize(r.ObjectsJson, AuditJsonContext.Default.StoredObjectArray)!
                    .Any(o => (filter.ObjectType is null || o.Type == filter.ObjectType) && (filter.ObjectId is null || o.Id == filter.ObjectId)));
            }

            if (filter.Outcome is { } outcome)
            {
                query = query.Where(r => r.Outcome == outcome);
            }

            if (filter.MinSeverity is { } severity)
            {
                query = query.Where(r => r.Severity >= severity);
            }

            if (filter.CorrelationId is { } correlation)
            {
                query = query.Where(r => r.CorrelationId == correlation);
            }

            if (!string.IsNullOrWhiteSpace(filter.Text))
            {
                query = query.Where(r => (r.Reason?.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) ?? false) || r.ObjectsJson.Contains(filter.Text, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = filter.Descending ? query.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id) : query.OrderBy(r => r.OccurredAt).ThenBy(r => r.Id);
            if (after is not null)
            {
                ordered = (filter.Descending
                    ? ordered.Where(r => r.OccurredAt < after.OccurredAt || (r.OccurredAt == after.OccurredAt && r.Id < after.Id)).OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id)
                    : ordered.Where(r => r.OccurredAt > after.OccurredAt || (r.OccurredAt == after.OccurredAt && r.Id > after.Id)).OrderBy(r => r.OccurredAt).ThenBy(r => r.Id));
            }

            IReadOnlyList<AuditRow> result = [.. ordered.Take(limit)];
            return Task.FromResult(result);
        }
    }

    public Task<AuditRow?> GetAsync(long id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));
        }
    }

    public Task AddCheckpointAsync(AuditCheckpointRow checkpoint, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!Checkpoints.Any(c => c.ChainId == checkpoint.ChainId && c.Sequence == checkpoint.Sequence))
            {
                Checkpoints.Add(checkpoint);
            }

            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AuditCheckpointRow>> GetCheckpointsAsync(string chainId, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<AuditCheckpointRow> result = [.. Checkpoints.Where(c => c.ChainId == chainId).OrderBy(c => c.Sequence)];
            return Task.FromResult(result);
        }
    }

    public Task<AuditVerificationState?> GetVerificationStateAsync(string chainId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(States.GetValueOrDefault(chainId));
        }
    }

    public Task SaveVerificationStateAsync(AuditVerificationState state, CancellationToken ct)
    {
        lock (_gate)
        {
            States[state.ChainId] = state;
            return Task.CompletedTask;
        }
    }

    public Task<long> PurgeUpToAsync(string chainId, long sequence, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult((long)Rows.RemoveAll(r => r.ChainId == chainId && r.Sequence <= sequence));
        }
    }

    private void Commit(IReadOnlyList<AuditRow> rows)
    {
        BatchSizes.Add(rows.Count);
        foreach (var row in rows)
        {
            Rows.Add(row with { Id = _nextId++ });
        }
    }
}
