namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class HealthBehaviour
{
    public static void Tick(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        if (state.Device.NativeId == "sim-root")
        {
            state.IsOnline = true;
            return;
        }

        if (state.IsOnline)
        {
            double offlinePerHour = rates.DeviceOfflinePerHour * rateMultiplier;
            double p = offlinePerHour * delta.TotalHours;
            if (state.Random.NextDouble() < p)
            {
                state.IsOnline = false;
                int minutes = state.Random.Next(1, 11);
                state.OfflineTimer = TimeSpan.FromMinutes(minutes);

                site.EmitHealth(state.Device.NativeId, false, "disconnected", null, "connectionLost", now);
            }
        }
        else
        {
            if (state.OfflineTimer.HasValue)
            {
                state.OfflineTimer -= delta;
                if (state.OfflineTimer <= TimeSpan.Zero)
                {
                    state.OfflineTimer = null;
                    state.IsOnline = true;
                    site.EmitHealth(state.Device.NativeId, false, "connected", null, "restored", now);
                }
            }
        }
    }
}
