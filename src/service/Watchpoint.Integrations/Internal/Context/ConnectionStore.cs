using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>Thrown to a connector whose store write exceeds a limit (<c>irt.store.limit</c>).</summary>
internal sealed class ConnectionStoreLimitException(Error error) : InvalidOperationException(error.Code.Value + ": " + string.Join(", ", error.Args.Select(a => $"{a.Key}={a.Value}")))
{
    public Error Error { get; } = error;
}

/// <summary>
/// The connection store (spec IRT §8, INT §7.3): the <c>connection_store</c> table with a write-through cache.
/// Writes of one connection are serialised and the cache changes only after the row is committed, so a reader never
/// sees a value that is not durable and concurrent writers cannot break the total-size limit.
/// </summary>
internal sealed class ConnectionStore(ConnectionId connection, IConnectionStoreRepository repository, ILogger logger) : IConnectionStore, IDisposable
{
    public const int MaxValueBytes = 64 * 1024;
    public const int MaxTotalBytes = 1024 * 1024;
    public const int MaxKeyLength = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, byte[]>? _cache;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        CheckKey(key);
        var cache = await CacheAsync(ct).ConfigureAwait(false);
        lock (cache)
        {
            return cache.TryGetValue(key, out var value) ? value.ToArray() : null;
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        CheckKey(key);
        if (value.Length > MaxValueBytes)
        {
            throw new ConnectionStoreLimitException(IntegrationErrors.StoreLimitExceeded("value", MaxValueBytes));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadLockedAsync(ct).ConfigureAwait(false);
            int total;
            lock (cache)
            {
                total = cache.Where(e => e.Key != key).Sum(e => e.Key.Length + e.Value.Length) + key.Length + value.Length;
            }

            if (total > MaxTotalBytes)
            {
                throw new ConnectionStoreLimitException(IntegrationErrors.StoreLimitExceeded("total", MaxTotalBytes));
            }

            var copy = value.ToArray();
            await Persist(() => repository.SetAsync(connection, key, copy, ct)).ConfigureAwait(false);
            lock (cache)
            {
                cache[key] = copy;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        CheckKey(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cache = await LoadLockedAsync(ct).ConfigureAwait(false);
            await Persist(() => repository.DeleteAsync(connection, key, ct)).ConfigureAwait(false);
            lock (cache)
            {
                cache.Remove(key);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.Length > MaxKeyLength)
        {
            throw new ConnectionStoreLimitException(IntegrationErrors.StoreLimitExceeded("key", MaxKeyLength));
        }
    }

    private async ValueTask<Dictionary<string, byte[]>> CacheAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _cache) is { } cache)
        {
            return cache;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<Dictionary<string, byte[]>> LoadLockedAsync(CancellationToken ct)
    {
        if (_cache is { } cache)
        {
            return cache;
        }

        IReadOnlyDictionary<string, byte[]> rows = new Dictionary<string, byte[]>();
        await Persist(async () => rows = await repository.LoadAsync(connection, ct).ConfigureAwait(false)).ConfigureAwait(false);
        cache = new Dictionary<string, byte[]>(rows, StringComparer.Ordinal);
        Volatile.Write(ref _cache, cache);
        return cache;
    }

    private async Task Persist(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IntegrationLog.StoreFailed(logger, ex, connection.Value);
            throw;
        }
    }
}
