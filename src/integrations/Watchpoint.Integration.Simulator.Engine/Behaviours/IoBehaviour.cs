namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class IoBehaviour
{
    public static void TickInput(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        double hours = delta.TotalHours;
        double actP = rates.ZoneActivationsPerHour * rateMultiplier * hours;
        if (state.Random.NextDouble() < actP)
        {
            bool cur = state.Properties.TryGetValue("binaryInput.state", out var b) && b is true;
            bool nextVal = !cur;
            state.Properties["binaryInput.state"] = nextVal;

            site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
            {
                ["binaryInput.state"] = nextVal,
            }, now);
        }
    }
}
