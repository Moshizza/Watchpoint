using Watchpoint.Storage.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>
/// A module with per-partition in-memory state (CMD pending commands, ALM active alarms and rule windows) that is
/// snapshotted on the cadence of the state module (spec STA §8.1) and restored inside its start-up task
/// <c>StateRestore</c> (spec STA §8.2). Registered in the service collection as <see cref="ISnapshotParticipant"/>.
/// </summary>
public interface ISnapshotParticipant
{
    /// <summary>Gets the checkpoint consumer of the participant, e.g. <c>commands</c>, <c>alarms</c>.</summary>
    string Consumer { get; }

    /// <summary>Gets the state item kind of the participant, e.g. <c>command</c>, <c>alarm</c>.</summary>
    string Kind { get; }

    /// <summary>
    /// Called on the partition worker: returns the items changed since the previous call (serialised) and the ones
    /// deleted. Must not do I/O.
    /// </summary>
    /// <param name="partition">The partition, 0…63.</param>
    /// <returns>The changes since the previous call.</returns>
    SnapshotContribution Collect(int partition);

    /// <summary>Called during recovery, before the replay, with every persisted item of the partition.</summary>
    /// <param name="partition">The partition, 0…63.</param>
    /// <param name="items">The persisted items of <see cref="Kind"/> in the partition.</param>
    void Restore(int partition, IReadOnlyList<StateItem> items);

    /// <summary>
    /// Called during recovery after <see cref="Restore"/> with the persisted checkpoint of <see cref="Consumer"/> for
    /// the partition (0 when none), so the participant's own partition subscriber can report it to the pipeline
    /// (<c>IPartitionSubscriber.GetRecoveryCheckpoint</c>).
    /// </summary>
    /// <param name="partition">The partition, 0…63.</param>
    /// <param name="checkpoint">The persisted checkpoint.</param>
    void RestoreCheckpoint(int partition, long checkpoint)
    {
    }
}

/// <summary>The changes of one partition of a snapshot participant (spec STA §8.1).</summary>
/// <param name="Upserts">The items to insert or replace.</param>
/// <param name="Deletes">The items to delete.</param>
public sealed record SnapshotContribution(IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes);
