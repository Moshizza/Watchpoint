using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Standard error factory for identity errors (spec IDN §13).</summary>
public static class IdentityErrors
{
    /// <summary>Unauthenticated: Invalid credentials.</summary>
    public static Error InvalidCredentials() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.signin.invalid_credentials");

    /// <summary>Unauthenticated: Account locked until a given instant.</summary>
    public static Error Locked(DateTimeOffset until) =>
        Error.Create(ErrorKind.Unauthenticated, "identity.signin.locked", ("until", until.ToString("o")));

    /// <summary>Forbidden: Account is disabled.</summary>
    public static Error Disabled() =>
        Error.Create(ErrorKind.Forbidden, "identity.signin.disabled");

    /// <summary>Forbidden: Account validity window expired.</summary>
    public static Error Expired() =>
        Error.Create(ErrorKind.Forbidden, "identity.signin.expired");

    /// <summary>Forbidden: Client kind is not allowed for this account.</summary>
    public static Error ClientNotAllowed() =>
        Error.Create(ErrorKind.Forbidden, "identity.signin.client_not_allowed");

    /// <summary>Forbidden: Workstation is not allowed for this account.</summary>
    public static Error WorkstationNotAllowed() =>
        Error.Create(ErrorKind.Forbidden, "identity.signin.workstation_not_allowed");

    /// <summary>Unauthenticated: MFA verification failed or token expired.</summary>
    public static Error MfaInvalid() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.signin.mfa_invalid");

    /// <summary>Unauthenticated: Invalid access token.</summary>
    public static Error TokenInvalid(string? details = null) =>
        Error.Create(ErrorKind.Unauthenticated, "identity.token.invalid", ("details", details ?? "Invalid access token"));

    /// <summary>Unauthenticated: Access token expired.</summary>
    public static Error TokenExpired() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.token.expired");

    /// <summary>Unauthenticated: Session revoked.</summary>
    public static Error TokenRevoked() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.token.revoked");

    /// <summary>Unauthenticated: Refresh token invalid or expired.</summary>
    public static Error RefreshInvalid() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.refresh.invalid");

    /// <summary>Unauthenticated: Refresh token reuse detected.</summary>
    public static Error RefreshReused() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.refresh.reused");

    /// <summary>NotFound: User does not exist.</summary>
    public static Error UserNotFound(UserId id) =>
        Error.Create(ErrorKind.NotFound, "identity.user.not_found", ("userId", id.ToString()));

    /// <summary>Validation: Login name already in use.</summary>
    public static Error LoginTaken(string login) =>
        Error.Create(ErrorKind.Validation, "identity.user.login_taken", ("login", login));

    /// <summary>Conflict: At least one active administrator of kind Person must exist.</summary>
    public static Error LastAdministrator() =>
        Error.Create(ErrorKind.Conflict, "identity.user.last_administrator");

    /// <summary>Forbidden: User cannot modify their own Administrator, Enabled, or Kind flags.</summary>
    public static Error SelfChangeForbidden() =>
        Error.Create(ErrorKind.Forbidden, "identity.user.self_change_forbidden");

    /// <summary>Forbidden: Only administrators can create or promote administrators.</summary>
    public static Error AdminRequired() =>
        Error.Create(ErrorKind.Forbidden, "identity.user.admin_required");

    /// <summary>Validation: Password is too short.</summary>
    public static Error PasswordTooShort(int minLength) =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.too_short"), ("min", minLength))]);

    /// <summary>Validation: Password does not satisfy character class composition rules.</summary>
    public static Error PasswordTooSimple() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.too_simple"))]);

    /// <summary>Validation: Password contains user login or display name parts.</summary>
    public static Error PasswordContainsLogin() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.contains_login"))]);

    /// <summary>Validation: Password appears in breached passwords list.</summary>
    public static Error PasswordBreached() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.breached"))]);

    /// <summary>Validation: Password was recently used.</summary>
    public static Error PasswordReused(int historyCount) =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.reused"), ("historyCount", historyCount))]);

    /// <summary>Validation: Current password is incorrect.</summary>
    public static Error PasswordIncorrect() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/current", ErrorCode.FromTrusted("identity.password.incorrect"))]);

    /// <summary>TooManyRequests: Rate limit exceeded.</summary>
    public static Error RateLimited() =>
        Error.Create(ErrorKind.TooManyRequests, "common.rate_limited");

    /// <summary>PreconditionFailed: Concurrency version conflict.</summary>
    public static Error VersionConflict() =>
        Error.Create(ErrorKind.PreconditionFailed, "common.version.conflict");
}
