using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Connections;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Logging;
using Watchpoint.Testing.Time;
using ConnectorStatus = Watchpoint.Integration.Abstractions.Context.ConnectionStatus;
using KernelConnectionStatus = Watchpoint.Integrations.Contracts.ConnectionStatus;

namespace Watchpoint.Integrations.Tests.Support;

/// <summary>
/// A connection manager on fakes (packages, host, ingest, registry, vault, MOD, event log) and a <see cref="TestClock"/>.
/// Connector instances are <see cref="FakeConnector"/>s whose behaviour each test sets.
/// </summary>
internal sealed class ConnectionHarness : IAsyncDisposable
{
    public const string ServerType = "acme.server";
    public static readonly PackageId Package = PackageId.FromTrusted("vendor.acme");
    public static readonly SemVersion Version = new(1, 0, 0);

    private readonly LoggerFactory _loggers;
    private readonly ConcurrentDictionary<ConnectionId, List<ConnectionStatusChangedEventArgs>> _transitions = new();
    private readonly List<(ConnectionId Id, ConnectionState State, TaskCompletionSource<KernelConnectionStatus> Done)> _waiters = [];
    private readonly Lock _lock = new();
    private ConnectionManager? _manager;

    public ConnectionHarness(Action<ConnectionsOptions>? configure = null)
    {
        _loggers = new LoggerFactory([Logs]);
        Options = new ConnectionsOptions { RestartJitter = 0 };
        configure?.Invoke(Options);
        Router = new InboundRouter(_loggers.CreateLogger<InboundRouter>());
        Meter = new ConnectorMeter();
    }

    public TestClock Clock { get; } = new();

    public LogCollector Logs { get; } = new();

    public ConnectionsOptions Options { get; }

    public FakePackages Packages { get; } = new();

    public FakeHost Host { get; } = new();

    public FakeIngest Ingest { get; } = new();

    public FakeRegistry Registry { get; } = new();

    public FakeVault Vault { get; } = new();

    public FakeTypes Types { get; } = new();

    public FakeEventLog EventLog { get; } = new();

    public InMemoryConnectionRepository Repository { get; } = new();

    public InboundRouter Router { get; }

    public ConnectorMeter Meter { get; }

    public ConnectorContextFactory Contexts => new(Ingest, Registry, Vault, Router, Meter, Options, Clock, _loggers);

    public ConnectionManager Manager => _manager ??= Build();

    public ILogger Logger => _loggers.CreateLogger("test");

    public ILoggerFactory Loggers => _loggers;

    public static ChangeContext Ctx { get; } = new(null, "test");

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public NewConnection NewConnection(string name = "Acme", string settings = """{ "host": "acme.local", "port": 443 }""",
        IReadOnlyDictionary<string, string>? secrets = null, bool startNow = true, bool autoImport = true) =>
        new(LocalizedName.Of(name), Package, null, ServerType, Json(settings), secrets ?? new Dictionary<string, string> { ["password"] = "S3cret-value!" },
            Registry.Root, autoImport, startNow);

    public async Task<ConnectionId> CreateAsync(string name = "Acme", bool startNow = true, bool autoImport = true)
    {
        var created = await Manager.CreateAsync(NewConnection(name, startNow: startNow, autoImport: autoImport), Ctx, TestContext.Current.CancellationToken);
        return created.Value;
    }

    /// <summary>Creates a connection and activates the manager; waits for <paramref name="state"/>.</summary>
    public async Task<ConnectionId> StartedAsync(ConnectionState state = ConnectionState.Connected, string name = "Acme")
    {
        var id = await CreateAsync(name);
        await Manager.ActivateAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(id, state);
        return id;
    }

    public IReadOnlyList<ConnectionStatusChangedEventArgs> Transitions(ConnectionId id)
    {
        lock (_lock)
        {
            return _transitions.TryGetValue(id, out var list) ? [.. list] : [];
        }
    }

