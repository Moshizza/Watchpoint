namespace Watchpoint.Identity.Contracts;

/// <summary>
/// The effective permissions of a user, as a summary (RGT, M3). Identity uses it for the MFA policy
/// <c>configurationUsers</c> (any <c>config.*</c> permission), for the enrolled-workstation requirement (spec IDN §12),
/// for session management permissions and for <c>GET /auth/me</c>. Until RGT exists, the default implementation
/// grants everything (<c>*</c>) to administrators and nothing to anyone else.
/// </summary>
public interface IPermissionSummaryProvider
{
    /// <summary>Gets the permission codes of the user; <c>*</c> means every permission.</summary>
    /// <param name="user">The user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The permission codes.</returns>
    ValueTask<IReadOnlyList<string>> GetEffectivePermissionsAsync(User user, CancellationToken ct);
}

/// <summary>Helpers over permission summaries.</summary>
public static class PermissionSummary
{
    /// <summary>Whether the summary grants <paramref name="permission"/> (exact code or <c>*</c>).</summary>
    /// <param name="permissions">The summary.</param>
    /// <param name="permission">The permission code.</param>
    /// <returns>Whether it is granted.</returns>
    public static bool Grants(IReadOnlyList<string> permissions, string permission)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Any(p => p == "*" || string.Equals(p, permission, StringComparison.Ordinal));
    }

    /// <summary>Whether the summary holds any configuration permission (<c>config.*</c> or <c>*</c>).</summary>
    /// <param name="permissions">The summary.</param>
    /// <returns>Whether the user is a configuration user.</returns>
    public static bool IsConfigurationUser(IReadOnlyList<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.Any(p => p == "*" || p.StartsWith("config.", StringComparison.Ordinal));
    }
}
