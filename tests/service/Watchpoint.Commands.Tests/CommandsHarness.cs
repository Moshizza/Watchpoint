using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Commands.Internal.Dispatch;
using Watchpoint.Identity.Contracts;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Commands.Tests;

/// <summary>
/// The command service and its subscriber on fakes: registry, type catalogue (a hand-built door type), a deny-by-default
/// authorizer, step-up and connections, and a partition runner that serialises work per partition, appends emitted
/// records to an in-memory log and hands them to the processor — the order the pipeline guarantees (PIP §7.3).
/// The value validator and the record kind registry are the real ones of the Model and Pipeline modules.
/// </summary>
internal sealed class CommandsHarness : IDisposable, IAsyncDisposable
{
    public static readonly CommandPath Open = CommandPath.FromTrusted("openable.open");
    public static readonly CommandPath UnlockFor = CommandPath.FromTrusted("openable.unlockFor");
    public static readonly CommandPath Reset = CommandPath.FromTrusted("maintenance.reset");
    public static readonly CommandPath Lockdown = CommandPath.FromTrusted("lockdown.engage");
    public static readonly CommandPath TamperTest = CommandPath.FromTrusted("tamper.test");
    public static readonly PropertyPath Position = PropertyPath.FromTrusted("doorPosition.position");

    private static readonly Lazy<(IValueValidator Values, ILogRecordTypeRegistry Records)> _shared = new(Load);
    private readonly ILoggerFactory _loggerFactory;

    public CommandsHarness(Action<CommandsOptions>? configure = null, ICommandAuthorizer? authorizer = null, bool dispatch = false)
    {
        configure?.Invoke(Options);
        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Registry.Servers[Server] = new Server(Server, new TypeRef(TypeId.FromTrusted("generic.server"), new SemVersion(1, 0, 0)), Connection, null,
            "srv-1", null, "Server", Location, [], new Dictionary<string, WpValue>(), true, false, 1);
        Connections.Known.Add(Connection);
        Types.Types[DoorType.Ref] = DoorType;
        AddDoor(Door);
        Authorizer.Allowed[Operator] = CommandConstraints.None;
        Authorizer.Allowed[Supervisor] = new CommandConstraints(MayRaiseToCritical: true);
        Authorizer.Approvers.Add(Supervisor);

        Metrics = new CommandMetrics(Meters);
        Codec = new CommandRecordCodec(_shared.Value.Records);
        Directory = new CommandDirectory(Options);
        Processor = new CommandProcessor(Directory, Codec, new CommandWaiters(), Metrics, _loggerFactory.CreateLogger<CommandProcessor>());
        Partitions = new FakePartitions(Processor, Time);
        StepUpGuard = new StepUpGuard(StepUp);
        var rights = authorizer ?? Authorizer;
        Validator = new CommandValidator(Registry, Types, _shared.Value.Values, rights, StepUpGuard, Connections, Options);
        Service = new CommandService(Validator, Processor, Directory, Partitions, Codec, Registry, Types, _shared.Value.Values, rights, StepUpGuard, Time, Metrics);
        if (dispatch)
        {
            var services = new DispatchServices(Processor, Partitions, Codec, Channel, Connections, Registry, Options, Time,
                _loggerFactory.CreateLogger<CommandDispatch>());
            Dispatch = new CommandDispatch(services, Types, Metrics);
        }
    }

    /// <summary>The connector channel seen by the dispatch (calls wait until the test completes them, unless a handler is set).</summary>
    public FakeChannel Channel { get; } = new();

    /// <summary>The dispatch, when the harness was built with <c>dispatch: true</c>.</summary>
    public CommandDispatch? Dispatch { get; }

    public FakeTimeProvider Time { get; } = TestTime.CreateProvider();

    public LogCollector Logs { get; } = new();

    public TestMeterFactory Meters { get; } = new();

    public CommandsOptions Options { get; } = new();

    public FakeRegistry Registry { get; } = new();

    public FakeTypes Types { get; } = new();

    public TestAuthorizer Authorizer { get; } = new();

    public FakeStepUp StepUp { get; } = new();

    public FakeConnections Connections { get; } = new();

    public CommandMetrics Metrics { get; }

    public CommandRecordCodec Codec { get; }

