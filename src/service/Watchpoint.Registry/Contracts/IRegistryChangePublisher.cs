namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Publishes applied registry batches to the event log as <c>registry.changed</c> records (spec REG §6, PIP §5.3).
/// The pipeline module replaces the default no-op implementation.
/// </summary>
public interface IRegistryChangePublisher
{
    /// <summary>
    /// Publishes an applied change batch. Called after the batch was committed, in apply order.
    /// </summary>
    /// <param name="result">The revision after the batch and the entities it changed.</param>
    /// <param name="context">The change context.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(RegistryApplyResult result, ChangeContext context, CancellationToken ct = default);
}

/// <summary>What an applied batch changed (spec REG §6).</summary>
/// <param name="Revision">The registry revision after the batch: incremented by one per applied batch since the node started.</param>
/// <param name="Changes">The entities named by the batch, in batch order.</param>
public sealed record RegistryApplyResult(long Revision, IReadOnlyList<EntityChange> Changes);

/// <summary>One entity changed by a batch.</summary>
/// <param name="Entity">The entity type: <c>site</c>, <c>location</c>, <c>tagFamily</c>, <c>tag</c>, <c>server</c>, <c>device</c>, <c>link</c> or <c>customField</c>.</param>
/// <param name="Id">The entity id (for a link: its source device).</param>
/// <param name="Kind">What happened to the entity.</param>
public sealed record EntityChange(string Entity, Guid Id, ChangeKind Kind);

/// <summary>What happened to an entity.</summary>
public enum ChangeKind
{
    /// <summary>The entity was created.</summary>
    Created = 0,

    /// <summary>The entity was updated.</summary>
    Updated = 1,

    /// <summary>The entity was deleted.</summary>
    Deleted = 2,
}
