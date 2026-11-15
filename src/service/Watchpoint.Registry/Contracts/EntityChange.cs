namespace Watchpoint.Registry.Contracts;

/// <summary>One entity changed by an applied batch (spec REG §6).</summary>
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
