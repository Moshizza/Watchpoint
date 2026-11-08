using Npgsql;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres;

/// <summary>Session advisory lock <c>hashtext('watchpoint.migrations')</c> on a dedicated connection (spec STO §6.3).</summary>
internal sealed class PostgresMigrationLock(NpgsqlDataSource source, TimeProvider clock) : IMigrationLock
{
    internal const string LockSql = "SELECT pg_try_advisory_lock(hashtext('watchpoint.migrations'))";
    internal const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('watchpoint.migrations'))";
    private static readonly TimeSpan _poll = TimeSpan.FromMilliseconds(250);

    public async Task<IAsyncDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken ct)
    {
        var connection = await source.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var deadline = clock.GetUtcNow() + timeout;
            while (true)
            {
                await using (var command = new NpgsqlCommand(LockSql, connection))
                {
                    if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
                    {
                        return new Held(connection);
                    }
                }

                if (clock.GetUtcNow() >= deadline)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    return null;
                }

                await Task.Delay(_poll, clock, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Held(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand(UnlockSql, connection);
                await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
            finally
            {
                // Closing the session releases the lock even if the unlock failed.
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
