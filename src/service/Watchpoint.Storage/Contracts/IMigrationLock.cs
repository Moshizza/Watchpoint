namespace Watchpoint.Storage.Contracts;

/// <summary>Serialises migrations of all nodes on one database (PostgreSQL: advisory lock).</summary>
public interface IMigrationLock
{
    /// <summary>Waits for the lock.</summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The held lock (dispose to release), or <see langword="null"/> when <paramref name="timeout"/> elapsed.</returns>
    Task<IAsyncDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken ct);
}
