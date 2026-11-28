using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Rights.Contracts;

/// <summary>Decides permissions (spec RGT §6). In memory, no I/O.</summary>
public interface IRightsEvaluator
{
    /// <summary>Whether any matching active grant exists anywhere.</summary>
    /// <param name="user">The user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="now">The time.</param>
    /// <returns><see langword="true"/> when granted.</returns>
    bool HasPermission(UserId user, PermissionCode permission, DateTimeOffset now);

    /// <summary>Decides for a target; <paramref name="client"/> is checked against the client-kind constraint when given.</summary>
    /// <param name="user">The user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="target">The target.</param>
    /// <param name="now">The time.</param>
    /// <param name="client">The client kind of the request, if known.</param>
    /// <returns>The decision.</returns>
    AuthorizationDecision Authorize(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now, ClientKind? client = null);

    /// <summary>The objects visible under a permission.</summary>
    /// <param name="user">The user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="now">The time.</param>
    /// <returns>The scope.</returns>
    IVisibleScope GetVisibleScope(UserId user, PermissionCode permission, DateTimeOffset now);

    /// <summary>Why a decision is what it is: each grant for the permission and whether it matched.</summary>
    /// <param name="user">The user.</param>
    /// <param name="permission">The permission.</param>
    /// <param name="target">The target.</param>
    /// <param name="now">The time.</param>
    /// <returns>One row per grant.</returns>
    IReadOnlyList<GrantExplanation> Explain(UserId user, PermissionCode permission, AuthorizationTarget target, DateTimeOffset now);
}

/// <summary>What a decision is about.</summary>
public abstract record AuthorizationTarget
{
    private AuthorizationTarget()
    {
    }

    /// <summary>A device.</summary>
    /// <param name="Id">The device.</param>
    public sealed record Device(DeviceId Id) : AuthorizationTarget;

    /// <summary>A location.</summary>
    /// <param name="Id">The location.</param>
    public sealed record Location(LocationId Id) : AuthorizationTarget;

    /// <summary>The system.</summary>
    public sealed record System : AuthorizationTarget;
}

/// <summary>A decision (spec RGT §6).</summary>
/// <param name="Allowed">Whether allowed.</param>
/// <param name="EffectiveConstraints">Least restrictive combination over matching grants.</param>
/// <param name="DenialCode"><c>common.forbidden</c>, <c>rights.outside_schedule</c> or <c>rights.client_kind</c> when denied.</param>
public sealed record AuthorizationDecision(bool Allowed, GrantConstraints EffectiveConstraints, string? DenialCode);

/// <summary>Why one grant did or did not match.</summary>
/// <param name="Grant">The grant.</param>
/// <param name="Matched">Whether it applies.</param>
/// <param name="Reason"><c>matched</c>, <c>permission</c>, <c>scope</c> or <c>schedule</c>.</param>
public sealed record GrantExplanation(RightGrant Grant, bool Matched, string Reason);

/// <summary>The objects a user may see under one permission (spec RGT §6).</summary>
public interface IVisibleScope
{
    /// <summary>Gets a value indicating whether everything is visible.</summary>
    bool Everything { get; }

    /// <summary>Whether a device is visible.</summary>
    /// <param name="id">The device.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    bool ContainsDevice(DeviceId id);

    /// <summary>Whether a location is visible: covered by a grant scope or containing a visible device.</summary>
    /// <param name="id">The location.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    bool ContainsLocation(LocationId id);

    /// <summary>Gets the visible devices; <see langword="null"/> when <see cref="Everything"/>.</summary>
    IReadOnlySet<DeviceId>? Devices { get; }

    /// <summary>Gets the visible locations; <see langword="null"/> when <see cref="Everything"/>.</summary>
    IReadOnlySet<LocationId>? Locations { get; }

    /// <summary>Gets the next schedule transition that affects this scope.</summary>
    DateTimeOffset ValidUntil { get; }
}

/// <summary>Removes objects after grant evaluation (spec RGT §6), e.g. location maintenance.</summary>
public interface IVisibleScopePostFilter
{
    /// <summary>Raised when the filter's decisions change.</summary>
    event EventHandler? Changed;

    /// <summary>Whether a device is removed from the visible set.</summary>
    /// <param name="device">The device.</param>
    /// <returns><see langword="true"/> to remove it.</returns>
    bool Excludes(DeviceId device);
}

/// <summary>Receives the users whose effective rights changed (published as <c>rights.changed</c> on WP_SYS).</summary>
public interface IRightsChangedSink
{
    /// <summary>Called when effective rights of the users changed.</summary>
    /// <param name="users">The users.</param>
    void RightsChanged(IReadOnlyCollection<UserId> users);
}
