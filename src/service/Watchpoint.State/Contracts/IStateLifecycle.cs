namespace Watchpoint.State.Contracts;

/// <summary>
/// Snapshots and recovery of the state module (spec STA §8). The host calls <see cref="RestoreAsync"/> in start-up
/// phase <c>Recovery</c> (order 50, before the pipeline recovery at order 100), <see cref="StartSnapshots"/> in
/// phase <c>Runtime</c> after the partition workers started, and <see cref="StopAsync"/> at shutdown after the
/// workers drained.
/// </summary>
public interface IStateLifecycle
{
    /// <summary>Gets a value indicating whether the recovery completed for every partition (health check <c>ready</c>).</summary>
    bool Recovered { get; }

    /// <summary>
    /// Reads the snapshot of every owned partition and rebuilds the state from it and the registry (spec STA §8.2
    /// step 1); snapshot participants restore inside it. The pipeline then replays the log after the checkpoints.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the state is restored.</returns>
    Task RestoreAsync(CancellationToken ct);

    /// <summary>Starts writing a snapshot every <c>State:SnapshotInterval</c> (spec STA §8.1).</summary>
    void StartSnapshots();

    /// <summary>Stops the periodic snapshots and writes a final one (spec STA §8.1, shutdown).</summary>
    /// <param name="ct">Cancels the final snapshot.</param>
    /// <returns>A task completing when the final snapshot is written.</returns>
    Task StopAsync(CancellationToken ct);
}
