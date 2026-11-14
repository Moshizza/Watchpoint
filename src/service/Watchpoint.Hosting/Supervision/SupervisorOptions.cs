namespace Watchpoint.Hosting.Supervision;

/// <summary>Settings of the <see cref="Supervisor"/>.</summary>
public sealed record SupervisorOptions
{
    /// <summary>Gets the folder of the pid files (<c>&lt;Data&gt;/run</c>).</summary>
    public required string RunDirectory { get; init; }

    /// <summary>Gets the pause between two attempts of a TCP or HTTP readiness probe.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Gets how long the supervisor waits for a killed process to disappear before it gives up on it.</summary>
    public TimeSpan KillTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long the supervisor waits for the output of an exited child to be read and logged.</summary>
    public TimeSpan OutputDrainTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the number of output lines of one child that may wait for the logger; more are dropped (event 1155).</summary>
    public int OutputQueueCapacity { get; init; } = 1024;

    /// <summary>Gets the source of the restart jitter: uniform samples in [0, 1). Default <see cref="Random.Shared"/>.</summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;
}
