using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchpoint.Api.Contracts;
using Watchpoint.Host.Modules;
using Watchpoint.Hosting;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Testing;
using Watchpoint.IntegrationTests.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;

namespace Watchpoint.IntegrationTests.Integrations;

/// <summary>
/// A node in this process composed with the host's own modules (Secrets, Storage, Model, Registry, Pipeline, Integration
/// runtime) on PostgreSQL and NATS, its start-up tasks run in phase order (as the host's orchestrator does). The builtin
/// folder holds the simulator and the test <see cref="RelayPackage"/>, signed with a development key. A restart keeps the
/// database, the NATS server and the data folder.
/// </summary>
internal sealed class ConnectionNode : IAsyncDisposable
{
    private static readonly IWatchpointModule[] _modules =
        [new SecretsModule(), new StorageModule(), new ModelModule(), new RegistryModule(), new PipelineModule(), new IntegrationRuntimeModule()];

    private readonly Guid _siteId = Guid.CreateVersion7();
    private readonly NodeOptions _options;
    private WebApplication? _app;

    private ConnectionNode(PostgresDatabase database, NatsContainerFixture nats, string data, NodeOptions options)
    {
        Database = database;
        Nats = nats;
        Data = data;
        _options = options;
    }

    public PostgresDatabase Database { get; }

    public NatsContainerFixture Nats { get; }

    public string Data { get; }

    public LogCollector Logs { get; private set; } = new();

    public IServiceProvider Services => _app!.Services;

    public IConnectionManager Connections => Services.GetRequiredService<IConnectionManager>();

    public IRegistryReader Registry => Services.GetRequiredService<IRegistryReader>();

