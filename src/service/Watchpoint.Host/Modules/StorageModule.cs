using Watchpoint.Hosting;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Storage module (spec HST §5.2, STO §3): storage services, the PostgreSQL provider and the storage start-up tasks
/// (<c>Infrastructure</c>: wait for the databases, <c>Schema</c>: migrations).
/// </summary>
public sealed class StorageModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Storage";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        services.AddWatchpointStorage(context.Configuration, context.IsDevelopment);
        services.AddPostgresStorage();
        services.AddStartupTask<StorageWaitStartupTask>();
        services.AddStartupTask<StorageMigrationStartupTask>();
    }
}

/// <summary>
/// Phase <c>Infrastructure</c>: waits until every database used by a store is reachable (STO §6.3). A database that stays
/// unreachable for <c>Database:StartupWait</c> stops the node with exit code 75; invalid storage settings with 78; any
/// other failure with 70.
/// </summary>
/// <param name="storage">The storage start-up service.</param>
public sealed class StorageWaitStartupTask(IStorageStartup storage) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Infrastructure;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public string Name => "StorageWait";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => StorageStartupExitCode.RunAsync(() => storage.WaitForDatabasesAsync(ct));
}

/// <summary>Phase <c>Schema</c>: runs the storage migrations (STO §6.3); a failure stops the node with exit code 70.</summary>
/// <param name="storage">The storage start-up service.</param>
public sealed class StorageMigrationStartupTask(IStorageStartup storage) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Schema;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public string Name => "StorageMigrations";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => StorageStartupExitCode.RunAsync(() => storage.MigrateAsync(ct));
}

/// <summary>Maps <see cref="StorageStartupException.ExitCode"/> to the process exit code (STO §3); other failures keep 70.</summary>
internal static class StorageStartupExitCode
{
    public static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (StorageStartupException ex)
        {
            Environment.ExitCode = ex.ExitCode;
            throw;
        }
    }
}
