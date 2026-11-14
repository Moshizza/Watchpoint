using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>What <c>GET /api/v1/auth/me</c> returns (spec IDN §7.2).</summary>
/// <param name="User">The user (never a hash or secret).</param>
/// <param name="Session">The session of the token.</param>
/// <param name="Permissions">The effective permissions summary (RGT; <c>*</c> = all).</param>
/// <param name="Mfa">The MFA enrolment state.</param>
public sealed record CurrentUser(User User, Session Session, IReadOnlyList<string> Permissions, MfaStatus Mfa);

/// <summary>Resolves the current user of a validated access token.</summary>
public interface ICurrentUserService
{
    /// <summary>Gets the user, session, permissions and MFA state of the token.</summary>
    /// <param name="claims">The validated claims.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current user, or <c>identity.token.revoked</c> / <c>identity.token.invalid</c> when the session or user is gone.</returns>
    Task<Result<CurrentUser>> GetAsync(AccessTokenClaims claims, CancellationToken ct);
}
