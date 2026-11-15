using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Pipeline.Internal.Partitions;
using Watchpoint.Pipeline.Internal.SideEffects;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Ids;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>
/// The real ingest (validator, quotas, partition workers, side-effect queue) on the in-memory broker, with fake time,
/// a fake registry directory, hand-built device types and the value validator of the Model module.
/// </summary>
internal sealed class IngestHarness : IAsyncDisposable
{
    public static readonly PropertyPath Position = PropertyPath.FromTrusted("doorPosition.position");
    public static readonly PropertyPath Temperature = PropertyPath.FromTrusted("measurement.value");
    public static readonly PropertyPath Tampered = PropertyPath.FromTrusted("tamper.tampered");
    public static readonly EventPath Forced = EventPath.FromTrusted("doorPosition.forced");

    private static readonly Lazy<ModelFixture> _model = new(ModelFixture.Load);
    private readonly TestMeterFactory _meters = new();
    private ILoggerFactory? _loggerFactory;
    private long _ids;

    public IngestHarness(Action<IngestOptions>? configure = null, params IPartitionSubscriber[] subscribers)
        : this(configure, null, false, subscribers)
    {
    }

    /// <param name="configure">Changes the ingest options.</param>
    /// <param name="lastValues">The sources of current values at recovery.</param>
    /// <param name="logTimeouts">
    /// Whether the log writer gives up after the times of the spec. Off by default: tests move the fake clock while the
    /// workers run on other threads, and an append must not time out because a thread was slow.
    /// </param>
    /// <param name="subscribers">The partition subscribers.</param>
    public IngestHarness(Action<IngestOptions>? configure, IEnumerable<ILastValueSource>? lastValues, bool logTimeouts, params IPartitionSubscriber[] subscribers)
    {
        var logOptions = new PipelineOptions { SubjectPrefix = "t1.wp", StreamPrefix = "T1_" };
        if (!logTimeouts)
        {
            logOptions.AttemptTimeout = TimeSpan.FromDays(1);
            logOptions.AppendTimeout = TimeSpan.FromDays(2);
        }

        Log = new PipelineHarness(logOptions);

        // No linger: the workers wait on the fake clock, which only moves when a test moves it.
        Options = new IngestOptions { Linger = TimeSpan.Zero };
        configure?.Invoke(Options);
        Metrics = new PipelineMetrics(_meters);
        SideEffects = new SideEffectQueue(Options, Time, Metrics, LoggerFactory.CreateLogger<SideEffectQueue>());
        Router = new PartitionRouter(
            Options, Log.Options, Log.Writer, Log.Reader, Log.Registry, subscribers, lastValues ?? [], SideEffects, Time, Metrics,
            LoggerFactory.CreateLogger<PartitionRouter>());
        Ingest = new IngestService(
            new ObservationValidator(Directory, _model.Value.Validator, Options), Router, Options, Time, Metrics, [Flooding],
            LoggerFactory.CreateLogger<IngestService>());

        Directory.Servers[Server] = new IngestTarget(Connection, true, _model.Value.Server, new HashSet<CapabilityId>());
        Directory.Devices[Door] = new IngestTarget(Connection, true, DoorType, new HashSet<CapabilityId>());
    }

    public PipelineHarness Log { get; }

    public FakeTimeProvider Time => Log.Time;

    public LogCollector Logs => Log.Logs;

    public IngestOptions Options { get; }

    public PipelineMetrics Metrics { get; }

    public FakeDirectory Directory { get; } = new();

    public FloodingRecorder Flooding { get; } = new();

    public PartitionRouter Router { get; }

    public IngestService Ingest { get; }

    public SideEffectQueue SideEffects { get; }

    public ConnectionId Connection { get; } = TestIds.Connection(1);

    public ServerId Server { get; } = TestIds.Server(1);

    /// <summary>An enabled door (type <c>ac.door</c> plus a temperature in °C) of <see cref="Connection"/>.</summary>
    public DeviceId Door { get; } = TestIds.Device(1);

    /// <summary>A door type: position (enum), temperature in °C, optional tamper capability, health faults, a forced event.</summary>
    public static DeviceTypeDefinition DoorType => _model.Value.Door;

