using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Lockout state of a user (spec IDN §6.4).</summary>
/// <param name="LockedUntil">End of the current lock, if any.</param>
/// <param name="RecentFailures">Failures in the sliding window.</param>
/// <param name="LockCount24h">Locks counted for the progressive duration.</param>
public sealed record LockoutStatus(DateTimeOffset? LockedUntil, int RecentFailures, int LockCount24h)
{
    /// <summary>Whether a lock is active at <paramref name="now"/>.</summary>
    /// <param name="now">The instant.</param>
    /// <returns>Whether locked.</returns>
    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

/// <summary>Administrator view and control of lockouts (spec IDN §6.4, <c>POST /users/{id}/unlock</c>).</summary>
public interface ILockoutManager
{
    /// <summary>Gets the lockout state of a user; <see langword="null"/> when the user never failed.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The state.</returns>
    Task<LockoutStatus?> GetStatusAsync(UserId user, CancellationToken ct);

    /// <summary>Clears the lock and the failure counters (audited <c>identity.accountUnlocked</c>). The actor must be an enabled administrator (or the system).</summary>
    /// <param name="user">The user.</param>
    /// <param name="ctx">The actor.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or <c>identity.user.admin_required</c>.</returns>
    Task<Result> UnlockAsync(UserId user, ChangeContext ctx, CancellationToken ct);
}
