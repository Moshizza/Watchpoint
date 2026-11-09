using Watchpoint.Integration.Simulator.Engine.Behaviours;
using Watchpoint.Integration.Simulator.Engine.Scenario;

namespace Watchpoint.Integration.Simulator.Engine;

public sealed record CommandExecutionResult(
    bool Success,
    string? FailureCategory = null,
    string? MessageKey = null,
    IReadOnlyDictionary<string, object?>? MessageArgs = null,
    string? VendorCode = null);

public sealed class SimulatedSite
{
    private readonly object _sync = new();
    private readonly SimulatorSettings _settings;
    private readonly TimeZoneInfo _timeZone;
    private readonly FailureInjector _failureInjector;
    private readonly ScenarioPlayer? _scenarioPlayer;

    private readonly List<SimEventRecord> _eventLog = new();
    private const int MaxLogRetained = 100_000;
    private ulong _nextSeq = 1;

    private readonly Dictionary<string, DeviceRuntimeState> _deviceStates = new();
    private SimInventory _inventory;
    private bool _activityPaused;

    public SimInventory Inventory => _inventory;
    public ulong LastSeq { get { lock (_sync) return _nextSeq - 1; } }
    public FailureInjector FailureInjector => _failureInjector;

    public event Action<SimEventRecord>? EventEmitted;
    public event Action<SimChangeRecord>? ChangeEmitted;
    public event Action<SimHealthRecord>? HealthEmitted;
    public event Action<SimInventory>? InventoryChanged;

    public SimulatedSite(SimulatorSettings settings)
    {
        _settings = settings;
        _failureInjector = new FailureInjector(settings.EffectiveFailures);
        _scenarioPlayer = ScenarioPlayer.TryParse(settings.Scenario);

        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        }
        catch
        {
            _timeZone = TimeZoneInfo.Utc;
        }

