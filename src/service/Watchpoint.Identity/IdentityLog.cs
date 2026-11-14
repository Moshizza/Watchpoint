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

    private static readonly Action<ILogger, string, string, string, Exception?> _breakGlassAccountChanged =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(2731, "BreakGlassAccountChanged"),
            "Break-glass account {Action} ({UserId}) by {Actor}");

    private static readonly Action<ILogger, string, string, Exception?> _mfaEnrolled =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2740, "MfaEnrolled"),
            "User {UserId} enrolled MFA method {Method}");

    private static readonly Action<ILogger, string, string, Exception?> _mfaReset =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2741, "MfaReset"),
            "MFA of user {UserId} reset by {Actor}");

    private static readonly Action<ILogger, string, string, Exception?> _mfaVerificationFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2742, "MfaVerificationFailed"),
            "MFA verification failed for user {UserId} ({Method})");

    private static readonly Action<ILogger, string, Exception?> _mfaTokenInvalidated =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(2743, "MfaTokenInvalidated"),
            "MFA token of user {UserId} invalidated after too many wrong codes");

    private static readonly Action<ILogger, string, string, string, string, Exception?> _auditNormal =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Information,
            new EventId(2790, "Audit"),
            "Audit {Action} {Outcome} actor {Actor} subject {Subject}");

    private static readonly Action<ILogger, string, string, string, string, Exception?> _auditSensitive =
        LoggerMessage.Define<string, string, string, string>(
            LogLevel.Warning,
            new EventId(2791, "AuditSensitive"),
            "Audit {Action} {Outcome} actor {Actor} subject {Subject}");

    private static readonly Action<ILogger, string, string, string, string, string, Exception?> _auditCritical =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Critical,
            new EventId(2792, "AuditCritical"),
            "Audit {Action} {Outcome} actor {Actor} subject {Subject}: {Reason}");

    /// <summary>Logs event 2731 Warning: the break-glass account was created, enabled or its password reset.</summary>
    public static void BreakGlassAccountChanged(ILogger logger, string action, string userId, string actor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _breakGlassAccountChanged(logger, action, userId, actor, null);
    }

    /// <summary>Logs event 2740 Information: an MFA method was enrolled.</summary>
    public static void MfaEnrolled(ILogger logger, string userId, string method)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _mfaEnrolled(logger, userId, method, null);
    }

    /// <summary>Logs event 2741 Warning: an administrator reset a user's MFA.</summary>
    public static void MfaReset(ILogger logger, string userId, string actor)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _mfaReset(logger, userId, actor, null);
    }

    /// <summary>Logs event 2742 Warning: a second factor was refused.</summary>
    public static void MfaVerificationFailed(ILogger logger, string userId, string method)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _mfaVerificationFailed(logger, userId, method, null);
    }

    /// <summary>Logs event 2743 Warning: the MFA token died after five wrong codes.</summary>
    public static void MfaTokenInvalidated(ILogger logger, string userId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _mfaTokenInvalidated(logger, userId, null);
    }

    /// <summary>Logs an audit record as event 2790 (normal), 2791 (sensitive) or 2792 (critical). Details never hold secrets.</summary>
    public static void AuditRecord(ILogger logger, Contracts.IdentityAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(record);
        var actor = record.Actor?.ToString() ?? "system";
        var subject = record.Subject?.ToString() ?? "-";
        var outcome = record.Outcome.ToString();
        using (logger.BeginScope(record.Details))
        {
            switch (record.Severity)
            {
                case Contracts.IdentityAuditSeverity.Critical:
                    _auditCritical(logger, record.Action, outcome, actor, subject, record.Reason ?? "-", null);
                    break;
                case Contracts.IdentityAuditSeverity.Sensitive:
                    _auditSensitive(logger, record.Action, outcome, actor, subject, null);
                    break;
                default:
                    _auditNormal(logger, record.Action, outcome, actor, subject, null);
                    break;
            }
        }
    }

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
