using System.Text.Json;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Mqtt.Mapping;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Mqtt;

public sealed class DynamicInventory
{
    private const string InventoryStoreKey = "inventory";
    private readonly IConnectorContext _context;
    private readonly int _maxDevices;
    private readonly object _lock = new();
    private readonly Dictionary<string, InventoryDeviceItem> _devices = new(StringComparer.Ordinal);
    private ITimer? _debounceTimer;
    private bool _hasPendingChanges;
    private bool _serverReported;

    public DynamicInventory(IConnectorContext context, int maxDevices = 10000)
    {
        _context = context;
        _maxDevices = maxDevices;
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var bytes = await _context.Store.GetAsync(InventoryStoreKey, ct).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0) return;

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("devices", out var devArr) && devArr.ValueKind == JsonValueKind.Array)
            {
                lock (_lock)
                {
                    foreach (var item in devArr.EnumerateArray())
                    {
                        var nativeId = item.GetProperty("nativeId").GetString()!;
                        var type = item.GetProperty("type").GetString()!;
                        var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString()! : nativeId;
                        _devices[nativeId] = new InventoryDeviceItem(nativeId, "broker", null, type, "1.0.0", name, new Dictionary<string, WpValue>(), [], []);
                    }
                }
            }
        }
        catch
        {
        }
    }

    public void ReportBrokerServer(string brokerNativeId, string host, int port)
    {
        lock (_lock)
        {
            if (_serverReported) return;
            _serverReported = true;
        }

        var serverItem = new InventoryServerItem(
            NativeId: brokerNativeId,
            ParentNativeId: null,
            TypeId: "mqtt.broker",
            TypeVersion: "1.0.0",
            VendorName: $"MQTT Broker ({host}:{port})",
            VendorAttributes: new Dictionary<string, WpValue>());

        var snapshot = new InventorySnapshot(
            Servers: [serverItem],
            Devices: [],
            IsComplete: false);

        _context.Sink.ReportInventoryChanged(snapshot);
    }

    public bool TryTrackDevice(string nativeId, MappingRule rule, Dictionary<string, string> captures, out InventoryDeviceItem? deviceItem)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(nativeId, out var existing))
            {
                deviceItem = existing;
                return false;
            }

            if (_devices.Count >= _maxDevices)
            {
                deviceItem = null;
                return false;
            }

            deviceItem = CreateDeviceItem(nativeId, rule, captures);
            _devices[nativeId] = deviceItem;
            _hasPendingChanges = true;

            ScheduleDebouncedFlush();
            return true;
        }
    }

    private static InventoryDeviceItem CreateDeviceItem(string nativeId, MappingRule rule, Dictionary<string, string> captures)
    {
        var type = !string.IsNullOrEmpty(rule.DeviceTypeTemplate)
            ? ValueMapper.Substitute(rule.DeviceTypeTemplate, captures)
            : "mqtt.device";

        var name = !string.IsNullOrEmpty(rule.DeviceNameTemplate)
            ? ValueMapper.Substitute(rule.DeviceNameTemplate, captures)
            : null;

        return new InventoryDeviceItem(
            NativeId: nativeId,
            ServerNativeId: "broker",
            ParentNativeId: null,
            TypeId: type,
            TypeVersion: "1.0.0",
            VendorName: name ?? nativeId,
            VendorAttributes: new Dictionary<string, WpValue>(),
            UnsupportedOptionalCapabilities: [],
            Links: []);
    }

    private void ScheduleDebouncedFlush()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = _context.Time.CreateTimer(
            _ => FlushChanges(),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);
    }

    public void FlushPending()
    {
        lock (_lock)
        {
            if (!_hasPendingChanges) return;
            FlushChanges();
        }
    }

    private void FlushChanges()
    {
        List<InventoryDeviceItem> currentSnapshot;
        lock (_lock)
        {
            if (!_hasPendingChanges) return;
            _hasPendingChanges = false;
            currentSnapshot = _devices.Values.ToList();
        }

        var snapshot = new InventorySnapshot(
            Servers: [],
            Devices: currentSnapshot,
            IsComplete: false);

        _context.Sink.ReportInventoryChanged(snapshot);

        var serializable = new
        {
            devices = currentSnapshot.Select(d => new
            {
                nativeId = d.NativeId,
                type = d.TypeId,
                name = d.VendorName
            })
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(serializable));
        var vt = _context.Store.SetAsync(InventoryStoreKey, bytes, CancellationToken.None);
        if (!vt.IsCompletedSuccessfully)
        {
            _ = vt.AsTask();
        }
    }

    public IReadOnlyList<InventoryDeviceItem> GetAllDevices()
    {
        lock (_lock)
        {
            return _devices.Values.ToList();
        }
    }
}
