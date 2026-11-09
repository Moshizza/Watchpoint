namespace Watchpoint.Integration.Simulator.Engine;

public enum SiteProfile
{
    Office,
    Bank,
    Campus,
    Custom,
}

public enum SiteActivity
{
    Calm,
    Normal,
    Busy,
    Stress,
}

public enum FailureMode
{
    Refuse,
    Timeout,
    Error,
    NoEffect,
}

public sealed record FailureRule(string Command, FailureMode Mode, double Probability);

public sealed record SimulatorSettings(
    string Mode = "embedded",
    string Url = "ws://localhost:5090/sim",
    SiteProfile Profile = SiteProfile.Office,
    int? Doors = null,
    int ReadersPerDoor = 1,
    int Areas = 2,
    int ZonesPerArea = 5,
    int Inputs = 4,
    int Outputs = 4,
    int Analogs = 2,
    int Seed = 1,
    SiteActivity Activity = SiteActivity.Normal,
    TimeSpan? CommandLatency = null,
    IReadOnlyList<FailureRule>? Failures = null,
    string? Scenario = null,
    string TimeZone = "Europe/Paris",
    string? Token = null)
{
    public TimeSpan EffectiveCommandLatency => CommandLatency ?? TimeSpan.Zero;
    public IReadOnlyList<FailureRule> EffectiveFailures => Failures ?? [];
}
