using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Storage;

[Trait("layer", "I")]
[Trait("spec", "STO")]
public sealed class PostgresStorageTests(PostgresContainerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task STO_I_001_Probe_real_PostgreSQL()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(STO_I_001_Probe_real_PostgreSQL), Ct);
        await using var node = new StorageNode(db.ConnectionString);

        var info = (await node.Provider.ProbeAsync(node.Database, Ct)).Value;

        int.Parse(info.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(16);
        info.Extensions.ShouldContain("timescaledb");
        info.SizeBytes.ShouldBeGreaterThan(0);
        node.Provider.CapabilitiesFor(info).TimeSeriesExtension.ShouldBeTrue();

        await node.Startup.WaitForDatabasesAsync(Ct);
        node.Logs.WithEventId(1201).ShouldHaveSingleItem();
        node.Logs.WithEventId(1203).ShouldBeEmpty();
    }

    [Fact]
    public async Task STO_I_002_Migrations_create_module_schema_and_history_table()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(STO_I_002_Migrations_create_module_schema_and_history_table), Ct);
        await using var node = new StorageNode(db.ConnectionString);

        await node.Startup.MigrateAsync(Ct);

        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_testmod.__ef_migrations WHERE migration_id = @p", InitialTestModMigration.Id)).ShouldBe(1);
        (await ScalarAsync<string>(db.ConnectionString, "SELECT to_regclass('wp_testmod.widgets')::text")).ShouldBe("wp_testmod.widgets");
        node.Logs.WithEventId(1210).ShouldHaveSingleItem().Properties["Migration"].ShouldBe(InitialTestModMigration.Id);
    }

    [Fact]
    public async Task STO_I_003_Migrations_idempotent()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(STO_I_003_Migrations_idempotent), Ct);
        await using var node = new StorageNode(db.ConnectionString);

        await node.Startup.MigrateAsync(Ct);
        await node.Startup.MigrateAsync(Ct);

        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_testmod.__ef_migrations")).ShouldBe(1);
        node.Logs.WithEventId(1210).Count().ShouldBe(1);
    }

    [Fact]
    public async Task STO_I_004_Concurrent_nodes_only_one_migrates_at_a_time()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(STO_I_004_Concurrent_nodes_only_one_migrates_at_a_time), Ct);
        await using var first = new StorageNode(db.ConnectionString);
        await using var second = new StorageNode(db.ConnectionString);
        var clock = TimeProvider.System;
        var start = clock.GetTimestamp();

        await Task.WhenAll(
            Timed(first.Startup.MigrateAsync(Ct)),
            Timed(second.Startup.MigrateAsync(Ct)));

        (await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_testmod.__ef_migrations")).ShouldBe(1);
        (first.Logs.WithEventId(1210).Count() + second.Logs.WithEventId(1210).Count()).ShouldBe(1);

        async Task Timed(Task migration)
        {
            await migration;

            // Both runners return only after the single migration (pg_sleep inside) is complete: the second waited on the lock.
            clock.GetElapsedTime(start).ShouldBeGreaterThanOrEqualTo(InitialTestModMigration.Duration);
        }
    }

    [Fact]
    public async Task STO_I_005_Health_check_unhealthy_when_database_stops()
    {
        DockerAvailability.Ensure(new ContainerFixtureOptions());
        var port = FreePort();
        await using var container = new PostgreSqlBuilder(ContainerImages.TimescaleDb).WithPortBinding(port, 5432).Build();
        await container.StartAsync(Ct);
        await using var node = new StorageNode(container.GetConnectionString());
        (await node.CheckHealthAsync(Ct)).ShouldBe(HealthStatus.Healthy);

        await container.StopAsync(Ct);
        await HealthBecomesAsync(node, HealthStatus.Unhealthy, TimeSpan.FromSeconds(5));

        await container.StartAsync(Ct);
        await HealthBecomesAsync(node, HealthStatus.Healthy, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task STO_I_006_Pool_exhaustion_surfaces_as_timeout_not_hang()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(STO_I_006_Pool_exhaustion_surfaces_as_timeout_not_hang), Ct);
        await using var node = new StorageNode(db.ConnectionString,
            ("Database:Databases:default:MaxPoolSize", "5"), ("Database:Databases:default:ExtraOptions", "Timeout=2"));
        var held = new List<System.Data.Common.DbConnection>();
        for (var i = 0; i < 5; i++)
        {
            held.Add(await node.Connections.OpenConnectionAsync(Watchpoint.Storage.Contracts.StoreKind.Configuration, Ct));
        }

        var clock = TimeProvider.System;
        var start = clock.GetTimestamp();
        await Should.ThrowAsync<NpgsqlException>(async () =>
            await node.Connections.OpenConnectionAsync(Watchpoint.Storage.Contracts.StoreKind.Configuration, Ct));
        var waited = clock.GetElapsedTime(start);

        waited.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1.5));
        waited.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        foreach (var connection in held)
        {
            await connection.DisposeAsync();
        }
    }

    private static async Task HealthBecomesAsync(StorageNode node, HealthStatus expected, TimeSpan timeout)
    {
        var clock = TimeProvider.System;
        var start = clock.GetTimestamp();
        var status = await node.CheckHealthAsync(Ct);
        while (status != expected && clock.GetElapsedTime(start) < timeout)
        {
            await Task.Delay(TestTimeouts.PollInterval * 10, clock, Ct);
            status = await node.CheckHealthAsync(Ct);
        }

        status.ShouldBe(expected, $"within {timeout}");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, string? parameter = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        if (parameter is not null)
        {
            command.Parameters.AddWithValue("p", parameter);
        }

        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
