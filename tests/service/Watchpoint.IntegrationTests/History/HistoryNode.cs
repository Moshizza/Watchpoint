using Microsoft.Extensions.Logging;
using Npgsql;
using Watchpoint.History;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal;
using Watchpoint.History.Internal.Lag;
using Watchpoint.History.Internal.Policies;
using Watchpoint.History.Internal.Queries;
using Watchpoint.History.Internal.Writers;
using Watchpoint.IntegrationTests.Pipeline;
using Watchpoint.IntegrationTests.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;

namespace Watchpoint.IntegrationTests.History;

/// <summary>History on a real PostgreSQL database and a real NATS event log: the store, writers and queries of one node.</summary>
internal sealed class HistoryNode : IAsyncDisposable
{
    public static readonly TypeRef DoorType = new(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0));

    private readonly StorageNode _storage;
    private readonly ILoggerFactory _loggers;
    private readonly HistoryMetrics _metrics = new();
    private readonly List<HistoryService> _services = [];

    private HistoryNode(PostgresDatabase database, PipelineNode pipeline, HistorySettings settings, StorageNode storage)
    {
        Database = database;
        Pipeline = pipeline;
        Settings = settings;
        _storage = storage;
        Store = storage.Provider.CreateHistoryStore(storage.Database);
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Queries = new HistoryQueries(Store, new NoTags(), settings);

        // The service registers the kinds when it is built; a test appends before that.
        HistoryJson.RegisterKinds(pipeline.Registry);

        // CMD registers its own bodies in the host; history reads the JSON, so a test appends it as it is.
        foreach (var kind in CommandKinds)
        {
            pipeline.Registry.Register(kind, 1, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<System.Text.Json.JsonElement>)System.Text.Json.JsonSerializerOptions.Default.GetTypeInfo(typeof(System.Text.Json.JsonElement)));
        }
    }

    public PostgresDatabase Database { get; }

    public PipelineNode Pipeline { get; }

    public HistorySettings Settings { get; }

    public IHistoryStore Store { get; }

    public IHistoryQueries Queries { get; }

    public LogCollector Logs { get; } = new();

    public Dictionary<DeviceId, HistorySource> Devices { get; } = [];

    public static IReadOnlyList<string> CommandKinds { get; } =
    [
        "command.requested", "command.rejected", "command.awaitingApproval", "command.approved", "command.approvalRejected", "command.queued",
        "command.dispatched", "command.succeeded", "command.failed", "command.timedOut", "command.expired", "command.cancelled", "command.verified",
        "command.unverified",
    ];

    public static HistorySettings FastSettings { get; } = new()
    {
        MaxWait = TimeSpan.FromMilliseconds(200),
        RetryDelayMin = TimeSpan.FromMilliseconds(100),
        RetryDelayMax = TimeSpan.FromMilliseconds(500),
        LagWarning = TimeSpan.FromSeconds(1),
    };

    public static Task<HistoryNode> CreateAsync(PostgresDatabase database, PipelineNode pipeline, HistorySettings? settings = null) =>
        Task.FromResult(new HistoryNode(database, pipeline, settings ?? FastSettings, new StorageNode(database.ConnectionString)));

    /// <summary>A device known to the enrichment, with the location chain root → location.</summary>
    public DeviceId AddDevice(int partition, Guid? location = null, string name = "Door")
    {
        var id = PipelineNode.DeviceIn(partition);
        var loc = location ?? Guid.CreateVersion7();
        Devices[id] = new HistorySource(id.Value, false, name, Guid.CreateVersion7(), loc, [Guid.Empty, loc], ["Site", "Floor"], ["entrance"], DoorType, "ac.door");
        return id;
    }

    /// <summary>Starts the history service; <paramref name="factory"/> replaces the consumer factory (to lose an acknowledgement).</summary>
    public async Task<HistoryService> StartServiceAsync(Func<ILogConsumerFactory, ILogConsumerFactory>? factory = null, CancellationToken ct = default)
    {
        var registry = Pipeline.Registry;
        var enrichment = new DictionaryEnrichment(Devices);
        var policies = new PolicyResolver(new NoCatalog(), Store, Settings, TimeProvider.System);
        var writers = new HistoryWriters(registry, enrichment, new NoCatalog(), Store, policies, new LastStoredCache(1000), _loggers);
        var lag = new LagMonitor(Settings, TimeProvider.System, _metrics, _loggers.CreateLogger<LagMonitor>(), Pipeline.Writer, registry);
        var consumers = factory?.Invoke(Pipeline.Consumers) ?? Pipeline.Consumers;
        var runner = new ConsumerRunner(consumers, registry, Settings, TimeProvider.System, _metrics, lag, _loggers.CreateLogger<ConsumerRunner>());
        var service = new HistoryService(Store, writers, runner, Settings, TimeProvider.System, registry, _loggers.CreateLogger<HistoryService>(), _loggers);
        _services.Add(service);
        await service.StartAsync(ct);
        return service;
    }

    public async Task<long> CountAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Waits until a count query returns at least <paramref name="expected"/>, polling for at most <paramref name="timeout"/>.</summary>
    public async Task<long> WaitForCountAsync(string sql, long expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = TimeProvider.System.GetUtcNow() + timeout;
        long count = -1;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            try
            {
                count = await CountAsync(sql, ct);
            }
            catch (NpgsqlException)
            {
                // The database may be restarting or the schema not created yet.
            }

            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        return count;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        _metrics.Dispose();
        _loggers.Dispose();
        await _storage.DisposeAsync();
        Logs.Dispose();
    }

    private sealed class DictionaryEnrichment(Dictionary<DeviceId, HistorySource> devices) : IHistoryEnrichment
    {
        public HistorySource? ForDevice(DeviceId device) => devices.GetValueOrDefault(device);

        public HistorySource? ForServer(ServerId server) => null;
    }

    private sealed class NoTags : IHistoryTagKeys
    {
        public IReadOnlyList<string> KeysOf(IReadOnlyCollection<TagId> tags) => [];
    }

    /// <summary>A catalogue that knows no type: every policy comes from the overrides table or defaults to every change.</summary>
    private sealed class NoCatalog : ITypeCatalog
    {
        public long Revision => 1;

        public event EventHandler<CatalogChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Result<DeviceTypeDefinition> Get(TypeRef type) => Error.Create(ErrorKind.NotFound, "model.type.not_found");

        public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotSupportedException();

        public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => throw new NotSupportedException();

        public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();

        public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>Wraps a consumer factory so that the first batch is stored but never acknowledged: the process "dies" between commit and acknowledgement.</summary>
internal sealed class CrashBeforeAckFactory(ILogConsumerFactory inner, Action crash) : ILogConsumerFactory
{
    public ILogConsumer CreateDurable(string name, LogStream stream, IReadOnlyList<string> kindFilter, LogConsumerOptions options) =>
        new Consumer(inner.CreateDurable(name, stream, kindFilter, options), crash);

    private sealed class Consumer(ILogConsumer inner, Action crash) : ILogConsumer
    {
        public async IAsyncEnumerable<LogRecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var batch in inner.ReadBatchesAsync(ct))
            {
                yield return batch with
                {
                    AckAsync = _ =>
                    {
                        crash();
                        throw new OperationCanceledException();
                    },
                };
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
