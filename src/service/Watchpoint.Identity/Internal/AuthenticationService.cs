using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Audit;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Mfa;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal;

/// <summary>Full local sign-in and token authentication pipeline (spec IDN §6.1, §10, §11, §12).</summary>
internal sealed class AuthenticationService : IAuthenticationService
{
    internal static readonly TimeSpan MfaTokenLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan EnrolmentTokenLifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan StepUpLifetime = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan BreakGlassSessionLength = TimeSpan.FromHours(2);
    internal const int MaxMfaFailures = 5;
    internal const int MaxReasonLength = 512;

    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly IUserDirectory _userDirectory;
    private readonly Argon2PasswordHasher _hasher;
    private readonly ITokenService _tokenService;
    private readonly RefreshTokenStore _refreshStore;
    private readonly SessionRegistry _sessionRegistry;
    private readonly LockoutService _lockoutService;
    private readonly SignInRateLimiter _rateLimiter;
    private readonly IEnumerable<ISessionAdmission> _admissions;
    private readonly TimeProvider _clock;
    private readonly MfaService _mfa;
    private readonly IdentityAuditor _auditor;
    private readonly IPermissionSummaryProvider _permissions;
    private readonly ClientBindingOptions _binding;
    private readonly AuthenticationOptions _options;
    private readonly ILogger<AuthenticationService>? _logger;

    // Tokens of partial sign-ins (password change, MFA, MFA enrolment) and step-up proofs (spec IDN §6.1 step 7, §10).
    private readonly PendingTokenStore _pending = new();

    public AuthenticationService(
        IDbContextFactory<IdentityDbContext> dbFactory,
        IUserDirectory userDirectory,
        Argon2PasswordHasher hasher,
        ITokenService tokenService,
        RefreshTokenStore refreshStore,
        SessionRegistry sessionRegistry,
        LockoutService lockoutService,
        SignInRateLimiter rateLimiter,
        IEnumerable<ISessionAdmission> admissions,
        TimeProvider clock,
        MfaService mfa,
        IdentityAuditor auditor,
        AuthenticationOptions? options = null,
        ILogger<AuthenticationService>? logger = null,
        IPermissionSummaryProvider? permissions = null,
        ClientBindingOptions? binding = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _userDirectory = userDirectory ?? throw new ArgumentNullException(nameof(userDirectory));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _refreshStore = refreshStore ?? throw new ArgumentNullException(nameof(refreshStore));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _lockoutService = lockoutService ?? throw new ArgumentNullException(nameof(lockoutService));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _admissions = (admissions ?? []).OrderBy(a => a.Order).ToList();
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _mfa = mfa ?? throw new ArgumentNullException(nameof(mfa));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
        _options = options ?? new AuthenticationOptions();
        _logger = logger;
        _permissions = permissions ?? new AdministratorPermissionSummaryProvider();
        _binding = binding ?? new ClientBindingOptions();
    }

    public async Task<Result<SignInOutcome>> SignInLocalAsync(SignInRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Rate limit per remote IP and per login
        var login = request.Login ?? string.Empty;
        if (!_rateLimiter.TryAcquire(request.Client.RemoteIp, login))
        {
            LogSignInFailed(login, request.Client, "common.rate_limited");
            return IdentityErrors.RateLimited();
        }

        try
        {
            return await SignInCoreAsync(request, login, ct).ConfigureAwait(false);
        }
        catch (HashPoolBusyException)
        {
            // Hash pool full for 5 s (spec IDN §5).
            return IdentityErrors.RateLimited();
        }
    }

    private async Task<Result<SignInOutcome>> SignInCoreAsync(SignInRequest request, string login, CancellationToken ct)
    {
        // Input that can never be a valid credential (login longer than U1 allows; password empty, longer than 256
        // characters or not valid Unicode) is refused here, before the user is looked up, so that the answer and
        // its cost are the same whether the login exists or not.
        if (string.IsNullOrWhiteSpace(login) || login.Trim().Length > 64 || !Argon2PasswordHasher.TryNormalize(request.Password, out _))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            LogSignInFailed(login, request.Client, "identity.signin.invalid_credentials");
            return IdentityErrors.InvalidCredentials();
        }