    private ILoggerFactory LoggerFactory => _loggerFactory ??=
        Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));

    public Task StartAsync() => Router.StartAsync(TestContext.Current.CancellationToken);

    /// <summary>Adds another enabled door of <see cref="Connection"/>.</summary>
    public DeviceId AddDoor(long n, int? partition = null)
    {
        var id = partition is { } p ? PipelineHarness.DeviceIn(p, (int)n) : TestIds.Device(n);
        Directory.Devices[id] = new IngestTarget(Connection, true, DoorType, new HashSet<CapabilityId>());
        return id;
    }

    public Observation Property(DeviceId device, PropertyPath path, WpValue value, Quality quality = Quality.Good) => new()
    {
        Id = NextId(),
        Kind = ObservationKind.Property,
        Device = device,
        Property = path,
        Value = value,
        Quality = quality,
        Connection = Connection,
    };

    public Observation PositionOf(DeviceId device, string code, Quality quality = Quality.Good) =>
        Property(device, Position, WpValue.Enum(code), quality);

    public Observation Event(DeviceId device, EventPath? path = null) => new()
    {
        Id = NextId(),
        Kind = ObservationKind.Event,
        Device = device,
        Event = path ?? Forced,
        Connection = Connection,
    };

    public Observation Health(DeviceId device, Connectivity connectivity, params string[] faults) => new()
    {
        Id = NextId(),
        Kind = ObservationKind.Health,
        Device = device,
        Health = new HealthReport(connectivity, faults, null),
        Connection = Connection,
    };

    public Task<IngestResult> IngestAsync(params Observation[] batch) =>
        Ingest.IngestAsync(batch, TestContext.Current.CancellationToken).AsTask();

    /// <summary>The records of the log, in sequence order.</summary>
    public IReadOnlyList<LogRecord> Records() =>
        [.. Log.Broker.Messages(Log.LogStreamName).Select(m => new LogRecord(
            m.Sequence, Watchpoint.Pipeline.Internal.Log.LogEnvelopeJson.Deserialize(m.Payload)))];

    public PropertyRecordBody PropertyBody(LogRecord record) => record.BodyAs<PropertyRecordBody>(Log.Registry);

    /// <summary>The value of a counter of this harness, summed over the measurements with the given tag.</summary>
    public long Counter(string instrument, string tag, string value) => _meters.Sum(instrument, tag, value);

    /// <summary>Waits (bounded, real time) until a condition holds on another thread.</summary>
    public static Task UntilAsync(Func<bool> condition, string because) =>
        Eventually.UntilAsync(condition, TestTimeouts.Default + TestTimeouts.Default, because, ct: TestContext.Current.CancellationToken);

    /// <summary>
    /// Advances the fake clock in steps until the task completes; after each step the other threads get a moment (real
    /// time, bounded) to act on it.
    /// </summary>
    public async Task<T> PumpAsync<T>(Task<T> task, TimeSpan step)
    {
        for (var i = 0; i < 20_000 && !task.IsCompleted; i++)
        {
            Time.Advance(step);
            await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken));
        }

        return await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Ingest.Dispose();
        await Router.DisposeAsync();
        LoggerFactory.Dispose();
        await SideEffects.DisposeAsync();
        Metrics.Dispose();
        _meters.Dispose();
        Log.Dispose();
    }

    private Guid NextId() => TestIds.Uuid(5_000_000 + Interlocked.Increment(ref _ids));

    private sealed record ModelFixture(IValueValidator Validator, DeviceTypeDefinition Door, DeviceTypeDefinition Server)
    {
        private static readonly string[] _faultCodes = ["tamper", "batteryLow", "powerFail", "commFail"];

        public static ModelFixture Load()
        {
            // Only the value validator comes from the Model module: the catalogue needs a database.
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddWatchpointModel();
            using var provider = services.BuildServiceProvider();

            var everyChange = new HistoryPolicy("everyChange");
            PropertyDefinition Enum(PropertyPath path, params string[] codes) => new(
                path, "enum", [.. codes.Select(c => new EnumValueDefinition(c, Severity.Normal))], null, null, null, null, null, null, false, everyChange, null, null, null);
            var faults = new PropertyDefinition(
                PropertyPath.FromTrusted("health.faults"),
                "list",
                [.. _faultCodes.Select(c => new EnumValueDefinition(c, Severity.Fault))],
                null, null, null, null, "enum", null, false, everyChange, null, null, null);
            var doorProperties = new Dictionary<PropertyPath, PropertyDefinition>
            {
                [Position] = Enum(Position, "closed", "open", "heldOpen", "forced", "unknown"),
                [Temperature] = new(Temperature, "decimal", [], "Cel", null, 2, null, null, null, false, everyChange, null, null, null),
                [Tampered] = new(Tampered, "boolean", [], null, null, null, null, null, null, false, everyChange, null, null, null),
                [faults.Path] = faults,
            };
            var noFields = new ParameterSchema(JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement);
            var doorEvents = new Dictionary<EventPath, EventDefinition> { [Forced] = new(Forced, Severity.Alarm, noFields, []) };
            return new ModelFixture(
                provider.GetRequiredService<IValueValidator>(),
                Type("ac.door", TypeRole.Device, doorProperties, doorEvents, noFields),
                Type("generic.server", TypeRole.Server, new Dictionary<PropertyPath, PropertyDefinition> { [faults.Path] = faults }, [], noFields));
        }

        private static DeviceTypeDefinition Type(
            string id,
            TypeRole role,
            Dictionary<PropertyPath, PropertyDefinition> properties,
            Dictionary<EventPath, EventDefinition> events,
            ParameterSchema noFields)
        {
            var reference = new TypeRef(TypeId.FromTrusted(id), new SemVersion(1, 0, 0));
            return new DeviceTypeDefinition
            {
                Ref = reference,
                Role = role,
                Category = Category.Generic,
                Source = "standard",
                Lineage = [reference],
                Capabilities = properties.Keys.Select(p => p.Capability).Distinct()
                    .ToDictionary(c => c, c => new CapabilityUseDefinition(c, new SemVersion(1, 0, 0), c.Value == "tamper")),
                Properties = properties,
                Commands = new Dictionary<CommandPath, CommandDefinition>(),
                Events = events,
                Attributes = noFields,
                AllowedChildTypes = [],
                LicenceClass = LicenceClass.Full,
                Display = new TypeDisplay("door", id, "health.connectivity"),
                StateOverrides = [],
                DefaultAlarmRules = [],
                DefaultLinks = [],
            };
        }
    }
}

