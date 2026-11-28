using System.Data.Common;
using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Contracts;

/// <summary>
/// Applies the published entries of one module (spec CFG §4, §7.1). Modules that own configuration register one
/// applier; publishing an entry of a type whose module has none is refused (<c>config.publish.not_supported</c>).
/// </summary>
public interface IConfigApplier
{
    /// <summary>Gets the module (as in <see cref="IConfigurableObjectType.Module"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Same name as IConfigurableObjectType.Module (spec CFG §4).")]
    string Module { get; }

    /// <summary>
    /// Writes the entries in the publish transaction and returns the new versions, with the documents before and after.
    /// Changes nothing in memory: the transaction may still be rolled back.
    /// </summary>
    /// <param name="entries">The module's entries, in change-set order.</param>
    /// <param name="connection">The open connection of the configuration database.</param>
    /// <param name="transaction">The publish transaction.</param>
    /// <param name="context">Who publishes, and the change set.</param>
    /// <param name="ct">Cancels the apply.</param>
    /// <returns>One applied entry per entry, or the reason the module refuses them (the publish is rolled back).</returns>
    Task<Result<IReadOnlyList<AppliedEntry>>> PrepareApplyAsync(IReadOnlyList<DraftEntry> entries, DbConnection connection, DbTransaction transaction,
        ConfigApplyContext context, CancellationToken ct);

    /// <summary>After the commit: updates the module's in-memory state. Must not fail; a failure is logged and followed by <see cref="ReloadAsync"/>.</summary>
    /// <param name="applied">What <see cref="PrepareApplyAsync"/> returned.</param>
    /// <param name="context">Who published, and the change set.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task.</returns>
    ValueTask CompleteApplyAsync(IReadOnlyList<AppliedEntry> applied, ConfigApplyContext context, CancellationToken ct);

    /// <summary>The publish transaction was rolled back after <see cref="PrepareApplyAsync"/> succeeded: forget what was prepared.</summary>
    /// <param name="context">The publish.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task.</returns>
    ValueTask AbortApplyAsync(ConfigApplyContext context, CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Reloads the listed objects from the database into memory (other nodes, M9; self-healing after a failed complete).</summary>
    /// <param name="objects">The objects.</param>
    /// <param name="ct">Cancels the reload.</param>
    /// <returns>A task.</returns>
    ValueTask ReloadAsync(IReadOnlyList<ConfigObjectRef> objects, CancellationToken ct);
}

/// <summary>The context of a publish, given to the appliers.</summary>
/// <param name="Actor">Who publishes.</param>
/// <param name="ChangeSet">The change set.</param>
/// <param name="Version">The configuration version being written.</param>
/// <param name="Comment">The publish comment.</param>
public sealed record ConfigApplyContext(UserId Actor, ChangeSetId ChangeSet, long Version, string? Comment)
{
    /// <summary>Gets the change origin, <c>publish:&lt;changeSetId&gt;</c> (CFG §5).</summary>
    public string Origin => "publish:" + ChangeSet.Value.ToString("D");
}

/// <summary>One changed value of a document (spec CFG §7.4).</summary>
/// <param name="Path">The JSON pointer; array items with an <c>id</c> are addressed by it.</param>
/// <param name="Before">The value before, or <see langword="null"/> when it was absent.</param>
/// <param name="After">The value after, or <see langword="null"/> when it is absent.</param>
public sealed record DiffChange(string Path, JsonElement? Before, JsonElement? After);

/// <summary>The diff of one object in a version (spec CFG §7.4).</summary>
/// <param name="Ref">The object.</param>
/// <param name="Operation">What was done.</param>
/// <param name="Changes">The changed values.</param>
public sealed record DiffEntry(ConfigObjectRef Ref, EntryOperation Operation, IReadOnlyList<DiffChange> Changes);

/// <summary>A published configuration version (spec CFG §7.3, table <c>wp_config.versions</c>).</summary>
/// <param name="Version">The version number (1, 2…).</param>
/// <param name="PublishedAt">When it was published.</param>
/// <param name="Author">Who published it.</param>
/// <param name="Comment">The publish comment.</param>
/// <param name="Draft">The draft it came from.</param>
/// <param name="Summary">The number of entries per <c>type:operation</c>.</param>
public sealed record ConfigVersion(long Version, DateTimeOffset PublishedAt, UserId Author, string? Comment, DraftId Draft, IReadOnlyDictionary<string, int> Summary);

/// <summary>A version with its entries and diff.</summary>
/// <param name="Version">The version.</param>
/// <param name="Entries">What was applied (documents before and after).</param>
/// <param name="Diff">The diff, one item per entry.</param>
public sealed record ConfigVersionDetail(ConfigVersion Version, IReadOnlyList<AppliedEntry> Entries, IReadOnlyList<DiffEntry> Diff);

/// <summary>A query on the versions, newest first.</summary>
/// <param name="Type">Only versions that changed an object of this type.</param>
/// <param name="ObjectId">Only versions that changed this object.</param>
/// <param name="From">Published at or after.</param>
/// <param name="To">Published before.</param>
public sealed record VersionQuery(string? Type = null, Guid? ObjectId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

/// <summary>Versions, diff and rollback (spec CFG §7.3).</summary>
public interface IVersionService
{
    /// <summary>Lists versions, newest first.</summary>
    /// <param name="query">The filter.</param>
    /// <param name="page">The page.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The page.</returns>
    Task<Page<ConfigVersion>> ListAsync(VersionQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Gets a version with its entries and diff.</summary>
    /// <param name="version">The version.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The version, or <c>config.version.not_found</c>.</returns>
    Task<Result<ConfigVersionDetail>> GetAsync(long version, CancellationToken ct);

    /// <summary>
    /// Creates a draft that undoes <paramref name="version"/> (<paramref name="restore"/> = false), or that undoes every
    /// version after it ("restore to version v", <paramref name="restore"/> = true). The draft is validated against the
    /// current state and published like any other.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <param name="actor">Who rolls back (the draft's owner).</param>
    /// <param name="restore">Whether to restore the configuration as it was right after <paramref name="version"/>.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The new draft, or <c>config.version.not_found</c>.</returns>
    Task<Result<DraftId>> CreateRollbackDraftAsync(long version, UserId actor, bool restore, CancellationToken ct);

    /// <summary>The document of an object as it was right after <paramref name="version"/>.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="version">The version.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The document, or <c>config.version.not_found</c> (unknown version, or the object did not exist then).</returns>
    Task<Result<JsonElement>> GetObjectAtVersionAsync(ConfigObjectRef obj, long version, CancellationToken ct);
}