    public Uri BaseAddress => new(Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/");

    public LocationId Root => Registry.GetLocationTree().Single(l => l.ParentId is null).Id;

    public static Task<ConnectionNode> StartAsync(PostgresContainerFixture postgres, string name, CancellationToken ct) =>
        StartAsync(postgres, name, new NodeOptions(), ct);

    /// <summary>Starts a node with more modules (after the runtime's), settings and services registered before the modules.</summary>
    public static async Task<ConnectionNode> StartAsync(PostgresContainerFixture postgres, string name, NodeOptions options, CancellationToken ct)
    {
        var database = await postgres.CreateDatabaseAsync(nameof(ConnectionNode) + "." + name, ct);
        var nats = NatsContainerFixture.WithOptions(new ContainerFixtureOptions());
        await nats.StartAsync(ct);
        var data = Directory.CreateTempSubdirectory("wp-irt-c-").FullName;
        using (var key = new TestKey("dev-irt"))
        {
            key.WriteDevelopmentKey(Path.Combine(data, "integrations", "dev-keys"));
            SimulatorPackage.Stage(AppContext.BaseDirectory, Path.Combine(data, "builtin", "simulator"), key);
            RelayPackage.Package().WriteFolder(Path.Combine(data, "builtin", "relay"), key);
        }

        var node = new ConnectionNode(database, nats, data, options);
        await node.StartAsync(ct);
        return node;
    }

    /// <summary>Stops the node (shutdown tasks, then the host) and starts it again on the same database, NATS server and folder.</summary>
    public async Task RestartAsync(CancellationToken ct)
    {
        await StopAsync(ct);
        await StartAsync(ct);
    }

    public HttpClient Http() => new HttpClient { BaseAddress = BaseAddress }.WithAdminToken();

    /// <summary>The node's warnings and errors so far (diagnostics for a failed wait).</summary>
    public string Problems() => string.Join(Environment.NewLine, Logs.Entries
        .Where(e => e.Level >= LogLevel.Warning)
        .TakeLast(40)
        .Select(e => $"{e.Level} {e.EventId} {e.Category}: {e.RenderedMessage} {e.Exception?.GetType().Name} {e.Exception?.GetBaseException().Message}"));

    /// <summary>Reads every record of the log (all partitions, to the end).</summary>
    public async Task<IReadOnlyList<LogRecord>> ReadLogAsync(CancellationToken ct)
    {
        var reader = Services.GetRequiredService<IEventLogReader>();
        var records = new List<LogRecord>();
        await foreach (var record in reader.ReadPartitionsAsync(Enumerable.Range(0, Partitioning.PartitionCount).ToHashSet(), 1, ct))
        {
            records.Add(record);
        }

        return records;
    }

    public async Task<T?> ScalarAsync<T>(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? default : (T)result;
    }

    public async Task<byte[]?> StoreValueAsync(ConnectionId connection, string key, CancellationToken ct) =>
        await ScalarAsync<byte[]>("SELECT value FROM wp_integrations.connection_store WHERE connection_id = @c AND key = @k", ct, ("c", connection.Value), ("k", key));

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        await Nats.DisposeAsync();
        await Database.DisposeAsync();
        Logs.Dispose();
        try
        {
            Directory.Delete(Data, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        Logs = new LogCollector();
        var cs = new NpgsqlConnectionStringBuilder(Database.ConnectionString);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
            ["Log:MaxBytes"] = "1GB",
            ["Log:AllowInsecureRemote"] = "true",
            ["Integrations:BuiltinPath"] = Path.Combine(Data, "builtin"),
            ["Secrets:MasterKeyProvider"] = "file",
        });
        builder.Configuration.AddInMemoryCollection(_options.Settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(Logs).SetMinimumLevel(LogLevel.Debug);
        var settings = new NodeSettings
        {
            Node = new NodeSection { Environment = NodeEnvironment.Development, Id = Guid.CreateVersion7(), Name = "irt" },
            Site = new SiteSection { Id = _siteId },
        };
        var paths = new NodePaths(Data, Path.Combine(Data, "logs"), Path.Combine(Data, "packages"), Path.Combine(Data, "bin"));
        paths.EnsureDirectories();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(new NatsEndpoint(Nats.Url, null));
        var context = new ModuleContext { Node = settings, Configuration = builder.Configuration, Paths = paths, IsDevelopment = true };
        _options.Services?.Invoke(builder.Services);
        IWatchpointModule[] modules = [.. _modules, .. _options.Modules];
        foreach (var module in modules)
        {
            module.ConfigureServices(builder.Services, context);
        }

        builder.Services.AddTestApi(builder.Configuration);
        _app = builder.Build();
        _app.UseWatchpointApi();
        foreach (var module in modules)
        {
            module.MapEndpoints(_app);
        }

        await _app.StartAsync(ct);
        foreach (var task in _app.Services.GetServices<IStartupTask>().OrderBy(t => t.Phase).ThenBy(t => t.Order))
        {
            if (task.Phase == StartupPhase.Connections)
            {
                await EnsureLocationsAsync(ct);
            }

            await task.ExecuteAsync(ct);
        }
    }

    private async Task EnsureLocationsAsync(CancellationToken ct)
    {
        if (Registry.GetLocationTree().Count > 0)
        {
            return;
        }

        var result = await Services.GetRequiredService<IRegistryWriter>().ApplyAsync(new RegistryChangeBatch(
        [
            new RegistryChange.UpdateSite(Registry.Site with { Name = LocalizedName.Of("IRT site"), DefaultTimezone = "UTC" }),
            new RegistryChange.UpsertLocation(new Location(LocationId.New(), null, LocalizedName.Of("HQ"), LocationKind.Site, string.Empty, 0, null, null, null, 0, 0)),
        ]), new ChangeContext(null, "test"), ct);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error.Code.Value);
        }
    }

    private async Task StopAsync(CancellationToken ct)
    {
        if (_app is null)
        {
            return;
        }

        foreach (var task in _app.Services.GetServices<IShutdownTask>().OrderByDescending(t => t.Phase))
        {
            await task.ExecuteAsync(ct);
        }

        await _app.StopAsync(ct);
        await _app.DisposeAsync();
        _app = null;
    }
}

/// <summary>What a <see cref="ConnectionNode"/> adds to the runtime's modules.</summary>
/// <param name="Modules">More host modules, configured after the runtime's.</param>
/// <param name="Settings">More configuration.</param>
/// <param name="Services">Services registered before the modules (their <c>TryAdd</c> defaults then yield).</param>
internal sealed record NodeOptions(IReadOnlyList<IWatchpointModule> Modules, IReadOnlyDictionary<string, string?> Settings, Action<IServiceCollection>? Services)
{
    public NodeOptions()
        : this([], new Dictionary<string, string?>(), null)
    {
    }
}

/// <summary>
/// A test package whose connector (compiled at test time) exercises the runtime: it describes one server and one door,
/// counts its starts in its store (<c>failStarts</c> first starts throw), reports five numbered events per start from a
/// stored cursor (source time = epoch + n s; flush before the cursor is saved, INT S4), registers a webhook
/// authenticated with its <c>password</c> secret, and its test checks that the stored secret equals <c>expectedPassword</c>.
/// </summary>
internal static class RelayPackage
{
    public const string Id = "vendor.relay";
    public const string ServerType = "vendor.relay.server";
    public static readonly DateTimeOffset WebhookEventTime = DateTimeOffset.UnixEpoch.AddSeconds(1_000_000);

