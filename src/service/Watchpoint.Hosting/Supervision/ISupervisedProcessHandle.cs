namespace Watchpoint.Hosting.Supervision;

/// <summary>A process kept alive by the <see cref="ISupervisor"/> (spec HST §7.1).</summary>
public interface ISupervisedProcessHandle
{
    /// <summary>Raised after every state transition, in order, with the new state. Handlers must not block.</summary>
    event EventHandler<SupervisedProcessState>? StateChanged;

    /// <summary>Gets the name from the <see cref="ProcessSpec"/>.</summary>
    string Name { get; }

    /// <summary>Gets the current state.</summary>
    SupervisedProcessState State { get; }

    /// <summary>Gets the process id while a process exists.</summary>
    int? Pid { get; }

    /// <summary>Gets the number of restarts since the last stable run (longer than the policy window) or manual restart.</summary>
    int RestartCount { get; }

    /// <summary>Gets when the process was last started.</summary>
    DateTimeOffset? LastStartedAt { get; }

    /// <summary>Gets why the process last exited or failed to start (never contains arguments or environment values).</summary>
    string? LastExitReason { get; }

    /// <summary>
    /// Stops the process for good: graceful stop signal, then kill after <see cref="ProcessSpec.StopGracePeriod"/>.
    /// Completes when the process is gone. Cancelling <paramref name="ct"/> kills immediately; the call never hangs.
    /// </summary>
    /// <param name="ct">Cancelling skips the rest of the grace period.</param>
    /// <returns>A task completing when the state is <see cref="SupervisedProcessState.Stopped"/>.</returns>
    Task StopAsync(CancellationToken ct);

    /// <summary>
    /// Stops the process if it runs and starts it again with a reset restart count (also from
    /// <see cref="SupervisedProcessState.Backoff"/> and <see cref="SupervisedProcessState.Failed"/>). Concurrent calls
    /// join the same restart. Completes when the process is <see cref="SupervisedProcessState.Running"/> again.
    /// </summary>
    /// <param name="ct">The cancellation token of the wait (the restart itself continues).</param>
    /// <returns>A task completing when the process is running again.</returns>
    /// <exception cref="SupervisorException">The process is stopped (<c>host.process.not_found</c>) or failed again.</exception>
    Task RestartAsync(CancellationToken ct);
}
