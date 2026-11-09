namespace Watchpoint.Integration.Simulator.Engine.Behaviours;

public sealed class ActivityRates
{
    public double DoorPassagesPerHour { get; }
    public double DeniedPerHour { get; }
    public double HeldOpenPerHour { get; }
    public double ForcedPerHour { get; }
    public double ZoneActivationsPerHour { get; }
    public TimeSpan AnalogStepInterval { get; }
    public double DeviceOfflinePerHour { get; }

    public ActivityRates(
        double doorPassages,
        double denied,
        double heldOpen,
        double forced,
        double zoneActivations,
        TimeSpan analogStep,
        double deviceOffline)
    {
        DoorPassagesPerHour = doorPassages;
        DeniedPerHour = denied;
        HeldOpenPerHour = heldOpen;
        ForcedPerHour = forced;
        ZoneActivationsPerHour = zoneActivations;
        AnalogStepInterval = analogStep;
        DeviceOfflinePerHour = deviceOffline;
    }

    public static ActivityRates ForActivity(SiteActivity activity)
    {
        return activity switch
        {
            SiteActivity.Calm => new ActivityRates(2, 0.1, 0.02, 0.002, 1, TimeSpan.FromSeconds(60), 0.001),
            SiteActivity.Normal => new ActivityRates(10, 0.5, 0.1, 0.01, 5, TimeSpan.FromSeconds(10), 0.005),
            SiteActivity.Busy => new ActivityRates(60, 3, 0.5, 0.05, 30, TimeSpan.FromSeconds(2), 0.02),
            SiteActivity.Stress => new ActivityRates(3600, 100, 10, 1, 600, TimeSpan.FromSeconds(0.25), 0.1),
            _ => ForActivity(SiteActivity.Normal),
        };
    }
}