        _inventory = SiteGenerator.Generate(settings);
        InitializeDeviceStates();
    }

    private void InitializeDeviceStates()
    {
        _deviceStates.Clear();
        foreach (var dev in _inventory.Devices)
        {
            int devIndex = ExtractDeviceIndex(dev.NativeId);
            int seed = _settings.Seed ^ devIndex;
            var rand = new Random(seed);
            _deviceStates[dev.NativeId] = new DeviceRuntimeState(dev, rand);
        }
    }

    private static int ExtractDeviceIndex(string nativeId)
    {
        int index = 0;
        for (int i = 0; i < nativeId.Length; i++)
        {
            if (char.IsDigit(nativeId[i]))
            {
                index = index * 10 + (nativeId[i] - '0');
            }
        }
        return index == 0 ? nativeId.GetHashCode() : index;
    }

    public void PauseActivity() => _activityPaused = true;
    public void ResumeActivity() => _activityPaused = false;

    public void Regenerate(int? newSeed = null)
    {
        lock (_sync)
        {
            var updatedSettings = newSeed.HasValue
                ? _settings with { Seed = newSeed.Value }
                : _settings;

            _inventory = SiteGenerator.Generate(updatedSettings);
            InitializeDeviceStates();
            InventoryChanged?.Invoke(_inventory);
        }
    }

    public bool IsBusyHour(DateTimeOffset nowUtc)
    {
        var localTime = TimeZoneInfo.ConvertTime(nowUtc, _timeZone);
        if (localTime.DayOfWeek == DayOfWeek.Saturday || localTime.DayOfWeek == DayOfWeek.Sunday)
        {
            return false;
        }

        var timeOfDay = localTime.TimeOfDay;
        return timeOfDay >= TimeSpan.FromHours(8) && timeOfDay < TimeSpan.FromHours(18);
    }

    public void Tick(TimeSpan delta, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_scenarioPlayer != null)
            {
                var scenarioSteps = _scenarioPlayer.Advance(delta);
                foreach (var step in scenarioSteps)
                {
                    ExecuteScenarioStep(step, now);
                }

                if (!_scenarioPlayer.WithRandom)
                {
                    return;
                }
            }

            if (_activityPaused)
            {
                return;
            }

            bool busyHour = IsBusyHour(now);
            var rates = ActivityRates.ForActivity(_settings.Activity);
            double rateMultiplier = busyHour ? 1.0 : 0.1;

            foreach (var state in _deviceStates.Values)
            {
                state.Tick(delta, now, rates, rateMultiplier, this);
            }
        }
    }

    private void ExecuteScenarioStep(ScenarioStep step, DateTimeOffset now)
    {
        if (step.Device != null && _deviceStates.TryGetValue(step.Device, out var devState))
        {
            if (step.Set != null)
            {
                foreach (var (k, v) in step.Set)
                {
                    devState.Properties[k] = v;
                }
                EmitChange(step.Device, step.Set, now);
            }

            if (step.Event != null)
            {
                EmitEvent(step.Device, step.Event, step.Payload ?? new Dictionary<string, object?>(), now);
            }
        }
    }

    public ulong EmitEvent(string device, string eventName, IReadOnlyDictionary<string, object?> payload, DateTimeOffset at)
    {
        SimEventRecord record;
        lock (_sync)
        {
            record = new SimEventRecord(_nextSeq++, device, eventName, payload, at);
            _eventLog.Add(record);
            if (_eventLog.Count > MaxLogRetained)
            {
                _eventLog.RemoveAt(0);
            }
        }

        EventEmitted?.Invoke(record);
        return record.Seq;
    }

    public ulong EmitChange(string device, IReadOnlyDictionary<string, object?> props, DateTimeOffset at)
    {
        SimChangeRecord record;
        lock (_sync)
        {
            record = new SimChangeRecord(_nextSeq++, device, props, at);
            if (_deviceStates.TryGetValue(device, out var devState))
            {
                foreach (var (k, v) in props)
                {
                    devState.Properties[k] = v;
                }
            }
        }

        ChangeEmitted?.Invoke(record);
        return record.Seq;
    }

    public void EmitHealth(string nativeId, bool isServer, string connectivity, IReadOnlyList<string>? faults, string? reason, DateTimeOffset at)
    {
        var record = new SimHealthRecord(nativeId, isServer, connectivity, faults, reason, at);
        HealthEmitted?.Invoke(record);
    }

    public IReadOnlyList<SimEventRecord> GetEventsAfter(ulong afterSeq)
    {
        lock (_sync)
        {
            var results = new List<SimEventRecord>();
            for (int i = 0; i < _eventLog.Count; i++)
            {
                if (_eventLog[i].Seq > afterSeq)
                {
                    results.Add(_eventLog[i]);
                }
            }
            return results;
        }
    }

    public bool HasDevice(string deviceId)
    {
        lock (_sync) return _deviceStates.ContainsKey(deviceId);
    }

    public DeviceRuntimeState? GetDeviceState(string deviceId)
    {
        lock (_sync)
        {
            return _deviceStates.TryGetValue(deviceId, out var s) ? s : null;
        }
    }

    public Dictionary<string, IReadOnlyDictionary<string, object?>> GetSnapshot(IReadOnlyList<string>? deviceIds = null)
    {
        lock (_sync)
        {
            var snapshot = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            if (deviceIds != null && deviceIds.Count > 0)
            {
                foreach (var id in deviceIds)
                {
                    if (_deviceStates.TryGetValue(id, out var s))
                    {
                        snapshot[id] = new Dictionary<string, object?>(s.Properties);
                    }
                }
            }
            else
            {
                foreach (var (id, s) in _deviceStates)
                {
                    snapshot[id] = new Dictionary<string, object?>(s.Properties);
                }
            }
            return snapshot;
        }
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(
        string deviceId,
        string command,
        IReadOnlyDictionary<string, object?> args,
        DateTimeOffset now,
        CancellationToken ct)
    {
        DeviceRuntimeState devState;
        Random rand;
        lock (_sync)
        {
            if (!_deviceStates.TryGetValue(deviceId, out var s))
            {
                return new CommandExecutionResult(false, "DeviceOffline", "sim.errors.notFound");
            }
            devState = s;
            rand = devState.Random;
        }

        var injected = _failureInjector.CheckFailure(command, rand);
        if (injected.HasValue)
        {
            switch (injected.Value)
            {
                case FailureMode.Refuse:
                    return new CommandExecutionResult(false, "RefusedBySystem", "sim.errors.refused");
                case FailureMode.Timeout:
                    var tcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    {
                        return await tcs.Task;
                    }
                case FailureMode.Error:
                    throw new InvalidOperationException($"Simulated error on command '{command}' for device '{deviceId}'");
                case FailureMode.NoEffect:
                    return new CommandExecutionResult(true);
            }
        }

        if (!devState.IsOnline)
        {
            return new CommandExecutionResult(false, "DeviceOffline", "sim.errors.offline");
        }

        return devState.ExecuteCommand(command, args, now, this);
    }
}
