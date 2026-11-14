using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Identity;

/// <summary>Structured logging for identity module (EventId range 2700–2799, spec IDN §14, README §8).</summary>
public static partial class IdentityLog
{
    private static readonly Action<ILogger, Exception?> _noAdminAccount =
        LoggerMessage.Define(
            LogLevel.Critical,
            new EventId(2701, "NoAdministrator"),
            "No administrator account exists; run wpctl reset-admin");

    private static readonly Action<ILogger, string, Exception?> _bootstrapAdminFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2702, "BootstrapAdminFailed"),
            "Bootstrap administrator could not be created: {Code}");

    private static readonly Action<ILogger, string, string, string, Exception?> _userSignedIn =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(2710, "UserSignedIn"),
            "User {UserId} signed in ({ClientKind}) session {SessionId}");

    private static readonly Action<ILogger, string, string, string, Exception?> _signInFailed =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2711, "SignInFailed"),
            "Sign-in failed for login {LoginHash} from {RemoteIp}: {Code}");

    private static readonly Action<ILogger, string, string, Exception?> _accountLocked =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2712, "AccountLocked"),
            "Account {UserId} locked until {Until}");

    private static readonly Action<ILogger, string, Exception?> _refreshTokenReuse =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2713, "RefreshTokenReuse"),
            "Refresh token reuse detected; session {SessionId} revoked");

    private static readonly Action<ILogger, string, string, Exception?> _sessionRevoked =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2720, "SessionRevoked"),
            "Session {SessionId} revoked: {Reason}");

    private static readonly Action<ILogger, string, string, Exception?> _breakGlassUsed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2730, "BreakGlassUsed"),
            "Break-glass sign-in by {RemoteIp}: {Reason}");

    /// <summary>Logs event 2701 Critical: No administrator account exists.</summary>
    public static void NoAdministrator(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _noAdminAccount(logger, null);
    }

    /// <summary>Logs event 2702 Error: the bootstrap administrator could not be created (error code only).</summary>
    public static void BootstrapAdminFailed(ILogger logger, string code)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _bootstrapAdminFailed(logger, code, null);
    }

    /// <summary>Logs event 2710 Information: User signed in.</summary>
    public static void UserSignedIn(ILogger logger, string userId, string clientKind, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _userSignedIn(logger, userId, clientKind, sessionId, null);
    }

    /// <summary>Logs event 2711 Warning: Sign-in failed (login name hashed with SHA-256).</summary>
    public static void SignInFailed(ILogger logger, string login, string remoteIp, string code)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var loginHash = HashLogin(login);
        _signInFailed(logger, loginHash, remoteIp, code, null);
    }

    /// <summary>Logs event 2712 Warning: Account locked.</summary>
    public static void AccountLocked(ILogger logger, string userId, string until)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _accountLocked(logger, userId, until, null);
    }

    /// <summary>Logs event 2713 Warning: Refresh token reuse detected.</summary>
    public static void RefreshTokenReuse(ILogger logger, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _refreshTokenReuse(logger, sessionId, null);
    }

    /// <summary>Logs event 2720 Information: Session revoked.</summary>
    public static void SessionRevoked(ILogger logger, string sessionId, string reason)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _sessionRevoked(logger, sessionId, reason, null);
    }

    /// <summary>Logs event 2730 Warning: Break-glass sign-in used.</summary>
    public static void BreakGlassUsed(ILogger logger, string remoteIp, string reason)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _breakGlassUsed(logger, remoteIp, reason, null);
    }

    private static string HashLogin(string login)
    {
        if (string.IsNullOrEmpty(login))
        {
            return "empty";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(login.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
