namespace Watchpoint.Hosting.Supervision;

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
