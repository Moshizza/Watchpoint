using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Api.Internal.Idempotency;

/// <summary>Identity of an idempotent request (spec API §5.5): PK (user, key); method, route and body hash must match.</summary>
internal sealed record IdempotencyRequest(UserId User, string Key, string Method, string Route, string BodySha256);

/// <summary>A stored response, replayed for the same key and body.</summary>
internal sealed record StoredResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string> Headers, DateTimeOffset CreatedAt);

/// <summary>Outcome of <see cref="IIdempotencyStore.BeginAsync"/>.</summary>
internal enum IdempotencyBeginKind
{
    /// <summary>First request with this key: run it, then complete or abandon.</summary>
    Started,

    /// <summary>A stored response exists: replay it.</summary>
    Replay,

    /// <summary>Same key with another body, method or route (422).</summary>
    Mismatch,

    /// <summary>The first request is still running (409).</summary>
    InProgress,
}

/// <summary>The outcome and, for <see cref="IdempotencyBeginKind.Replay"/>, the stored response.</summary>
internal sealed record IdempotencyBegin(IdempotencyBeginKind Kind, StoredResponse? Response = null);

/// <summary>Store of idempotency records (table <c>wp_api.idempotency</c>, retention 24 h).</summary>
internal interface IIdempotencyStore
{
    ValueTask<IdempotencyBegin> BeginAsync(IdempotencyRequest request, CancellationToken ct);

    ValueTask CompleteAsync(IdempotencyRequest request, StoredResponse response, CancellationToken ct);

    ValueTask AbandonAsync(IdempotencyRequest request, CancellationToken ct);

    ValueTask<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct);
}

/// <summary>Node-local store (single node in M1; the PostgreSQL store replaces it when storage is configured).</summary>
internal sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(UserId, string), Entry> _entries = new();

    public ValueTask<IdempotencyBegin> BeginAsync(IdempotencyRequest request, CancellationToken ct)
    {
        var key = (request.User, request.Key);
        var started = new Entry(request, null, time.GetUtcNow());
        if (_entries.TryAdd(key, started))
        {
            return ValueTask.FromResult(new IdempotencyBegin(IdempotencyBeginKind.Started));
        }

        var existing = _entries[key];
        if (existing.Request != request)
        {
            return ValueTask.FromResult(new IdempotencyBegin(IdempotencyBeginKind.Mismatch));
        }

        return ValueTask.FromResult(existing.Response is null
            ? new IdempotencyBegin(IdempotencyBeginKind.InProgress)
            : new IdempotencyBegin(IdempotencyBeginKind.Replay, existing.Response));
    }

    public ValueTask CompleteAsync(IdempotencyRequest request, StoredResponse response, CancellationToken ct)
    {
        _entries[(request.User, request.Key)] = new Entry(request, response, response.CreatedAt);
        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonAsync(IdempotencyRequest request, CancellationToken ct)
    {
        _entries.TryRemove((request.User, request.Key), out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct)
    {
        var removed = 0;
        foreach (var (key, entry) in _entries)
        {
            if (entry.CreatedAt < createdBefore && _entries.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return ValueTask.FromResult(removed);
    }

    private sealed record Entry(IdempotencyRequest Request, StoredResponse? Response, DateTimeOffset CreatedAt);
}
