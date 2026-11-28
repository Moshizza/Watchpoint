using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Contracts;

/// <summary>What happened to a draft asked to publish.</summary>
/// <param name="Status">Its status now: <c>Published</c>, <c>AwaitingApproval</c> or <c>Scheduled</c>.</param>
/// <param name="Version">The configuration version, when published.</param>
public sealed record PublishOutcome(DraftStatus Status, long? Version);

/// <summary>One change listed in the <c>config.published</c> record (spec CFG §7.1 step 6) for one changed object.</summary>
/// <param name="Type">The object type.</param>
/// <param name="Id">The object id.</param>
/// <param name="Operation">What was done.</param>
/// <param name="NewVersion">The object's version after the change (0 after a delete).</param>
public sealed record PublishedChange(string Type, Guid Id, EntryOperation Operation, long NewVersion);

/// <summary>Body of <c>config.published</c> on <c>WP_CFG</c> (spec CFG §7.1 step 6, §7.2).</summary>
/// <param name="Version">The configuration version.</param>
/// <param name="DraftId">The published draft.</param>
/// <param name="Changes">The changed objects.</param>
/// <param name="NodeId">The publishing node (other nodes reload, M9).</param>
public sealed record ConfigPublishedBody(long Version, DraftId DraftId, IReadOnlyList<PublishedChange> Changes, Guid? NodeId);

/// <summary>The result of rebasing a draft entry on the live document (spec CFG §8).</summary>
/// <param name="Merged">The merged document (conflicting paths keep the draft's value).</param>
/// <param name="Conflicts">The JSON pointers changed differently in the draft and live.</param>
/// <param name="LiveVersion">The live version the entry is now based on, when it was updated.</param>
/// <param name="Draft">The draft, updated when there was no conflict or a resolution was given.</param>
public sealed record RebaseResult(JsonElement Merged, IReadOnlyList<string> Conflicts, long? LiveVersion, Draft Draft);

/// <summary>
/// Publishing drafts (spec CFG §7): directly, after approval (<c>Configuration:ApprovalRequiredFor</c>), or at a set time;
/// and rebasing an outdated entry (§8).
/// </summary>
public interface IDraftPublisher
{
    /// <summary>
    /// Publishes a draft: it needs approval when one of its types requires it (status <c>AwaitingApproval</c>), waits when
    /// <paramref name="publishAt"/> is in the future (status <c>Scheduled</c>), and is published now otherwise.
    /// </summary>
    /// <param name="draft">The draft (its owner or a collaborator, with the publish permission of each of its types).</param>
    /// <param name="actor">Who publishes.</param>
    /// <param name="expectedVersion">The draft version the actor read.</param>
    /// <param name="comment">The version comment.</param>
    /// <param name="publishAt">When to publish, if later.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The outcome, or the reason (<c>config.publish.invalid</c>, <c>config.publish.conflict</c>…).</returns>
    Task<Result<PublishOutcome>> PublishDraftAsync(DraftId draft, UserId actor, long expectedVersion, string? comment, DateTimeOffset? publishAt, CancellationToken ct);

    /// <summary>Approves a draft waiting for approval (not by who requested it) and publishes it (or schedules it).</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="approver">The approver (publish permission of each type).</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>The outcome, or <c>config.approval.self_forbidden</c>, <c>config.draft.not_open</c>…</returns>
    Task<Result<PublishOutcome>> ApproveAsync(DraftId draft, UserId approver, CancellationToken ct);

    /// <summary>Rejects a draft waiting for approval: it is open again, with the reason as a problem.</summary>
    /// <param name="draft">The draft.</param>
    /// <param name="approver">The approver.</param>
    /// <param name="reason">Why.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>Success, or the reason it is refused.</returns>
    Task<Result> RejectAsync(DraftId draft, UserId approver, string reason, CancellationToken ct);

    /// <summary>
    /// Three-way merges an entry with the live document (base = the document at the entry's base version). Without
    /// conflict, or with <paramref name="resolved"/>, the entry takes the result and the live version as its base.
    /// </summary>
    /// <param name="draft">The draft.</param>
    /// <param name="actor">The editor.</param>
    /// <param name="expectedVersion">The draft version the editor read.</param>
    /// <param name="entryId">The entry.</param>
    /// <param name="resolved">The resolved document (the editor's choices on the conflicting paths), if any.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The merge, or the reason it is refused.</returns>
    Task<Result<RebaseResult>> RebaseEntryAsync(DraftId draft, UserId actor, long expectedVersion, Guid entryId, JsonElement? resolved, CancellationToken ct);
}

/// <summary>Audit of publishes (spec CFG §7.1 step 6). AUD implements it; until then publishes are only logged (1301).</summary>
public interface IConfigAudit
{
    /// <summary>Records a publish.</summary>
    /// <param name="version">The version.</param>
    /// <param name="entries">What was applied.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task.</returns>
    ValueTask PublishedAsync(ConfigVersion version, IReadOnlyList<AppliedEntry> entries, CancellationToken ct);
}

/// <summary>Announces a publish: <c>config.published</c> on <c>WP_CFG</c> (the pipeline's writer in the host).</summary>
public interface IConfigPublishedSink
{
    /// <summary>Appends the record.</summary>
    /// <param name="body">The body.</param>
    /// <param name="actor">Who published.</param>
    /// <param name="at">When.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>A task.</returns>
    ValueTask AppendAsync(ConfigPublishedBody body, UserId actor, DateTimeOffset at, CancellationToken ct);
}
