using Watchpoint.Integration.Simulator.Engine.Behaviours;

namespace Watchpoint.Integration.Simulator.Engine;

public sealed class DeviceRuntimeState
{
    public SimDevice Device { get; }
    public Random Random { get; }
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.Ordinal);
    public bool IsOnline { get; set; } = true;

    public TimeSpan? DoorCloseTimer { get; set; }
    public TimeSpan? HeldOpenTimer { get; set; }
    public bool IsInPassage { get; set; }
    public bool IsHeldOpen { get; set; }
    public bool IsForced { get; set; }

    public string AreaState { get; set; } = "disarmed";
    public TimeSpan? ExitDelayTimer { get; set; }

    public TimeSpan? ZoneActiveTimer { get; set; }
    public TimeSpan? ZoneFaultTimer { get; set; }
    public bool IsBypassed { get; set; }

    public double AnalogValue { get; set; } = 21.0;
    public TimeSpan AnalogStepTimer { get; set; } = TimeSpan.Zero;
    public TimeSpan? HeatSpikeTimer { get; set; }

    public TimeSpan? OfflineTimer { get; set; }

    public DeviceRuntimeState(SimDevice device, Random random)
    {
        Device = device;
        Random = random;

        InitializeDefaultProperties();
    }

    private void InitializeDefaultProperties()
    {
        switch (Device.TypeId)
        {
            case "ac.door":
                Properties["doorPosition.position"] = "closed";
                Properties["lockable.state"] = "locked";
                Properties["doorMode.mode"] = "normal";
                break;
            case "ac.reader":
                Properties["health.status"] = "online";
                if (Random.NextDouble() < 0.02)
                {
                    Properties["battery.low"] = true;
                }
                break;
            case "intrusion.area":
                Properties["armable.state"] = "disarmed";
                break;
            case "intrusion.zone":
                Properties["zone.state"] = "normal";
                Properties["bypassable.bypassed"] = false;
                break;
            case "generic.binary-input":
                Properties["binaryInput.state"] = false;
                break;
            case "generic.binary-output":
                Properties["binaryOutput.state"] = false;
                break;
            case "generic.analog-input":
                Properties["measurement.value"] = AnalogValue;
                break;
        }
    }

    public void Tick(TimeSpan delta, DateTimeOffset now, ActivityRates rates, double rateMultiplier, SimulatedSite site)
    {
        HealthBehaviour.Tick(this, delta, now, rates, rateMultiplier, site);

        if (!IsOnline)
        {
            return;
        }

        switch (Device.TypeId)
        {
            case "ac.door":
                DoorBehaviour.Tick(this, delta, now, rates, rateMultiplier, site);
                break;
            case "intrusion.area":
                IntrusionBehaviour.TickArea(this, delta, now, rates, rateMultiplier, site);
                break;
            case "intrusion.zone":
                IntrusionBehaviour.TickZone(this, delta, now, rates, rateMultiplier, site);
                break;
            case "generic.binary-input":
                IoBehaviour.TickInput(this, delta, now, rates, rateMultiplier, site);
                break;
            case "generic.analog-input":
                AnalogBehaviour.Tick(this, delta, now, rates, rateMultiplier, site);
                break;
        }
    }

    public CommandExecutionResult ExecuteCommand(
        string command,
        IReadOnlyDictionary<string, object?> args,
        DateTimeOffset now,
        SimulatedSite site)
    {
        switch (command)
        {
            case "openable.open":
                {
                    string currentMode = Properties.TryGetValue("doorMode.mode", out var m) ? m?.ToString() ?? "normal" : "normal";
                    if (string.Equals(currentMode, "lockedDown", StringComparison.OrdinalIgnoreCase))
                    {
                        return new CommandExecutionResult(false, "InvalidState", "sim.errors.lockedDown");
                    }

                    int durationSec = 5;
                    if (args.TryGetValue("duration", out var durObj))
                    {
                        if (durObj is int d) durationSec = d;
                        else if (int.TryParse(durObj?.ToString(), out var dParsed)) durationSec = dParsed;
                    }

                    Properties["lockable.state"] = "unlocked";
                    Properties["doorPosition.position"] = "open";
                    DoorCloseTimer = TimeSpan.FromSeconds(durationSec);

                    site.EmitEvent(Device.NativeId, "openable.opened", new Dictionary<string, object?>(), now);
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?>
                    {
                        ["lockable.state"] = "unlocked",
                        ["doorPosition.position"] = "open",
                    }, now);

                    return new CommandExecutionResult(true);
                }

            case "lockable.lock":
                {
                    Properties["lockable.state"] = "locked";
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["lockable.state"] = "locked" }, now);
                    return new CommandExecutionResult(true);
                }

            case "lockable.unlock":
                {
                    Properties["lockable.state"] = "unlocked";
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["lockable.state"] = "unlocked" }, now);
                    return new CommandExecutionResult(true);
                }

            case "doorMode.setMode":
                {
                    string newMode = args.TryGetValue("mode", out var mObj) ? mObj?.ToString() ?? "normal" : "normal";
                    Properties["doorMode.mode"] = newMode;
                    var changes = new Dictionary<string, object?> { ["doorMode.mode"] = newMode };

                    if (string.Equals(newMode, "emergencyOpen", StringComparison.OrdinalIgnoreCase))
                    {
                        Properties["lockable.state"] = "unlocked";
                        Properties["doorPosition.position"] = "open";
                        changes["lockable.state"] = "unlocked";
                        changes["doorPosition.position"] = "open";
                    }

                    site.EmitChange(Device.NativeId, changes, now);
                    return new CommandExecutionResult(true);
                }

            case "armable.arm":
                {
                    var openZones = IntrusionBehaviour.GetOpenZonesForArea(Device.NativeId, site);
                    if (openZones.Count > 0)
                    {
                        site.EmitEvent(Device.NativeId, "armable.armFailed", new Dictionary<string, object?>
                        {
                            ["openZones"] = openZones,
                        }, now);

                        return new CommandExecutionResult(
                            false,
                            "InvalidState",
                            "sim.errors.zonesOpen",
                            new Dictionary<string, object?> { ["zones"] = openZones, ["count"] = openZones.Count });
                    }

                    AreaState = "arming";
                    ExitDelayTimer = TimeSpan.FromSeconds(30);
                    Properties["armable.state"] = "arming";
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["armable.state"] = "arming" }, now);
                    return new CommandExecutionResult(true);
                }

            case "armable.disarm":
                {
                    AreaState = "disarmed";
                    ExitDelayTimer = null;
                    Properties["armable.state"] = "disarmed";
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["armable.state"] = "disarmed" }, now);
                    site.EmitEvent(Device.NativeId, "armable.disarmed", new Dictionary<string, object?>(), now);
                    return new CommandExecutionResult(true);
                }

            case "armable.cancelArming":
                {
                    if (AreaState != "arming" && AreaState != "exitDelay")
                    {
                        return new CommandExecutionResult(false, "InvalidState", "sim.errors.notArming");
                    }

                    AreaState = "disarmed";
                    ExitDelayTimer = null;
                    Properties["armable.state"] = "disarmed";
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["armable.state"] = "disarmed" }, now);
                    return new CommandExecutionResult(true);
                }

            case "bypassable.bypass":
                {
                    IsBypassed = true;
                    Properties["bypassable.bypassed"] = true;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["bypassable.bypassed"] = true }, now);
                    return new CommandExecutionResult(true);
                }

            case "bypassable.unbypass":
                {
                    IsBypassed = false;
                    Properties["bypassable.bypassed"] = false;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["bypassable.bypassed"] = false }, now);
                    return new CommandExecutionResult(true);
                }

            case "switchable.switchOn":
                {
                    Properties["binaryOutput.state"] = true;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["binaryOutput.state"] = true }, now);
                    return new CommandExecutionResult(true);
                }

            case "switchable.switchOff":
                {
                    Properties["binaryOutput.state"] = false;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["binaryOutput.state"] = false }, now);
                    return new CommandExecutionResult(true);
                }

            case "switchable.toggle":
                {
                    bool cur = Properties.TryGetValue("binaryOutput.state", out var b) && b is true;
                    Properties["binaryOutput.state"] = !cur;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["binaryOutput.state"] = !cur }, now);
                    return new CommandExecutionResult(true);
                }

            case "pulse.pulse":
                {
                    Properties["binaryOutput.state"] = true;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["binaryOutput.state"] = true }, now);
                    Properties["binaryOutput.state"] = false;
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?> { ["binaryOutput.state"] = false }, now);
                    return new CommandExecutionResult(true);
                }

            case "health.refresh":
                {
                    site.EmitChange(Device.NativeId, new Dictionary<string, object?>(Properties), now);
                    return new CommandExecutionResult(true);
                }

            default:
                return new CommandExecutionResult(false, "NotSupported", "sim.errors.notSupported");
        }
    }
}
