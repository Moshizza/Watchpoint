using System.Collections.Concurrent;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Recorded property observation in <see cref="TestObservationSink"/>.
/// </summary>
public sealed record RecordedProperty(
    DeviceKey Device,
    string PropertyPath,
    WpValue Value,
    Quality Quality,
    DateTimeOffset? SourceTime,
    string? Unit,
    Guid? CausedByCommand);

/// <summary>
/// Recorded event observation in <see cref="TestObservationSink"/>.
/// </summary>
public sealed record RecordedEvent(
    DeviceKey Device,
    string EventPath,
    IReadOnlyDictionary<string, WpValue>? Payload,
    DateTimeOffset? SourceTime);

/// <summary>
/// Recorded device health observation.
/// </summary>
public sealed record RecordedDeviceHealth(
    DeviceKey Device,
    Connectivity Connectivity,
    IReadOnlyList<string>? Faults,
    string? Reason);

/// <summary>
/// Recorded server health observation.
/// </summary>
public sealed record RecordedServerHealth(
    string ServerNativeId,
    Connectivity Connectivity,
    IReadOnlyList<string>? Faults,
    string? Reason);

/// <summary>
/// In-memory fake implementation of <see cref="IObservationSink"/>.
/// </summary>
public sealed class TestObservationSink : IObservationSink
{
    private readonly ConcurrentQueue<RecordedProperty> _properties = new();
    private readonly ConcurrentQueue<RecordedEvent> _events = new();
    private readonly ConcurrentQueue<RecordedDeviceHealth> _deviceHealths = new();
    private readonly ConcurrentQueue<RecordedServerHealth> _serverHealths = new();
    private readonly ConcurrentQueue<InventorySnapshot> _inventoryChanges = new();

    /// <summary>List of recorded property updates.</summary>
    public IReadOnlyList<RecordedProperty> Properties => _properties.ToArray();

    /// <summary>List of recorded events.</summary>
    public IReadOnlyList<RecordedEvent> Events => _events.ToArray();

    /// <summary>List of recorded device health updates.</summary>
    public IReadOnlyList<RecordedDeviceHealth> DeviceHealths => _deviceHealths.ToArray();

    /// <summary>List of recorded server health updates.</summary>
    public IReadOnlyList<RecordedServerHealth> ServerHealths => _serverHealths.ToArray();

    /// <summary>List of recorded inventory changes.</summary>
    public IReadOnlyList<InventorySnapshot> InventoryChanges => _inventoryChanges.ToArray();

    /// <summary>Total flush count.</summary>
    public int FlushCount { get; private set; }

    /// <summary>Optional simulated delay for FlushAsync to test backpressure.</summary>
    public TimeSpan FlushDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Whether calls after stop should be recorded or tracked.</summary>
    public bool TrackCallsAfterStop { get; set; }

    /// <summary>Number of sink calls recorded after stop.</summary>
    public int CallsAfterStopCount { get; private set; }

    /// <inheritdoc />
    public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
        _properties.Enqueue(new RecordedProperty(device, propertyPath, value, quality, sourceTime, unit, causedByCommand));
    }

    /// <inheritdoc />
    public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
        _events.Enqueue(new RecordedEvent(device, eventPath, payload, sourceTime));
    }

    /// <inheritdoc />
    public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
        _deviceHealths.Enqueue(new RecordedDeviceHealth(device, connectivity, faults, reason));
    }

    /// <inheritdoc />
    public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
        _serverHealths.Enqueue(new RecordedServerHealth(serverNativeId, connectivity, faults, reason));
    }

    /// <inheritdoc />
    public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
    }

    /// <inheritdoc />
    public void ReportInventoryChanged(InventorySnapshot partialOrComplete)
    {
        if (TrackCallsAfterStop)
        {
            CallsAfterStopCount++;
        }
        _inventoryChanges.Enqueue(partialOrComplete);
    }

    /// <inheritdoc />
    public async ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct)
    {
        if (FlushDelay > TimeSpan.Zero)
        {
            await Task.Delay(FlushDelay, ct);
        }

        FlushCount++;
        return new SinkFlushResult(_properties.Count + _events.Count, 0, 0);
    }

    /// <summary>Clears recorded observations.</summary>
    public void Clear()
    {
        _properties.Clear();
        _events.Clear();
        _deviceHealths.Clear();
        _serverHealths.Clear();
        _inventoryChanges.Clear();
    }
}