    public CommandDirectory Directory { get; }

    public CommandProcessor Processor { get; }

    public FakePartitions Partitions { get; }

    public StepUpGuard StepUpGuard { get; }

    public CommandValidator Validator { get; }

    public CommandService Service { get; }

    public static ILogRecordTypeRegistry Records => _shared.Value.Records;

    public ConnectionId Connection { get; } = TestIds.Of<ConnectionId>(1);

    public ServerId Server { get; } = TestIds.Server(1);

    public LocationId Location { get; } = TestIds.Of<LocationId>(1);

    public DeviceId Door { get; } = TestIds.Device(1);

    /// <summary>An operator allowed every command under no constraint.</summary>
    public UserId Operator { get; } = TestIds.User(1);

    /// <summary>A supervisor allowed everything, holding <c>commands.priority.critical</c>, and an approver.</summary>
    public UserId Supervisor { get; } = TestIds.User(2);

    public SessionId Session { get; } = TestIds.Of<SessionId>(1);

    /// <summary>
    /// A door: <c>openable.open</c> (normal, expected effect position = open within 5 s), <c>openable.unlockFor</c>
    /// (duration 1…3600 s default 5, level 0…100 decimal, mode normal|emergency — emergency is critical),
    /// <c>maintenance.reset</c> (sensitive), <c>lockdown.engage</c> (critical), <c>tamper.test</c> (optional capability).
    /// </summary>
    public static DeviceTypeDefinition DoorType { get; } = BuildDoorType();

    public CommandIssuer.User User(UserId? user = null, ClientKind client = ClientKind.Desktop) =>
        new(user ?? Operator, Session, client, "ws-1");

    public DeviceId AddDoor(DeviceId id, bool enabled = true, bool orphaned = false, params string[] unsupported)
    {
        Registry.Devices[id] = new Device(id, DoorType.Ref, Server, null, "door-" + id.Value, null, "Door", Location, [],
            new Dictionary<string, WpValue>(), new Dictionary<CustomFieldId, WpValue>(),
            new HashSet<CapabilityId>(unsupported.Select(CapabilityId.FromTrusted)), null, enabled, false, orphaned, TestTime.Start, 1);
        return id;
    }

    public static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public CommandRequest Request(CommandPath? path = null, string arguments = "{}", CommandIssuer? issuer = null, string? reason = null,
        string? stepUp = null, string? key = null, CommandPriority? priority = null, DateTimeOffset? notBefore = null,
        TimeSpan? timeout = null, RecordCorrelation? correlation = null, DeviceId? target = null) =>
        new(new CommandTargetSpec.Device(target ?? Door), path ?? Open, Json(arguments), issuer ?? User(), reason, stepUp, key, priority,
            notBefore, timeout, correlation);

    public async Task<CommandAccepted> AcceptAsync(CommandRequest request) =>
        (await Service.RequestAsync(request, TestContext.Current.CancellationToken)).ShouldBeSuccess();

    /// <summary>Records of the in-memory log, in append order.</summary>
    public IReadOnlyList<LogRecord> Log => Partitions.Log;

    public IReadOnlyList<LogRecord> RecordsOf(CommandId id) => [.. Log.Where(r => r.Envelope.Corr.Command == id)];

    public Command Command(CommandId id) => Service.Get(id).ShouldBeSuccess();

    /// <summary>A lifecycle record of a later task (dispatch, outcome, verification) for <paramref name="id"/>, applied in its partition.</summary>
    public Task DeliverAsync<TBody>(CommandId id, string kind, TBody body)
    {
        var command = Command(id);
        return Partitions.DeliverAsync(Partitioning.Of(command.Target), Codec.Envelope(kind, command, body, Time.GetUtcNow()));
    }

    public Task TickAsync(DeviceId? device = null) => Partitions.TickAsync(Partitioning.Of(device ?? Door));

    /// <summary>Stops the dispatch (if any), then disposes the rest.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Dispatch is { } dispatch)
        {
            Channel.ReleaseAll();
            await dispatch.DisposeAsync();
        }