internal static class IngestResultExtensions
{
    /// <summary>Accepted, unchanged, coalesced and rejected observations of a result.</summary>
    public static (int Accepted, int Unchanged, int Coalesced, int Rejected) Counts(this IngestResult result) =>
        (result.Accepted, result.Unchanged, result.Coalesced, result.Rejected.Count);
}

/// <summary>The registry seen by the ingest, as a dictionary.</summary>
internal sealed class FakeDirectory : IIngestDirectory
{
    public Dictionary<DeviceId, IngestTarget> Devices { get; } = [];

    public Dictionary<ServerId, IngestTarget> Servers { get; } = [];

    public IngestTarget? FindDevice(DeviceId id) => Devices.GetValueOrDefault(id);

    public IngestTarget? FindServer(ServerId id) => Servers.GetValueOrDefault(id);
}

/// <summary>Records the flooding signals (the IRT hook, spec PIP §8).</summary>
internal sealed class FloodingRecorder : IIngestFloodingListener
{
    private readonly List<(ConnectionId Connection, bool Flooding, int Pending)> _signals = [];

    public IReadOnlyList<(ConnectionId Connection, bool Flooding, int Pending)> Signals
    {
        get
        {
            lock (_signals)
            {
                return [.. _signals];
            }
        }
    }

    public void OnFloodingChanged(ConnectionId connection, bool flooding, int pending)
    {
        lock (_signals)
        {
            _signals.Add((connection, flooding, pending));
        }
    }
}

/// <summary>A subscriber that records every call; hooks let a test emit follow-ups, block or throw.</summary>
internal sealed class RecordingSubscriber(string name, int order) : IPartitionSubscriber
{
    private readonly List<string> _calls = [];
    private readonly List<LogRecord> _records = [];
    private readonly List<TouchInfo> _touches = [];
    private readonly List<ClockDriftHint> _drifts = [];
    private int _ticks;

    public int Order => order;

    public string Name => name;

    /// <summary>A journal shared by several subscribers, to assert the order of calls between them.</summary>
    public List<string>? Journal { get; init; }

    public Action<PartitionContext, IReadOnlyList<LogRecord>>? OnRecordsHook { get; set; }

    public Action<PartitionContext>? OnTickHook { get; set; }

    public Dictionary<int, long> Checkpoints { get; } = [];

    public List<(int Partition, long LastSequence)> Recovered { get; } = [];

    public IReadOnlyList<LogRecord> Records => Snapshot(_records);

    public IReadOnlyList<TouchInfo> Touches => Snapshot(_touches);

    public IReadOnlyList<ClockDriftHint> Drifts => Snapshot(_drifts);

    public IReadOnlyList<string> Calls => Snapshot(_calls);

    public int Ticks => Volatile.Read(ref _ticks);

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        lock (_calls)
        {
            _records.AddRange(records);
            _calls.Add("records:" + string.Join(',', records.Select(r => r.Sequence)));
        }

        if (Journal is { } journal)
        {
            lock (journal)
            {
                journal.Add(name + ":" + string.Join(',', records.Select(r => r.Sequence)));
            }
        }

        OnRecordsHook?.Invoke(context, records);
    }

    public void OnTouched(PartitionContext context, TouchInfo touch)
    {
        lock (_calls)
        {
            _touches.Add(touch);
        }
    }

    public void OnClockDrift(PartitionContext context, ClockDriftHint hint)
    {
        lock (_calls)
        {
            _drifts.Add(hint);
        }
    }

    public long? GetRecoveryCheckpoint(int partition) => Checkpoints.TryGetValue(partition, out var checkpoint) ? checkpoint : null;

    public void OnRecoveryCompleted(int partition, long lastSequence)
    {
        lock (_calls)
        {
            Recovered.Add((partition, lastSequence));
        }
    }

    public void OnTick(PartitionContext context)
    {
        Interlocked.Increment(ref _ticks);
        OnTickHook?.Invoke(context);
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_calls)
        {
            return [.. list];
        }
    }
}

/// <summary>Meters of one harness, so that tests running in parallel do not see each other's measurements.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = [];
    private readonly List<(string Instrument, KeyValuePair<string, object?>[] Tags, long Value)> _measurements = [];
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
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (_measurements)
            {
                _measurements.Add((instrument.Name, tags.ToArray(), value));
            }
        });
        _listener.Start();
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

    public long Sum(string instrument, string tag, string value)
    {
        lock (_measurements)
        {
            return _measurements
                .Where(m => m.Instrument == instrument && m.Tags.Any(t => t.Key == tag && Equals(t.Value, value)))
                .Sum(m => m.Value);
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
}
