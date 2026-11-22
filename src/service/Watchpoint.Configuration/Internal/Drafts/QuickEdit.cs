using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Configuration.Internal.Drafts;

/// <summary>
/// The quick edit (spec CFG §6.4): the changes go into a hidden draft, which is validated and published at once. Types that need
/// approval leave the draft awaiting an approver instead; errors leave it unpublished.
/// </summary>
internal sealed class QuickEdit(
    DraftService drafts,
    IConfigObjectTypeRegistry types,
    IConfigurationAuthorizer authorizer,
    IApprovalPolicy approval,
    IPublishService? publisher) : IQuickEdit
{
    public async Task<Result<QuickEditResult>> ApplyAsync(IReadOnlyList<EntryRequest> entries, UserId actor, string? comment, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var request in entries)
        {
            if (types.Find(request.Ref.Type) is not { } type)
            {
                return Error.Create(ErrorKind.Validation, ConfigurationErrors.EntryInvalid, ("reason", "unknown_type"), ("type", request.Ref.Type));
            }

            foreach (var access in new[] { ConfigAccess.Edit, ConfigAccess.Publish })
            {
                var permission = type.PermissionFor(access);
                if (!await authorizer.IsAllowedAsync(actor, permission, request.Ref, ct).ConfigureAwait(false))
                {
                    return ConfigurationErrors.Forbidden(permission.Value);
                }
            }
        }

        var created = await drafts.CreateCoreAsync(LocalizedName.Of("Quick edit"), actor, hidden: true, ct).ConfigureAwait(false);
        var draft = created.Value;
        foreach (var request in entries)
        {
            if (!(await drafts.SetEntryAsync(draft.Id, actor, draft.Version, request, ct).ConfigureAwait(false)).TryGetValue(out var set, out var error))
            {
                await drafts.DiscardAsync(draft.Id, actor, ct).ConfigureAwait(false);
                return error;
            }

            draft = set.Draft;
        }

        var errors = draft.Problems.Where(p => p.Severity == ProblemSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            await drafts.DiscardAsync(draft.Id, actor, ct).ConfigureAwait(false);
            return new QuickEditResult(QuickEditStatus.Invalid, draft.Id, null, draft.Problems);
        }

        if (approval.RequiresApproval([.. entries.Select(e => e.Ref.Type).Distinct(StringComparer.Ordinal)]))
        {
            return (await drafts.RequestApprovalAsync(draft, actor, ct).ConfigureAwait(false)).TryGetValue(out var waiting, out var waitError)
                ? new QuickEditResult(QuickEditStatus.AwaitingApproval, waiting.Id, null, draft.Problems)
                : waitError;
        }

        if (publisher is null)
        {
            return Error.Create(ErrorKind.Unavailable, CommonErrorCodes.Unavailable.Value, ("service", "publish"));
        }

        if (!(await drafts.BuildChangeSetAsync(draft.Id, actor, ct).ConfigureAwait(false)).TryGetValue(out var changeSet, out var invalid))
        {
            return invalid;
        }

        return (await publisher.PublishAsync(changeSet, actor, comment, ct).ConfigureAwait(false)).TryGetValue(out var version, out var publishError)
            ? new QuickEditResult(QuickEditStatus.Published, draft.Id, version, draft.Problems)
            : publishError;
    }
}
