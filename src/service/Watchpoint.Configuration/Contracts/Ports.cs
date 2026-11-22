using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Contracts;

/// <summary>Settings of the configuration module (spec CFG §12).</summary>
public sealed record ConfigurationSettings
{
    /// <summary>The section name.</summary>
    public const string SectionName = "Configuration";

    /// <summary>Gets <c>Configuration:MaxDraftEntries</c>.</summary>
    public int MaxDraftEntries { get; init; } = 20_000;

    /// <summary>Gets how many entries a validation job checks between two progress reports; drafts up to this size validate on every change.</summary>
    public int ValidationChunkSize { get; init; } = 100;

    /// <summary>Gets <c>Configuration:ApprovalRequiredFor</c>: the object types whose changes need approval before they are published.</summary>
    public IReadOnlyList<string> ApprovalRequiredFor { get; init; } = [];
}

/// <summary>The rights check of the configuration module (RGT, spec CFG §5): can a user do a permission, in the scope of an object?</summary>
public interface IConfigurationAuthorizer
{
    /// <summary>Checks a permission.</summary>
    /// <param name="user">The user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="scope">The object the right is needed for (for scope rules such as "devices of Building A only"), or <see langword="null"/> for the type as a whole.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    ValueTask<bool> IsAllowedAsync(UserId user, PermissionCode permission, ConfigObjectRef? scope, CancellationToken ct);
}

/// <summary>The licence hook (LIC, M7, spec CFG §5): problems when the draft would take the site over its licence.</summary>
public interface ILicenceProjection
{
    /// <summary>Projects the licence use after the draft is published.</summary>
    /// <param name="entries">The entries.</param>
    /// <param name="view">The draft view.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The problems (usually <c>config.licence.quota_exceeded</c>).</returns>
    ValueTask<IReadOnlyList<Problem>> ProjectAsync(IReadOnlyList<DraftEntry> entries, IDraftView view, CancellationToken ct);
}

/// <summary>Which changes need an approver before they are published (spec CFG §6.4, §7).</summary>
public interface IApprovalPolicy
{
    /// <summary>Returns whether a change touching these object types needs approval.</summary>
    /// <param name="objectTypes">The object types.</param>
    /// <returns><see langword="true"/> when approval is required.</returns>
    bool RequiresApproval(IReadOnlyCollection<string> objectTypes);
}

/// <summary>
/// The publish service (T3.02): applies a change set to the live configuration. Drafts and quick edits call it; nothing in the
/// draft module changes live configuration by itself.
/// </summary>
public interface IPublishService
{
    /// <summary>Publishes a change set.</summary>
    /// <param name="changeSet">The change set.</param>
    /// <param name="actor">Who publishes.</param>
    /// <param name="comment">The comment.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The new configuration version.</returns>
    Task<Result<long>> PublishAsync(ChangeSet changeSet, UserId actor, string? comment, CancellationToken ct);
}

/// <summary>A secret a draft holds for an entry (spec CFG §6.3).</summary>
/// <param name="EntryId">The entry.</param>
/// <param name="FieldPath">The JSON pointer of the field in the entry's content.</param>
/// <param name="Secret">The vault secret.</param>
public sealed record DraftSecretRef(Guid EntryId, string FieldPath, SecretId Secret);

/// <summary>Where drafts are kept (the PostgreSQL implementation, tables of spec CFG §11, comes with publishing in T3.02).</summary>
public interface IDraftRepository
{
    /// <summary>Stores a new draft.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="hidden"><see langword="true"/> for the draft of a quick edit (not listed).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task AddAsync(Draft draft, bool hidden, CancellationToken ct);

    /// <summary>Reads a draft.</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft, or <see langword="null"/>.</returns>
    Task<Draft?> GetAsync(DraftId id, CancellationToken ct);

    /// <summary>Replaces a draft if it still has <paramref name="expectedVersion"/> (compare and swap; the new draft carries the next version).</summary>
    /// <param name="draft">The new state.</param>
    /// <param name="expectedVersion">The version the change was made from.</param>
    /// <param name="secrets">The secrets of the draft after the change, or <see langword="null"/> to keep them.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="false"/> when the stored draft has another version.</returns>
    Task<bool> TryReplaceAsync(Draft draft, long expectedVersion, IReadOnlyList<DraftSecretRef>? secrets, CancellationToken ct);

    /// <summary>Stores the problems of a validation if the draft still has <paramref name="version"/> (the version does not change).</summary>
    /// <param name="id">The draft.</param>
    /// <param name="version">The version that was validated.</param>
    /// <param name="problems">The problems.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns><see langword="false"/> when the draft changed meanwhile.</returns>
    Task<bool> TrySetProblemsAsync(DraftId id, long version, IReadOnlyList<Problem> problems, CancellationToken ct);

    /// <summary>Returns the drafts a user owns or collaborates on (not hidden ones).</summary>
    /// <param name="user">The user.</param>
    /// <param name="ownedOnly">Only drafts the user owns.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The drafts, newest first.</returns>
    Task<IReadOnlyList<Draft>> ListAsync(UserId user, bool ownedOnly, CancellationToken ct);

    /// <summary>Returns the secrets a draft holds.</summary>
    /// <param name="id">The draft.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The secrets.</returns>
    Task<IReadOnlyList<DraftSecretRef>> GetSecretsAsync(DraftId id, CancellationToken ct);
}

/// <summary>Live documents of a module that has no document-oriented service yet (the adapters of the <c>Adapters</c> folder read through it).</summary>
public interface IConfigDocumentSource
{
    /// <summary>Loads a document.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The document, or <see langword="null"/>.</returns>
    Task<ConfigDocument?> LoadAsync(string type, Guid id, CancellationToken ct);

    /// <summary>Lists the documents of a type.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The documents.</returns>
    IAsyncEnumerable<ConfigDocument> ListAsync(string type, CancellationToken ct);
}
