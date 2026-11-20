namespace Watchpoint.Integrations.Contracts;

/// <summary>Settings of the connection runtime (spec IRT §7.2, §7.3, §8, §9: keys <c>Connections:*</c>).</summary>
public sealed class ConnectionsOptions
{
    /// <summary>Gets or sets the delay between the starts of two enabled connections at the <c>Connections</c> phase (<c>Connections:StartStagger</c>).</summary>
    public TimeSpan StartStagger { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets or sets how long a connection may reconnect before it shows as faulted (<c>Connections:FaultAfter</c>).</summary>
    public TimeSpan FaultAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets how long a connected connector may stay silent before a warning (<c>Connections:SilenceTimeout</c>).</summary>
    public TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets or sets the sink buffer per connection, in observations (<c>Connections:SinkBuffer</c>); the overflow holds as many again.</summary>
    public int SinkBuffer { get; set; } = 50_000;

    /// <summary>Gets or sets the longest delay between two restarts after faults (<c>Connections:MaxRestartBackoff</c>).</summary>
    public TimeSpan MaxRestartBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the first delay of the restart back-off (doubles per consecutive fault). Not a setting.</summary>
    public TimeSpan InitialRestartBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the time <c>StartAsync</c> has to return (INT L5). Not a setting.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the time <c>InitializeAsync</c> has to return. Not a setting.</summary>
    public TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the time <c>StopAsync</c>, then <c>DisposeAsync</c>, have before the instance is abandoned. Not a setting.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the time <c>TestAsync</c> has (INT L5). Not a setting.</summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the time <c>DescribeAsync</c> has. Not a setting.</summary>
    public TimeSpan DescribeTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the watchdog period. Not a setting.</summary>
    public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets how long after its deadline a command call counts as hung. Not a setting.</summary>
    public TimeSpan HungCommandGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the jitter of the restart back-off (±, as a fraction). Not a setting.</summary>
    public double RestartJitter { get; set; } = 0.2;

    /// <summary>Gets or sets the culture given to connectors (<see cref="Integration.Abstractions.Context.ConnectionInfo.Culture"/>).</summary>
    public string Culture { get; set; } = "en";
}
