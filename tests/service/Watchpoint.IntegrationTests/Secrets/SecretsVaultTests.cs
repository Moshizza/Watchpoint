using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Secrets.Internal;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.IntegrationTests.Secrets;

[Trait("layer", "I")]
[Trait("spec", "SEC")]
public sealed class SecretsVaultTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private static readonly SecretOwner _owner = new("Integrations", Guid.CreateVersion7());
    private readonly string _data = Path.Combine(Path.GetTempPath(), "wp-sec-i-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    [Fact]
    public async Task SEC_I_001_Vault_persists_and_reads_after_restart()
    {
        var db = await DatabaseAsync(nameof(SEC_I_001_Vault_persists_and_reads_after_restart));
        SecretId id;
        await using (var first = await NodeAsync(db))
        {
            id = (await first.Vault.CreateAsync("Salto A password", SecretKind.Password, _owner, "s3cret-1"u8.ToArray(), null, Ct)).Value;
        }

        await using var second = await NodeAsync(db);
        using var value = (await second.Vault.ReadAsync(id, _owner, "connect", Ct)).Value;

        value.UseAsString(s => s).ShouldBe("s3cret-1");
    }

    [Fact]
    public async Task SEC_I_002_Rewrap_to_a_new_master_key()
    {
        var db = await DatabaseAsync(nameof(SEC_I_002_Rewrap_to_a_new_master_key));
        await using var node = await NodeAsync(db);
        var ids = new List<SecretId>();
        for (var i = 0; i < 50; i++)
        {
            ids.Add((await node.Vault.CreateAsync($"secret {i}", SecretKind.Generic, _owner, Encoding.UTF8.GetBytes($"value-{i}"), null, Ct)).Value);
        }

        var keys = node.Services.GetRequiredService<MasterKeyAccessor>();
        var oldKey = keys.Ring.Current;
        using var newKey = MasterKey.Generate();
        keys.Use(new MasterKeyRing(newKey, [oldKey]));

        (await node.Vault.RewrapAllAsync(Ct)).Value.ShouldBe(50);

        keys.Use(new MasterKeyRing(newKey)); // old key removed
        for (var i = 0; i < ids.Count; i++)
        {
            using var value = (await node.Vault.ReadAsync(ids[i], _owner, "check", Ct)).Value;
            value.UseAsString(s => s).ShouldBe($"value-{i}");
        }

        (await ScalarAsync<long>(db.ConnectionString, $"SELECT count(*) FROM wp_secrets.secrets WHERE key_id = '{newKey.KeyId}'")).ShouldBe(50);
        (await node.Vault.RewrapAllAsync(Ct)).Value.ShouldBe(0);
        node.Logs.WithEventId(2220).Count().ShouldBe(2);
    }

    [Fact]
    public async Task SEC_I_003_Database_dump_does_not_contain_plaintext()
    {
        var db = await DatabaseAsync(nameof(SEC_I_003_Database_dump_does_not_contain_plaintext));
        await using var node = await NodeAsync(db);
        await node.Vault.CreateAsync("dump test", SecretKind.Password, _owner, "UniqueP@ss123"u8.ToArray(), null, Ct);

        // Textual dump of every row of the schema (what pg_dump would write for this table) and the raw bytes of every bytea.
        var dump = await ScalarAsync<string>(db.ConnectionString, "SELECT string_agg(t::text, E'\\n') FROM wp_secrets.secrets t");
        var raw = await ScalarAsync<byte[]>(db.ConnectionString,
            "SELECT string_agg(wrapped_dek || dek_nonce || ciphertext || nonce, ''::bytea) FROM wp_secrets.secrets");

        dump.ShouldNotContain("UniqueP@ss123");
        dump.ShouldNotContain(Convert.ToHexStringLower("UniqueP@ss123"u8));
        raw.AsSpan().IndexOf("UniqueP@ss123"u8).ShouldBe(-1);
    }

    [Fact]
    public async Task SEC_I_004_No_secret_in_logs()
    {
        var db = await DatabaseAsync(nameof(SEC_I_004_No_secret_in_logs));
        await using var node = await NodeAsync(db);
        var detector = new SecretLeakDetector();
        detector.Register("Leak-Me-If-You-Can-1");
        detector.Register("Leak-Me-If-You-Can-2");

        var id = (await node.Vault.CreateAsync("leak test", SecretKind.ApiKey, _owner, "Leak-Me-If-You-Can-1"u8.ToArray(), null, Ct)).Value;
        using (var value = (await node.Vault.ReadAsync(id, _owner, "connect", Ct)).Value)
        {
            value.UseAsString(s => s.Length).ShouldBe(20);
        }

        (await node.Vault.ReplaceAsync(id, _owner, "Leak-Me-If-You-Can-2"u8.ToArray(), null, Ct)).IsSuccess.ShouldBeTrue();
        using (var value = (await node.Vault.ReadAsync(id, _owner, "connect", Ct)).Value)
        {
            value.ToString().ShouldBe("***");
        }

        await node.Vault.ReadAsync(id, new SecretOwner("Other", null), "probe", Ct);
        await node.Vault.DeleteAsync(id, _owner, Ct);

        node.Logs.Entries.Count.ShouldBeGreaterThan(5);
        detector.AssertNoLeak(node.Logs);
    }

    [Fact]
    public async Task SEC_I_005_Last_accessed_at_throttled()
    {
        var db = await DatabaseAsync(nameof(SEC_I_005_Last_accessed_at_throttled));
        await using var node = await NodeAsync(db);
        var id = (await node.Vault.CreateAsync("throttle", SecretKind.Token, _owner, "t"u8.ToArray(), null, Ct)).Value;
        var row = $"SELECT xmin::text FROM wp_secrets.secrets WHERE id = '{id.Value:D}'";

        await ReadAsync(node, id);
        var afterFirst = await ScalarAsync<string>(db.ConnectionString, row);
        for (var i = 0; i < 99; i++)
        {
            node.Clock.Advance(TimeSpan.FromMilliseconds(500)); // 100 reads within a minute
            await ReadAsync(node, id);
        }

        (await ScalarAsync<string>(db.ConnectionString, row)).ShouldBe(afterFirst); // one update only
        (await node.Vault.GetMetadataAsync(id, Ct)).Value.LastAccessedAt.ShouldBe(TestClock.DefaultStart);

        node.Clock.Advance(TimeSpan.FromSeconds(11));
        await ReadAsync(node, id);
        (await ScalarAsync<string>(db.ConnectionString, row)).ShouldNotBe(afterFirst);
        (await node.Vault.GetMetadataAsync(id, Ct)).Value.Version.ShouldBe(1);
    }

    private static async Task ReadAsync(Node node, SecretId id)
    {
        using var value = (await node.Vault.ReadAsync(id, _owner, "poll", Ct)).Value;
    }

    private async Task<PostgresDatabase> DatabaseAsync(string name)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(SecretsVaultTests) + "." + name, Ct);
        _owned.Add(db);
        return db;
    }

    private async Task<Node> NodeAsync(PostgresDatabase db)
    {
        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
            ["Secrets:MasterKeyProvider"] = "file",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var node = new Node();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(node.Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(node.Clock);
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointSecrets(configuration, o => (o.DataDirectory, o.NodeId) = (_data, "node-1"));
        node.Services = services.BuildServiceProvider();
        await node.Services.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        return node;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Node : IAsyncDisposable
    {
        public LogCollector Logs { get; } = new();

        public TestClock Clock { get; } = new();

        public ServiceProvider Services { get; set; } = null!;

        public ISecretVault Vault => Services.GetRequiredService<ISecretVault>();

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
