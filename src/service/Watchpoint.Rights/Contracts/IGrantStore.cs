using Watchpoint.Kernel.Ids;

namespace Watchpoint.Rights.Contracts;

/// <summary>The grants of every user, held in memory and persisted (spec RGT §5, §11).</summary>
public interface IGrantStore
{
    /// <summary>Gets the grants of a user (empty when none). In memory.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The grants.</returns>
    IReadOnlyList<RightGrant> GetGrants(UserId user);

    /// <summary>Replaces all grants of a user and persists them.</summary>
    /// <param name="user">The user.</param>
    /// <param name="grants">The new grants (already validated).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReplaceAsync(UserId user, IReadOnlyList<RightGrant> grants, CancellationToken ct);

    /// <summary>Raised after the grants of some users changed.</summary>
    event EventHandler<GrantsChangedEventArgs>? Changed;
}

/// <summary>The users whose grants changed.</summary>
public sealed class GrantsChangedEventArgs(IReadOnlyCollection<UserId> users) : EventArgs
{
    /// <summary>Gets the users.</summary>
    public IReadOnlyCollection<UserId> Users { get; } = users;
}
