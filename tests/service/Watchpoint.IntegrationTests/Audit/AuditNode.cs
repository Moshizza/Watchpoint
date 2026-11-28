using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.IntegrationTests.Storage;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Containers;

namespace Watchpoint.IntegrationTests.Audit;

/// <summary>An audit database for a test: from the container fixture, or from the server named by <c>WATCHPOINT_TEST_PG_ADMIN</c> (local development without Docker).</summary>
internal sealed class AuditDatabase : IAsyncDisposable
{
    private const string LocalServerVariable = "WATCHPOINT_TEST_PG_ADMIN";

    private readonly PostgresDatabase? _container;
    private readonly string? _localAdmin;
    private readonly string _name;

    private AuditDatabase(string connectionString, string name, PostgresDatabase? container, string? localAdmin)
    {
        ConnectionString = connectionString;
        _name = name;
        _container = container;
        _localAdmin = localAdmin;
    }

    public string ConnectionString { get; }

    public static async Task<AuditDatabase> CreateAsync(PostgresContainerFixture postgres, string testName, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(LocalServerVariable) is { Length: > 0 } admin)
        {
            var name = "audit_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync(ct);
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
                await create.ExecuteNonQueryAsync(ct);
            }

            return new AuditDatabase(new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString, name, null, admin);
        }

        var db = await postgres.CreateDatabaseAsync(testName, ct);
        return new AuditDatabase(db.ConnectionString, db.Name, db, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }

        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(_localAdmin);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>The audit module of one node on a real PostgreSQL database: store, writer, verifier and query.</summary>
internal sealed class AuditNode : IAsyncDisposable
{
    private readonly StorageNode _storage;
    private readonly HmacSigner _signer = new();
    private readonly AuditMetrics _metrics = new();

    private AuditNode(string connectionString, AuditSettings settings, Func<IAuditStore, IAuditStore>? wrap)
    {
        _storage = new StorageNode(connectionString);
        var real = _storage.Provider.CreateAuditStore(_storage.Database);
        Store = wrap?.Invoke(real) ?? real;
        Settings = settings;
        Writer = new AuditWriter(Store, settings, TimeProvider.System, NullLogger<AuditWriter>.Instance, _metrics);
        Verifier = new AuditVerifier(Store, _signer, settings, TimeProvider.System, _metrics);
        Query = new AuditQueryService(Store, new DefaultAuditLabelResolver());
        Checkpoints = new CheckpointService(Store, _signer, Writer, settings, TimeProvider.System, NullLogger<CheckpointService>.Instance);
    }

    public IAuditStore Store { get; }

    public AuditSettings Settings { get; }

    public AuditWriter Writer { get; }

    public AuditVerifier Verifier { get; }

    public AuditQueryService Query { get; }

    public CheckpointService Checkpoints { get; }

    public static async Task<AuditNode> StartAsync(string connectionString, string fallbackDirectory, Func<AuditSettings, AuditSettings>? tweak = null, Func<IAuditStore, IAuditStore>? wrap = null)
    {
        var settings = new AuditSettings { ChainId = "node-it", FallbackDirectory = fallbackDirectory, BatchDelay = TimeSpan.FromMilliseconds(5) };
        var node = new AuditNode(connectionString, tweak?.Invoke(settings) ?? settings, wrap);
        await node.Writer.StartAsync(CancellationToken.None);
        await node.Writer.Started.WaitAsync(TimeSpan.FromSeconds(30));
        return node;
    }

    public async ValueTask DisposeAsync()
    {
        await Writer.StopAsync(CancellationToken.None);
        await Writer.DisposeAsync();
        await _storage.DisposeAsync();
        _signer.Dispose();
        _metrics.Dispose();
    }

    /// <summary>Signs checkpoints with HMAC-SHA256; the signature scheme is not what these tests exercise.</summary>
    private sealed class HmacSigner : IAuditSigner, IDisposable
    {
        private readonly HMACSHA256 _hmac = new(RandomNumberGenerator.GetBytes(32));

        public ValueTask<(string KeyId, byte[] Signature)> SignAsync(byte[] data, CancellationToken ct) => ValueTask.FromResult(("it-key", _hmac.ComputeHash(data)));

        public ValueTask<bool> VerifyAsync(string keyId, byte[] data, byte[] signature, CancellationToken ct) =>
            ValueTask.FromResult(keyId == "it-key" && CryptographicOperations.FixedTimeEquals(_hmac.ComputeHash(data), signature));

        public void Dispose() => _hmac.Dispose();
    }
}