    public static TestPackage Package()
    {
        var package = new TestPackage(Id);
        var source = $$"""
            using System;
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Watchpoint.Integration.Abstractions;
            using Watchpoint.Integration.Abstractions.Context;
            using Watchpoint.Integration.Abstractions.Model;
            using Watchpoint.Kernel.Results;
            using Watchpoint.Kernel.Values;

            namespace {{package.AssemblyName}};

            [Connector("{{ServerType}}")]
            public sealed class TestConnector : IConnector
            {
                private static readonly DeviceKey Door = new("root", "door-1");
                private IConnectorContext _ctx = null!;
                private CancellationTokenSource? _cts;
                private Task? _loop;
                private InboundRegistration? _webhook;

                public ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
                {
                    _ctx = context;
                    return ValueTask.FromResult(Result.Success());
                }

                public async ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct)
                {
                    using var secret = await _ctx.Secrets.GetAsync("password", ct);
                    var expected = _ctx.Settings.TryGetProperty("expectedPassword", out var e) ? e.GetString() : null;
                    return secret is not null && secret.UseAsString(s => s == expected)
                        ? ConnectionTestResult.Succeeded()
                        : ConnectionTestResult.Failed(FailureCategory.AuthenticationFailed, ConnectorMessage.Of("wrongPassword"));
                }

                public async ValueTask StartAsync(CancellationToken ct)
                {
                    var bytes = await _ctx.Store.GetAsync("starts", ct);
                    var starts = (bytes is null ? 0 : BitConverter.ToInt32(bytes)) + 1;
                    await _ctx.Store.SetAsync("starts", BitConverter.GetBytes(starts), ct);
                    var failStarts = _ctx.Settings.TryGetProperty("failStarts", out var f) ? f.GetInt32() : 0;
                    if (starts <= failStarts)
                    {
                        throw new InvalidOperationException("planned start failure " + starts);
                    }

                    _webhook = await _ctx.Inbound.RegisterHttpAsync("events", new InboundAuth.SharedSecretHeader("X-Webhook-Token", "password"), async (request, token) =>
                    {
                        _ctx.Sink.ReportEvent(Door, "doorPosition.forced", null, DateTimeOffset.UnixEpoch.AddSeconds(1_000_000));
                        await _ctx.Sink.FlushAsync(token);
                        return InboundResponse.NoContent;
                    }, ct);
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Stopping);
                    _loop = Task.Run(() => RunAsync(_cts.Token));
                    _ctx.Health.Report(ConnectionStatus.Connected);
                }

                private async Task RunAsync(CancellationToken ct)
                {
                    try
                    {
                        while (!_ctx.Devices.TryGet(Door, out _))
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(50), _ctx.Time, ct);
                        }

                        var bytes = await _ctx.Store.GetAsync("cursor", ct);
                        var cursor = bytes is null ? 0L : BitConverter.ToInt64(bytes);
                        for (var n = cursor + 1; n <= cursor + 5; n++)
                        {
                            _ctx.Sink.ReportEvent(Door, "doorPosition.forced", null, DateTimeOffset.UnixEpoch.AddSeconds(n));
                        }

                        await _ctx.Sink.FlushAsync(ct);
                        await _ctx.Store.SetAsync("cursor", BitConverter.GetBytes(cursor + 5), ct);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                public async ValueTask StopAsync(CancellationToken ct)
                {
                    _cts?.Cancel();
                    if (_loop is not null)
                    {
                        try
                        {
                            await _loop.WaitAsync(ct);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    if (_webhook is not null)
                    {
                        await _webhook.DisposeAsync();
                    }
                }

                public ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct) => ValueTask.FromResult(new InventorySnapshot(
                    new[] { new InventoryServerItem("root", null, "generic.server", "1.0", "Relay", new Dictionary<string, WpValue>()) },
                    new[] { new InventoryDeviceItem("door-1", "root", null, "ac.door", "1.0", "Door 1", new Dictionary<string, WpValue>(), Array.Empty<string>(), Array.Empty<InventoryLinkItem>()) },
                    true));

                public ValueTask DisposeAsync() => default;
            }
            """;
        var image = TestAssemblies.Compile(package.AssemblyName, source);
        return package.WithConnectorAssembly(image, package.ConnectorClass, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["expectedPassword"] = new JsonObject { ["type"] = "string" },
                ["failStarts"] = new JsonObject { ["type"] = "integer" },
            },
        });
    }
}
