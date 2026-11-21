using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Tests;

/// <summary>A provider without a database: probes, locks and bootstrap are scripted and recorded.</summary>
internal sealed class FakeStorageProvider(string id = "postgres", bool schemas = true) : IStorageProvider
{
    public List<string> Events { get; } = [];

    public Func<DatabaseDefinition, Result<DatabaseInfo>> Probe { get; set; } =
        _ => new DatabaseInfo("17.2", null, ["timescaledb"], 1024);

    public string ProviderId => id;

    public ProviderCapabilities Capabilities { get; } =
        new(schemas, true, false, false, false, true, BulkInsertMethod.BinaryCopy, null);

    public IStorageNaming Naming => schemas ? StorageNaming.Schemas : StorageNaming.TablePrefixes;

    public int ProbeCount { get; private set; }

    public Task<Result<DatabaseInfo>> ProbeAsync(DatabaseDefinition db, CancellationToken ct)
    {
        ProbeCount++;
        return Task.FromResult(Probe(db));
    }

    public ProviderCapabilities CapabilitiesFor(DatabaseInfo info) =>
        Capabilities with { TimeSeriesExtension = info.Extensions.Contains("timescaledb") };

    /// <summary>SQLite in-memory database shared by the contexts and connections of this provider (kept open by the test).</summary>
    public string SqliteConnectionString { get; } = $"DataSource=file:fake{Guid.NewGuid():N}?mode=memory&cache=shared";

    public void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseDefinition db, string migrationsHistorySchema) =>
        builder.UseSqlite(SqliteConnectionString);

    public async ValueTask<DbConnection> OpenConnectionAsync(DatabaseDefinition db, CancellationToken ct)
    {
        var connection = new SqliteConnection(SqliteConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public IStateStore CreateStateStore(DatabaseDefinition db) => new MemoryStateStore();

    public IHistoryStore CreateHistoryStore(DatabaseDefinition db) => throw new NotSupportedException();

    public IMigrationLock CreateMigrationLock(DatabaseDefinition db) => new Lock(this, db.Key);

    public Task BootstrapAsync(DatabaseDefinition db, IReadOnlyCollection<string> schemas, IReadOnlyCollection<StoreKind> stores, CancellationToken ct)
    {
        Events.Add($"bootstrap {db.Key} [{string.Join(",", schemas)}]");
        return Task.CompletedTask;
    }

    public PoolStatistics? GetPoolStatistics(DatabaseDefinition db) => null;

    public string Sanitize(string connectionString) => connectionString;

    private sealed class Lock(FakeStorageProvider provider, string key) : IMigrationLock
    {
        public Task<IAsyncDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken ct)
        {
            provider.Events.Add($"lock {key}");
            return Task.FromResult<IAsyncDisposable?>(new Held(provider, key));
        }
    }

    private sealed class Held(FakeStorageProvider provider, string key) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            provider.Events.Add($"unlock {key}");
            return ValueTask.CompletedTask;
        }
    }
}
