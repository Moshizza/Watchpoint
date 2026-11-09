using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.IntegrationTests.Model;

[Trait("layer", "I")]
[Trait("spec", "MOD")]
public sealed class ModelIntegrationTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task MOD_I_001_Documents_persisted_on_registration()
    {
        var db = await DatabaseAsync(nameof(MOD_I_001_Documents_persisted_on_registration));
        await using var node = await NodeAsync(db);

        var pkg = PackageId.FromTrusted("vendor.test");
        var pkgVer = SemVersion.Parse("1.0.0").Value;

        var typeDoc = new DeviceTypeDocument(
            TypeId.FromTrusted("vendor.test.cam"),
            pkgVer,
            TypeRole.Device,
            Category.Video,
            null,
            [new CapabilityUse(CapabilityId.FromTrusted("health"), SemVersionRange.Parse("^1.0").Value)],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("icon", "symbol", "health.connectivity"),
            null,
            null,
            null);

        var pkgDocs = new PackageTypeDocuments(
            [],
            [typeDoc],
            new Dictionary<string, JsonDocument>(),
            new Dictionary<string, ReadOnlyMemory<byte>>());

        var regRes = await node.Catalog.RegisterPackageAsync(pkg, pkgVer, pkgDocs, Ct);
        regRes.IsSuccess.ShouldBeTrue();

        var count = await ScalarAsync<long>(db.ConnectionString, "SELECT count(*) FROM wp_model.documents WHERE source = 'vendor.test'");
        count.ShouldBe(1);

        var sha256 = await ScalarAsync<string>(db.ConnectionString, "SELECT sha256 FROM wp_model.documents WHERE id = 'vendor.test.cam'");
        sha256.Length.ShouldBe(64);
    }

    [Fact]
    public async Task MOD_I_002_Historical_type_resolution_after_restart()
    {
        var db = await DatabaseAsync(nameof(MOD_I_002_Historical_type_resolution_after_restart));

        var pkg = PackageId.FromTrusted("vendor.test");
        var pkgVer = SemVersion.Parse("1.0.0").Value;
        var typeId = TypeId.FromTrusted("vendor.test.cam");
        var typeRef = new TypeRef(typeId, pkgVer);

        var typeDoc = new DeviceTypeDocument(
            typeId,
            pkgVer,
            TypeRole.Device,
            Category.Video,
            null,
            [new CapabilityUse(CapabilityId.FromTrusted("health"), SemVersionRange.Parse("^1.0").Value)],
            null,
            [],
            LicenceClass.Free,
            new TypeDisplay("icon", "symbol", "health.connectivity"),
            null,
            null,
            null);

        var pkgDocs = new PackageTypeDocuments(
            [],
            [typeDoc],
            new Dictionary<string, JsonDocument>(),
            new Dictionary<string, ReadOnlyMemory<byte>>());

        await using (var first = await NodeAsync(db))
        {
            await first.Catalog.RegisterPackageAsync(pkg, pkgVer, pkgDocs, Ct);
            await first.Catalog.UnregisterPackageAsync(pkg, pkgVer, Ct);
        }

        // Restart node: package is not re-registered, but type should be resolvable through DB copy
        await using var second = await NodeAsync(db);
        var getRes = second.Catalog.Get(typeRef);
        getRes.IsSuccess.ShouldBeTrue();
        getRes.Value.Source.ShouldContain("(removed)");
        getRes.Value.Ref.Id.ShouldBe(typeId);
    }

    [Fact]
    public async Task MOD_I_003_Symbol_endpoint_headers()
    {
        var db = await DatabaseAsync(nameof(MOD_I_003_Symbol_endpoint_headers));
        await using var node = await NodeAsync(db);

        // MOD owns GET /api/v1/symbols/{symbolId}/{state}.svg
        // Verify symbol retrieval from ITypeResources conforms to the spec response
        var symbolRes = node.Resources.GetSymbol("ac.door", OverallState.Normal);
        symbolRes.IsSuccess.ShouldBeTrue();
        symbolRes.Value.Length.ShouldBeGreaterThan(0);

        // Verify fallback to default.svg for unmapped state if needed
        var defaultRes = node.Resources.GetSymbol("ac.door", OverallState.Maintenance);
        defaultRes.IsSuccess.ShouldBeTrue();
        defaultRes.Value.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task MOD_I_004_i18n_endpoint_caching()
    {
        var db = await DatabaseAsync(nameof(MOD_I_004_i18n_endpoint_caching));
        await using var node = await NodeAsync(db);

        // Verify translation bundle and ETag generation
        var bundle = node.Resources.GetTranslationBundle("en", out var etag);
        bundle.Length.ShouldBeGreaterThan(0);
        etag.ShouldNotBeNullOrWhiteSpace();

        // Second call with same state returns same ETag
        node.Resources.GetTranslationBundle("en", out var secondEtag);
        secondEtag.ShouldBe(etag);
    }

    private async Task<PostgresDatabase> DatabaseAsync(string name)
    {
        var db = await postgres.CreateDatabaseAsync(nameof(ModelIntegrationTests) + "." + name, Ct);
        _owned.Add(db);
        return db;
    }

    private static async Task<Node> NodeAsync(PostgresDatabase db)
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
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var node = new Node();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(node.Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<TimeProvider>(node.Clock);
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddWatchpointModel();
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

        public ITypeCatalog Catalog => Services.GetRequiredService<ITypeCatalog>();

        public ITypeResources Resources => Services.GetRequiredService<ITypeResources>();

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Logs.Dispose();
        }
    }
}
