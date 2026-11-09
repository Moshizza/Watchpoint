using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Simulator.Tests;

internal sealed class TestDeviceMap : IDeviceMap
{
    private readonly Dictionary<DeviceKey, DeviceHandle> _devices = new();

    public IReadOnlyCollection<DeviceHandle> All => _devices.Values;

    public bool TryGet(DeviceKey key, [NotNullWhen(true)] out DeviceHandle? handle) => _devices.TryGetValue(key, out handle);

    public event EventHandler<DeviceMapChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public void Add(DeviceHandle handle)
    {
        _devices[handle.Key] = handle;
    }
}

internal sealed class TestObservationSink : IObservationSink
{
    public List<(DeviceKey Device, string PropertyPath, WpValue Value, Quality Quality, DateTimeOffset? SourceTime)> Properties { get; } = [];
    public List<(DeviceKey Device, string EventPath, IReadOnlyDictionary<string, WpValue>? Payload, DateTimeOffset? SourceTime)> Events { get; } = [];
    public List<(DeviceKey Device, Connectivity Connectivity)> DeviceHealths { get; } = [];
    public List<(string ServerNativeId, Connectivity Connectivity)> ServerHealths { get; } = [];
    public List<InventorySnapshot> InventoryChanges { get; } = [];

    public int FlushCount { get; private set; }

    public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null)
    {
        Properties.Add((device, propertyPath, value, quality, sourceTime));
    }

    public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null)
    {
        Events.Add((device, eventPath, payload, sourceTime));
    }

    public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        DeviceHealths.Add((device, connectivity));
    }

    public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        ServerHealths.Add((serverNativeId, connectivity));
    }

    public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null)
    {
    }

    public void ReportInventoryChanged(InventorySnapshot partialOrComplete)
    {
        InventoryChanges.Add(partialOrComplete);
    }

    public ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct)
    {
        FlushCount++;
        return ValueTask.FromResult(new SinkFlushResult(Properties.Count + Events.Count, 0, 0));
    }
}

internal sealed class TestConnectionStore : IConnectionStore
{
    private readonly Dictionary<string, byte[]> _storage = new(StringComparer.Ordinal);
    public int FlushCountAtLastSave { get; private set; }
    private readonly TestObservationSink _sink;

    public TestConnectionStore(TestObservationSink sink)
    {
        _sink = sink;
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        _storage.TryGetValue(key, out var val);
        return ValueTask.FromResult(val);
    }

    public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        FlushCountAtLastSave = _sink.FlushCount;
        _storage[key] = value.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        _storage.Remove(key);
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestConnectorScheduler : IConnectorScheduler
{
    private readonly FakeTimeProvider _time;
    private readonly List<ScheduledJob> _jobs = [];

    public TestConnectorScheduler(FakeTimeProvider time)
    {
        _time = time;
    }

    public IDisposable SchedulePolling(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions? options = null)
    {
        var job = new ScheduledJob(name, interval, work, options ?? new PollingOptions());
        _jobs.Add(job);
        return job;
    }

    public async ValueTask TriggerAsync(TimeSpan delta, CancellationToken ct)
    {
        _time.Advance(delta);
        foreach (var job in _jobs)
        {
            await job.Work(ct);
        }
    }

    private sealed class ScheduledJob : IDisposable
    {
        public string Name { get; }
        public TimeSpan Interval { get; }
        public Func<CancellationToken, ValueTask> Work { get; }
        public PollingOptions Options { get; }

        public ScheduledJob(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions options)
        {
            Name = name;
            Interval = interval;
            Work = work;
            Options = options;
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class TestConnectorContext : IConnectorContext
{
    public ConnectionInfo Connection { get; } = new(Guid.NewGuid(), "TestSim", "watchpoint.integration.simulator", "1.0.0", "sim.server", "en-US");
    public JsonElement Settings { get; set; }
    public ISecretAccessor Secrets { get; } = null!;
    public TestObservationSink Sink { get; }
    public TestDeviceMap Devices { get; }
    public TestConnectionStore Store { get; }
    public TestConnectorScheduler Scheduler { get; }
    public IRequestGate Gate { get; } = null!;
    public IConnectionHealth Health { get; } = new TestConnectionHealth();
    public ISecureHttp Http { get; } = null!;
    public IInboundEndpoints Inbound { get; } = null!;
    public IConnectorMetrics Metrics { get; } = new TestMetrics();
    public ILoggerFactory Logging { get; } = NullLoggerFactory.Instance;
    public TimeProvider Time => FakeTime;
    public FakeTimeProvider FakeTime { get; }
    public CancellationToken Stopping => CancellationToken.None;

    IObservationSink IConnectorContext.Sink => Sink;
    IDeviceMap IConnectorContext.Devices => Devices;
    IConnectionStore IConnectorContext.Store => Store;
    IConnectorScheduler IConnectorContext.Scheduler => Scheduler;

    public TestConnectorContext(JsonElement settings, FakeTimeProvider time)
    {
        Settings = settings;
        FakeTime = time;
        Sink = new TestObservationSink();
        Devices = new TestDeviceMap();
        Store = new TestConnectionStore(Sink);
        Scheduler = new TestConnectorScheduler(time);
    }

    public T GetSettings<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Settings, typeInfo)!;
}

internal sealed class TestConnectionHealth : IConnectionHealth
{
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;
    public ConnectorMessage? Reason { get; private set; }

    public void Report(ConnectionStatus status, ConnectorMessage? reason = null)
    {
        Status = status;
        Reason = reason;
    }
}

internal sealed class TestMetrics : IConnectorMetrics
{
    public void CountRequest(string operation, bool success, TimeSpan duration) { }
    public void SetGauge(string name, double value) { }
}
