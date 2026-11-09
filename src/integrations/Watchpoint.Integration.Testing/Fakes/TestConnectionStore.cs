using System.Collections.Concurrent;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// In-memory fake implementation of <see cref="IConnectionStore"/> that tracks flush ordering relative to an observation sink.
/// </summary>
public sealed class TestConnectionStore : IConnectionStore
{
    private readonly ConcurrentDictionary<string, byte[]> _storage = new(StringComparer.Ordinal);
    private readonly TestObservationSink _sink;

    /// <summary>Records the sink flush count at the time of each key write.</summary>
    public ConcurrentDictionary<string, int> FlushCountAtSave { get; } = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of <see cref="TestConnectionStore"/>.</summary>
    public TestConnectionStore(TestObservationSink sink)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        _storage.TryGetValue(key, out var val);
        return ValueTask.FromResult(val);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        FlushCountAtSave[key] = _sink.FlushCount;
        _storage[key] = value.ToArray();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        _storage.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}
