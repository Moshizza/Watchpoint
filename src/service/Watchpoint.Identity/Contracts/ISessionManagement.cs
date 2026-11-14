using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>
/// Session management on behalf of a user (spec IDN §7.2 <c>GET /sessions</c>, <c>DELETE /sessions/{id}</c>): a user sees
/// and revokes their own sessions; other users' sessions need <c>admin.sessions.view</c> / <c>admin.sessions.manage</c>
/// (administrators until RGT exists). A revocation takes effect at once for the live stream and at the next request
/// for access tokens (revocation set), and the refresh token of the session is dead.
/// </summary>
public interface ISessionManagement
{
    /// <summary>Lists active sessions.</summary>
    /// <param name="actor">The acting user.</param>
    /// <param name="forUser">The user whose sessions are listed; <see langword="null"/> = every user (administrators).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The sessions, or <c>identity.session.forbidden</c>.</returns>
    Task<Result<IReadOnlyList<Session>>> ListAsync(UserId actor, UserId? forUser, CancellationToken ct);

    /// <summary>Revokes a session (audited <c>identity.sessionRevoked</c>).</summary>
    /// <param name="actor">The acting user.</param>
    /// <param name="session">The session.</param>
    /// <param name="reason">The reason recorded on the session.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success, <c>identity.session.not_found</c> or <c>identity.session.forbidden</c>.</returns>
    Task<Result> RevokeAsync(UserId actor, SessionId session, string reason, CancellationToken ct);
}