    /// <summary>Waits until the transitions of the connection so far are recorded.</summary>
    public Task RecordedAsync(ConnectionId id) => Manager.Runtime(id)!.RecordedAsync().WaitAsync(TestTimeouts.Default, TestContext.Current.CancellationToken);

    public IReadOnlyList<ConnectionState> States(ConnectionId id) => [.. Transitions(id).Select(t => t.Status.State)];

    /// <summary>Waits (bounded, real time) until the connection records a transition to <paramref name="state"/> (or is in it).</summary>
    public Task<KernelConnectionStatus> WaitForAsync(ConnectionId id, ConnectionState state, int occurrence = 1)
    {
        TaskCompletionSource<KernelConnectionStatus> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            var seen = _transitions.TryGetValue(id, out var list) ? list.Where(t => t.Status.State == state).ToList() : [];
            if (seen.Count >= occurrence)
            {
                return Task.FromResult(seen[occurrence - 1].Status);
            }

            _waiters.Add((id, state, done));
        }

        return done.Task.WaitAsync(TestTimeouts.Default, TestContext.Current.CancellationToken);
    }

    /// <summary>Advances the clock by <paramref name="step"/> until <paramref name="condition"/> holds (bounded, real time).</summary>
    public async Task AdvanceUntilAsync(Func<bool> condition, TimeSpan step, string because)
    {
        await Eventually.UntilAsync(() =>
        {
            if (condition())
            {
                return true;
            }

            Clock.Advance(step);
            return condition();
        }, TestTimeouts.Default, because, TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        {
            // Stop timeouts run on the test clock: keep it moving while the manager shuts down (bounded).
            var disposing = _manager.DisposeAsync().AsTask();
            await Eventually.UntilAsync(() =>
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                return disposing.IsCompleted;
            }, TimeSpan.FromSeconds(30), "the manager is disposed", TimeSpan.FromMilliseconds(5), CancellationToken.None);
            await disposing;
        }

        Meter.Dispose();
        _loggers.Dispose();
        Logs.Dispose();
    }

    private ConnectionManager Build()
    {
        var recorder = new ConnectionStateRecorder(Repository, Ingest, Registry, Clock, _loggers.CreateLogger<ConnectionStateRecorder>(), EventLog, EventLog);
        var manager = new ConnectionManager(Repository, Repository, Packages, Host, Vault, Registry, Registry, new AllowAllConnectionAdmission(), Contexts,
            new InventoryCoordinator(Registry, Types, _loggers.CreateLogger<InventoryCoordinator>()), recorder, Options, Clock,
            _loggers.CreateLogger<ConnectionManager>(), () => 0.5);
        manager.StatusChanged += (_, e) =>
        {
            List<TaskCompletionSource<KernelConnectionStatus>> done = [];
            lock (_lock)
            {
                _transitions.GetOrAdd(e.Status.Id, _ => []).Add(e);
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_waiters[i].Id == e.Status.Id && _waiters[i].State == e.Status.State)
                    {
                        done.Add(_waiters[i].Done);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            done.ForEach(d => d.TrySetResult(e.Status));
        };
        return manager;
    }
}

/// <summary>A connector whose behaviour the test sets; by default it connects on start and stops at once.</summary>
internal sealed class FakeConnector : IConnector, ICommandExecutor
{
    private static int _count;

    public FakeConnector() => Number = Interlocked.Increment(ref _count);

    public int Number { get; }

    public IConnectorContext Context { get; private set; } = null!;

    public Func<FakeConnector, ValueTask<Result>> OnInitialize { get; set; } = _ => ValueTask.FromResult(Result.Success());

    public Func<FakeConnector, CancellationToken, ValueTask> OnStart { get; set; } = (c, _) =>
    {
        c.Context.Health.Report(ConnectorStatus.Connected);
        return ValueTask.CompletedTask;
    };

    public Func<FakeConnector, CancellationToken, ValueTask> OnStop { get; set; } = (_, _) => ValueTask.CompletedTask;

