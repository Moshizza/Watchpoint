using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Storage;

namespace Watchpoint.Rights.Internal.Groups;

/// <summary>User groups: organisation only, no rights (spec RGT §7).</summary>
internal sealed class UserGroupService(IRightsPersistence persistence, IRightsEvaluator evaluator, IUserBulkActions bulk, TimeProvider time) : IUserGroupService
{
    private static readonly PermissionCode EditUsers = PermissionCode.FromTrusted("config.users.edit");
    private readonly ConcurrentDictionary<UserGroupId, UserGroup> _groups = new();

    public async Task LoadAsync(CancellationToken ct)
    {
        _groups.Clear();
        foreach (var g in await persistence.LoadGroupsAsync(ct).ConfigureAwait(false))
        {
            _groups[g.Id] = g;
        }
    }

    public Result<UserGroup> Get(UserGroupId id) => _groups.TryGetValue(id, out var g) ? g : RightsErrors.GroupNotFound(id);

    public IReadOnlyList<UserGroup> List() => [.. _groups.Values.OrderBy(g => g.Name.Default, StringComparer.Ordinal)];

    public async Task<Result<UserGroup>> CreateAsync(LocalizedName name, string? description, IReadOnlyList<UserId> members, IReadOnlyList<UserId> managers, UserId actor, CancellationToken ct)
    {
        if (!CanEdit(actor))
        {
            return CommonErrors.Forbidden(EditUsers);
        }

        var group = new UserGroup(UserGroupId.New(), name, description, [.. members.Distinct()], [.. managers.Distinct()], 1);
        await persistence.SaveGroupAsync(group, ct).ConfigureAwait(false);
        _groups[group.Id] = group;
        return group;
    }

    public async Task<Result<UserGroup>> UpdateAsync(UserGroupId id, UserGroupUpdate update, long expectedVersion, UserId actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!_groups.TryGetValue(id, out var group))
        {
            return RightsErrors.GroupNotFound(id);
        }

        var onlyMembers = update.Name is null && update.Description is null && update.Managers is null;
        if (!CanEdit(actor) && !(onlyMembers && group.Managers.Contains(actor)))
        {
            return CommonErrors.Forbidden(EditUsers);
        }

        if (group.Version != expectedVersion)
        {
            return CommonErrors.VersionConflict(expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), group.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var updated = group with
        {
            Name = update.Name ?? group.Name,
            Description = update.Description ?? group.Description,
            Members = update.Members is null ? group.Members : [.. update.Members.Distinct()],
            Managers = update.Managers is null ? group.Managers : [.. update.Managers.Distinct()],
            Version = group.Version + 1,
        };
        await persistence.SaveGroupAsync(updated, ct).ConfigureAwait(false);
        _groups[id] = updated;
        return updated;
    }

    public async Task<Result> DeleteAsync(UserGroupId id, UserId actor, CancellationToken ct)
    {
        if (!CanEdit(actor))
        {
            return CommonErrors.Forbidden(EditUsers);
        }

        if (!_groups.TryRemove(id, out _))
        {
            return RightsErrors.GroupNotFound(id);
        }

        await persistence.DeleteGroupAsync(id, ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<UserId>>> BulkAsync(UserGroupId id, GroupBulkAction action, DateTimeOffset? parameter, UserId actor, CancellationToken ct)
    {
        if (!CanEdit(actor))
        {
            return CommonErrors.Forbidden(EditUsers);
        }

        if (!_groups.TryGetValue(id, out var group))
        {
            return RightsErrors.GroupNotFound(id);
        }

        var failed = new List<UserId>();
        foreach (var member in group.Members)
        {
            if (!(await bulk.RunAsync(member, action, parameter, actor, ct).ConfigureAwait(false)).IsSuccess)
            {
                failed.Add(member);
            }
        }

        return failed;
    }

    private bool CanEdit(UserId actor) => evaluator.HasPermission(actor, EditUsers, time.GetUtcNow());
}
