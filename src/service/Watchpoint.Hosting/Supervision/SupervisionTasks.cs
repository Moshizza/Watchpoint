namespace Watchpoint.Hosting.Supervision;

/// <summary>Kills the children left by a host that died, before anything is started (spec HST §7.3).</summary>
public sealed class LeftoverCleanupStartupTask(ChildProcessJanitor janitor) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Infrastructure;

    /// <inheritdoc />
    public int Order => -1000;

    /// <inheritdoc />
    public string Name => "LeftoverCleanup";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => janitor.CleanupAllAsync(ct);
}

/// <summary>Stops the supervised children when the node shuts down, after every module (phases run in descending order).</summary>
public sealed class SupervisorShutdownTask(Supervisor supervisor) : IShutdownTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Infrastructure;

    /// <inheritdoc />
    public string Name => "Supervisor";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => supervisor.StopAllAsync(ct);
}
