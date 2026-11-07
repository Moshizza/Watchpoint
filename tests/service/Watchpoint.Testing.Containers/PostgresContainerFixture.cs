using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Watchpoint.Testing.Containers;

/// <summary>
/// PostgreSQL + TimescaleDB container shared by a test assembly (register with
/// <c>[assembly: AssemblyFixture(typeof(PostgresContainerFixture))]</c>). Started lazily on first use; every test class
/// gets its own database cloned from a template that already has the <c>timescaledb</c> and <c>pgcrypto</c> extensions.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    /// <summary>The template database name.</summary>
    public const string TemplateDatabase = "wp_template";

    private const string ObjectInUse = "55006";

    private readonly ContainerFixtureOptions _options;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private PostgreSqlContainer? _container;
    private string? _adminConnectionString;

    /// <summary>Initializes a new instance of the <see cref="PostgresContainerFixture"/> class with default options.</summary>
    public PostgresContainerFixture()
        : this(new ContainerFixtureOptions())
    {
    }

    private PostgresContainerFixture(ContainerFixtureOptions options)
    {
        _options = options;
    }

    /// <summary>Creates a fixture with explicit options (the assembly fixture uses the defaults).</summary>
    /// <param name="options">The options.</param>
    /// <returns>The fixture; the caller disposes it.</returns>
    public static PostgresContainerFixture WithOptions(ContainerFixtureOptions options) =>
        new(options ?? throw new ArgumentNullException(nameof(options)));

    /// <summary>Gets the connection string of the <c>postgres</c> maintenance database (superuser).</summary>
    /// <exception cref="InvalidOperationException">The container is not started.</exception>
    public string AdminConnectionString => _adminConnectionString ?? throw new InvalidOperationException("Container not started: call StartAsync first.");

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Starts the container and creates the template database, once. Skips or fails the test when Docker is missing.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the server is ready.</returns>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_adminConnectionString is not null)
        {
            return;
        }

        DockerAvailability.Ensure(_options);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_adminConnectionString is not null)
            {
                return;
            }

            var container = new PostgreSqlBuilder(ContainerImages.TimescaleDb)
                .WithDatabase("postgres")
                .WithEnvironment("TIMESCALEDB_TELEMETRY", "off")
                .WithCommand(
                    "-c", "fsync=off",
                    "-c", "synchronous_commit=off",
                    "-c", "full_page_writes=off",
                    "-c", "max_connections=500",
                    "-c", "timescaledb.telemetry_level=off")
                .Build();
            _container = container;
            await container.StartAsync(ct).ConfigureAwait(false);
            var admin = container.GetConnectionString();
            await CreateTemplateAsync(admin, ct).ConfigureAwait(false);
            _adminConnectionString = admin;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Creates a dedicated database (<c>t_&lt;hash&gt;</c>) for a test class, cloned from the template.</summary>
    /// <param name="testClassName">The test class name, used in the database name hash.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The database; dispose it to drop the database.</returns>
    public async Task<PostgresDatabase> CreateDatabaseAsync(string testClassName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(testClassName);
        await StartAsync(ct).ConfigureAwait(false);
        var name = DatabaseName(testClassName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"", ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == ObjectInUse && attempt < 5)
            {
                // A TimescaleDB background worker briefly touched the template; terminate it and retry.
                await TerminateConnectionsAsync(TemplateDatabase, ct).ConfigureAwait(false);
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name };
        return new PostgresDatabase(this, name, builder.ConnectionString);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        _startGate.Dispose();
    }

    internal async Task DropDatabaseAsync(string name, string connectionString)
    {
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            NpgsqlConnection.ClearPool(connection);
        }

        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
    }

    private static string DatabaseName(string testClassName)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(testClassName)))[..12];
        var unique = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        return $"t_{hash}_{unique}";
    }

    private static async Task CreateTemplateAsync(string adminConnectionString, CancellationToken ct)
    {
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE \"{TemplateDatabase}\"", ct).ConfigureAwait(false);
        var template = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = TemplateDatabase, Pooling = false };
        await ExecuteAsync(
            template.ConnectionString,
            "CREATE EXTENSION IF NOT EXISTS timescaledb; CREATE EXTENSION IF NOT EXISTS pgcrypto;",
            ct).ConfigureAwait(false);
        await ExecuteAsync(
            adminConnectionString,
            $"ALTER DATABASE \"{TemplateDatabase}\" WITH IS_TEMPLATE true ALLOW_CONNECTIONS false",
            ct).ConfigureAwait(false);
        await TerminateConnectionsAsync(adminConnectionString, TemplateDatabase, ct).ConfigureAwait(false);
    }

    private Task TerminateConnectionsAsync(string database, CancellationToken ct) =>
        TerminateConnectionsAsync(AdminConnectionString, database, ct);

    private static Task TerminateConnectionsAsync(string adminConnectionString, string database, CancellationToken ct) =>
        ExecuteAsync(
            adminConnectionString,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid()",
            ct);

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