    public Func<FakeConnector, CancellationToken, ValueTask<InventorySnapshot>> OnDescribe { get; set; } = (_, _) => ValueTask.FromResult(Inventory());

    public Func<ConnectorCommand, CancellationToken, ValueTask<ConnectorCommandResult>> OnExecute { get; set; } =
        (_, _) => ValueTask.FromResult(ConnectorCommandResult.Succeeded());

    public int StopCalls;

    public int DescribeCalls;

    public bool Disposed { get; private set; }

    public static InventorySnapshot Inventory(bool complete = true, string deviceType = "ac.door") => new(
        [new InventoryServerItem("root", null, "acme.server", "1.0.0", "Acme", new Dictionary<string, WpValue>())],
        [new InventoryDeviceItem("d1", "root", null, deviceType, "1.0", "Door 1", new Dictionary<string, WpValue>(), [], [])],
        complete);

    public ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
    {
        Context = context;
        return OnInitialize(this);
    }

    public ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct) => ValueTask.FromResult(ConnectionTestResult.Succeeded());

    public ValueTask StartAsync(CancellationToken ct) => OnStart(this, ct);

    public ValueTask StopAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref StopCalls);
        return OnStop(this, ct);
    }

    public ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref DescribeCalls);
        return OnDescribe(this, ct);
    }

    public ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct) => OnExecute(command, ct);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Creates <see cref="FakeConnector"/>s; <see cref="Configure"/> sets up each new instance.</summary>
internal sealed class FakeHost : IConnectionHost
{
    private readonly List<FakeConnector> _created = [];

    public Action<FakeConnector> Configure { get; set; } = _ => { };

    public Func<FakeConnector> Factory { get; set; } = () => new FakeConnector();

    public IReadOnlySet<string> Permissions { get; set; } = new HashSet<string>();

    public Watchpoint.Model.Documents.ConnectorLimitsDoc? Limits { get; set; }

    public HostMode Mode => HostMode.InProcess;

    public int Released;

    public IReadOnlyList<FakeConnector> Created
    {
        get
        {
            lock (_created)
            {
                return [.. _created];
            }
        }
    }

    public List<Connection> Connections { get; } = [];

    public Result<HostedConnector> Create(Connection connection)
    {
        var connector = Factory();
        Configure(connector);
        lock (_created)
        {
            _created.Add(connector);
            Connections.Add(connection);
        }

        return new HostedConnector(connector, Permissions, new Release(() => Interlocked.Increment(ref Released))) { Limits = Limits };
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}

/// <summary>One active package <c>vendor.acme@1.0.0</c> with server type <c>acme.server</c> (settings host/port, secret password required, token optional).</summary>
internal sealed class FakePackages : IPackageCatalog
{
    public string Isolation { get; set; } = "inProcessAllowed";

    public PackageState State { get; set; } = PackageState.Active;

    public event EventHandler<PackageChangedEventArgs>? Changed;

    public PackageInfo Info => new(ConnectionHarness.Package, ConnectionHarness.Version, State, "Acme", "generic", PackageInfo.UserSource, null,
        [new ConnectorInfo(ConnectionHarness.ServerType, [], "*", false)], new LicenceRequirement("included", null), ["linux-x64"], Isolation, [], false,
        DateTimeOffset.UnixEpoch);

    public static ConnectionSettingsSchema Schema { get; } = new(ConnectionHarness.ServerType, ConnectionHarness.Json("""
        {
          "type": "object",
          "properties": {
            "host": { "type": "string" },
            "port": { "type": "integer", "minimum": 1, "maximum": 65535, "default": 443 },
            "allowInsecureHttp": { "type": "boolean" },
            "inboundTcp": { "type": "boolean" },
            "maxConcurrency": { "type": "integer" }
          }
        }
        """), [new SecretFieldSchema("password", "password", true), new SecretFieldSchema("token", "token", false)], null);

