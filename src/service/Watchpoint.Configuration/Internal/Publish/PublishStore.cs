using System.Data.Common;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Configuration.Internal.Publish;

/// <summary>A published version as stored (<c>wp_config.versions</c> + <c>version_entries</c>, spec CFG §7.1 step 5, §11).</summary>
/// <param name="Version">The version row.</param>
/// <param name="ChangeSet">The change set it applied.</param>
/// <param name="Entries">What was applied, documents before and after.</param>
/// <param name="Inverse">The entries that undo it (rollback, §7.3).</param>
/// <param name="Diff">The diff (§7.4).</param>
internal sealed record VersionRecord(ConfigVersion Version, ChangeSetId ChangeSet, IReadOnlyList<AppliedEntry> Entries, IReadOnlyList<DraftEntry> Inverse,
    IReadOnlyList<DiffEntry> Diff);

/// <summary>The versions in the configuration database, and the publish transaction.</summary>
internal interface IPublishStore
{
    /// <summary>
    /// Opens the configuration database connection and transaction, and takes the publish lock (one publish at a time per
    /// site, released with the transaction).
    /// </summary>
    Task<IPublishTransaction> BeginAsync(CancellationToken ct);

    Task<VersionRecord?> GetAsync(long version, CancellationToken ct);

    /// <summary>Versions matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<ConfigVersion>> ListAsync(VersionQuery query, int skip, int take, CancellationToken ct);

    /// <summary>The versions after <paramref name="version"/>, newest first.</summary>
    Task<IReadOnlyList<VersionRecord>> AfterAsync(long version, CancellationToken ct);

    /// <summary>The last applied entry of an object at or before <paramref name="version"/>.</summary>
    Task<AppliedEntry?> ObjectAtAsync(ConfigObjectRef obj, long version, CancellationToken ct);

    /// <summary>The applied entry that gave an object the version <paramref name="objectVersion"/> (the base of a three-way merge, §8).</summary>
    Task<AppliedEntry?> RevisionAsync(ConfigObjectRef obj, long objectVersion, CancellationToken ct);
}

/// <summary>A publish transaction, holding the publish lock until it is committed or disposed (rolled back).</summary>
internal interface IPublishTransaction : IAsyncDisposable
{
    DbConnection Connection { get; }

    DbTransaction Transaction { get; }

    /// <summary>The next version number (inside the lock).</summary>
    Task<long> NextVersionAsync(CancellationToken ct);

    Task WriteAsync(VersionRecord record, CancellationToken ct);

    Task CommitAsync(CancellationToken ct);
}
