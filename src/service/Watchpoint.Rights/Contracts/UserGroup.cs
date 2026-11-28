using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Rights.Contracts;

/// <summary>A group of users; it holds no rights (spec RGT §7).</summary>
/// <param name="Id">The id.</param>
/// <param name="Name">The name.</param>
/// <param name="Description">The description.</param>
/// <param name="Members">The members.</param>
/// <param name="Managers">Users who may edit the membership.</param>
/// <param name="Version">Row version.</param>
public sealed record UserGroup(UserGroupId Id, LocalizedName Name, string? Description, IReadOnlyList<UserId> Members, IReadOnlyList<UserId> Managers, long Version);

/// <summary>A change to a group; <see langword="null"/> members keep the current value.</summary>
/// <param name="Name">New name.</param>
/// <param name="Description">New description.</param>
/// <param name="Members">New members.</param>
/// <param name="Managers">New managers.</param>
public sealed record UserGroupUpdate(LocalizedName? Name = null, string? Description = null, IReadOnlyList<UserId>? Members = null, IReadOnlyList<UserId>? Managers = null);

/// <summary>Bulk actions on the members of a group (spec RGT §7).</summary>
public enum GroupBulkAction
{
    /// <summary>Disable the users.</summary>
    Disable,

    /// <summary>Extend the validity of the users.</summary>
    ExtendValidity,

    /// <summary>Sign the users out.</summary>
    SignOut,

    /// <summary>Force a password change at the next sign-in.</summary>
    ForcePasswordChange,

    /// <summary>Reset the MFA enrolment.</summary>
    ResetMfa,
}

/// <summary>User groups (spec RGT §7).</summary>
public interface IUserGroupService
{
    /// <summary>Gets a group.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The group, or <c>rights.group.not_found</c>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec RGT §7.")]
    Result<UserGroup> Get(UserGroupId id);

    /// <summary>Lists the groups.</summary>
    /// <returns>All groups.</returns>
    IReadOnlyList<UserGroup> List();

    /// <summary>Creates a group (needs <c>config.users.edit</c>).</summary>
    /// <param name="name">Name.</param>
    /// <param name="description">Description.</param>
    /// <param name="members">Members.</param>
    /// <param name="managers">Managers.</param>
    /// <param name="actor">The acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The group.</returns>
    Task<Result<UserGroup>> CreateAsync(LocalizedName name, string? description, IReadOnlyList<UserId> members, IReadOnlyList<UserId> managers, UserId actor, CancellationToken ct);

    /// <summary>Updates a group: members by managers or <c>config.users.edit</c> holders, everything else by the latter.</summary>
    /// <param name="id">The group.</param>
    /// <param name="update">The change.</param>
    /// <param name="expectedVersion">The version read.</param>
    /// <param name="actor">The acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The group.</returns>
    Task<Result<UserGroup>> UpdateAsync(UserGroupId id, UserGroupUpdate update, long expectedVersion, UserId actor, CancellationToken ct);

    /// <summary>Deletes a group.</summary>
    /// <param name="id">The group.</param>
    /// <param name="actor">The acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result> DeleteAsync(UserGroupId id, UserId actor, CancellationToken ct);

    /// <summary>Runs a bulk action on every member through IDN (needs <c>config.users.edit</c>).</summary>
    /// <param name="id">The group.</param>
    /// <param name="action">The action.</param>
    /// <param name="parameter">Action parameter (the new validity end for <see cref="GroupBulkAction.ExtendValidity"/>).</param>
    /// <param name="actor">The acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The users the action failed for (empty when all succeeded).</returns>
    Task<Result<IReadOnlyList<UserId>>> BulkAsync(UserGroupId id, GroupBulkAction action, DateTimeOffset? parameter, UserId actor, CancellationToken ct);
}

/// <summary>The IDN operations behind group bulk actions.</summary>
public interface IUserBulkActions
{
    /// <summary>Runs <paramref name="action"/> for one user.</summary>
    /// <param name="user">The user.</param>
    /// <param name="action">The action.</param>
    /// <param name="parameter">Action parameter.</param>
    /// <param name="actor">The acting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<Result> RunAsync(UserId user, GroupBulkAction action, DateTimeOffset? parameter, UserId actor, CancellationToken ct);
}