    public IReadOnlyList<PackageInfo> List() => [Info];

    public Result<PackageInfo> Get(PackageId id, SemVersion version) =>
        id == ConnectionHarness.Package && version == ConnectionHarness.Version ? Info : IntegrationErrors.NotFound(id, version.ToString());

    public Result<PackageInfo> GetLatestActive(PackageId id) => id == ConnectionHarness.Package && State == PackageState.Active ? Info : IntegrationErrors.NotFound(id);

    public Task<Result<PackageInfo>> InstallAsync(Stream wpkg, string fileName, UserId actor, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result> SetEnabledAsync(PackageId id, SemVersion version, bool enabled, UserId actor, CancellationToken ct) => throw new NotSupportedException();

    public Result<ConnectionSettingsSchema> GetSettingsSchema(PackageId id, SemVersion version, string serverType) =>
        serverType == ConnectionHarness.ServerType ? Schema : IntegrationErrors.NotFound(id, version.ToString());

    public void RaiseChanged(PackageState? previous) => Changed?.Invoke(this, new PackageChangedEventArgs(Info, previous));
}

/// <summary>Records the batches; <see cref="Gate"/> stalls every call until set.</summary>
internal sealed class FakeIngest : IObservationIngest
{
    private readonly List<IReadOnlyList<Observation>> _batches = [];

    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Holds call number n (1-based) until the returned task completes.</summary>
    public Func<int, Task>? Hold { get; set; }

    public Func<IReadOnlyList<Observation>, IngestResult>? Result { get; set; }

    public int Calls;

    public IReadOnlyList<IReadOnlyList<Observation>> Batches
    {
        get
        {
            lock (_batches)
            {
                return [.. _batches];
            }
        }
    }

    public IReadOnlyList<Observation> All => [.. Batches.SelectMany(b => b)];

    public async ValueTask<IngestResult> IngestAsync(IReadOnlyList<Observation> batch, CancellationToken ct)
    {
        var call = Interlocked.Increment(ref Calls);
        lock (_batches)
        {
            _batches.Add([.. batch]);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        if (Hold?.Invoke(call) is { } hold)
        {
            await hold.WaitAsync(ct);
        }

        return Result?.Invoke(batch) ?? new IngestResult(batch.Count, 0, 0, []);
    }
}

/// <summary>A registry with one root location, servers and devices set by the test, and a recording reconciler.</summary>
internal sealed class FakeRegistry : IRegistryReader, IRegistryWriter, IInventoryReconciler
{
    private readonly Lock _lock = new();
    private readonly List<Server> _servers = [];
    private readonly List<Device> _devices = [];

    public LocationId Root { get; } = LocationId.New();

    public List<(ConnectionId Connection, InventoryReport Report, ReconcileOptions Options)> Reconciled { get; } = [];

    public List<RegistryChangeBatch> Applied { get; } = [];

    /// <summary>Called by the reconciler fake (e.g. to import devices).</summary>
    public Action<ConnectionId, InventoryReport>? OnReconcile { get; set; }

    public long Revision => 1;

    public Site Site => new(SiteId.New(), LocalizedName.Of("Site"), "UTC", 1);

    public event EventHandler<RegistryChangedEventArgs>? Changed;

    public Server AddServer(ConnectionId connection, string nativeId, ServerId? parent = null)
    {
        var server = new Server(ServerId.New(), new TypeRef(TypeId.FromTrusted("acme.server"), new SemVersion(1, 0, 0)), connection, parent, nativeId, null,
            nativeId, Root, [], new Dictionary<string, WpValue>(), true, false, 1);
        lock (_lock)
        {
            _servers.Add(server);
        }

        return server;
    }

    public Device AddDevice(Server server, string nativeId, bool enabled = true, bool orphaned = false)
    {
        var device = new Device(DeviceId.New(), new TypeRef(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0)), server.Id, null, nativeId, null, nativeId,
            Root, [], new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(), new HashSet<CapabilityId>(), null, enabled, false, orphaned,
            DateTimeOffset.UnixEpoch, 1);
        lock (_lock)
        {
            _devices.Add(device);
        }

