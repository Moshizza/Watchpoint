using Watchpoint.Storage.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>The dirty set of one partition, collected on its worker (spec STA §8.1).</summary>
/// <param name="Partition">The partition.</param>
/// <param name="Sequence">The partition sequence at collection: the checkpoint of the snapshot.</param>
/// <param name="Upserts">The persisted form of every changed entity.</param>
/// <param name="Deletes">The ids removed since the previous collection.</param>
/// <param name="Changed">The changed ids, to merge back when the write fails.</param>
/// <param name="Deleted">The removed ids, to merge back when the write fails.</param>
internal sealed record PartitionSnapshot(
    int Partition,
    long Sequence,
    IReadOnlyList<StateItem> Upserts,
    IReadOnlyList<Guid> Deletes,
    IReadOnlyCollection<Guid> Changed,
    IReadOnlyCollection<Guid> Deleted);