        Dispose();
    }

    public void Dispose()
    {
        Metrics.Dispose();
        Meters.Dispose();
        _loggerFactory.Dispose();
    }

    private static (IValueValidator, ILogRecordTypeRegistry) Load()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWatchpointModel();
        services.AddWatchpointPipeline(new ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IValueValidator>(), provider.GetRequiredService<ILogRecordTypeRegistry>());
    }

    private static DeviceTypeDefinition BuildDoorType()
    {
        static ParameterSchema Schema(string json) => new(Json(json));
        var none = Schema("""{"type":"object","properties":{}}""");
        var unlock = Schema("""
            {"type":"object","properties":{
              "duration":{"type":"integer","minimum":1,"maximum":3600,"default":5},
              "level":{"type":"number","minimum":0,"maximum":100},
              "mode":{"type":"string","enum":["normal","emergency"],"default":"normal"}}}
            """);
        var noRisk = new Dictionary<string, IReadOnlyDictionary<string, Risk>>();
        var byMode = new Dictionary<string, IReadOnlyDictionary<string, Risk>> { ["mode"] = new Dictionary<string, Risk> { ["emergency"] = Risk.Critical } };
        CommandDefinition Def(CommandPath path, ParameterSchema schema, Risk risk, IReadOnlyDictionary<string, IReadOnlyDictionary<string, Risk>> riskBy,
            ExpectedEffect? effect = null) => new(path, schema, risk, riskBy, TimeSpan.FromSeconds(10), Idempotent: true, Batchable: true, effect);
        var commands = new[]
        {
            Def(Open, none, Risk.Normal, noRisk, new ExpectedEffect(Position, WpValue.Enum("open"), null, TimeSpan.FromSeconds(5))),
            Def(UnlockFor, unlock, Risk.Normal, byMode),
            Def(Reset, none, Risk.Sensitive, noRisk),
            Def(Lockdown, none, Risk.Critical, noRisk),
            Def(TamperTest, none, Risk.Normal, noRisk),
        }.ToDictionary(d => d.Path);
        var reference = new TypeRef(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0));
        return new DeviceTypeDefinition
        {
            Ref = reference,
            Role = TypeRole.Device,
            Category = Category.Generic,
            Source = "standard",
            Lineage = [reference],
            Capabilities = commands.Keys.Select(p => p.Capability).Distinct()
                .ToDictionary(c => c, c => new CapabilityUseDefinition(c, new SemVersion(1, 0, 0), c.Value == "tamper")),
            Properties = new Dictionary<PropertyPath, PropertyDefinition>(),
            Commands = commands,
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = none,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("door", "ac.door", "health.connectivity"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
    }
}

/// <summary>
/// The rights hook as the tests see it: <b>deny by default</b> — an identity not in <see cref="Allowed"/> is refused
/// with <c>common.forbidden</c>. <see cref="Denial"/> overrides every decision (outside schedule…).
/// </summary>
internal sealed class TestAuthorizer : ICommandAuthorizer
{
    public Dictionary<UserId, CommandConstraints> Allowed { get; } = [];

    public HashSet<UserId> Approvers { get; } = [];

    public Error? Denial { get; set; }

    public int Calls { get; private set; }

    public CommandAuthorization Authorize(CommandIssuer issuer, Device device, CommandDefinition command, Risk effectiveRisk,
        IReadOnlyDictionary<string, WpValue> arguments, DateTimeOffset now)
    {
        Calls++;
        if (Denial is { } denial)
        {
            return CommandAuthorization.Deny(denial);
        }

        return Allowed.TryGetValue(issuer.Identity, out var constraints)
            ? CommandAuthorization.Allow(constraints)
            : CommandAuthorization.Deny(CommonErrors.Forbidden(FallbackCommandAuthorizer.Permission(command)));
    }

    public bool CanApprove(UserId approver, Command command, DateTimeOffset now) => Approvers.Contains(approver);
}

internal sealed class FakeStepUp : IStepUpValidator
{
    public HashSet<(string Token, SessionId Session)> Valid { get; } = [];

    public Result Validate(string stepUpToken, SessionId session) =>
        Valid.Contains((stepUpToken, session)) ? Result.Success() : IdentityErrors.StepUpRequired();
}

internal sealed class FakeConnections : ICommandConnections
{
    private readonly Dictionary<ConnectionId, ConnectionState> _states = [];

