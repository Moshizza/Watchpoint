using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Internal;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class StateStoreAndFactoryTests : IDisposable
{
    private readonly FakeStorageProvider _provider = new();
    private readonly SqliteConnection _keepAlive;
    private readonly LogCollector _logs = new();
    private readonly ServiceProvider _services;

    public StateStoreAndFactoryTests()
    {
        _keepAlive = new SqliteConnection(_provider.SqliteConnectionString);
        _keepAlive.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs));
        services.AddSingleton<TimeProvider>(new TestClock());
        services.AddSingleton<IStorageProvider>(_provider);
        services.AddWatchpointStorage(new ConfigurationBuilder().Build(), isDevelopment: true);
        services.AddModuleStorage<TestModuleContext>("TestMod", p => p.Add<TestModulePostgresContext>("postgres"));
        _services = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _services.Dispose();
        _keepAlive.Dispose();
        _logs.Dispose();
    }

    [Fact]
    public async Task STO_U_016_State_batch_over_5000_rejected()
    {
        var store = _services.GetRequiredService<IStateStore>();
        var items = Enumerable.Range(0, 5001).Select(i => Item("device", partition: 1)).ToList();

        var ex = await Should.ThrowAsync<StorageException>(() => store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 10, items, []), Ct));

        ex.Error.Code.Value.ShouldBe(StorageErrors.StateBatchTooLargeCode);
        ex.Error.Args["count"].ShouldBe(5001);
        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 10, items[..5000], []), Ct); // the limit itself is accepted
    }

    [Theory]
    [InlineData("Device")]
    [InlineData("a-kind-that-is-much-longer-than-32")]
    [InlineData("")]
    public async Task STO_U_017_State_kind_validation(string kind)
    {
        var store = _services.GetRequiredService<IStateStore>();

        await Should.ThrowAsync<ArgumentException>(() => store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 1, [Item(kind, 1)], []), Ct));
        Should.Throw<ArgumentException>(() => store.ReadItemsAsync(kind, [1], Ct));
        await store.WriteSnapshotAsync(new StateSnapshotBatch("state", 1, 1, [Item("rule-window", 1)], []), Ct);
    }

    [Fact]
    public async Task STO_U_018_Module_registration_requires_provider_context()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStorageProvider>(new FakeStorageProvider());
        services.AddWatchpointStorage(new ConfigurationBuilder().Build(), isDevelopment: true);
        services.AddModuleStorage<TestModuleContext>("Registry", p => p.Add<TestModulePostgresContext>("sqlserver"));
        await using var provider = services.BuildServiceProvider();

        var ex = await Should.ThrowAsync<StorageStartupException>(() => provider.GetRequiredService<IStorageStartup>().WaitForDatabasesAsync(Ct));

        ex.Error.Code.Value.ShouldBe(StorageErrors.ModuleContextMissingCode);
        ex.Error.Args["module"].ShouldBe("Registry");
        ex.Error.Args["provider"].ShouldBe("postgres");
        ex.ExitCode.ShouldBe(StorageStartupException.Config);
    }

    [Fact]
    public async Task STO_U_019_DbContext_factory_creates_provider_subclass()
    {
        await using var context = await _services.GetRequiredService<IDbContextFactory<TestModuleContext>>().CreateDbContextAsync(Ct);

        context.ShouldBeOfType<TestModulePostgresContext>();
        context.Schema.ShouldBe("wp_testmod");
    }

    [Fact]
    public async Task STO_U_020_NoTracking_default()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<TestModuleContext>>();
        var id = DeviceId.New();
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            await context.Database.EnsureCreatedAsync(Ct);
            context.Gadgets.Add(new Gadget { Id = id, Name = "a" });
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            (await context.Gadgets.SingleAsync(g => g.Id == id, Ct)).Name.ShouldBe("a");
            context.ChangeTracker.Entries().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task STO_U_021_LocalizedName_JSON_column_mapping()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<TestModuleContext>>();
        var id = DeviceId.New();
        var label = new LocalizedName("Lobby door", new Dictionary<string, string> { ["fr"] = "Porte du hall", ["ar"] = "باب الردهة" });
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            await context.Database.EnsureCreatedAsync(Ct);
            context.Gadgets.Add(new Gadget { Id = id, Label = label });
            await context.SaveChangesAsync(Ct);
        }

        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            var loaded = (await context.Gadgets.SingleAsync(g => g.Id == id, Ct)).Label.ShouldNotBeNull();
            loaded.Default.ShouldBe("Lobby door");
            loaded.Translations.ShouldBe(label.Translations, ignoreOrder: true);
        }

        await using var raw = new SqliteCommand("SELECT Label FROM gadgets", _keepAlive);
        using var json = System.Text.Json.JsonDocument.Parse((string)(await raw.ExecuteScalarAsync(Ct))!);
        json.RootElement.GetProperty("default").GetString().ShouldBe("Lobby door");
        json.RootElement.GetProperty("translations").GetProperty("ar").GetString().ShouldBe("باب الردهة");
    }

    [Fact]
    public async Task STO_U_022_IVersioned_mismatch_is_a_concurrency_error()
    {
        var factory = _services.GetRequiredService<IDbContextFactory<TestModuleContext>>();
        var id = DeviceId.New();
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            await context.Database.EnsureCreatedAsync(Ct);
            context.Gadgets.Add(new Gadget { Id = id, Name = "a" });
            await context.SaveChangesAsync(Ct);
        }

        await using var first = await factory.CreateDbContextAsync(Ct);
        await using var second = await factory.CreateDbContextAsync(Ct);
        var a = await first.Gadgets.AsTracking().SingleAsync(g => g.Id == id, Ct);
        var b = await second.Gadgets.AsTracking().SingleAsync(g => g.Id == id, Ct);
        a.Name = "first";
        await first.SaveChangesAsync(Ct);
        b.Name = "second";

        var ex = await Should.ThrowAsync<StorageException>(() => second.SaveChangesAsync(Ct));

        ex.Error.Code.Value.ShouldBe(StorageErrors.ConcurrencyConflictCode);
        ex.Error.Args["entity"].ShouldBe(nameof(Gadget));
    }

    [Fact]
    public async Task STO_U_023_Health_check_reports_provider_and_version()
    {
        var report = await _services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);

        var entry = report.Entries[StorageServiceCollectionExtensions.HealthCheckName];
        entry.Status.ShouldBe(HealthStatus.Healthy);
        entry.Data["provider"].ShouldBe("postgres");
        ((string)entry.Data["version"]).ShouldNotBeNullOrEmpty();
        entry.Data["default.provider"].ShouldBe("postgres");
    }

    [Fact]
    public async Task STO_U_024_Capabilities_TimescaleDB_probed()
    {
        _provider.Probe = _ => new DatabaseInfo("17.2", null, ["plpgsql"], 1);

        await _services.GetRequiredService<IStorageStartup>().WaitForDatabasesAsync(Ct);

        _provider.CapabilitiesFor(new DatabaseInfo("17.2", null, ["plpgsql"], 1)).TimeSeriesExtension.ShouldBeFalse();
        _logs.WithEventId(1203).ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
        var postgres = new PostgresProvider(new PostgresDataSources(new DevelopmentPasswordProvider()), new TestClock());
        postgres.CapabilitiesFor(new DatabaseInfo("17.2", null, ["plpgsql"], 1)).TimeSeriesExtension.ShouldBeFalse();
        postgres.CapabilitiesFor(new DatabaseInfo("17.2", null, ["timescaledb"], 1)).TimeSeriesExtension.ShouldBeTrue();
    }

    [Theory]
    [InlineData("15.4", false)]
    [InlineData("9.6.24", false)]
    [InlineData("16.0", true)]
    [InlineData("17.2 (Debian 17.2-1.pgdg120+1)", true)]
    public void STO_U_025_Version_below_16_rejected(string version, bool accepted)
    {
        var error = PostgresProvider.CheckVersion(version);

        if (accepted)
        {
            error.ShouldBeNull();
        }
        else
        {
            error.ShouldNotBeNull().Code.Value.ShouldBe(StorageErrors.UnsupportedVersionCode);
            error.Args["minimum"].ShouldBe("16");
            error.Args["actual"].ShouldBe(version);
        }
    }

    private static StateItem Item(string kind, int partition) =>
        new(kind, Guid.CreateVersion7(), partition, Encoding.UTF8.GetBytes("""{"a":1}"""), 1);
}
