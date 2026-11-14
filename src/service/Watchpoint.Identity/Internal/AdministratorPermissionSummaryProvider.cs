using Watchpoint.Identity.Contracts;

namespace Watchpoint.Identity.Internal;

/// <summary>Default until RGT exists (M3): administrators hold every permission, nobody else holds any.</summary>
internal sealed class AdministratorPermissionSummaryProvider : IPermissionSummaryProvider
{
    private static readonly IReadOnlyList<string> _all = ["*"];
    private static readonly IReadOnlyList<string> _none = [];

    public ValueTask<IReadOnlyList<string>> GetEffectivePermissionsAsync(User user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return ValueTask.FromResult(user.Administrator && user.Enabled ? _all : _none);
    }
}
