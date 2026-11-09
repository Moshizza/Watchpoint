namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class DoorBehaviour
{
    public static void Tick(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        if (state.IsInPassage)
        {
            state.DoorCloseTimer -= delta;
            if (state.DoorCloseTimer <= TimeSpan.Zero)
            {
                state.IsInPassage = false;
                state.DoorCloseTimer = null;
                state.Properties["doorPosition.position"] = "closed";
                state.Properties["lockable.state"] = "locked";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["doorPosition.position"] = "closed",
                    ["lockable.state"] = "locked",
                }, now);
            }
            return;
        }

        if (state.IsHeldOpen)
        {
            state.HeldOpenTimer -= delta;
            if (state.HeldOpenTimer <= TimeSpan.Zero)
            {
                state.IsHeldOpen = false;
                state.HeldOpenTimer = null;
                state.Properties["doorPosition.position"] = "closed";
                state.Properties["lockable.state"] = "locked";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["doorPosition.position"] = "closed",
                    ["lockable.state"] = "locked",
                }, now);
            }
            return;
        }

        if (state.IsForced)
        {
            state.DoorCloseTimer -= delta;
            if (state.DoorCloseTimer <= TimeSpan.Zero)
            {
                state.IsForced = false;
                state.DoorCloseTimer = null;
                state.Properties["doorPosition.position"] = "closed";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["doorPosition.position"] = "closed",
                }, now);
            }
            return;
        }

        if (state.DoorCloseTimer.HasValue)
        {
            state.DoorCloseTimer -= delta;
            if (state.DoorCloseTimer <= TimeSpan.Zero)
            {
                state.DoorCloseTimer = null;
                state.Properties["doorPosition.position"] = "closed";
                state.Properties["lockable.state"] = "locked";

                site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
                {
                    ["doorPosition.position"] = "closed",
                    ["lockable.state"] = "locked",
                }, now);
            }
            return;
        }

        double hours = delta.TotalHours;

        double forcedP = rates.ForcedPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < forcedP)
        {
            state.IsForced = true;
            int closeSec = state.Random.Next(5, 21);
            state.DoorCloseTimer = TimeSpan.FromSeconds(closeSec);
            state.Properties["doorPosition.position"] = "forced";

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["doorPosition.position"] = "forced",
            }, now);
            site.EmitEvent(state.Device.NativeId, "doorPosition.forced", new Dictionary<string, object?>(), now);
            return;
        }

        double heldP = rates.HeldOpenPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < heldP)
        {
            state.IsHeldOpen = true;
            int heldSec = state.Random.Next(30, 121);
            state.HeldOpenTimer = TimeSpan.FromSeconds(heldSec);
            state.Properties["doorPosition.position"] = "heldOpen";

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["doorPosition.position"] = "heldOpen",
            }, now);
            site.EmitEvent(state.Device.NativeId, "doorPosition.heldOpen", new Dictionary<string, object?>(), now);
            return;
        }

        double deniedP = rates.DeniedPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < deniedP)
        {
            string readerId = $"{state.Device.NativeId}-r1";
            var (hRef, hName) = HolderCatalog.GetHolder(state.Random.Next(HolderCatalog.Names.Count));
            site.EmitEvent(readerId, "accessEvents.denied", new Dictionary<string, object?>
            {
                ["holderRef"] = hRef,
                ["holderName"] = hName,
                ["reason"] = "unauthorizedZone",
            }, now);
            return;
        }

        double passageP = rates.DoorPassagesPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < passageP)
        {
            state.IsInPassage = true;
            int openSec = state.Random.Next(3, 9);
            state.DoorCloseTimer = TimeSpan.FromSeconds(openSec);

            string readerId = $"{state.Device.NativeId}-r1";
            var (hRef, hName) = HolderCatalog.GetHolder(state.Random.Next(HolderCatalog.Names.Count));

            state.Properties["doorPosition.position"] = "open";
            state.Properties["lockable.state"] = "unlocked";

            site.EmitEvent(readerId, "accessEvents.granted", new Dictionary<string, object?>
            {
                ["holderRef"] = hRef,
                ["holderName"] = hName,
            }, now);

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["doorPosition.position"] = "open",
                ["lockable.state"] = "unlocked",
            }, now);
        }
    }
}