    public event EventHandler<ConnectionId>? StateChanged;

    public HashSet<ConnectionId> Known { get; } = [];

    public bool Exists(ConnectionId connection) => Known.Contains(connection);

    public ConnectionState State(ConnectionId connection)
    {
        lock (_states)
        {
            return _states.GetValueOrDefault(connection, Known.Contains(connection) ? ConnectionState.Connected : ConnectionState.Disabled);
        }
    }

    public string? PackageOf(ConnectionId connection) => Known.Contains(connection) ? "sim" : null;

    /// <summary>Changes the state of a connection and raises <see cref="StateChanged"/>.</summary>
    public void Set(ConnectionId connection, ConnectionState state)
    {
        lock (_states)
        {
            _states[connection] = state;
        }

        StateChanged?.Invoke(this, connection);
    }
}

internal sealed class FakeTypes : ITypeCatalog
{
    public Dictionary<TypeRef, DeviceTypeDefinition> Types { get; } = [];

    public long Revision => 1;

    public event EventHandler<CatalogChangedEventArgs>? Changed { add { } remove { } }

    public Result<DeviceTypeDefinition> Get(TypeRef type) =>
        Types.TryGetValue(type, out var definition) ? definition : Error.NotFound("model.type.not_found", ("typeId", type.Id.Value));

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotSupportedException();

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => [.. Types.Values];

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class FakeRegistry : IRegistryReader
{
    public Dictionary<DeviceId, Device> Devices { get; } = [];

    public Dictionary<ServerId, Server> Servers { get; } = [];

    public long Revision => 1;

    public Site Site => throw new NotSupportedException();

    public event EventHandler<RegistryChangedEventArgs>? Changed { add { } remove { } }

    public Result<Device> GetDevice(DeviceId id) =>
        Devices.TryGetValue(id, out var device) ? device : Error.NotFound("registry.device.not_found", ("id", id.Value));

    public Result<Server> GetServer(ServerId id) =>
        Servers.TryGetValue(id, out var server) ? server : Error.NotFound("registry.server.not_found", ("id", id.Value));

    public Page<Device> QueryDevices(DeviceQuery query, PageRequest page) => new([.. Devices.Values], null);

    public Result<Location> GetLocation(LocationId id) => throw new NotSupportedException();

    public IReadOnlyList<Location> GetLocationTree() => throw new NotSupportedException();

    public IReadOnlyList<Location> GetAncestors(LocationId id) => throw new NotSupportedException();

    public Result<Device> FindDeviceByNativeId(ServerId server, string nativeId) => throw new NotSupportedException();

    public IReadOnlyList<Server> GetServersOfConnection(ConnectionId connection) => throw new NotSupportedException();

    public IReadOnlyList<Device> GetChildren(DeviceId parent) => throw new NotSupportedException();

    public IReadOnlyList<DeviceLink> GetLinks(DeviceId device, LinkKind? kind = null, LinkDirection direction = LinkDirection.Both) => throw new NotSupportedException();

    public Page<Server> QueryServers(ServerQuery query, PageRequest page) => throw new NotSupportedException();

    public IReadOnlyList<Tag> GetTags() => throw new NotSupportedException();

    public IReadOnlyList<TagFamily> GetTagFamilies() => throw new NotSupportedException();

    public IReadOnlyList<CustomFieldDefinition> GetCustomFields(CustomFieldTarget target) => throw new NotSupportedException();

    public DeviceEnrichment GetEnrichment(DeviceId id) => throw new NotSupportedException();
}

/// <summary>
/// Runs command work like the pipeline's posted work (PIP §7.3): one call at a time per partition; the records the work
/// (or a tick) emits are validated as log envelopes, appended in one batch with consecutive sequences and given to the
/// processor before the next work of the partition runs.
/// </summary>
internal sealed class FakePartitions(CommandProcessor processor, TimeProvider time) : ICommandPartitions
{
    private readonly SemaphoreSlim[] _partitions = [.. Enumerable.Range(0, Partitioning.PartitionCount).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly List<LogRecord> _log = [];
    private long _sequence;
    private int _running;

    public IReadOnlyList<LogRecord> Log
    {
        get
        {
            lock (_log)
            {
                return [.. _log];
            }
        }
    }

    /// <summary>The most works that ever ran at the same time in one partition (must stay 1).</summary>
    public int MaxConcurrent { get; private set; }

    /// <summary>Awaited between the work and the append: widens the window in which a racing update could interleave.</summary>
    public Func<Task>? BetweenWorkAndAppend { get; set; }

    public async Task<T> RunAsync<T>(int partition, Func<ICommandEmitter, T> work, CancellationToken ct)
    {
        await _partitions[partition].WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var running = Interlocked.Increment(ref _running);
            MaxConcurrent = Math.Max(MaxConcurrent, running);
            var emitter = new Emitter(partition, time.GetUtcNow());
            var result = work(emitter);
            if (BetweenWorkAndAppend is { } pause)
            {
                await pause().ConfigureAwait(false);
            }

            Append(partition, emitter.Records);
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _partitions[partition].Release();
        }
    }

