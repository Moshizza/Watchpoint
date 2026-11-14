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

    /// <summary>Unauthenticated: Invalid access token. Carries no detail: the reason is never told to the caller.</summary>
    public static Error TokenInvalid() =>
        Error.Create(ErrorKind.Unauthenticated, "identity.token.invalid");

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

    /// <summary>Validation: Password is not a valid Unicode string.</summary>
    public static Error PasswordInvalidCharacters() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.invalid_characters"))]);

    /// <summary>Validation: Password is longer than the maximum.</summary>
    public static Error PasswordTooLong(int maxLength) =>
        Error.Validation("common.validation.failed", [FieldError.Create("/password", ErrorCode.FromTrusted("identity.password.too_long"), ("max", maxLength))]);

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

    /// <summary>Validation: the break-glass sign-in needs a reason (spec IDN §11).</summary>
    public static Error ReasonRequired() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/reason", ErrorCode.FromTrusted("identity.signin.reason_required"))]);

    /// <summary>Forbidden: a configuration session needs a workstation verified by its certificate (spec IDN §12).</summary>
    public static Error WorkstationNotVerified() =>
        Error.Create(ErrorKind.Forbidden, "identity.signin.workstation_not_verified");

    /// <summary>TooManyRequests: the user's concurrent session limit is reached (spec IDN §6.1 step 6).</summary>
    public static Error SessionLimitReached(int max) =>
        Error.Create(ErrorKind.TooManyRequests, "identity.session.limit_reached", ("max", max));

    /// <summary>NotFound: Session does not exist.</summary>
    public static Error SessionNotFound(SessionId id) =>
        Error.Create(ErrorKind.NotFound, "identity.session.not_found", ("sessionId", id.ToString()));

    /// <summary>Forbidden: the actor may not see or revoke this session.</summary>
    public static Error SessionForbidden() =>
        Error.Create(ErrorKind.Forbidden, "identity.session.forbidden");

    /// <summary>Unprocessable: no MFA method is enrolled for the user.</summary>
    public static Error MfaNotEnrolled() =>
        Error.Create(ErrorKind.Unprocessable, "identity.mfa.not_enrolled");

    /// <summary>Conflict: TOTP is already enrolled and confirmed; reset it first.</summary>
    public static Error MfaAlreadyEnrolled() =>
        Error.Create(ErrorKind.Conflict, "identity.mfa.already_enrolled");

    /// <summary>Validation: the code given to confirm an enrolment is wrong.</summary>
    public static Error MfaCodeInvalid() =>
        Error.Validation("common.validation.failed", [FieldError.Create("/code", ErrorCode.FromTrusted("identity.mfa.code_invalid"))]);

    /// <summary>Unprocessable: WebAuthn is not configured on this site (<c>Identity:WebAuthn:RpId</c>).</summary>
    public static Error WebAuthnNotConfigured() =>
        Error.Create(ErrorKind.Unprocessable, "identity.webauthn.not_configured");

    /// <summary>Unavailable: the secrets vault needed for MFA secrets is not available.</summary>
    public static Error MfaVaultUnavailable() =>
        Error.Create(ErrorKind.Unavailable, "identity.mfa.vault_unavailable");

    /// <summary>Forbidden: a step-up proof is required or invalid (spec IDN §10, CMD critical commands).</summary>
    public static Error StepUpRequired() =>
        Error.Create(ErrorKind.Forbidden, "identity.stepup.required");

    /// <summary>Forbidden: the step-up factor was wrong. No detail.</summary>
    public static Error StepUpInvalid() =>
        Error.Create(ErrorKind.Forbidden, "identity.stepup.invalid");

    /// <summary>Conflict: a break-glass account already exists (spec IDN §11).</summary>
    public static Error BreakGlassExists() =>
        Error.Create(ErrorKind.Conflict, "identity.breakglass.exists");

    /// <summary>Validation: the break-glass account rules are not met (login <c>breakglass</c>, kind, local authentication, administrator).</summary>
    public static Error BreakGlassInvalid(string path) =>
        Error.Validation("common.validation.failed", [FieldError.Create(path, ErrorCode.FromTrusted("identity.breakglass.invalid"))]);

    /// <summary>TooManyRequests: Rate limit exceeded.</summary>
    public static Error RateLimited() =>
        Error.Create(ErrorKind.TooManyRequests, "common.rate_limited");

    /// <summary>PreconditionFailed: Concurrency version conflict.</summary>
    public static Error VersionConflict() =>
        Error.Create(ErrorKind.PreconditionFailed, "common.version.conflict");
}
