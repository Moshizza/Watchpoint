using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Contracts;

/// <summary>The kind of user account (spec IDN §4).</summary>
public enum UserKind
{
    /// <summary>Individual human person.</summary>
    Person,

    /// <summary>Rights template for role/group mapping.</summary>
    Template,

    /// <summary>Automated script/service identity.</summary>
    Script,

    /// <summary>Federated directory user.</summary>
    Federation,

    /// <summary>Emergency break-glass account.</summary>
    BreakGlass,
}

/// <summary>Computed status of a user (spec IDN §4 rule U6).</summary>
public enum UserStatus
{
    /// <summary>Active and allowed to sign in.</summary>
    Active,

    /// <summary>Explicitly disabled.</summary>
    Disabled,

    /// <summary>Locked due to failed sign-in attempts.</summary>
    Locked,

    /// <summary>Outside its valid from/until lifetime window.</summary>
    Expired,
}

/// <summary>Authentication source of the user (spec IDN §4).</summary>
public enum AuthSource
{
    /// <summary>Local password authenticated via Argon2id.</summary>
    Local,

    /// <summary>LDAP / Active Directory.</summary>
    Directory,

    /// <summary>OpenID Connect.</summary>
    Oidc,

    /// <summary>No authentication credentials (templates and script identities).</summary>
    None,
}

/// <summary>Client software kind (spec IDN §4, §6.1).</summary>
public enum ClientKind
{
    /// <summary>Hardened Electron desktop application.</summary>
    Desktop,

    /// <summary>Web browser operation client.</summary>
    Web,

    /// <summary>Mobile application.</summary>
    Mobile,

    /// <summary>Direct API integration caller.</summary>
    Api,
}

/// <summary>MFA enforcement policy for a user account (spec IDN §4).</summary>
public enum MfaPolicy
{
    /// <summary>Inherit the site-wide MFA policy.</summary>
    Inherit,

    /// <summary>Always require MFA.</summary>
    Required,

    /// <summary>MFA not required.</summary>
    NotRequired,
}

/// <summary>Session policy configuration for a user (spec IDN §4).</summary>
/// <param name="MaxConcurrentSessions">Max concurrent sessions allowed (0 = unlimited).</param>
/// <param name="IdleLock">Client-side idle lock timeout.</param>
/// <param name="MaxSessionLength">Absolute maximum session length.</param>
public sealed record SessionPolicy(
    int MaxConcurrentSessions,
    TimeSpan IdleLock,
    TimeSpan MaxSessionLength)
{
    /// <summary>Default session policy.</summary>
    public static readonly SessionPolicy Default = new(
        MaxConcurrentSessions: 0,
        IdleLock: TimeSpan.FromMinutes(15),
        MaxSessionLength: TimeSpan.FromDays(7));
}

/// <summary>User account entity (spec IDN §4).</summary>
public sealed record User(
    UserId Id,
    string Login,
    string DisplayName,
    string? Email,
    string? Phone,
    string Language,
    string? TimeZone,
    UserKind Kind,
    bool Enabled,
    bool Administrator,
    AuthSource AuthSource,
    string? ExternalId,
    string? ExternalProvider,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    IReadOnlySet<ClientKind> AllowedClients,
    IReadOnlyList<string> AllowedWorkstations,
    SessionPolicy SessionPolicy,
    MfaPolicy Mfa,
    DateTimeOffset? PasswordChangedAt,
    bool MustChangePassword,
    DateTimeOffset? LastSignInAt,
    string? Notes,
    long Version);

/// <summary>Creation request payload for a new user account.</summary>
public sealed record NewUser(
    string Login,
    string DisplayName,
    string? Email = null,
    string? Phone = null,
    string Language = "en",
    string? TimeZone = null,
    UserKind Kind = UserKind.Person,
    bool Enabled = true,
    bool Administrator = false,
    AuthSource AuthSource = AuthSource.Local,
    string? ExternalId = null,
    string? ExternalProvider = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null,
    IReadOnlySet<ClientKind>? AllowedClients = null,
    IReadOnlyList<string>? AllowedWorkstations = null,
    SessionPolicy? SessionPolicy = null,
    MfaPolicy Mfa = MfaPolicy.Inherit,
    bool MustChangePassword = false,
    string? Notes = null);

/// <summary>Update request payload for an existing user account.</summary>
public sealed record UserUpdate(
    string? DisplayName = null,
    string? Email = null,
    string? Phone = null,
    string? Language = null,
    string? TimeZone = null,
    UserKind? Kind = null,
    bool? Enabled = null,
    bool? Administrator = null,
    AuthSource? AuthSource = null,
    string? ExternalId = null,
    string? ExternalProvider = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null,
    IReadOnlySet<ClientKind>? AllowedClients = null,
    IReadOnlyList<string>? AllowedWorkstations = null,
    SessionPolicy? SessionPolicy = null,
    MfaPolicy? Mfa = null,
    string? Notes = null);

/// <summary>Query filters for searching users.</summary>
public sealed record UserQuery(
    string? Text = null,
    UserKind? Kind = null,
    bool? Enabled = null,
    AuthSource? AuthSource = null,
    bool? Administrator = null);

/// <summary>Event raised when a user account is created, updated, or deleted.</summary>
public sealed class UserChangedEventArgs : EventArgs
{
    /// <summary>The identifier of the changed user.</summary>
    public UserId UserId { get; }

    /// <summary>The type of change.</summary>
    public string ChangeType { get; }

    /// <summary>Creates a new instance of <see cref="UserChangedEventArgs"/>.</summary>
    public UserChangedEventArgs(UserId userId, string changeType)
    {
        UserId = userId;
        ChangeType = changeType;
    }
}
