using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Mapping;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Simulator.Embedded;

public sealed class EmbeddedTransport : IAsyncDisposable
{
    private readonly IConnectorContext _context;
    private readonly SimulatedSite _site;
    private IDisposable? _tickSubscription;

    public SimulatedSite Site => _site;

    public EmbeddedTransport(IConnectorContext context, SimulatorSettings settings, SimulatedSite? existingSite = null)
    {
        _context = context;
        _site = existingSite ?? new SimulatedSite(settings);

        _site.EventEmitted += OnEventEmitted;
        _site.ChangeEmitted += OnChangeEmitted;
        _site.HealthEmitted += OnHealthEmitted;
        _site.InventoryChanged += OnInventoryChanged;
    }

    public void Start()
    {
        var options = new PollingOptions(Jitter: 0.0, RunWhenDisconnected: true);
        _tickSubscription = _context.Scheduler.SchedulePolling(
            "sim.tick",
            TimeSpan.FromMilliseconds(250),
            ct =>
            {
                var now = _context.Time.GetUtcNow();
                _site.Tick(TimeSpan.FromMilliseconds(250), now);
                return ValueTask.CompletedTask;
            },
            options);
    }

    private void OnEventEmitted(SimEventRecord record)
    {
        var deviceKey = FindDeviceKey(record.Device);
        var payloadDict = SimMapping.ToWpValueDict(record.Payload);
        _context.Sink.ReportEvent(deviceKey, record.Event, payloadDict, record.At);
    }

    private void OnChangeEmitted(SimChangeRecord record)
    {
        var deviceKey = FindDeviceKey(record.Device);
        foreach (var (k, v) in record.Props)
        {
            var wpVal = SimMapping.ToWpValue(v);
            _context.Sink.ReportProperty(deviceKey, k, wpVal, Quality.Good, record.At);
        }
    }

    private void OnHealthEmitted(SimHealthRecord record)
    {
        var connectivity = record.Connectivity switch
        {
            "connected" => Connectivity.Online,
            "disconnected" => Connectivity.Offline,
            _ => Connectivity.Unknown,
        };

        if (record.IsServer)
        {
            _context.Sink.ReportServerHealth(record.NativeId, connectivity, record.Faults, record.Reason);
        }
        else
        {
            var deviceKey = FindDeviceKey(record.NativeId);
            _context.Sink.ReportDeviceHealth(deviceKey, connectivity, record.Faults, record.Reason);
        }
    }

    private void OnInventoryChanged(SimInventory newInventory)
    {
        var snapshot = SimMapping.ToInventorySnapshot(newInventory);
        _context.Sink.ReportInventoryChanged(snapshot);
    }

    private DeviceKey FindDeviceKey(string nativeId)
    {
        if (_context.Devices.TryGet(new DeviceKey("sim-root", nativeId), out var handle))
        {
            return handle.Key;
        }

        foreach (var d in _context.Devices.All)
        {
            if (d.Key.NativeId == nativeId)
            {
                return d.Key;
            }
        }

        foreach (var dev in _site.Inventory.Devices)
        {
            if (dev.NativeId == nativeId)
            {
                return new DeviceKey(dev.ServerNativeId, nativeId);
            }
        }

        return new DeviceKey("sim-root", nativeId);
    }

    public ValueTask DisposeAsync()
    {
        _tickSubscription?.Dispose();
        _site.EventEmitted -= OnEventEmitted;
        _site.ChangeEmitted -= OnChangeEmitted;
        _site.HealthEmitted -= OnHealthEmitted;
        _site.InventoryChanged -= OnInventoryChanged;
        return ValueTask.CompletedTask;
    }
}
