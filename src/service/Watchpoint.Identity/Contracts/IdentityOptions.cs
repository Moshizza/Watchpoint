namespace Watchpoint.Identity.Contracts;

/// <summary>Configurable Argon2id parameters (spec IDN §5).</summary>
/// <param name="MemorySizeKiB">Memory in KiB (default 65536 = 64 MiB).</param>
/// <param name="Iterations">Number of iterations (default 3).</param>
/// <param name="Parallelism">Degree of parallelism (default 2).</param>
/// <param name="SaltSize">Salt size in bytes (default 16).</param>
/// <param name="HashSize">Output hash size in bytes (default 32).</param>
public sealed record Argon2Parameters(
    int MemorySizeKiB = 65536,
    int Iterations = 3,
    int Parallelism = 2,
    int SaltSize = 16,
    int HashSize = 32);

/// <summary>Options for password policy evaluation (spec IDN §5, §7.1).</summary>
/// <param name="MinLength">Minimum password length (default 12).</param>
/// <param name="HistoryCount">Number of previous password hashes rejected (default 5).</param>
/// <param name="ExpiryDays">Password expiry in days (default 0 = disabled).</param>
/// <param name="RejectLoginDisplayNameParts">Whether to reject login or display name parts of length >= 4.</param>
/// <param name="CheckBreachedList">Whether to check against the breached passwords list.</param>
public sealed record PasswordPolicyOptions(
    int MinLength = 12,
    int HistoryCount = 5,
    int ExpiryDays = 0,
    bool RejectLoginDisplayNameParts = true,
    bool CheckBreachedList = true);

/// <summary>Options for JWT token creation and validation (spec IDN §6.2, §7.1).</summary>
/// <param name="AccessTokenLifetime">Lifetime of access tokens.</param>
/// <param name="SiteId">Site identifier for token issuer.</param>
/// <param name="Audience">Audience claim value.</param>
public sealed record JwtTokenOptions(
    TimeSpan AccessTokenLifetime,
    string SiteId = "default",
    string Audience = "watchpoint");

/// <summary>Options for refresh token lifetimes (spec IDN §6.3, §7.1).</summary>
/// <param name="IdleLifetime">Sliding idle lifetime.</param>
/// <param name="WebMaxSessionLength">Absolute maximum session length for Web clients.</param>
/// <param name="DesktopMaxSessionLength">Absolute maximum session length for Desktop clients.</param>
/// <param name="MobileMaxSessionLength">Absolute maximum session length for Mobile clients.</param>
public sealed record RefreshTokenOptions(
    TimeSpan IdleLifetime,
    TimeSpan WebMaxSessionLength,
    TimeSpan DesktopMaxSessionLength,
    TimeSpan MobileMaxSessionLength);

/// <summary>Options for lockout service (spec IDN §6.4, §7.1).</summary>
/// <param name="Threshold">Number of failures triggering lockout.</param>
/// <param name="Window">Sliding failure window.</param>
/// <param name="BaseDuration">Initial lock duration.</param>
/// <param name="MaxDuration">Maximum lock duration cap.</param>
public sealed record LockoutOptions(
    int Threshold = 5,
    TimeSpan Window = default,
    TimeSpan BaseDuration = default,
    TimeSpan MaxDuration = default)
{
    /// <summary>Gets the sliding failure window duration.</summary>
    public TimeSpan Window { get; init; } = Window == default ? TimeSpan.FromMinutes(15) : Window;

    /// <summary>Gets the initial base lockout duration.</summary>
    public TimeSpan BaseDuration { get; init; } = BaseDuration == default ? TimeSpan.FromMinutes(15) : BaseDuration;

    /// <summary>Gets the maximum cap for progressive lockout duration.</summary>
    public TimeSpan MaxDuration { get; init; } = MaxDuration == default ? TimeSpan.FromDays(1) : MaxDuration;
}

/// <summary>Options for sign-in rate limiting (spec IDN §6.1, §7.1).</summary>
/// <param name="RatePerIp">Max attempts allowed per remote IP in window.</param>
/// <param name="RatePerLogin">Max attempts allowed per login in window.</param>
/// <param name="Window">Sliding rate limit window.</param>
public sealed record SignInRateLimiterOptions(
    int RatePerIp = 20,
    int RatePerLogin = 10,
    TimeSpan Window = default)
{
    /// <summary>Gets the rate limiter sliding window duration.</summary>
    public TimeSpan Window { get; init; } = Window == default ? TimeSpan.FromMinutes(1) : Window;
}

/// <summary>Options for bootstrap administrator creation (spec IDN §7.1).</summary>
/// <param name="Login">Bootstrap admin login name.</param>
/// <param name="PasswordEnv">Environment variable holding initial password.</param>
public sealed record BootstrapAdminOptions(
    string Login = "admin",
    string PasswordEnv = "WATCHPOINT_BOOTSTRAP_ADMIN_PASSWORD");

/// <summary>Options for authentication service.</summary>
/// <param name="ChangeTokenLifetime">Lifetime of password change tokens.</param>
public sealed record AuthenticationOptions(
    TimeSpan ChangeTokenLifetime = default)
{
    /// <summary>Gets the lifetime of password change tokens.</summary>
    public TimeSpan ChangeTokenLifetime { get; init; } = ChangeTokenLifetime == default ? TimeSpan.FromMinutes(10) : ChangeTokenLifetime;
}
