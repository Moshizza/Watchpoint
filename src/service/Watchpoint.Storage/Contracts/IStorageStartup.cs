namespace Watchpoint.Storage.Contracts;

/// <summary>
/// Start-up work of the storage module, run by the host's start-up tasks (HST §5.3): phase <c>Infrastructure</c> calls
/// <see cref="WaitForDatabasesAsync"/>, phase <c>Schema</c> calls <see cref="MigrateAsync"/>. Failures throw
/// <see cref="StorageStartupException"/> carrying the exit code (78 settings, 75 database unreachable, 70 migration).
/// </summary>
public interface IStorageStartup
{
    /// <summary>Validates settings, then probes every database used by a store until reachable or <c>Database:StartupWait</c> elapses.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task WaitForDatabasesAsync(CancellationToken ct);

    /// <summary>Runs the migration procedure of STO §6.3 on every database.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task MigrateAsync(CancellationToken ct);
}
