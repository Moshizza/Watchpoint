using Npgsql;
using Shouldly;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Testing;

[Trait("layer", "I")]
[Trait("spec", "TST")]
public sealed class PostgresFixtureTests(PostgresContainerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TST_I_001_PostgreSQL_container_starts_with_TimescaleDB()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(PostgresFixtureTests), Ct);

        var extensions = await QueryListAsync(db.ConnectionString, "SELECT extname FROM pg_extension");

        extensions.ShouldContain("timescaledb");
        extensions.ShouldContain("pgcrypto");
        (await QueryListAsync(db.ConnectionString, "SHOW server_version")).Single().ShouldStartWith("17.");
    }

    [Fact]
    public async Task TST_I_002_Per_class_databases_are_isolated()
    {
        await using var first = await postgres.CreateDatabaseAsync("ClassA", Ct);
        await using var second = await postgres.CreateDatabaseAsync("ClassB", Ct);

        await ExecuteAsync(first.ConnectionString, "CREATE TABLE isolated (id int)");

        first.Name.ShouldNotBe(second.Name);
        (await QueryListAsync(first.ConnectionString, "SELECT to_regclass('isolated')::text")).Single().ShouldBe("isolated");
        (await QueryListAsync(second.ConnectionString, "SELECT coalesce(to_regclass('isolated')::text, '')")).Single().ShouldBeEmpty();
    }

    [Fact]
    public async Task TST_I_003_Database_creation_from_template_under_500_ms()
    {
        await postgres.StartAsync(Ct);

        // Warm-up outside the timed loop: the first creation after start-up pays for the template's first copy and
        // connection set-up (CI flake); the 500 ms p95 limit applies to the steady state.
        await (await postgres.CreateDatabaseAsync(nameof(TST_I_003_Database_creation_from_template_under_500_ms), Ct)).DisposeAsync();

        var durations = new List<TimeSpan>();
        var clock = TimeProvider.System;

        // 20 samples: the p95 is the 19th fastest, so one slow outlier (CI I/O hiccup) does not fail the test,
        // while a systematically slow template copy still does.
        for (var i = 0; i < 20; i++)
        {
            var started = clock.GetTimestamp();
            await using var db = await postgres.CreateDatabaseAsync(nameof(TST_I_003_Database_creation_from_template_under_500_ms), Ct);
            durations.Add(clock.GetElapsedTime(started));
        }

        var p95 = durations.Order().ElementAt((int)Math.Ceiling(0.95 * durations.Count) - 1);
        p95.ShouldBeLessThan(TimeSpan.FromMilliseconds(500), $"creation times: {string.Join(", ", durations.Select(d => $"{d.TotalMilliseconds:0} ms"))}");
    }

    [Fact]
    public async Task TST_I_004_Database_dropped_on_dispose()
    {
        var db = await postgres.CreateDatabaseAsync(nameof(PostgresFixtureTests), Ct);
        await ExecuteAsync(db.ConnectionString, "CREATE TABLE t (id int)");

        await db.DisposeAsync();

        var count = await QueryListAsync(postgres.AdminConnectionString, $"SELECT count(*)::text FROM pg_database WHERE datname = '{db.Name}'");
        count.Single().ShouldBe("0");
    }

    internal static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    internal static async Task<List<string>> QueryListAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
