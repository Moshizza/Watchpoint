using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Postgres;

/// <summary>The PostgreSQL provider (spec STO §5.1): minimum server version 16; TimescaleDB optional (probed).</summary>
internal sealed class PostgresProvider(PostgresDataSources sources, TimeProvider clock) : IStorageProvider
{
    public const string Id = "postgres";
    public const int MinimumMajorVersion = 16;

    private static readonly ProviderCapabilities _static = new(
        Schemas: true, JsonDocuments: true, TimeSeriesExtension: false, NativeCompression: false, ContinuousAggregates: false,
        Partitioning: true, BulkInsert: BulkInsertMethod.BinaryCopy, MaxDatabaseSizeBytes: null);

    public string ProviderId => Id;

    public ProviderCapabilities Capabilities => _static;

    public IStorageNaming Naming => StorageNaming.Schemas;

    public async Task<Result<DatabaseInfo>> ProbeAsync(DatabaseDefinition db, CancellationToken ct)
    {
        string version;
        List<string> extensions = [];
        long size;
        try
        {
            await using var connection = await sources.Get(db).OpenConnectionAsync(ct).ConfigureAwait(false);
            await using (var command = new NpgsqlCommand("SHOW server_version", connection))
            {
                version = (string)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            await using (var command = new NpgsqlCommand("SELECT extname FROM pg_extension ORDER BY extname", connection))
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    extensions.Add(reader.GetString(0));
                }
            }

            await using (var command = new NpgsqlCommand("SELECT pg_database_size(current_database())", connection))
            {
                size = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException or InvalidOperationException
            && !ct.IsCancellationRequested)
        {
            return StorageErrors.Unavailable(db.Key) with { DeveloperDetail = ex.Message };
        }

        return CheckVersion(version) is { } unsupported
            ? unsupported
            : new DatabaseInfo(ShortVersion(version), Edition: null, extensions, size);
    }

    public ProviderCapabilities CapabilitiesFor(DatabaseInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var timescale = info.Extensions.Contains("timescaledb", StringComparer.Ordinal);
        return _static with { TimeSeriesExtension = timescale, NativeCompression = timescale, ContinuousAggregates = timescale };
    }

    public void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseDefinition db, string migrationsHistorySchema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .UseNpgsql(sources.Get(db), o => o.MigrationsHistoryTable("__ef_migrations", migrationsHistorySchema))
            .UseSnakeCaseNamingConvention()
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    public async ValueTask<DbConnection> OpenConnectionAsync(DatabaseDefinition db, CancellationToken ct) =>
        await sources.Get(db).OpenConnectionAsync(ct).ConfigureAwait(false);

    public IMigrationLock CreateMigrationLock(DatabaseDefinition db) => new PostgresMigrationLock(sources.Get(db), clock);

    public async Task BootstrapAsync(DatabaseDefinition db, IReadOnlyCollection<string> schemas, IReadOnlyCollection<StoreKind> stores,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(stores);
        await using var connection = await sources.Get(db).OpenConnectionAsync(ct).ConfigureAwait(false);
        foreach (var schema in schemas)
        {
            // Schema names come from StorageNaming (wp_ + ASCII letters/digits); quoting is still applied.
            await using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS \"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (stores.Contains(StoreKind.State))
        {
            await using var command = new NpgsqlCommand(EmbeddedSql("state-tables.sql"), connection);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public IStateStore CreateStateStore(DatabaseDefinition db) => new PostgresStateStore(sources.Get(db));

    internal static string EmbeddedSql(string name)
    {
        using var stream = typeof(PostgresProvider).Assembly.GetManifestResourceStream("sql/" + name)
            ?? throw new InvalidOperationException($"Embedded SQL {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public PoolStatistics? GetPoolStatistics(DatabaseDefinition db) => null; // Npgsql publishes pool metrics on its own meter "Npgsql".

    public string Sanitize(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        builder.Remove("Password");
        return builder.ConnectionString;
    }

    /// <summary><c>17.2 (Debian 17.2-1.pgdg120+1)</c> → <c>17.2</c>.</summary>
    internal static string ShortVersion(string serverVersion) => serverVersion.Split(' ', 2)[0];

    internal static Error? CheckVersion(string serverVersion)
    {
        var shortVersion = ShortVersion(serverVersion);
        var majorText = new string([.. shortVersion.TakeWhile(char.IsAsciiDigit)]);
        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major >= MinimumMajorVersion
            ? null
            : StorageErrors.UnsupportedVersion(MinimumMajorVersion.ToString(CultureInfo.InvariantCulture), shortVersion);
    }
}
