using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Versions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Internal.Publish;

/// <summary>
/// Publishing drafts (spec CFG §7–§8): the publish permission of every type, approval when a type requires it (approver ≠
/// requester), scheduled publish, and rebasing an entry on the live document by three-way merge.
/// </summary>
internal sealed class DraftPublisher(
    DraftService drafts,
    IDraftRepository repository,
    IPublishService publisher,
    IConfigObjectTypeRegistry types,
    IConfigurationAuthorizer authorizer,
    IApprovalPolicy approval,
    IPublishStore store,
    TimeProvider time,
    ILogger<DraftPublisher> logger,
    IConfigScheduler? scheduler = null) : IDraftPublisher, IDisposable
{
    /// <summary>The comments of drafts waiting for approval or a scheduled time (kept until they are published).</summary>
    private readonly ConcurrentDictionary<DraftId, string?> _comments = new();
    private readonly ConcurrentDictionary<DraftId, IDisposable> _schedules = new();

    public async Task<Result<PublishOutcome>> PublishDraftAsync(DraftId id, UserId actor, long expectedVersion, string? comment, DateTimeOffset? publishAt,
        CancellationToken ct)
    {
        if (!(await OpenAsync(id, actor, expectedVersion, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (await MayPublishAsync(draft, actor, ct).ConfigureAwait(false) is { } forbidden)
        {
            return forbidden;
        }

        if (approval.RequiresApproval([.. draft.Entries.Select(e => e.Ref.Type).Distinct(StringComparer.Ordinal)]))
        {
            if (!(await drafts.SetStatusAsync(draft, DraftStatus.AwaitingApproval, publishAt, new ApprovalInfo(actor, time.GetUtcNow()), null, ct).ConfigureAwait(false))
                .TryGetValue(out _, out var waitError))
            {
                return waitError;
            }

            _comments[id] = comment;
            return new PublishOutcome(DraftStatus.AwaitingApproval, null);
        }

        return await PublishOrScheduleAsync(draft, actor, comment, publishAt, ct).ConfigureAwait(false);
    }

    public async Task<Result<PublishOutcome>> ApproveAsync(DraftId id, UserId approver, CancellationToken ct)
    {
        if (await repository.GetAsync(id, ct).ConfigureAwait(false) is not { Status: not DraftStatus.Discarded } draft)
        {
            return ConfigurationErrors.NotFound(id.Value);
        }

        if (draft is not { Status: DraftStatus.AwaitingApproval, Approval: { } request })
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        if (request.RequestedBy == approver)
        {
            return Error.Create(ErrorKind.Forbidden, ConfigurationErrors.ApprovalSelfForbidden, ("draftId", id.Value));
        }

        if (await MayPublishAsync(draft, approver, ct).ConfigureAwait(false) is { } forbidden)
        {
            return forbidden;
        }

        if (!(await drafts.SetStatusAsync(draft, DraftStatus.Open, draft.PublishAt, null, null, ct).ConfigureAwait(false)).TryGetValue(out var open, out var error))
        {
            return error;
        }

        _comments.TryRemove(id, out var comment);
        return await PublishOrScheduleAsync(open, draft.Owner, comment, draft.PublishAt, ct).ConfigureAwait(false);
    }

    public async Task<Result> RejectAsync(DraftId id, UserId approver, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Create(ErrorKind.Validation, CommonErrorCodes.ValidationFailed.Value, ("field", "reason"));
        }

        if (await repository.GetAsync(id, ct).ConfigureAwait(false) is not { Status: not DraftStatus.Discarded } draft)
        {
            return ConfigurationErrors.NotFound(id.Value);
        }

        if (draft is not { Status: DraftStatus.AwaitingApproval, Approval: { } request })
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        if (request.RequestedBy == approver)
        {
            return Error.Create(ErrorKind.Forbidden, ConfigurationErrors.ApprovalSelfForbidden, ("draftId", id.Value));
        }

        if (await MayPublishAsync(draft, approver, ct).ConfigureAwait(false) is { } forbidden)
        {
            return forbidden;
        }

        var rejected = new Problem(ProblemSeverity.Warning, ConfigurationErrors.ApprovalRejected,
            new Dictionary<string, object?> { ["reason"] = reason, ["by"] = approver.Value }, null, null, []);
        _comments.TryRemove(id, out _);
        return (await drafts.SetStatusAsync(draft, DraftStatus.Open, null, null, [.. draft.Problems, rejected], ct).ConfigureAwait(false)).IsSuccess
            ? Result.Success()
            : ConfigurationErrors.VersionConflict(draft.Version, -1);
    }

    public async Task<Result<RebaseResult>> RebaseEntryAsync(DraftId id, UserId actor, long expectedVersion, Guid entryId, JsonElement? resolved, CancellationToken ct)
    {
        if (!(await OpenAsync(id, actor, expectedVersion, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Entries.FirstOrDefault(e => e.EntryId == entryId) is not { Operation: not EntryOperation.Create } entry)
        {
            return Error.Create(ErrorKind.NotFound, ConfigurationErrors.EntryInvalid, ("reason", "not_found"), ("entryId", entryId));
        }

        if (await types.Find(entry.Ref.Type)!.LoadAsync(entry.Ref.Id, ct).ConfigureAwait(false) is not { } live)
        {
            return Error.Create(ErrorKind.NotFound, ConfigurationErrors.EntryInvalid, ("reason", "not_found"), ("type", entry.Ref.Type), ("id", entry.Ref.Id));
        }

        // Base = the document the editor started from: the one published with the entry's base version.
        var @base = entry.BaseVersion is { } baseVersion ? (await store.RevisionAsync(entry.Ref, baseVersion, ct).ConfigureAwait(false))?.After : null;
        var merge = entry.Content is { } mine ? ThreeWayMerge.Merge(@base, mine, live.Content) : new MergeResult(live.Content, []);
        if (merge.Conflicts.Count > 0 && resolved is null)
        {
            return new RebaseResult(merge.Merged, merge.Conflicts, null, draft);
        }

        var rebased = await drafts.RebaseAsync(id, actor, draft.Version, entryId, resolved ?? merge.Merged, live.Version, ct).ConfigureAwait(false);
        return rebased.TryGetValue(out var done, out var rebaseError)
            ? new RebaseResult(resolved ?? merge.Merged, merge.Conflicts, live.Version, done.Draft)
            : rebaseError;
    }

    /// <summary>Re-arms the schedules of scheduled drafts (start-up).</summary>
    public async Task ResumeSchedulesAsync(CancellationToken ct)
    {
        foreach (var draft in await repository.ListByStatusAsync(DraftStatus.Scheduled, ct).ConfigureAwait(false))
        {
            Schedule(draft.Id, draft.PublishAt ?? time.GetUtcNow());
        }
    }

    public void Dispose()
    {
        foreach (var schedule in _schedules.Values)
        {
            schedule.Dispose();
        }
    }

    /// <summary>Publishes the scheduled draft (as its owner); a refusal leaves it open with its problems.</summary>
    internal async ValueTask FireAsync(DraftId id, CancellationToken ct)
    {
        _schedules.TryRemove(id, out _);
        if (await repository.GetAsync(id, ct).ConfigureAwait(false) is not { Status: DraftStatus.Scheduled } draft)
        {
            return;
        }

        if (!(await drafts.SetStatusAsync(draft, DraftStatus.Open, draft.PublishAt, null, null, ct).ConfigureAwait(false)).TryGetValue(out var open, out var error))
        {
            ConfigurationLog.ScheduledFailed(logger, id.Value, error.Code.Value);
            return;
        }

        _comments.TryRemove(id, out var comment);
        if (!(await PublishNowAsync(open, draft.Owner, comment, ct).ConfigureAwait(false)).TryGetValue(out _, out var refused))
        {
            ConfigurationLog.ScheduledFailed(logger, id.Value, refused.Code.Value);
        }
    }

    private async Task<Result<PublishOutcome>> PublishOrScheduleAsync(Draft draft, UserId actor, string? comment, DateTimeOffset? publishAt, CancellationToken ct)
    {
        if (publishAt is { } at && at > time.GetUtcNow())
        {
            if (scheduler is null)
            {
                return Error.Create(ErrorKind.Unavailable, CommonErrorCodes.Unavailable.Value, ("service", "scheduling"));
            }

            if (!(await drafts.SetStatusAsync(draft, DraftStatus.Scheduled, at, null, null, ct).ConfigureAwait(false)).TryGetValue(out _, out var error))
            {
                return error;
            }

            _comments[draft.Id] = comment;
            Schedule(draft.Id, at);
            return new PublishOutcome(DraftStatus.Scheduled, null);
        }

        return await PublishNowAsync(draft, actor, comment, ct).ConfigureAwait(false);
    }

    private async Task<Result<PublishOutcome>> PublishNowAsync(Draft draft, UserId actor, string? comment, CancellationToken ct)
    {
        if (!(await drafts.BuildChangeSetAsync(draft.Id, draft.Owner, ct).ConfigureAwait(false)).TryGetValue(out var changeSet, out var invalid))
        {
            return invalid;
        }

        return (await publisher.PublishAsync(changeSet, actor, comment, ct).ConfigureAwait(false)).TryGetValue(out var version, out var error)
            ? new PublishOutcome(DraftStatus.Published, version)
            : error;
    }

    private void Schedule(DraftId id, DateTimeOffset at)
    {
        if (scheduler is null)
        {
            return;
        }

        // The previous schedule (same key) goes first: disposing it after registering would cancel the new one.
        if (_schedules.TryRemove(id, out var previous))
        {
            previous.Dispose();
        }

        _schedules[id] = scheduler.At("draft:" + id.Value.ToString("D"), at, ct => FireAsync(id, ct));
    }

    private async Task<Result<Draft>> OpenAsync(DraftId id, UserId actor, long expectedVersion, CancellationToken ct)
    {
        if (!(await drafts.FindVisibleAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        if (draft.Status != DraftStatus.Open)
        {
            return ConfigurationErrors.NotOpen(id.Value, draft.Status.ToString());
        }

        return draft.Version == expectedVersion ? draft : ConfigurationErrors.VersionConflict(expectedVersion, draft.Version);
    }

    private async Task<Error?> MayPublishAsync(Draft draft, UserId actor, CancellationToken ct)
    {
        foreach (var entry in draft.Entries)
        {
            if (types.Find(entry.Ref.Type) is { } type && !await authorizer.IsAllowedAsync(actor, type.PermissionFor(ConfigAccess.Publish), entry.Ref, ct).ConfigureAwait(false))
            {
                return ConfigurationErrors.Forbidden(type.PermissionFor(ConfigAccess.Publish).Value);
            }
        }

        return null;
    }
}
