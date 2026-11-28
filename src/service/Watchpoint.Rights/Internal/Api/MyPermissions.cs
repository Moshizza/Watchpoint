using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights;

/// <summary>The permission codes a user holds somewhere ("anywhere" semantics), for UI gating (<c>GET /me/permissions</c>, <c>/auth/me</c>).</summary>
internal static class MyPermissions
{
    public static IReadOnlyList<string> Of(IPermissionCatalogue catalogue, IRightsEvaluator evaluator, UserId user, bool administrator, DateTimeOffset now) =>
        [.. catalogue.All.Where(p => administrator || evaluator.HasPermission(user, p.Code, now)).Select(p => p.Code.Value)];
}
