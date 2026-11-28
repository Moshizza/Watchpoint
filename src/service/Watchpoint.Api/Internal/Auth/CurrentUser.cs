using Watchpoint.Api.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Api.Internal.Auth;

/// <summary>The identity of one request, built from validated token claims (or anonymous).</summary>
internal sealed class CurrentUser : ICurrentUser
{
    private readonly IPermissionEvaluator _evaluator;

    private CurrentUser(IPermissionEvaluator evaluator, AccessTokenClaims? claims)
    {
        _evaluator = evaluator;
        Claims = claims;
    }

    public AccessTokenClaims? Claims { get; }

    public bool IsAuthenticated => Claims is not null;

    public UserId UserId => Claims?.UserId ?? default;

    public SessionId SessionId => Claims?.SessionId ?? default;

    public ClientKind ClientKind => Claims?.ClientKind ?? ClientKind.Api;

    public bool IsAdministrator => Claims?.Administrator ?? false;

    public string Language => Claims?.Language ?? "en";

    public static CurrentUser Anonymous(IPermissionEvaluator evaluator) => new(evaluator, null);

    public static CurrentUser Of(IPermissionEvaluator evaluator, AccessTokenClaims claims) => new(evaluator, claims);

    public ValueTask<bool> HasPermissionAsync(PermissionCode permission, CancellationToken ct) =>
        IsAuthenticated ? _evaluator.HasPermissionAsync(this, permission, ct) : ValueTask.FromResult(false);

    public ValueTask<IVisibleScope> GetVisibleScopeAsync(PermissionCode permission, CancellationToken ct) =>
        _evaluator.GetVisibleScopeAsync(this, permission, ct);
}

/// <summary>
/// The M1–M2 permission fallback (spec API §5.2): <c>config.*</c> and <c>admin.*</c> need an administrator; anything
/// else needs authentication. Every object is visible. RGT (M3) registers its own <see cref="IPermissionEvaluator"/>.
/// </summary>
internal sealed class FallbackPermissionEvaluator : IPermissionEvaluator
{
    public ValueTask<bool> HasPermissionAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!user.IsAuthenticated)
        {
            return ValueTask.FromResult(false);
        }

        var code = permission.Value;
        var adminOnly = code.StartsWith("config.", StringComparison.Ordinal) || code.StartsWith("admin.", StringComparison.Ordinal);
        return ValueTask.FromResult(!adminOnly || user.IsAdministrator);
    }

    public ValueTask<IVisibleScope> GetVisibleScopeAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct) =>
        ValueTask.FromResult<IVisibleScope>(EverythingScope.Instance);
}
