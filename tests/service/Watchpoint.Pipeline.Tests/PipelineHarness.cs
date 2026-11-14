using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Testing;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Log;
using Watchpoint.Pipeline.Tests.Fakes;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Pipeline.Tests;

/// <summary>The real log writer, reader and consumer factory on the in-memory broker, with fake time and captured logs.</summary>
internal sealed class PipelineHarness : IDisposable
{
    public const string AlarmRaised = "alarm.raised";
    public const string AlarmCleared = "alarm.cleared";

    private readonly ILoggerFactory _loggerFactory;
    private readonly PipelineMetrics _metrics = new();
    private readonly EventLogWriter _writer;

    public PipelineHarness(PipelineOptions? options = null)
    {
        Options = options ?? new PipelineOptions { SubjectPrefix = "t1.wp", StreamPrefix = "T1_" };
        Naming = new LogNaming(Options);
        Broker = new InMemoryLogBroker(Time);
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        Registry = PipelineModule.CreateRegistry();
        Registry.Register(AlarmRaised, 1, TestJsonContext.Default.AlarmBody);
        Registry.Register(AlarmCleared, 1, TestJsonContext.Default.AlarmBody);
        _writer = new EventLogWriter(Broker, Registry, Options, Time, _metrics, _loggerFactory.CreateLogger<EventLogWriter>());
        Reader = new EventLogReader(Broker, Options, Time, _loggerFactory.CreateLogger<EventLogReader>());
        Consumers = new LogConsumerFactory(Broker, Options, Time, _loggerFactory);
    }

    public FakeTimeProvider Time { get; } = TestTime.CreateProvider();

    public LogCollector Logs { get; } = new();

    public PipelineOptions Options { get; }

    public LogNaming Naming { get; }

    public InMemoryLogBroker Broker { get; }

    public ILogRecordTypeRegistry Registry { get; }

    public IEventLogWriter Writer => _writer;

    public IEventLogReader Reader { get; }

    public ILogConsumerFactory Consumers { get; }

    public string LogStreamName => Naming.StreamName(LogStream.Log);

    public ILogger Logger(string category) => _loggerFactory.CreateLogger(category);

    /// <summary>The n-th test device whose partition is <paramref name="partition"/>.</summary>
    public static DeviceId DeviceIn(int partition, int nth = 1)
    {
        for (long n = 1; ; n++)
        {
            var id = TestIds.Device(n);
            if (Partitioning.Of(id) == partition && --nth == 0)
            {
                return id;
            }
        }
    }

    /// <summary>A valid device-bound envelope; the record id is derived from <paramref name="n"/>.</summary>
    public LogEnvelope Envelope(long n, DeviceId device, string kind = RecordKinds.Health) => new(
        TestIds.Uuid(1_000_000 + n),
        kind,
        TestTime.Start.AddMilliseconds(n),
        Src: null,
        Partitioning.Of(device),
        device,
        Srv: null,
        new RecordOrigin(RecordOriginType.Connection, TestIds.Uuid(7)),
        RecordCorrelation.None,
        Body(kind, n));

    public LogEnvelope GlobalEnvelope(long n) => new(
        TestIds.Uuid(2_000_000 + n),
        RecordKinds.RegistryChanged,
        TestTime.Start.AddMilliseconds(n),
        Src: null,
        P: 0,
        Dev: null,
        Srv: null,
        RecordOrigin.System,
        RecordCorrelation.None,
        Registry.SerializeBody(RecordKinds.RegistryChanged, new RegistryChangedBody(n, new RegistryChangedContext(null, null, null), [])));

    public void Dispose()
    {
        _writer.Dispose();
        _metrics.Dispose();
        _loggerFactory.Dispose();
        Logs.Dispose();
    }

    private JsonElement Body(string kind, long n) => kind switch
    {
        RecordKinds.Health => Registry.SerializeBody(kind, new HealthRecordBody(Kernel.Values.Connectivity.Online, [], null)),
        AlarmRaised or AlarmCleared => Registry.SerializeBody(kind, new AlarmBody("alarm-" + n, 3)),
        _ => JsonDocument.Parse("{}").RootElement.Clone(),
    };
}

/// <summary>A body type of another module, registered by the tests.</summary>
internal sealed record AlarmBody(string Rule, int Priority);

[JsonSerializable(typeof(AlarmBody))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
    /// <summary>The metadata of a type with the <c>Log</c> JSON profile.</summary>
    public static JsonTypeInfo<T> LogProfile<T>() =>
        (JsonTypeInfo<T>)WatchpointJson.Create(JsonProfile.Log, Default).GetTypeInfo(typeof(T));
}

internal static class HarnessExtensions
{
    /// <summary>
    /// Advances the fake clock in steps until <paramref name="done"/> holds: the code under test waits on fake timers
    /// (retry delays, timeouts), and each step lets the next one fire.
    /// </summary>
    public static async Task PumpUntilAsync(this PipelineHarness harness, Func<bool> done, TimeSpan? step = null)
    {
        for (var i = 0; i < 100_000; i++)
        {
            if (done())
            {
                return;
            }

            harness.Time.Advance(step ?? harness.Options.RetryDelayMin);
            await Task.Yield();
        }

        throw new TimeoutException("The condition was not met while advancing the fake clock.");
    }

    public static async Task<T> PumpAsync<T>(this PipelineHarness harness, Task<T> task, TimeSpan? step = null)
    {
        await harness.PumpUntilAsync(() => task.IsCompleted, step);
        return await task;
    }

    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }
}
