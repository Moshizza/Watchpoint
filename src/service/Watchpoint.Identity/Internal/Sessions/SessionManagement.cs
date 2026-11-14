using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Audit;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal.Sessions;

/// <summary>Session listing and revocation on behalf of a user, with permissions and audit (spec IDN §6.3, §7.2).</summary>
internal sealed class SessionManagement(
    ISessionRegistry sessions,
    IUserDirectory users,
    IdentityAuditor auditor,
    IPermissionSummaryProvider? permissions = null) : ISessionManagement
{
    internal const string ViewPermission = "admin.sessions.view";
    internal const string ManagePermission = "admin.sessions.manage";
    private readonly IPermissionSummaryProvider _permissions = permissions ?? new AdministratorPermissionSummaryProvider();

    public async Task<Result<IReadOnlyList<Session>>> ListAsync(UserId actor, UserId? forUser, CancellationToken ct)
    {
        if (!users.Get(actor).TryGetValue(out var acting, out _) || !acting.Enabled)
        {
            return IdentityErrors.SessionForbidden();
        }

        if (forUser != actor)
        {
            var granted = await _permissions.GetEffectivePermissionsAsync(acting, ct).ConfigureAwait(false);
            if (!PermissionSummary.Grants(granted, ViewPermission))
            {
                return IdentityErrors.SessionForbidden();
            }
        }

        return Result.Success(sessions.ListActive(new SessionQuery(UserId: forUser)));
    }

    public async Task<Result> RevokeAsync(UserId actor, SessionId session, string reason, CancellationToken ct)
    {
        if (!users.Get(actor).TryGetValue(out var acting, out _) || !acting.Enabled)
        {
            return IdentityErrors.SessionForbidden();
        }

        if (!sessions.Get(session).TryGetValue(out var target, out _))
        {
            return IdentityErrors.SessionNotFound(session);
        }

        var own = target.UserId == actor;
        if (!own)
        {
            var granted = await _permissions.GetEffectivePermissionsAsync(acting, ct).ConfigureAwait(false);
            if (!PermissionSummary.Grants(granted, ManagePermission))
            {
                await auditor.RecordAsync("identity.sessionRevoked", IdentityAuditOutcome.Denied, actor, target.UserId, IdentityAuditSeverity.Sensitive, ct, reason,
                    ("sessionId", session.ToString())).ConfigureAwait(false);
                return IdentityErrors.SessionForbidden();
            }
        }

        var revoked = await sessions.RevokeAsync(session, string.IsNullOrWhiteSpace(reason) ? "Revoked" : reason.Trim(), actor, ct).ConfigureAwait(false);
        if (revoked.IsSuccess)
        {
            await auditor.RecordAsync("identity.sessionRevoked", IdentityAuditOutcome.Success, actor, target.UserId,
                own ? IdentityAuditSeverity.Normal : IdentityAuditSeverity.Sensitive, ct, reason,
                ("sessionId", session.ToString()), ("clientKind", target.ClientKind.ToString())).ConfigureAwait(false);
        }

        return revoked;
    }
}
