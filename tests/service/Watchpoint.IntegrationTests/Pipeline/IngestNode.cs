using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Internal;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Testing.Containers;

namespace Watchpoint.IntegrationTests.Pipeline;

/// <summary>
/// Helpers for ingest tests on real NATS without a database: the standard device types, a directory of devices in
/// memory, and subscribers that record what the partition workers hand them.
/// </summary>
internal static class IngestNode
{
    public static readonly PropertyPath Position = PropertyPath.FromTrusted("doorPosition.position");
    public static readonly EventPath Forced = EventPath.FromTrusted("doorPosition.forced");

    private static readonly Lazy<(DeviceTypeDefinition Door, DeviceTypeDefinition Server)> _types = new(() =>
    {
        var reader = DocumentReaderFactory.FromEmbeddedSchemas();
        var library = StandardLibraryLoader.Load(reader, NullLogger.Instance).Value;
        var catalog = new TypeCatalog(library, null, reader, NullLogger<TypeCatalog>.Instance, TimeProvider.System, new ModelMetrics());
        return (catalog.GetLatest(TypeId.FromTrusted("ac.door"), 1).Value, catalog.GetLatest(TypeId.FromTrusted("generic.server"), 1).Value);
    });

    /// <summary>The standard <c>ac.door</c> type.</summary>
    public static DeviceTypeDefinition DoorType => _types.Value.Door;

    /// <summary>Starts the pipeline with an in-memory directory and the given subscribers; the workers run.</summary>
    public static async Task<PipelineNode> StartAsync(
        NatsContainerFixture nats,
        NatsScope scope,
        MemoryDirectory directory,
        CancellationToken ct,
        IngestOptions? ingest = null,
        Action<IServiceCollection>? configure = null,
        bool start = true)
    {
        var node = await PipelineNode.StartAsync(
            nats.Url,
            PipelineNode.OptionsFor(scope),
            ct,
            services =>
            {
                services.AddSingleton<IIngestDirectory>(directory);
                services.AddSingleton<IValueValidator, ValueValidator>();
                configure?.Invoke(services);
            },
            ingest);
        if (start)
        {
            await node.Services.GetRequiredService<IPipelineLifecycle>().StartAsync(ct);
        }

        return node;
    }

    public static Observation Property(DeviceId device, ConnectionId connection, string code) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = ObservationKind.Property,
        Device = device,
        Property = Position,
        Value = WpValue.Enum(code),
        Connection = connection,
    };

    public static Observation Event(DeviceId device, ConnectionId connection) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = ObservationKind.Event,
        Device = device,
        Event = Forced,
        Connection = connection,
    };
}

/// <summary>Devices known to the ingest, in memory (instead of the registry).</summary>
internal sealed class MemoryDirectory : IIngestDirectory
{
    private static readonly IReadOnlySet<CapabilityId> _none = new HashSet<CapabilityId>();
    private readonly ConcurrentDictionary<DeviceId, IngestTarget> _devices = new();

    public DeviceId AddDoor(ConnectionId connection)
    {
        var id = DeviceId.New();
        _devices[id] = new IngestTarget(connection, true, IngestNode.DoorType, _none);
        return id;
    }

    public IngestTarget? FindDevice(DeviceId id) => _devices.GetValueOrDefault(id);

    public IngestTarget? FindServer(ServerId id) => null;
}

/// <summary>
/// A stand-in for the state module (STA, T1.11): keeps the current property values, counts how often each record was
/// applied, takes snapshots with a checkpoint per partition, and provides the values to the change detection.
/// </summary>
internal sealed class StateStandIn : IPartitionSubscriber, ILastValueSource
{
    private readonly ConcurrentDictionary<(Guid Target, string Path), (WpValue Value, Quality Quality, int Partition)> _values = new();
    private readonly ConcurrentDictionary<Guid, int> _applied = new();
    private readonly ConcurrentDictionary<int, long> _checkpoints = new();
    private readonly ConcurrentQueue<LogRecord> _records = new();
    private readonly ILogRecordTypeRegistry _registry = Watchpoint.Pipeline.PipelineModule.CreateRegistry();
    private int _touches;

    public StateStandIn(StateSnapshot? restored = null)
    {
        if (restored is null)
        {
            return;
        }

        foreach (var (key, value) in restored.Values)
        {
            _values[key] = value;
        }

        foreach (var (partition, sequence) in restored.Checkpoints)
        {
            _checkpoints[partition] = sequence;
        }
    }

    public int Order => 100;

    public string Name => "state";

    public Action<PartitionContext, IReadOnlyList<LogRecord>>? OnRecordsHook { get; set; }

    public IReadOnlyCollection<LogRecord> Records => _records;

    public int Touches => Volatile.Read(ref _touches);

    /// <summary>How often each record was applied, by record id.</summary>
    public IReadOnlyDictionary<Guid, int> Applied => _applied;

    public WpValue? ValueOf(DeviceId device, PropertyPath path) =>
        _values.TryGetValue((device.Value, path.Value), out var entry) ? entry.Value : null;

    /// <summary>The state and the checkpoints as a snapshot (taken while nothing is ingested).</summary>
    public StateSnapshot Snapshot() => new(_values.ToDictionary(), _checkpoints.ToDictionary());

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        foreach (var record in records)
        {
            _records.Enqueue(record);
            _applied.AddOrUpdate(record.Envelope.Id, 1, (_, n) => n + 1);
            if (record.Envelope.Kind == RecordKinds.Property && record.Envelope.Dev is { } device)
            {
                var body = record.BodyAs<PropertyRecordBody>(_registry);
                _values[(device.Value, body.Path)] = (body.Value, body.Quality, context.Partition);
            }

            _checkpoints[context.Partition] = record.Sequence;
        }

        OnRecordsHook?.Invoke(context, records);
    }

    public void OnTouched(PartitionContext context, TouchInfo touch) => Interlocked.Increment(ref _touches);

    public long? GetRecoveryCheckpoint(int partition) => _checkpoints.GetValueOrDefault(partition);

    public void ProvideLastValues(int partition, ILastValueSink sink)
    {
        foreach (var (key, entry) in _values)
        {
            if (entry.Partition == partition)
            {
                sink.SetProperty(key.Target, PropertyPath.FromTrusted(key.Path), entry.Value, entry.Quality);
            }
        }
    }
}

/// <summary>A snapshot of <see cref="StateStandIn"/>: values and the last sequence applied per partition.</summary>
internal sealed record StateSnapshot(
    Dictionary<(Guid Target, string Path), (WpValue Value, Quality Quality, int Partition)> Values,
    Dictionary<int, long> Checkpoints);

/// <summary>Records the flooding signals of the pipeline (the hook the integration runtime implements).</summary>
internal sealed class FloodingSignals : IIngestFloodingListener
{
    private readonly ConcurrentQueue<bool> _signals = new();

    public IReadOnlyCollection<bool> Signals => _signals;

    public void OnFloodingChanged(ConnectionId connection, bool flooding, int pending) => _signals.Enqueue(flooding);
}
