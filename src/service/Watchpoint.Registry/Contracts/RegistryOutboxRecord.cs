namespace Watchpoint.Registry.Contracts;

/// <summary>
/// A <c>registry.changed</c> record waiting in the registry outbox (spec REG §6, §10): written in the transaction of the
/// batch it describes, published to the event log by the pipeline module in revision order.
/// </summary>
/// <param name="Revision">The registry revision after the batch: persisted, increased by one per committed batch, never reused.</param>
/// <param name="CreatedAt">When the batch was committed (node clock, UTC).</param>
/// <param name="Context">The change context of the batch.</param>
/// <param name="Changes">The entities named by the batch, in batch order.</param>
public sealed record RegistryOutboxRecord(long Revision, DateTimeOffset CreatedAt, ChangeContext Context, IReadOnlyList<EntityChange> Changes);
