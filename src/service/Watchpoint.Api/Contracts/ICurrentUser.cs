using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Api.Contracts;

/// <summary>Identity of the current request (spec API §5.2); anonymous when no valid token was presented.</summary>
public interface ICurrentUser
{
    /// <summary>Gets a value indicating whether the request carried a valid access token.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Gets the user (default when anonymous).</summary>
    UserId UserId { get; }

    /// <summary>Gets the session (default when anonymous).</summary>
    SessionId SessionId { get; }

    /// <summary>Gets the client kind of the token (<see cref="ClientKind.Api"/> when anonymous).</summary>
    ClientKind ClientKind { get; }

    /// <summary>Gets a value indicating whether the user is an administrator (token claim <c>adm</c>).</summary>
    bool IsAdministrator { get; }

    /// <summary>Gets the user's language (token claim), <c>en</c> when anonymous.</summary>
    string Language { get; }

    /// <summary>Whether the user holds <paramref name="permission"/> somewhere (RGT from M3, the M1 fallback before).</summary>
    /// <param name="permission">The permission.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when granted.</returns>
    ValueTask<bool> HasPermissionAsync(PermissionCode permission, CancellationToken ct);

    /// <summary>The objects the user may see under <paramref name="permission"/> (RGT; M1: everything).</summary>
    /// <param name="permission">The permission.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The visible scope.</returns>
    ValueTask<IVisibleScope> GetVisibleScopeAsync(PermissionCode permission, CancellationToken ct);
}

/// <summary>The objects a user may see for one permission (RGT); applied by endpoints, e.g. REG <c>DeviceQuery.Within</c>.</summary>
public interface IVisibleScope
{
    /// <summary>Gets a value indicating whether every object is visible.</summary>
    bool IsEverything { get; }

    /// <summary>Gets the visible devices, <see langword="null"/> when <see cref="IsEverything"/>.</summary>
    IReadOnlySet<DeviceId>? Devices { get; }
}

/// <summary>
/// Decides permissions for the current user (spec API §5.2). The API registers the M1–M2 fallback (<c>config.*</c> and
/// <c>admin.*</c> need an administrator, the rest needs authentication); RGT (M3) replaces it.
/// </summary>
public interface IPermissionEvaluator
{
    /// <summary>Whether <paramref name="user"/> holds <paramref name="permission"/> somewhere.</summary>
    /// <param name="user">The authenticated user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when granted.</returns>
    ValueTask<bool> HasPermissionAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct);

    /// <summary>The objects <paramref name="user"/> may see under <paramref name="permission"/>.</summary>
    /// <param name="user">The authenticated user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The visible scope.</returns>
    ValueTask<IVisibleScope> GetVisibleScopeAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct);
}

/// <summary>The scope that contains every object.</summary>
public sealed class EverythingScope : IVisibleScope
{
    /// <summary>Gets the shared instance.</summary>
    public static EverythingScope Instance { get; } = new();

    private EverythingScope()
    {
    }

    /// <inheritdoc />
    public bool IsEverything => true;

    /// <inheritdoc />
    public IReadOnlySet<DeviceId>? Devices => null;
}
