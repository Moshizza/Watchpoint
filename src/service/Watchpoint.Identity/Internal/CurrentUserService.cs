using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal;

/// <summary>Resolves <c>GET /auth/me</c>: user, session, permissions summary (RGT hook) and MFA state (spec IDN §7.2).</summary>
internal sealed class CurrentUserService(
    IUserDirectory users,
    ISessionRegistry sessions,
    IMfaService mfa,
    TimeProvider clock,
    IPermissionSummaryProvider? permissions = null) : ICurrentUserService
{
    private readonly IPermissionSummaryProvider _permissions = permissions ?? new AdministratorPermissionSummaryProvider();

    public async Task<Result<CurrentUser>> GetAsync(AccessTokenClaims claims, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (!sessions.Get(claims.SessionId).TryGetValue(out var session, out _) || session.RevokedAt is not null ||
            sessions.IsRevoked(claims.SessionId) || session.ExpiresAt <= clock.GetUtcNow())
        {
            return IdentityErrors.TokenRevoked();
        }

        if (!users.Get(claims.UserId).TryGetValue(out var user, out _) || session.UserId != user.Id)
        {
            return IdentityErrors.TokenInvalid();
        }

        var granted = await _permissions.GetEffectivePermissionsAsync(user, ct).ConfigureAwait(false);
        var status = await mfa.GetStatusAsync(user.Id, ct).ConfigureAwait(false);
        return new CurrentUser(user, session, granted, status);
    }
}