        // 2. Find user; if not found / template / script -> dummy hash verify -> invalid_credentials
        var userResult = _userDirectory.FindByLogin(login);
        if (!userResult.TryGetValue(out var user, out _))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            LogSignInFailed(login, request.Client, "identity.signin.invalid_credentials");
            return IdentityErrors.InvalidCredentials();
        }

        if (user.Kind is UserKind.Template or UserKind.Script || user.AuthSource == AuthSource.None ||
            (user.Kind == UserKind.BreakGlass && user.AuthSource != AuthSource.Local))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            LogSignInFailed(request.Login, request.Client, "identity.signin.invalid_credentials");
            return IdentityErrors.InvalidCredentials();
        }

        // Load stored hash
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var userEntity = await db.Users.FindAsync([user.Id], ct).ConfigureAwait(false);
        if (userEntity is null || string.IsNullOrEmpty(userEntity.PasswordHash))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            return IdentityErrors.InvalidCredentials();
        }

        // 3. If a lockout is active: verify password anyway; wrong -> invalid_credentials; correct -> locked with until
        var (isLocked, lockedUntil) = await _lockoutService.CheckLockoutAsync(user.Id, ct).ConfigureAwait(false);
        if (isLocked)
        {
            var (lockedVerify, _) = await _hasher.VerifyPasswordAsync(request.Password, userEntity.PasswordHash, ct).ConfigureAwait(false);
            if (!lockedVerify)
            {
                LogSignInFailed(request.Login, request.Client, "identity.signin.invalid_credentials");
                return IdentityErrors.InvalidCredentials();
            }

            return IdentityErrors.Locked(lockedUntil ?? _clock.GetUtcNow().AddMinutes(15));
        }

        // 4. Verify password; on failure increment failure count, audit, return invalid_credentials
        var (verified, needsRehash) = await _hasher.VerifyPasswordAsync(request.Password, userEntity.PasswordHash, ct).ConfigureAwait(false);
        if (!verified)
        {
            await _lockoutService.RecordFailureAsync(user.Id, ct).ConfigureAwait(false);
            LogSignInFailed(request.Login, request.Client, "identity.signin.invalid_credentials");
            await _auditor.RecordAsync("identity.signInFailed", IdentityAuditOutcome.Failure, null, user.Id, IdentityAuditSeverity.Normal, ct, null,
                ("code", "identity.signin.invalid_credentials"), ("clientKind", request.Client.Kind.ToString()), ("remoteIp", request.Client.RemoteIp?.ToString())).ConfigureAwait(false);
            return IdentityErrors.InvalidCredentials();
        }

        // Rehash on parameter upgrade (IDN §5, IDN-U-009)
        if (needsRehash)
        {
            var newHash = await _hasher.HashPasswordAsync(request.Password, ct).ConfigureAwait(false);
            userEntity.PasswordHash = newHash;
            await SaveBestEffortAsync(db, ct).ConfigureAwait(false);
        }

        var now = _clock.GetUtcNow();

        // Break-glass (spec IDN §11): a reason, the desktop client; everything it does is audited as critical.
        var reason = request.Reason?.Trim();
        if (user.Kind == UserKind.BreakGlass)
        {
            if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength)
            {
                await AuditBreakGlassAsync(user, request.Client, IdentityAuditOutcome.Denied, reason, "identity.signin.reason_required", ct).ConfigureAwait(false);
                return IdentityErrors.ReasonRequired();
            }

            if (request.Client.Kind != ClientKind.Desktop)
            {
                await AuditBreakGlassAsync(user, request.Client, IdentityAuditOutcome.Denied, reason, "identity.signin.client_not_allowed", ct).ConfigureAwait(false);
                return IdentityErrors.ClientNotAllowed();
            }
        }

        // 5. Status checks: disabled, expired, client kind, browser origin, workstation, enrolled workstation
        if (await CheckStatusAsync(user, request.Client, now, ct).ConfigureAwait(false) is { } statusError)
        {
            return statusError;
        }

        // 6. ISessionAdmission hooks (all must allow) and the concurrent session limit
        if (await AdmitAsync(user, request.Client, ct).ConfigureAwait(false) is { } admissionError)
        {
            return admissionError;
        }

        // 7. If MustChangePassword -> PasswordChangeRequired
        if (user.MustChangePassword)
        {
            return new SignInOutcome.PasswordChangeRequired(_pending.Issue(PendingKind.PasswordChange, user.Id, request.Client, now, _options.ChangeTokenLifetime));
        }

        // 8. MFA (spec IDN §10): nothing is issued but a token that can only complete (or enrol) the second factor.
        if (await _mfa.IsRequiredAsync(user, ct).ConfigureAwait(false))
        {
            var status = await _mfa.GetStatusAsync(user.Id, ct).ConfigureAwait(false);
            if (status.HasMethod)
            {
                return new SignInOutcome.MfaRequired(_pending.Issue(PendingKind.Mfa, user.Id, request.Client, now, MfaTokenLifetime), status.Methods);
            }

            return new SignInOutcome.MfaEnrolmentRequired(_pending.Issue(PendingKind.MfaEnrolment, user.Id, request.Client, now, EnrolmentTokenLifetime));
        }

        // 9. Create session and tokens; reset failure count; update LastSignInAt; audit signedIn
        var tokenPair = await IssueTokensAndCreateSessionAsync(user, request.Client, ct).ConfigureAwait(false);
        await _lockoutService.ResetFailuresAsync(user.Id, ct).ConfigureAwait(false);

        userEntity.LastSignInAt = now;
        await SaveBestEffortAsync(db, ct).ConfigureAwait(false);

        var updatedUser = user with { LastSignInAt = now };
        await AuditSignedInAsync(user, request.Client, tokenPair.Session, "password", ct).ConfigureAwait(false);

        if (user.Kind == UserKind.BreakGlass)
        {
            await AuditBreakGlassAsync(user, request.Client, IdentityAuditOutcome.Success, reason, null, ct, tokenPair.Session).ConfigureAwait(false);
        }

        return new SignInOutcome.Signed(tokenPair, updatedUser);
    }

    public async Task<Result<TokenPair>> VerifyMfaAsync(MfaVerification verification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(verification);
        var now = _clock.GetUtcNow();
        if (!_pending.TryPeek(verification.MfaToken, PendingKind.Mfa, now, out var entry) || !SameClient(entry.Client, verification.Client))
        {
            return IdentityErrors.MfaInvalid();
        }

        var verified = await _mfa.VerifyAsync(entry.UserId, verification.Method, verification.Code, verification.Assertion, ct).ConfigureAwait(false);
        if (!verified.IsSuccess)
        {
            if (verified.Error.Code.Value is "common.rate_limited" or "identity.mfa.vault_unavailable" or "identity.webauthn.not_configured")
            {
                return verified.Error;
            }

            await CountMfaFailureAsync(verification.MfaToken, entry, verification.Method.ToString().ToLowerInvariant(), ct).ConfigureAwait(false);
            return IdentityErrors.MfaInvalid();
        }

        // Single use: a concurrent verification with the same token loses here.
        if (!_pending.TryTake(verification.MfaToken, PendingKind.Mfa, now, out entry))
        {
            return IdentityErrors.MfaInvalid();
        }

        return await CompleteSignedAsync(entry.UserId, entry.Client, "mfa:" + verification.Method.ToString().ToLowerInvariant(), ct).ConfigureAwait(false);
    }

    public Task<Result<WebAuthnCeremony>> BeginMfaWebAuthnAsync(string mfaToken, ClientContext client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_pending.TryPeek(mfaToken, PendingKind.Mfa, _clock.GetUtcNow(), out var entry) || !SameClient(entry.Client, client))
        {
            return Task.FromResult<Result<WebAuthnCeremony>>(IdentityErrors.MfaInvalid());
        }

        return _mfa.BeginAssertionAsync(entry.UserId, ct);
    }

    public Task<Result<TotpEnrolment>> BeginMfaEnrolmentAsync(string enrolmentToken, ClientContext client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!_pending.TryPeek(enrolmentToken, PendingKind.MfaEnrolment, _clock.GetUtcNow(), out var entry) || !SameClient(entry.Client, client))
        {
            return Task.FromResult<Result<TotpEnrolment>>(IdentityErrors.TokenInvalid());
        }

        return _mfa.EnrollTotpAsync(entry.UserId, ct);
    }

    public async Task<Result<MfaEnrolmentCompleted>> CompleteMfaEnrolmentAsync(string enrolmentToken, string code, ClientContext client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = _clock.GetUtcNow();
        if (!_pending.TryPeek(enrolmentToken, PendingKind.MfaEnrolment, now, out var entry) || !SameClient(entry.Client, client))
        {
            return IdentityErrors.TokenInvalid();
        }

        var confirmed = await _mfa.ConfirmTotpAsync(entry.UserId, code, ct).ConfigureAwait(false);
        if (!confirmed.TryGetValue(out var codes, out var error))
        {
            if (error.FieldErrors.Any(f => f.Code.Value == "identity.mfa.code_invalid"))
            {
                await CountMfaFailureAsync(enrolmentToken, entry, "totp-enrolment", ct).ConfigureAwait(false);
            }

            return error;
        }

        if (!_pending.TryTake(enrolmentToken, PendingKind.MfaEnrolment, now, out entry))
        {
            return IdentityErrors.TokenInvalid();
        }

        var signedIn = await CompleteSignedAsync(entry.UserId, entry.Client, "mfa:totp-enrolment", ct).ConfigureAwait(false);
        if (!signedIn.TryGetValue(out var tokens, out var signInError))
        {
            return signInError;
        }

        return new MfaEnrolmentCompleted(tokens, codes);
    }

    public async Task<Result<StepUpProof>> StepUpAsync(SessionId session, StepUpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _clock.GetUtcNow();
        var factors = new[] { request.Password is not null, request.Totp is not null, request.Assertion is not null }.Count(f => f);
        if (factors != 1 || !_sessionRegistry.Get(session).TryGetValue(out var current, out _) ||
            current.RevokedAt is not null || _sessionRegistry.IsRevoked(session) || current.ExpiresAt <= now ||
            !_userDirectory.Get(current.UserId).TryGetValue(out var user, out _) || !user.Enabled)
        {
            return IdentityErrors.StepUpInvalid();
        }

        string method;
        try
        {
            if (request.Password is not null)
            {
                method = "password";
                var (locked, _) = await _lockoutService.CheckLockoutAsync(user.Id, ct).ConfigureAwait(false);
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var entity = await db.Users.FindAsync([user.Id], ct).ConfigureAwait(false);
                var ok = !locked && entity?.PasswordHash is { } hash && Argon2PasswordHasher.TryNormalize(request.Password, out _) &&
                    (await _hasher.VerifyPasswordAsync(request.Password, hash, ct).ConfigureAwait(false)).Verified;
                if (!ok)
                {
                    // A stolen session must not allow unlimited guesses of the password.
                    await _lockoutService.RecordFailureAsync(user.Id, ct).ConfigureAwait(false);
                    await _auditor.RecordAsync("identity.stepUp", IdentityAuditOutcome.Failure, user.Id, user.Id, IdentityAuditSeverity.Normal, ct, null, ("method", method), ("sessionId", session.ToString())).ConfigureAwait(false);
                    return IdentityErrors.StepUpInvalid();
                }
            }
            else
            {
                method = request.Totp is not null ? "totp" : "webauthn";
                var verified = await _mfa.VerifyAsync(user.Id, request.Totp is not null ? MfaMethod.Totp : MfaMethod.WebAuthn, request.Totp, request.Assertion, ct).ConfigureAwait(false);
                if (!verified.IsSuccess)
                {
                    if (verified.Error.Code.Value == "common.rate_limited")
                    {
                        return verified.Error;
                    }

                    await _lockoutService.RecordFailureAsync(user.Id, ct).ConfigureAwait(false);
                    await _auditor.RecordAsync("identity.stepUp", IdentityAuditOutcome.Failure, user.Id, user.Id, IdentityAuditSeverity.Normal, ct, null, ("method", method), ("sessionId", session.ToString())).ConfigureAwait(false);
                    return IdentityErrors.StepUpInvalid();
                }
            }
        }
        catch (HashPoolBusyException)
        {
            return IdentityErrors.RateLimited();
        }

        var client = new ClientContext(current.ClientKind, current.ClientInstanceId, current.WorkstationId);
        var token = _pending.Issue(PendingKind.StepUp, user.Id, client, now, StepUpLifetime, session);
        await _auditor.RecordAsync("identity.stepUp", IdentityAuditOutcome.Success, user.Id, user.Id, IdentityAuditSeverity.Normal, ct, null, ("method", method), ("sessionId", session.ToString())).ConfigureAwait(false);
        return new StepUpProof(token, now + StepUpLifetime);
    }

    public Task<Result<WebAuthnCeremony>> BeginStepUpWebAuthnAsync(SessionId session, CancellationToken ct)
    {
        if (!_sessionRegistry.Get(session).TryGetValue(out var current, out _) || current.RevokedAt is not null || _sessionRegistry.IsRevoked(session))
        {
            return Task.FromResult<Result<WebAuthnCeremony>>(IdentityErrors.StepUpInvalid());
        }

        return _mfa.BeginAssertionAsync(current.UserId, ct);
    }

    public Result ValidateStepUp(string stepUpToken, SessionId session)
    {
        if (!_pending.TryPeek(stepUpToken, PendingKind.StepUp, _clock.GetUtcNow(), out var entry) || entry.Session != session || _sessionRegistry.IsRevoked(session))
        {
            return IdentityErrors.StepUpRequired();
        }

        return Result.Success();
    }

    public async Task<Result<TokenPair>> RefreshAsync(string refreshToken, ClientContext client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Order (spec IDN §6.3): the store validates and claims the token, then the user and admission checks run,
        // and the token is rotated only if they pass. A temporary refusal therefore leaves the token usable.
        User? admitted = null;
        var rotateResult = await _refreshStore.RotateRefreshTokenAsync(
            refreshToken,
            client,
            async (session, token) =>
            {
                var (error, user) = await AdmitRefreshAsync(session, client, token).ConfigureAwait(false);
                admitted = user;
                return error is null ? Result.Success() : Result.Failure(error);
            },
            ct).ConfigureAwait(false);

        if (!rotateResult.TryGetValue(out var tuple, out var rotateError))
        {
            return rotateError;
        }

        var (newToken, newExpiresAt, rotatedSession) = tuple;
        var (accessToken, accessExpiresAt) = _tokenService.IssueAccessToken(rotatedSession, admitted!);
        _sessionRegistry.Touch(rotatedSession.Id);

        return new TokenPair(accessToken, accessExpiresAt, newToken, newExpiresAt, rotatedSession.Id);
    }

    // A user who can no longer sign in at all (deleted, cannot-sign-in kind, disabled, expired) loses the session;
    // an admission hook (maintenance, seats) only refuses this refresh.
    private async Task<(Error? Error, User? User)> AdmitRefreshAsync(Session session, ClientContext client, CancellationToken ct)
    {
        if (!_userDirectory.Get(session.UserId).TryGetValue(out var user, out _) ||
            user.Kind is UserKind.Template or UserKind.Script)
        {
            await _sessionRegistry.RevokeAsync(session.Id, "User deleted", null, ct).ConfigureAwait(false);
            return (IdentityErrors.RefreshInvalid(), null);
        }

        var now = _clock.GetUtcNow();
        if (!user.Enabled)
        {
            await _sessionRegistry.RevokeAsync(session.Id, "User disabled", null, ct).ConfigureAwait(false);
            return (IdentityErrors.Disabled(), null);
        }

        if ((user.ValidFrom.HasValue && now < user.ValidFrom.Value) ||
            (user.ValidUntil.HasValue && now > user.ValidUntil.Value))
        {
            await _sessionRegistry.RevokeAsync(session.Id, "User expired", null, ct).ConfigureAwait(false);
            return (IdentityErrors.Expired(), null);
        }

        // A browser can only hold the Web client's session (spec IDN §12).
        if (client.BrowserOrigin is not null && session.ClientKind != ClientKind.Web)
        {
            return (IdentityErrors.RefreshInvalid(), null);
        }

        foreach (var admission in _admissions)
        {
            var admissionResult = await admission.AdmitAsync(user, client, AdmissionPhase.Refresh, ct).ConfigureAwait(false);
            if (!admissionResult.IsSuccess)
            {
                return (admissionResult.Error, null);
            }
        }

        return (null, user);
    }

    public async Task<Result> SignOutAsync(SessionId session, CancellationToken ct)
    {
        _pending.RemoveSession(session);
        return await _sessionRegistry.RevokeAsync(session, "Explicit sign-out", null, ct).ConfigureAwait(false);
    }

    public async Task<Result<SignInOutcome>> CompletePasswordChangeAsync(string changeToken, string newPassword, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        // Single use: the token is taken out before anything else.
        if (!_pending.TryTake(changeToken, PendingKind.PasswordChange, now, out var entry))
        {
            return IdentityErrors.TokenInvalid();
        }

        // The account may have changed since the token was issued: same checks as sign-in steps 5 and 6.
        if (!_userDirectory.Get(entry.UserId).TryGetValue(out var user, out _) ||
            !user.MustChangePassword ||
            user.Kind is UserKind.Template or UserKind.Script || user.AuthSource != AuthSource.Local)
        {
            return IdentityErrors.TokenInvalid();
        }

        if (await CheckStatusAsync(user, entry.Client, now, ct).ConfigureAwait(false) is { } statusError)
        {
            return statusError;
        }

        if (await AdmitAsync(user, entry.Client, ct, includeSessionLimit: false).ConfigureAwait(false) is { } admissionError)
        {
            return admissionError;
        }

        // Apply new password
        Result changeResult;
        try
        {
            changeResult = await _userDirectory.SetPasswordAsync(user.Id, newPassword, mustChange: false, new ChangeContext(user.Id, "changeToken"), ct).ConfigureAwait(false);
        }
        catch (HashPoolBusyException)
        {
            changeResult = IdentityErrors.RateLimited();
        }

        if (!changeResult.IsSuccess)
        {
            // A refused new password (policy, busy pool) does not cost the token: the user can try another one.
            if (changeResult.Error.Kind is ErrorKind.Validation or ErrorKind.TooManyRequests)
            {
                _pending.Restore(changeToken, entry);
            }

            return changeResult.Error;
        }

        // The password changed: every other change token of the user dies with the old password.
        _pending.RemoveAll(user.Id, PendingKind.PasswordChange, now);

        return await CompleteSignInAsync(user.Id, entry.Client, "password-change", ct).ConfigureAwait(false);
    }

    // A sign-in completed by a second factor can only end signed (or refused): no further step is pending.
    private async Task<Result<TokenPair>> CompleteSignedAsync(UserId userId, ClientContext client, string method, CancellationToken ct)
    {
        var outcome = await CompleteSignInAsync(userId, client, method, ct).ConfigureAwait(false);
        if (!outcome.TryGetValue(out var value, out var error))
        {
            return error;
        }

        return value is SignInOutcome.Signed signed ? signed.Tokens : IdentityErrors.TokenInvalid();
    }

    /// <summary>
    /// Final step of a sign-in that went through a pending token (password change, MFA, enrolment): the account is
    /// checked again (it may have been disabled meanwhile), the second factor is asked when it is still owed (steps 8),
    /// then the session and tokens are issued (step 9).
    /// </summary>
    private async Task<Result<SignInOutcome>> CompleteSignInAsync(UserId userId, ClientContext client, string method, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!_userDirectory.Get(userId).TryGetValue(out var user, out _) || user.Kind is UserKind.Template or UserKind.Script)
        {
            return IdentityErrors.TokenInvalid();
        }

        if (await CheckStatusAsync(user, client, now, ct).ConfigureAwait(false) is { } statusError)
        {
            return statusError;
        }

        if (await AdmitAsync(user, client, ct).ConfigureAwait(false) is { } admissionError)
        {
            return admissionError;
        }

        if (user.MustChangePassword)
        {
            return IdentityErrors.TokenInvalid();
        }

        // A sign-in completed by a password change still owes the second factor when one is required.
        if (!method.StartsWith("mfa:", StringComparison.Ordinal) && await _mfa.IsRequiredAsync(user, ct).ConfigureAwait(false))
        {
            var status = await _mfa.GetStatusAsync(user.Id, ct).ConfigureAwait(false);
            if (status.HasMethod)
            {
                return new SignInOutcome.MfaRequired(_pending.Issue(PendingKind.Mfa, user.Id, client, now, MfaTokenLifetime), status.Methods);
            }

            return new SignInOutcome.MfaEnrolmentRequired(_pending.Issue(PendingKind.MfaEnrolment, user.Id, client, now, EnrolmentTokenLifetime));
        }

        var tokens = await IssueTokensAndCreateSessionAsync(user, client, ct).ConfigureAwait(false);
        await _lockoutService.ResetFailuresAsync(user.Id, ct).ConfigureAwait(false);

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var entity = await db.Users.FindAsync([user.Id], ct).ConfigureAwait(false);
            if (entity is not null)
            {
                entity.LastSignInAt = now;
                await SaveBestEffortAsync(db, ct).ConfigureAwait(false);
            }
        }

        await AuditSignedInAsync(user, client, tokens.Session, method, ct).ConfigureAwait(false);
        return new SignInOutcome.Signed(tokens, user with { LastSignInAt = now });
    }

    private async Task CountMfaFailureAsync(string token, PendingEntry entry, string method, CancellationToken ct)
    {
        var failures = _pending.RecordFailure(token);
        await _lockoutService.RecordFailureAsync(entry.UserId, ct).ConfigureAwait(false);
        if (_logger is not null)
        {
            IdentityLog.MfaVerificationFailed(_logger, entry.UserId.ToString(), method);
        }

        await _auditor.RecordAsync("identity.mfaVerifyFailed", IdentityAuditOutcome.Failure, null, entry.UserId, IdentityAuditSeverity.Normal, ct, null,
            ("method", method), ("clientKind", entry.Client.Kind.ToString()), ("remoteIp", entry.Client.RemoteIp?.ToString())).ConfigureAwait(false);

        if (failures >= MaxMfaFailures)
        {
            _pending.Remove(token);
            if (_logger is not null)
            {
                IdentityLog.MfaTokenInvalidated(_logger, entry.UserId.ToString());
            }
        }
    }

    private static bool SameClient(ClientContext issued, ClientContext presented) =>
        issued.Kind == presented.Kind && string.Equals(issued.ClientInstanceId, presented.ClientInstanceId, StringComparison.Ordinal);

    // Step 5 (spec IDN §6.1) plus the client kind rules of §12.
    private async Task<Error?> CheckStatusAsync(User user, ClientContext client, DateTimeOffset now, CancellationToken ct)
    {
        if (!user.Enabled)
        {
            return IdentityErrors.Disabled();
        }

        if ((user.ValidFrom.HasValue && now < user.ValidFrom.Value) ||
            (user.ValidUntil.HasValue && now > user.ValidUntil.Value))
        {
            return IdentityErrors.Expired();
        }

        // A browser can only be the Web client: it cannot claim desktop (or any other kind) through the web origin.
        if (client.BrowserOrigin is not null && client.Kind != ClientKind.Web)
        {
            return IdentityErrors.ClientNotAllowed();
        }

        if (user.AllowedClients.Count > 0 && !user.AllowedClients.Contains(client.Kind))
        {
            return IdentityErrors.ClientNotAllowed();
        }

        if (user.AllowedWorkstations.Count > 0 &&
            (string.IsNullOrEmpty(client.WorkstationId) || !user.AllowedWorkstations.Contains(client.WorkstationId)))
        {
            return IdentityErrors.WorkstationNotAllowed();
        }

        // Sites can require an enrolled (certificate-verified) workstation for configuration sessions (spec IDN §12).
        // Break-glass is exempt: it must work when everything else fails (§11).
        if (_binding.RequireEnrolledWorkstationForConfiguration && !IsWorkstationVerified(client) && user.Kind != UserKind.BreakGlass)
        {
            var permissions = await _permissions.GetEffectivePermissionsAsync(user, ct).ConfigureAwait(false);
            if (user.Administrator || PermissionSummary.IsConfigurationUser(permissions))
            {
                return IdentityErrors.WorkstationNotVerified();
            }
        }

        return null;
    }

    private static bool IsWorkstationVerified(ClientContext client) => client.Kind == ClientKind.Desktop && client.WorkstationVerified;

    // Step 6: admission hooks in order, then the user's concurrent session limit.
    private async Task<Error?> AdmitAsync(User user, ClientContext client, CancellationToken ct, bool includeSessionLimit = true)
    {
        foreach (var admission in _admissions)
        {
            var admissionResult = await admission.AdmitAsync(user, client, AdmissionPhase.SignIn, ct).ConfigureAwait(false);
            if (!admissionResult.IsSuccess)
            {
                return admissionResult.Error;
            }
        }

        if (includeSessionLimit && user.SessionPolicy.MaxConcurrentSessions > 0)
        {
            var activeUserSessions = _sessionRegistry.ListActive(new SessionQuery(UserId: user.Id));
            if (activeUserSessions.Count >= user.SessionPolicy.MaxConcurrentSessions)
            {
                return IdentityErrors.SessionLimitReached(user.SessionPolicy.MaxConcurrentSessions);
            }
        }

        return null;
    }

    private void LogSignInFailed(string? login, ClientContext client, string code)
    {
        if (_logger is not null)
        {
            IdentityLog.SignInFailed(_logger, login ?? string.Empty, client.RemoteIp?.ToString() ?? "", code);
        }
    }

    private async Task AuditSignedInAsync(User user, ClientContext client, SessionId session, string method, CancellationToken ct)
    {
        if (_logger is not null)
        {
            IdentityLog.UserSignedIn(_logger, user.Id.ToString(), client.Kind.ToString(), session.ToString());
        }

        await _auditor.RecordAsync("identity.signedIn", IdentityAuditOutcome.Success, user.Id, user.Id, IdentityAuditSeverity.Normal, ct, null,
            ("clientKind", client.Kind.ToString()), ("sessionId", session.ToString()), ("method", method),
            ("remoteIp", client.RemoteIp?.ToString()), ("workstationVerified", IsWorkstationVerified(client) ? "true" : "false")).ConfigureAwait(false);
    }

    private async Task AuditBreakGlassAsync(User user, ClientContext client, IdentityAuditOutcome outcome, string? reason, string? code, CancellationToken ct, SessionId? session = null)
    {
        var remoteIp = client.RemoteIp?.ToString() ?? "";
        if (_logger is not null)
        {
            IdentityLog.BreakGlassUsed(_logger, remoteIp, reason ?? code ?? "");
        }

        await _auditor.RecordAsync("identity.breakGlassUsed", outcome, user.Id, user.Id, IdentityAuditSeverity.Critical, ct, reason,
            ("clientKind", client.Kind.ToString()), ("remoteIp", remoteIp), ("sessionId", session?.ToString()), ("code", code)).ConfigureAwait(false);
        await _auditor.PublishAsync("identity.breakGlassUsed", IdentitySystemEventSeverity.Alarm, ct,
            ("userId", user.Id.ToString()), ("outcome", outcome.ToString()), ("remoteIp", remoteIp), ("reason", reason)).ConfigureAwait(false);
    }

    // LastSignInAt and the rehash are conveniences: a concurrent change of the same user row (two sign-ins at once,
    // an administrator edit) must not fail a sign-in that already succeeded.
    private static async Task SaveBestEffortAsync(IdentityDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error.Code.Value == "storage.concurrency.conflict")
        {
            foreach (var entry in db.ChangeTracker.Entries().ToList())
            {
                await entry.ReloadAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<TokenPair> IssueTokensAndCreateSessionAsync(User user, ClientContext client, CancellationToken ct)
    {
        var sessionId = SessionId.New();
        var now = _clock.GetUtcNow();
        var sessionExpiresAt = _refreshStore.CalculateSessionExpiresAt(now, client.Kind, user.SessionPolicy);
        if (user.Kind == UserKind.BreakGlass && sessionExpiresAt > now + BreakGlassSessionLength)
        {
            // Break-glass sessions are limited to 2 hours (spec IDN §11).
            sessionExpiresAt = now + BreakGlassSessionLength;
        }

        var session = new Session(
            sessionId,
            user.Id,
            client.Kind,
            client.ClientInstanceId,
            client.WorkstationId,
            client.RemoteIp?.ToString(),
            client.UserAgent,
            client.AppVersion,
            now,
            now,
            sessionExpiresAt,
            null,
            null,
            IsWorkstationVerified(client));

        await _sessionRegistry.RegisterNewSessionAsync(session, ct).ConfigureAwait(false);
        var (accessToken, accessExpiresAt) = _tokenService.IssueAccessToken(session, user);
        var (refreshToken, refreshExpiresAt) = await _refreshStore.CreateRefreshTokenAsync(session, ct).ConfigureAwait(false);

        return new TokenPair(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt, sessionId);
    }
}
