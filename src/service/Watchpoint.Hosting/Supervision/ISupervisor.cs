namespace Watchpoint.Hosting.Supervision;

/// <summary>Starts helper processes and keeps them alive (spec HST §7).</summary>
public interface ISupervisor
{
    /// <summary>Gets the supervised processes in start order; stopped processes are removed.</summary>
    IReadOnlyCollection<ISupervisedProcessHandle> Processes { get; }

    /// <summary>
    /// Starts supervising a process. Completes when the child is <see cref="SupervisedProcessState.Running"/> for the
    /// first time, or <see cref="SupervisedProcessState.Failed"/> (the caller checks <see cref="ISupervisedProcessHandle.State"/>).
    /// Cancelling <paramref name="ct"/> before that stops the child.
    /// </summary>
    /// <param name="spec">The process to start.</param>
    /// <param name="ct">The cancellation token of the wait.</param>
    /// <returns>The handle of the supervised process.</returns>
    /// <exception cref="SupervisorException">The executable does not exist (<c>host.process.executable_missing</c>).</exception>
    /// <exception cref="InvalidOperationException">A process with the same name is supervised, or the supervisor is shutting down.</exception>
    Task<ISupervisedProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct);
}
