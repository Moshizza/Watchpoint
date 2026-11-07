namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>
/// Persisted key-value store of a connection, replicated with it (failover keeps cursors) (INT §7.3).
/// At most 64 KB per key and 1 MB in total. Flush the sink before persisting an event cursor (rule S4).
/// </summary>
public interface IConnectionStore
{
    /// <summary>Gets a value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct);

    /// <summary>Sets a value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value (≤ 64 KB).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completing when the value is durable.</returns>
    ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct);

    /// <summary>Deletes a value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task completing when deleted.</returns>
    ValueTask DeleteAsync(string key, CancellationToken ct);
}
