using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Configuration.Contracts;

/// <summary>A change to put in a draft.</summary>
/// <param name="Ref">The object.</param>
/// <param name="Operation">The operation.</param>
/// <param name="BaseVersion">The version of the object the editor read (updates and deletes); a replaced entry keeps its original base version.</param>
/// <param name="Content">The full new document (create, update).</param>
public sealed record EntryRequest(ConfigObjectRef Ref, EntryOperation Operation, long? BaseVersion, JsonElement? Content);

/// <summary>The result of changing an entry.</summary>
/// <param name="Draft">The draft after the change (its new version is what the editor sends next).</param>
/// <param name="Entry">The entry; <see langword="null"/> when the change cancelled out (a create that was deleted again).</param>
/// <param name="Problems">The problems of the entry and of the entries that refer to it.</param>
public sealed record EntryResult(Draft Draft, DraftEntry? Entry, IReadOnlyList<Problem> Problems);

/// <summary>A validation that runs in the background (spec CFG §5: large drafts).</summary>
public interface IValidationJob
{
    /// <summary>Gets the number of entries to check.</summary>
    int Total { get; }

    /// <summary>Gets the number of entries checked so far.</summary>
    int Done { get; }

    /// <summary>Gets the task that completes with the draft (its problems stored), or an error.</summary>
    Task<Result<Draft>> Completion { get; }
}

/// <summary>Drafts: changes kept apart from the live configuration until they are published (spec CFG §5–§6).</summary>
public interface IDraftService
{
    /// <summary>Creates an open draft.</summary>
    /// <param name="name">The name.</param>
    /// <param name="owner">The owner.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft.</returns>
    Task<Result<Draft>> CreateAsync(LocalizedName name, UserId owner, CancellationToken ct);

    /// <summary>Reads a draft the user owns or collaborates on.</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft, or <c>config.draft.not_found</c> (also for a draft the user may not see).</returns>
    Task<Result<Draft>> GetAsync(DraftId id, UserId actor, CancellationToken ct);

    /// <summary>Lists the drafts of a user.</summary>
    /// <param name="actor">The user.</param>
    /// <param name="mine"><see langword="true"/> for the drafts the user owns, else also those shared with the user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The drafts.</returns>
    Task<IReadOnlyList<Draft>> ListAsync(UserId actor, bool mine, CancellationToken ct);

    /// <summary>
    /// Adds an entry or replaces the entry of the object. Changes are made from the draft version the editor read: when someone
    /// else changed the draft since, nothing is changed and <c>common.version.conflict</c> is returned (spec README §6.1).
    /// </summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="expectedVersion">The draft version the editor read.</param>
    /// <param name="request">The change.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft and the problems; <c>config.draft.not_open</c>, <c>config.draft.too_large</c>, <c>config.entry.invalid</c>, <c>common.forbidden</c> or a conflict.</returns>
    Task<Result<EntryResult>> SetEntryAsync(DraftId id, UserId actor, long expectedVersion, EntryRequest request, CancellationToken ct);

    /// <summary>Removes an entry (reverts the object to its live state).</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="expectedVersion">The draft version the editor read.</param>
    /// <param name="entryId">The entry.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft.</returns>
    Task<Result<Draft>> RemoveEntryAsync(DraftId id, UserId actor, long expectedVersion, Guid entryId, CancellationToken ct);

    /// <summary>Adds a collaborator (owner only); the user needs the edit permission of every type in the draft.</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The owner.</param>
    /// <param name="expectedVersion">The draft version.</param>
    /// <param name="collaborator">The user to add.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft.</returns>
    Task<Result<Draft>> AddCollaboratorAsync(DraftId id, UserId actor, long expectedVersion, UserId collaborator, CancellationToken ct);

    /// <summary>Throws the draft away; its secrets are deleted.</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The owner.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Success, or an error.</returns>
    Task<Result> DiscardAsync(DraftId id, UserId actor, CancellationToken ct);

    /// <summary>Reads an object as the draft would leave it (draft content, live document, deleted or missing).</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="reference">The object.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The object's state.</returns>
    Task<Result<DraftObjectState>> GetObjectAsync(DraftId id, UserId actor, ConfigObjectRef reference, CancellationToken ct);

    /// <summary>Validates the whole draft and stores the problems.</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The draft with its problems.</returns>
    Task<Result<Draft>> ValidateAsync(DraftId id, UserId actor, CancellationToken ct);

    /// <summary>Starts a validation in the background with progress (for drafts with many entries).</summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The job.</returns>
    IValidationJob StartValidation(DraftId id, UserId actor, CancellationToken ct);

    /// <summary>
    /// Freezes the draft as a change set for publishing (T3.02): validates it and refuses a draft with errors
    /// (<c>config.publish.invalid</c>) or one that is not open. The live configuration is not touched.
    /// </summary>
    /// <param name="id">The draft.</param>
    /// <param name="actor">The user.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The change set.</returns>
    Task<Result<ChangeSet>> BuildChangeSetAsync(DraftId id, UserId actor, CancellationToken ct);
}

/// <summary>How a quick edit ended.</summary>
public enum QuickEditStatus
{
    /// <summary>Published at once.</summary>
    Published,

    /// <summary>The types need approval: a draft waits for an approver (HTTP 202).</summary>
    AwaitingApproval,

    /// <summary>The changes have errors; nothing was published (HTTP 422).</summary>
    Invalid,
}

/// <summary>The result of a quick edit.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Draft">The (hidden) draft of the quick edit.</param>
/// <param name="Version">The configuration version, when published.</param>
/// <param name="Problems">The problems, when invalid.</param>
public sealed record QuickEditResult(QuickEditStatus Status, DraftId Draft, long? Version, IReadOnlyList<Problem> Problems);

/// <summary>The quick edit (spec CFG §6.4): a hidden draft that is validated and published in one step.</summary>
public interface IQuickEdit
{
    /// <summary>Applies changes at once through a hidden draft.</summary>
    /// <param name="entries">The changes.</param>
    /// <param name="actor">The user.</param>
    /// <param name="comment">The comment.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The outcome; an error for rights and malformed entries.</returns>
    Task<Result<QuickEditResult>> ApplyAsync(IReadOnlyList<EntryRequest> entries, UserId actor, string? comment, CancellationToken ct);
}
