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
