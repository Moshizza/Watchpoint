using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Storage;

/// <summary>Persistence port of the rights module (tables of spec RGT §11).</summary>
internal interface IRightsPersistence
{
    Task<IReadOnlyList<RightGrant>> LoadGrantsAsync(CancellationToken ct);

    Task SaveGrantsAsync(UserId user, IReadOnlyList<RightGrant> grants, CancellationToken ct);

    Task<IReadOnlyList<UserGroup>> LoadGroupsAsync(CancellationToken ct);

    Task SaveGroupAsync(UserGroup group, CancellationToken ct);

    Task DeleteGroupAsync(UserGroupId id, CancellationToken ct);
}

/// <summary>Non-persistent implementation (tests, single-process use).</summary>
internal sealed class InMemoryRightsPersistence : IRightsPersistence
{
    private readonly Dictionary<UserId, IReadOnlyList<RightGrant>> _grants = [];
    private readonly Dictionary<UserGroupId, UserGroup> _groups = [];

    public Task<IReadOnlyList<RightGrant>> LoadGrantsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RightGrant>>([.. _grants.Values.SelectMany(x => x)]);

    public Task SaveGrantsAsync(UserId user, IReadOnlyList<RightGrant> grants, CancellationToken ct)
    {
        _grants[user] = grants;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<UserGroup>> LoadGroupsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UserGroup>>([.. _groups.Values]);

    public Task SaveGroupAsync(UserGroup group, CancellationToken ct)
    {
        _groups[group.Id] = group;
        return Task.CompletedTask;
    }

    public Task DeleteGroupAsync(UserGroupId id, CancellationToken ct)
    {
        _groups.Remove(id);
        return Task.CompletedTask;
    }
}
