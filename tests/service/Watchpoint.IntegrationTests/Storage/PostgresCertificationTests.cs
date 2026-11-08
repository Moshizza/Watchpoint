using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Watchpoint.Storage.Certification;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Storage;

/// <summary>The storage certification suite run against PostgreSQL + TimescaleDB (STO-I-007…STO-I-015).</summary>
[Trait("layer", "I")]
[Trait("spec", "STO")]
[Collection(SerialTiming.Name)]
public sealed class PostgresCertificationTests(PostgresContainerFixture postgres) : StorageCertificationTests
{
    [Fact]
    public void STO_I_015_Certification_suite_skeleton_runs_for_postgres()
    {
        var inherited = typeof(StorageCertificationTests).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        inherited.Count.ShouldBeGreaterThanOrEqualTo(8);
        inherited.ShouldAllBe(name => name.StartsWith("STO_I_", StringComparison.Ordinal));
        typeof(PostgresCertificationTests).IsSubclassOf(typeof(StorageCertificationTests)).ShouldBeTrue();
    }

    protected override async Task<(IServiceProvider Services, IAsyncDisposable Lifetime)> StartNodeAsync(string testName)
    {
        var db = await postgres.CreateDatabaseAsync(testName, Ct);
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
            ["Database:StartupWait"] = "PT30S",
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWatchpointStorage(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), isDevelopment: true);
        services.AddPostgresStorage();
        services.AddModuleStorage<CertificationContext>(CertificationContext.ModuleName, p => p.Add<CertificationPostgresContext>("postgres"));
        var provider = services.BuildServiceProvider();
        return (provider, new Lifetime(provider, db));
    }

    protected override async Task<bool> TableExistsAsync(IServiceProvider services, string schema, string table)
    {
        await using var connection = await services.GetRequiredService<IStoreConnectionFactory>().OpenConnectionAsync(StoreKind.State, Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT to_regclass('{schema}.{table}') IS NOT NULL";
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    protected override IStateStore StateStoreWithFault(IServiceProvider services, Func<CancellationToken, Task> beforeCommit)
    {
        var connections = services.GetRequiredService<IStoreConnectionFactory>();
        var store = (PostgresStateStore)connections.GetProvider(StoreKind.State).CreateStateStore(connections.GetDatabase(StoreKind.State));
        store.BeforeCommit = beforeCommit;
        return store;
    }

    private sealed class Lifetime(ServiceProvider services, PostgresDatabase db) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await db.DisposeAsync();
        }
    }
}

/// <summary>The PostgreSQL context of the certification module (owns its migration).</summary>
internal sealed class CertificationPostgresContext(DbContextOptions options, StorageContextInfo info) : CertificationContext(options, info);

[DbContext(typeof(CertificationPostgresContext))]
[Migration("20260101000000_CertItems")]
internal sealed class CertItemsMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("wp_cert");
        migrationBuilder.CreateTable(
            name: "items",
            schema: "wp_cert",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false),
                at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                label = table.Column<string>(type: "jsonb", nullable: true),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp(3) with time zone", nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_items", x => x.id));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "items", schema: "wp_cert");
}
