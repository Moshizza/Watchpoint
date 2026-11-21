using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal;
using Watchpoint.History.Internal.Lag;
using Watchpoint.History.Internal.Policies;
using Watchpoint.History.Internal.Writers;
using Watchpoint.History.Tests.Fakes;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Testing.Logging;

namespace Watchpoint.History.Tests;

/// <summary>The history writers on fakes: a registry of record kinds, a type catalogue, enrichment, a store, a consumer factory and fake time.</summary>
internal sealed class HistoryHarness : IDisposable
{
    public static readonly DeviceId Door = Watchpoint.Testing.Ids.TestIds.Device(1);

    public static readonly DeviceId Door2 = Watchpoint.Testing.Ids.TestIds.Device(2);

    public static readonly ServerId Server = Watchpoint.Testing.Ids.TestIds.Server(1);

    public static readonly TypeRef DoorType = new(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0));

    private readonly ServiceProvider _services;
    private readonly ILoggerFactory _loggers;

    public HistoryHarness(HistorySettings? settings = null, params (string Path, HistoryPolicy Policy)[] properties)
    {
        var services = new ServiceCollection();
        services.AddWatchpointPipeline(new ConfigurationBuilder().Build());
        _services = services.BuildServiceProvider();
        Registry = _services.GetRequiredService<ILogRecordTypeRegistry>();
        HistoryJson.RegisterKinds(Registry);
        Time = TestTime.CreateProvider();
        Settings = settings ?? new HistorySettings();
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Enrichment.Devices[Door] = Source(Door, "Main door");
        Enrichment.Devices[Door2] = Source(Door2, "Back door");
        Catalog.Types[DoorType] = DoorDefinition(properties);
        Policies = new PolicyResolver(Catalog, Store, Settings, Time);
        Metrics = new HistoryMetrics();
        Lag = new LagMonitor(Settings, Time, Metrics, _loggers.CreateLogger<LagMonitor>(), EventLog, Registry);
        Runner = new ConsumerRunner(Consumers, Registry, Settings, Time, Metrics, Lag, _loggers.CreateLogger<ConsumerRunner>());
        Writers = new HistoryWriters(Registry, Enrichment, Catalog, Store, Policies, Cache, _loggers);
    }

    public ILogRecordTypeRegistry Registry { get; }

    public FakeTimeProvider Time { get; }

    public HistorySettings Settings { get; }

    public LogCollector Logs { get; } = new();

    public FakeEnrichment Enrichment { get; } = new();

    public FakeTypeCatalog Catalog { get; } = new();

    public FakeHistoryStore Store { get; } = new();

    public ScriptedLogConsumers Consumers { get; } = new();

    public RecordingEventLog EventLog { get; } = new();

    public LastStoredCache Cache { get; } = new(500_000);

    public PolicyResolver Policies { get; }

    public HistoryMetrics Metrics { get; }

    public LagMonitor Lag { get; }

    public ConsumerRunner Runner { get; }

    public HistoryWriters Writers { get; }

    public static Guid LocationRoot => Watchpoint.Testing.Ids.TestIds.Uuid(100);

    public static Guid LocationFloor => Watchpoint.Testing.Ids.TestIds.Uuid(101);

    public static HistorySource Source(DeviceId device, string name) => new(
        device.Value, false, name, Server.Value, LocationFloor, [LocationRoot, LocationFloor], ["Site", "Floor 1"], ["entrance", "vip"], DoorType, "ac.door");

    public EventRowMapper EventMapper() => new(Registry, Enrichment, Catalog);

    public AlarmFolder AlarmFolder() => new(Registry, Enrichment);

    public CommandFolder CommandFolder() => new(Enrichment);

    public PropertyWriter PropertyWriter() => new(Registry, Enrichment, Policies, Cache, Store, _loggers.CreateLogger("test"), "his-properties");

    public LogRecord Record<TBody>(long sequence, string kind, TBody body, DeviceId? device = null, ServerId? server = null, DateTimeOffset? ts = null,
        RecordOrigin? origin = null, RecordCorrelation? correlation = null, DateTimeOffset? source = null, bool global = false)
    {
        var dev = global ? null : device ?? (server is null ? Door : null);
        var partition = global ? 0 : dev is { } d ? Partitioning.Of(d) : Partitioning.Of(server!.Value);
        var envelope = new LogEnvelope(
            Watchpoint.Testing.Ids.TestIds.Uuid(1_000_000 + sequence),
            kind,
            ts ?? Time.GetUtcNow(),
            source,
            partition,
            dev,
            global ? null : server,
            origin ?? RecordOrigin.System,
            correlation ?? RecordCorrelation.None,
            Registry.SerializeBody(kind, body));
        return new LogRecord(sequence, envelope);
    }

    /// <summary>A record whose body is given as JSON (as CMD writes <c>command.*</c>, which history reads without a registered type).</summary>
    public LogRecord Raw(long sequence, string kind, string json, DeviceId? device = null, DateTimeOffset? ts = null, RecordCorrelation? correlation = null)
    {
        var dev = device ?? Door;
        var envelope = new LogEnvelope(
            Watchpoint.Testing.Ids.TestIds.Uuid(1_000_000 + sequence), kind, ts ?? Time.GetUtcNow(), null, Partitioning.Of(dev), dev, null, RecordOrigin.System,
            correlation ?? RecordCorrelation.None, JsonDocument.Parse(json).RootElement.Clone());
        return new LogRecord(sequence, envelope);
    }

    public LogRecord Property(long sequence, string path, WpValue value, Quality quality = Quality.Good, DeviceId? device = null, DateTimeOffset? ts = null) =>
        Record(sequence, RecordKinds.Property, new PropertyRecordBody(path, value, quality, null), device, ts: ts);

    public void Dispose()
    {
        Metrics.Dispose();
        _loggers.Dispose();
        _services.Dispose();
        Logs.Dispose();
    }

    private static DeviceTypeDefinition DoorDefinition((string Path, HistoryPolicy Policy)[] properties)
    {
        var noFields = new ParameterSchema(JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement);
        var props = properties.ToDictionary(
            p => PropertyPath.FromTrusted(p.Path),
            p => new PropertyDefinition(PropertyPath.FromTrusted(p.Path), "decimal", [], null, null, null, null, null, null, false, p.Policy, null, null, null));
        var rejected = EventPath.FromTrusted("credential.rejected");
        var events = new Dictionary<EventPath, EventDefinition> { [rejected] = new(rejected, Severity.Warning, noFields, ["name", "holderRef", "credentialRef"]) };
        return new DeviceTypeDefinition
        {
            Ref = DoorType,
            Role = TypeRole.Device,
            Category = Category.Generic,
            Source = "standard",
            Lineage = [DoorType],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>(),
            Properties = props,
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = events,
            Attributes = noFields,
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Full,
            Display = new TypeDisplay("door", "ac.door", "health.connectivity"),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
    }
}
