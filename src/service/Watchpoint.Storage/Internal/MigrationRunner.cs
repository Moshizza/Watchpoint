using Microsoft.Extensions.Logging;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

/// <summary>Migration procedure (spec STO §6.3): per database lock → bootstrap → module migrations in order → release.</summary>
internal sealed class MigrationRunner(
    StoreMapHolder holder,
    IEnumerable<IModuleStorageRegistration> registrations,
    IModuleMigrator migrator,
    ILogger<MigrationRunner> logger)
{
    /// <summary>How long a node waits for another node's migrations.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Every module must have a context for the configuration store's provider (exit 78 otherwise).</summary>
    public void ValidateRegistrations()
    {
        var provider = holder.Map.Provider(StoreKind.Configuration).ProviderId;
        foreach (var registration in registrations.OrderBy(r => r.Order))
        {
            if (!registration.ProviderContexts.ContainsKey(provider))
            {
                throw new StorageStartupException(StorageErrors.ModuleContextMissing(registration.Module, provider), StorageStartupException.Config);
            }
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        ValidateRegistrations();
        var map = holder.Map;
        var configuration = map.Database(StoreKind.Configuration);
        var ordered = registrations.OrderBy(r => r.Order).ThenBy(r => r.Module, StringComparer.Ordinal).ToList();

        foreach (var db in map.UsedDatabases)
        {
            var provider = map.ProviderOf(db);
            var isConfiguration = string.Equals(db.Key, configuration.Key, StringComparison.Ordinal);
            StorageLog.MigrationLockWaiting(logger, db.Key);
            var handle = await provider.CreateMigrationLock(db).AcquireAsync(LockTimeout, ct).ConfigureAwait(false)
                ?? throw new StorageStartupException(StorageErrors.MigrationLockTimeout(db.Key), StorageStartupException.Software);
            await using (handle.ConfigureAwait(false))
            {
                var schemas = isConfiguration ? ordered.Select(r => provider.Naming.SchemaFor(r.Module)).ToList() : [];
                var stores = map.Bindings.Where(b => b.DatabaseKey == db.Key).Select(b => b.Kind).ToList();
                await provider.BootstrapAsync(db, schemas, stores, ct).ConfigureAwait(false);
                if (!isConfiguration)
                {
                    continue;
                }

                foreach (var registration in ordered)
                {
                    await MigrateModuleAsync(registration, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task MigrateModuleAsync(IModuleStorageRegistration registration, CancellationToken ct)
    {
        try
        {
            var applied = await migrator.MigrateAsync(registration, ct).ConfigureAwait(false);
            foreach (var migration in applied)
            {
                StorageLog.MigrationApplied(logger, migration, registration.Module);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not StorageStartupException)
        {
            var migration = (ex as ModuleMigrationException)?.Migration ?? string.Empty;
            StorageLog.MigrationFailed(logger, ex, migration, registration.Module);
            throw new StorageStartupException(StorageErrors.MigrationFailed(registration.Module, migration), StorageStartupException.Software, ex);
        }
    }
}

/// <summary>Applies the migrations of one module.</summary>
internal interface IModuleMigrator
{
    /// <summary>Migrates the module's context to the latest migration.</summary>
    /// <param name="registration">The module.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The migrations applied by this call, in order.</returns>
    Task<IReadOnlyList<string>> MigrateAsync(IModuleStorageRegistration registration, CancellationToken ct);
}

/// <summary>A module migration failed; names the migration when known.</summary>
internal sealed class ModuleMigrationException(string migration, Exception inner)
    : Exception($"Migration {migration} failed: {inner.Message}", inner)
{
    public string Migration { get; } = migration;
}
