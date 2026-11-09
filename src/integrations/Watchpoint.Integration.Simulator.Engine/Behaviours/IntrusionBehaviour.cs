namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class IntrusionBehaviour
{
    public static List<string> GetOpenZonesForArea(string areaId, SimulatedSite site)
    {
        var openZones = new List<string>();
        foreach (var dev in site.Inventory.Devices)
        {
            if (dev.TypeId == "intrusion.zone")
            {
                bool isMember = false;
                for (int i = 0; i < dev.Links.Count; i++)
                {
                    if (dev.Links[i].Kind == "memberOf" && dev.Links[i].ToNativeId == areaId)
                    {
                        isMember = true;
                        break;
                    }
                }

                if (isMember)
                {
                    var zoneState = site.GetDeviceState(dev.NativeId);
                    if (zoneState != null && !zoneState.IsBypassed)
                    {
                        string stateVal = zoneState.Properties.TryGetValue("zone.state", out var s) ? s?.ToString() ?? "normal" : "normal";
                        if (stateVal != "normal")
                        {
                            openZones.Add(dev.NativeId);
                        }
                    }
                }
            }
        }
        return openZones;
    }

    public static void TickArea(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        if (state.AreaState == "arming" && state.ExitDelayTimer.HasValue)
        {
            state.ExitDelayTimer -= delta;
            if (state.ExitDelayTimer <= TimeSpan.Zero)
            {
                state.ExitDelayTimer = null;
                state.AreaState = "full";
                state.Properties["armable.state"] = "full";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["armable.state"] = "full",
                }, now);
                site.EmitEvent(state.Device.NativeId, "armable.armed", new Dictionary<string, object?>
                {
                    ["mode"] = "full",
                }, now);
            }
        }
    }

    public static void TickZone(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        if (state.ZoneActiveTimer.HasValue)
        {
            state.ZoneActiveTimer -= delta;
            if (state.ZoneActiveTimer <= TimeSpan.Zero)
            {
                state.ZoneActiveTimer = null;
                state.Properties["zone.state"] = "normal";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["zone.state"] = "normal",
                }, now);
            }
            return;
        }

        if (state.ZoneFaultTimer.HasValue)
        {
            state.ZoneFaultTimer -= delta;
            if (state.ZoneFaultTimer <= TimeSpan.Zero)
            {
                state.ZoneFaultTimer = null;
                state.Properties["zone.state"] = "normal";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["zone.state"] = "normal",
                }, now);
            }
            return;
        }

        double hours = delta.TotalHours;
        double actP = rates.ZoneActivationsPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < actP)
        {
            int activeSec = state.Random.Next(2, 11);
            state.ZoneActiveTimer = TimeSpan.FromSeconds(activeSec);
            state.Properties["zone.state"] = "active";

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["zone.state"] = "active",
            }, now);
            site.EmitEvent(state.Device.NativeId, "zone.active", new Dictionary<string, object?>(), now);

            string? areaId = null;
            for (int i = 0; i < state.Device.Links.Count; i++)
            {
                if (state.Device.Links[i].Kind == "memberOf")
                {
                    areaId = state.Device.Links[i].ToNativeId;
                    break;
                }
            }

            if (areaId != null)
            {
                var areaState = site.GetDeviceState(areaId);
                if (areaState != null && (areaState.AreaState == "full" || areaState.AreaState == "partial"))
                {
                    areaState.AreaState = "alarm";
                    areaState.Properties["armable.state"] = "alarm";

                    site.EmitChange(areaId, new Dictionary<string, object?>
                    {
                        ["armable.state"] = "alarm",
                    }, now);
                    site.EmitEvent(areaId, "armable.alarm", new Dictionary<string, object?>
                    {
                        ["causeZone"] = state.Device.NativeId,
                    }, now);
                }
            }
            return;
        }

        double faultP = 0.001 * rateMultiplier * hours;
        if (state.Random.NextDouble() < faultP)
        {
            string faultKind = state.Random.Next(2) == 0 ? "tamper" : "masked";
            int faultMin = state.Random.Next(1, 6);
            state.ZoneFaultTimer = TimeSpan.FromMinutes(faultMin);
            state.Properties["zone.state"] = faultKind;

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["zone.state"] = faultKind,
            }, now);
        }
    }
}
