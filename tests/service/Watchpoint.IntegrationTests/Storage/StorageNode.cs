using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Logging;

namespace Watchpoint.IntegrationTests.Storage;

/// <summary>The storage part of a node, configured for one test database.</summary>
internal sealed class StorageNode : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public StorageNode(string connectionString, params (string Key, string? Value)[] extra)
    {
        var cs = new NpgsqlConnectionStringBuilder(connectionString);
        var values = new Dictionary<string, string?>
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
        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(Logs));
        services.AddWatchpointStorage(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), isDevelopment: true);
        services.AddPostgresStorage();
        services.AddModuleStorage<TestModContext>("TestMod", p => p.Add<TestModPostgresContext>("postgres"));
        _services = services.BuildServiceProvider();
    }

    public LogCollector Logs { get; } = new();

    public IStorageStartup Startup => _services.GetRequiredService<IStorageStartup>();

    public IStoreConnectionFactory Connections => _services.GetRequiredService<IStoreConnectionFactory>();

    public IStorageProvider Provider => Connections.GetProvider(StoreKind.Configuration);

    public DatabaseDefinition Database => Connections.GetDatabase(StoreKind.Configuration);

    public async Task<HealthStatus> CheckHealthAsync(CancellationToken ct) =>
        (await _services.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains("ready"), ct)).Status;

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        Logs.Dispose();
    }
}