    public async Task TickAsync(int partition)
    {
        await _partitions[partition].WaitAsync().ConfigureAwait(false);
        try
        {
            var emitter = new Emitter(partition, time.GetUtcNow());
            processor.OnTick(emitter);
            Append(partition, emitter.Records);
        }
        finally
        {
            _partitions[partition].Release();
        }
    }

    public async Task DeliverAsync(int partition, LogEnvelope record)
    {
        await _partitions[partition].WaitAsync().ConfigureAwait(false);
        try
        {
            Append(partition, [record]);
        }
        finally
        {
            _partitions[partition].Release();
        }
    }

    private void Append(int partition, List<LogEnvelope> envelopes)
    {
        if (envelopes.Count == 0)
        {
            return;
        }

        var records = new List<LogRecord>(envelopes.Count);
        lock (_log)
        {
            foreach (var envelope in envelopes)
            {
                var valid = envelope.Validate(LogStream.Log);
                if (!valid.IsSuccess || envelope.P != partition)
                {
                    throw new InvalidOperationException($"Invalid record {envelope.Kind}: {(valid.IsSuccess ? "partition" : valid.Error.Code.Value)}");
                }

                var record = new LogRecord(++_sequence, envelope) { SchemaVersion = 1 };
                _log.Add(record);
                records.Add(record);
            }
        }

        processor.OnRecords(partition, records);
    }

    private sealed class Emitter(int partition, DateTimeOffset now) : ICommandEmitter
    {
        public List<LogEnvelope> Records { get; } = [];

        public int Partition => partition;

        public DateTimeOffset Now => now;

        public void Emit(LogEnvelope record) => Records.Add(record);
    }
}

/// <summary>Meters of one harness (tests run in parallel).</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];
    private readonly List<(string Instrument, KeyValuePair<string, object?>[] Tags, double Value)> _measurements = [];
    private readonly MeterListener _listener = new();

    public TestMeterFactory()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            lock (_meters)
            {
                if (_meters.Contains(instrument.Meter))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, tags, value));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, tags, value));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, tags, value));
        _listener.Start();
    }

    /// <summary>Records the current values of the observable instruments (gauges).</summary>
    public void Observe() => _listener.RecordObservableInstruments();

    /// <summary>The measurements of an instrument carrying every given tag, oldest first.</summary>
    public List<double> Values(string instrument, params (string Tag, string Value)[] tags)
    {
        lock (_measurements)
        {
            return [.. _measurements
                .Where(m => m.Instrument == instrument && tags.All(w => m.Tags.Any(t => t.Key == w.Tag && Equals(t.Value?.ToString(), w.Value))))
                .Select(m => m.Value)];
        }
    }

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options);
        lock (_meters)
        {
            _meters.Add(meter);
        }

        return meter;
    }

    public double Sum(string instrument, string tag, string value)
    {
        lock (_measurements)
        {
            return _measurements
                .Where(m => m.Instrument == instrument && m.Tags.Any(t => t.Key == tag && Equals(t.Value, value)))
                .Sum(m => m.Value);
        }
    }

    public int Count(string instrument)
    {
        lock (_measurements)
        {
            return _measurements.Count(m => m.Instrument == instrument);
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }

    private void Add(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags, double value)
    {
        lock (_measurements)
        {
            _measurements.Add((instrument.Name, tags.ToArray(), value));
        }
    }
}