        return device;
    }

    public void RaiseDevicesChanged(Device device) =>
        Changed?.Invoke(this, new RegistryChangedEventArgs(new RegistryChangeBatch([new RegistryChange.UpsertDevice(device)]), new ChangeContext(null, "test")));

    public Result<Location> GetLocation(LocationId id) =>
        id == Root ? new Location(Root, null, LocalizedName.Of("Root"), LocationKind.Site, "/r/", 0, null, null, null, 0, 1) : CommonErrors.NotFound("location", id);

    public IReadOnlyList<Location> GetLocationTree() => [GetLocation(Root).Value];

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection)
    {
        lock (_lock)
        {
            return [.. _servers.Where(s => s.ConnectionId == connection)];
        }
    }

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page)
    {
        lock (_lock)
        {
            return new Page<Device>([.. _devices.Where(d => query.Servers is null || query.Servers.Contains(d.ServerId))], null);
        }
    }

    public Task<Result> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Applied.Add(batch);
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result<ReconciliationResult>> ReconcileAsync(ConnectionId connection, InventoryReport report, ReconcileOptions options, CancellationToken ct = default)
    {
        lock (_lock)
        {
            Reconciled.Add((connection, report, options));
        }

        OnReconcile?.Invoke(connection, report);
        return Task.FromResult<Result<ReconciliationResult>>(new ReconciliationResult(0, 0, 0, 0, report.Devices.Count, options.AutoImportNew ? report.Devices.Count : 0, []));
    }

    public IReadOnlyList<(ConnectionId Connection, InventoryReport Report, ReconcileOptions Options)> ReconciledSnapshot()
    {
        lock (_lock)
        {
            return [.. Reconciled];
        }
    }

    public Task<IReadOnlyList<DiscoveredItem>> GetDiscoveredAsync(ConnectionId connection, DiscoveredStatus? status = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<Result> ImportAsync(ConnectionId connection, ImportRequest request, ChangeContext ctx, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<Result> SetIgnoredAsync(ConnectionId connection, IReadOnlyList<string> nativeIds, bool ignored, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public IReadOnlyList<Location> GetAncestors(LocationId id) => throw new NotSupportedException();

    public Result<Server> GetServer(ServerId id) => throw new NotSupportedException();

    public Result<Device> GetDevice(DeviceId id) => throw new NotSupportedException();

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId) => throw new NotSupportedException();

    public IReadOnlyList<Device> GetChildren(DeviceId parent) => throw new NotSupportedException();

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both) => throw new NotSupportedException();

    public Page<Server> QueryServers(ServerQuery query, PageRequest page) => throw new NotSupportedException();

    public IReadOnlyList<Tag> GetTags() => [];

    public IReadOnlyList<TagFamily> GetTagFamilies() => [];

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) => [];

    public DeviceEnrichment GetEnrichment(DeviceId id) => throw new NotSupportedException();
}

/// <summary>A vault in memory that records every call with its owner and purpose.</summary>
internal sealed class FakeVault : ISecretVault
{
    private readonly ConcurrentDictionary<SecretId, (SecretOwner Owner, byte[] Value)> _secrets = new();

    public ConcurrentQueue<(string Action, SecretId Id, SecretOwner Owner, string? Purpose)> Calls { get; } = new();

    public int Count => _secrets.Count;

