namespace Watchpoint.Storage.Contracts;

/// <summary>An entity with an optimistic concurrency token <c>row_version</c> (incremented by the base context on save; used for ETags).</summary>
public interface IVersioned
{
    /// <summary>Gets or sets the version; starts at 0 for new entities, +1 on every saved modification.</summary>
    long RowVersion { get; set; }
}

/// <summary>An entity with <c>created_at</c> and <c>updated_at</c>, set by the base context from the node clock.</summary>
public interface ITimestamped
{
    /// <summary>Gets or sets the creation time (UTC).</summary>
    DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the time of the last saved modification (UTC).</summary>
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>What a module context needs besides its EF options: module name, naming rules and clock.</summary>
/// <param name="Module">The module name, e.g. <c>Registry</c>.</param>
/// <param name="Naming">The naming rules of the provider.</param>
/// <param name="Clock">The node clock.</param>
public sealed record StorageContextInfo(string Module, IStorageNaming Naming, TimeProvider Clock);
