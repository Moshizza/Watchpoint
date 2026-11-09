namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public static class AnalogBehaviour
{
    public static void Tick(
        DeviceRuntimeState state,
        TimeSpan delta,
        DateTimeOffset now,
        ActivityRates rates,
        double rateMultiplier,
        SimulatedSite site)
    {
        state.AnalogStepTimer += delta;
        if (state.AnalogStepTimer < rates.AnalogStepInterval)
        {
            return;
        }

        state.AnalogStepTimer = TimeSpan.Zero;

        double hourOfDay = now.TimeOfDay.TotalHours;
        double dailySine = 2.0 * Math.Sin(2 * Math.PI * (hourOfDay - 6.0) / 24.0);

        double heatSpikeBonus = 0.0;
        if (state.HeatSpikeTimer.HasValue)
        {
            state.HeatSpikeTimer -= rates.AnalogStepInterval;
            if (state.HeatSpikeTimer <= TimeSpan.Zero)
            {
                state.HeatSpikeTimer = null;
            }
            else
            {
                double remainingFrac = state.HeatSpikeTimer.Value.TotalMinutes / 10.0;
                heatSpikeBonus = 12.0 * Math.Sin((1.0 - remainingFrac) * Math.PI);
            }
        }
        else
        {
            double spikeP = 0.001 * rateMultiplier * rates.AnalogStepInterval.TotalHours;
            if (state.Random.NextDouble() < spikeP)
            {
                state.HeatSpikeTimer = TimeSpan.FromMinutes(10);
            }
        }

        double u1 = 1.0 - state.Random.NextDouble();
        double u2 = 1.0 - state.Random.NextDouble();
        double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        double step = 0.2 * randStdNormal;

        double baseline = 21.0 + step;
        state.AnalogValue = Math.Round(baseline + dailySine + heatSpikeBonus, 2);
        state.Properties["measurement.value"] = state.AnalogValue;

        site.EmitChange(state.Device.NativeId, new Dictionary<string, object?>
        {
            ["measurement.value"] = state.AnalogValue,
        }, now);
    }
}