    public Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var id = SecretId.New();
        _secrets[id] = (owner, value.ToArray());
        Calls.Enqueue(("create", id, owner, null));
        return Task.FromResult<Result<SecretId>>(id);
    }

    public Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct)
    {
        Calls.Enqueue(("read", id, caller, purpose));
        return Task.FromResult(_secrets.TryGetValue(id, out var secret) && secret.Owner == caller
            ? (Result<SecretValue>)SecretValue.Copy(secret.Value)
            : SecretsErrors.AccessDenied(id));
    }

    public Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct)
    {
        Calls.Enqueue(("delete", id, caller, null));
        _secrets.TryRemove(id, out _);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct) => throw new NotSupportedException();

    public Task<Result<int>> RewrapAllAsync(CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>MOD with the types <c>acme.server@1.0.0</c> (server) and <c>ac.door@1.2.0</c> (device).</summary>
internal sealed class FakeTypes : ITypeCatalog
{
    private readonly Dictionary<TypeRef, DeviceTypeDefinition> _types = new()
    {
        [Ref("acme.server", 1, 0, 0)] = Definition(Ref("acme.server", 1, 0, 0), TypeRole.Server),
        [Ref("ac.door", 1, 2, 0)] = Definition(Ref("ac.door", 1, 2, 0), TypeRole.Device),
    };

    public long Revision => 1;

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public static TypeRef Ref(string id, int major, int minor, int patch) => new(TypeId.FromTrusted(id), new SemVersion(major, minor, patch));

    public Result<DeviceTypeDefinition> Get(TypeRef type) => _types.TryGetValue(type, out var d) ? d : CommonErrors.NotFound("type", type);

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) =>
        _types.Values.Where(t => t.Ref.Id == id && t.Ref.Version.Major == major).MaxBy(t => t.Ref.Version) is { } d ? d : CommonErrors.NotFound("type", id);

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => [.. _types.Values];

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct)
    {
        Changed?.Invoke(this, new CatalogChangedEventArgs(new CatalogChange([], []), 1));
        throw new NotSupportedException();
    }

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotSupportedException();

    private static DeviceTypeDefinition Definition(TypeRef type, TypeRole role) => new()
    {
        Ref = type,
        Role = role,
        Category = Category.Generic,
        Source = "test",
        Lineage = [type],
        Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>(),
        Properties = new Dictionary<PropertyPath, PropertyDefinition>(),
        Commands = new Dictionary<CommandPath, CommandDefinition>(),
        Events = new Dictionary<EventPath, EventDefinition>(),
        Attributes = new ParameterSchema(ConnectionHarness.Json("""{ "type": "object", "properties": {} }""")),
        AllowedChildTypes = [],
        LicenceClass = LicenceClass.Full,
        Display = new TypeDisplay("icon", "symbol", "state"),
        StateOverrides = [],
        DefaultAlarmRules = [],
        DefaultLinks = [],
    };
}

/// <summary>The event log: records the global appends and the registered kinds.</summary>
internal sealed class FakeEventLog : IEventLogWriter, ILogRecordTypeRegistry
{
    private readonly ConcurrentDictionary<string, int> _kinds = new();

    public ConcurrentQueue<(LogStream Stream, LogEnvelope Record)> Appended { get; } = new();

    public ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<LogEnvelope> records, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<long> AppendGlobalAsync(LogStream stream, LogEnvelope record, CancellationToken ct)
    {
        Appended.Enqueue((stream, record));
        return ValueTask.FromResult((long)Appended.Count);
    }

    public void Register<TBody>(string kind, int schemaVersion, JsonTypeInfo<TBody> typeInfo)
    {
        _kinds[kind] = schemaVersion;
        _typeInfos[kind] = typeInfo;
    }

    public bool TryGetSchemaVersion(string kind, out int schemaVersion) => _kinds.TryGetValue(kind, out schemaVersion);

    public bool CanRead(LogRecord record) => true;

    public JsonElement SerializeBody<TBody>(string kind, TBody body) => JsonSerializer.SerializeToElement(body, (JsonTypeInfo<TBody>)_typeInfos[kind]);

    public TBody DeserializeBody<TBody>(string kind, JsonElement body) => body.Deserialize((JsonTypeInfo<TBody>)_typeInfos[kind])!;

    private readonly ConcurrentDictionary<string, JsonTypeInfo> _typeInfos = new();
}
