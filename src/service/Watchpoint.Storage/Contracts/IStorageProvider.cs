using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Storage.Contracts;

/// <summary>One database technology (spec STO §5). Implemented once per provider; registered as a singleton.</summary>
public interface IStorageProvider
{
    /// <summary>Gets the provider id, e.g. <c>postgres</c>.</summary>
    string ProviderId { get; }

    /// <summary>Gets the static capabilities; probed values are in <see cref="CapabilitiesFor"/>.</summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Gets the naming rules (schemas or table prefixes).</summary>
    IStorageNaming Naming { get; }

    /// <summary>Connects and reads version, extensions and size.</summary>
    /// <param name="db">The database.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The info; <c>storage.unavailable</c> when unreachable; <c>storage.provider.unsupported_version</c> when too old.</returns>
    Task<Result<DatabaseInfo>> ProbeAsync(DatabaseDefinition db, CancellationToken ct);

    /// <summary>Capabilities of a probed database (e.g. <see cref="ProviderCapabilities.TimeSeriesExtension"/>).</summary>
    /// <param name="info">The probe result.</param>
    /// <returns>The capabilities.</returns>
    ProviderCapabilities CapabilitiesFor(DatabaseInfo info);

    /// <summary>Configures an EF Core context for <paramref name="db"/>, with its migration history table in <paramref name="migrationsHistorySchema"/>.</summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="db">The database.</param>
    /// <param name="migrationsHistorySchema">Schema of the module (history table <c>__ef_migrations</c>).</param>
    void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseDefinition db, string migrationsHistorySchema);

    /// <summary>Opens a pooled connection.</summary>
    /// <param name="db">The database.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The open connection; the caller disposes it.</returns>
    ValueTask<DbConnection> OpenConnectionAsync(DatabaseDefinition db, CancellationToken ct);

    /// <summary>Creates the lock serialising migrations of all nodes on <paramref name="db"/>.</summary>
    /// <param name="db">The database.</param>
    /// <returns>The lock.</returns>
    IMigrationLock CreateMigrationLock(DatabaseDefinition db);

    /// <summary>Creates the schemas if missing and runs provider-owned bootstrap SQL idempotently (STO §6.3 step 2).</summary>
    /// <param name="db">The database.</param>
    /// <param name="schemas">The schemas to create.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task BootstrapAsync(DatabaseDefinition db, IReadOnlyCollection<string> schemas, CancellationToken ct);

    /// <summary>Pool statistics for health and metrics, when the provider exposes them.</summary>
    /// <param name="db">The database.</param>
    /// <returns>The statistics, or <see langword="null"/>.</returns>
    PoolStatistics? GetPoolStatistics(DatabaseDefinition db);

    /// <summary>A description of a connection string safe for logs (password removed).</summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The sanitised connection string.</returns>
    string Sanitize(string connectionString);

    // History and audit store factories and CreateStateStore are added by T1.04 (state), T2.13 (HIS), T3.06 (AUD).
}

/// <summary>Connection pool statistics.</summary>
/// <param name="Total">Open connections.</param>
/// <param name="Idle">Idle connections.</param>
/// <param name="Busy">Connections in use.</param>
public sealed record PoolStatistics(int Total, int Idle, int Busy);
