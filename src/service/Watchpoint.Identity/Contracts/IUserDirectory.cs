using System.Diagnostics.CodeAnalysis;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>CRUD operations for user accounts (spec IDN §4).</summary>
public interface IUserDirectory
{
    /// <summary>Gets a user by their identifier.</summary>
    /// <param name="id">The user identifier.</param>
    /// <returns>The user account if found.</returns>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Method name fixed by spec IDN §4.")]
    Result<User> Get(UserId id);

    /// <summary>Finds a user by login name (case-insensitive).</summary>
    /// <param name="login">The login name.</param>
    /// <returns>The user account if found.</returns>
    Result<User> FindByLogin(string login);

    /// <summary>Queries users with filtering and paging.</summary>
    /// <param name="query">Filter parameters.</param>
    /// <param name="page">Paging parameters.</param>
    /// <returns>The paged users.</returns>
    Page<User> Query(UserQuery query, PageRequest page);

    /// <summary>Creates a new user account.</summary>
    /// <param name="user">The user attributes.</param>
    /// <param name="initialPassword">Optional initial password (required for Local auth source).</param>
    /// <param name="ctx">Provenance context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly created user id.</returns>
    Task<Result<UserId>> CreateAsync(NewUser user, string? initialPassword, ChangeContext ctx, CancellationToken ct);

    /// <summary>Updates an existing user account.</summary>
    /// <param name="id">The user identifier.</param>
    /// <param name="update">The updated fields.</param>
    /// <param name="expectedVersion">The expected optimistic concurrency version.</param>
    /// <param name="ctx">Provenance context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<Result> UpdateAsync(UserId id, UserUpdate update, long expectedVersion, ChangeContext ctx, CancellationToken ct);

    /// <summary>Soft deletes an existing user account.</summary>
    /// <param name="id">The user identifier.</param>
    /// <param name="ctx">Provenance context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<Result> DeleteAsync(UserId id, ChangeContext ctx, CancellationToken ct);

    /// <summary>Administrator password reset.</summary>
    /// <param name="id">The user identifier.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="mustChange">Whether the user must change password upon next sign-in.</param>
    /// <param name="ctx">Provenance context.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<Result> SetPasswordAsync(UserId id, string newPassword, bool mustChange, ChangeContext ctx, CancellationToken ct);

    /// <summary>User self-service password change.</summary>
    /// <param name="id">The user identifier.</param>
    /// <param name="current">The current password.</param>
    /// <param name="newPassword">The new password.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<Result> ChangeOwnPasswordAsync(UserId id, string current, string newPassword, CancellationToken ct);

    /// <summary>Event raised when a user account is created, updated or deleted.</summary>
    event EventHandler<UserChangedEventArgs>? Changed;
}
