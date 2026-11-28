using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;
using ApiVisibleScope = Watchpoint.Api.Contracts.IVisibleScope;

namespace Watchpoint.Rights.Internal.Adapters;

/// <summary>The API's <see cref="IPermissionEvaluator"/> backed by the rights evaluator (spec RGT §6.1, replaces the M1–M2 fallback).</summary>
internal sealed class PermissionEvaluatorAdapter(IRightsEvaluator evaluator, TimeProvider time) : IPermissionEvaluator
{
    public ValueTask<bool> HasPermissionAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ValueTask.FromResult(user.IsAuthenticated && (user.IsAdministrator || evaluator.HasPermission(user.UserId, permission, time.GetUtcNow())));
    }

    public ValueTask<ApiVisibleScope> GetVisibleScopeAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.IsAdministrator)
        {
            return ValueTask.FromResult<ApiVisibleScope>(EverythingScope.Instance);
        }

        return ValueTask.FromResult<ApiVisibleScope>(new Scope(evaluator.GetVisibleScope(user.UserId, permission, time.GetUtcNow())));
    }

    private sealed class Scope(Contracts.IVisibleScope inner) : ApiVisibleScope
    {
        public bool IsEverything => inner.Everything;

        public IReadOnlySet<DeviceId>? Devices => inner.Devices;
    }
}
