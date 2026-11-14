using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Lockout;
using Watchpoint.Identity.Internal.Passwords;
using Watchpoint.Identity.Internal.Sessions;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Identity.Internal.Tokens;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal;

/// <summary>Full local sign-in and token authentication pipeline (spec IDN §6.1).</summary>
internal sealed class AuthenticationService : IAuthenticationService
{
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
    private readonly AuthenticationOptions _options;
    private readonly ILogger<AuthenticationService>? _logger;

    // Change tokens of the MustChangePassword flow, keyed by SHA-256(token): (UserId, ClientContext, ExpiresAt).
    // One live token per user; single use; dropped when the password changes.
    private readonly ConcurrentDictionary<string, (UserId UserId, ClientContext Client, DateTimeOffset ExpiresAt)> _changeTokens = new();

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
        AuthenticationOptions? options = null,
        ILogger<AuthenticationService>? logger = null)
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
        _options = options ?? new AuthenticationOptions();
        _logger = logger;
    }

    public async Task<Result<SignInOutcome>> SignInLocalAsync(SignInRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Rate limit per remote IP and per login
        var login = request.Login ?? string.Empty;
        if (!_rateLimiter.TryAcquire(request.Client.RemoteIp, login))
        {
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, login, request.Client.RemoteIp?.ToString() ?? "", "common.rate_limited");
            }
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
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
            }
            return IdentityErrors.InvalidCredentials();
        }

        // 2. Find user; if not found / template / script -> dummy hash verify -> invalid_credentials
        var userResult = _userDirectory.FindByLogin(login);
        if (!userResult.TryGetValue(out var user, out _))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
            }
            return IdentityErrors.InvalidCredentials();
        }

        if (user.Kind is UserKind.Template or UserKind.Script || user.AuthSource == AuthSource.None)
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, request.Login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
            }
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
                if (_logger is not null)
                {
                    IdentityLog.SignInFailed(_logger, request.Login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
                }
                return IdentityErrors.InvalidCredentials();
            }

            return IdentityErrors.Locked(lockedUntil ?? _clock.GetUtcNow().AddMinutes(15));
        }

        // 4. Verify password; on failure increment failure count, audit, return invalid_credentials
        var (verified, needsRehash) = await _hasher.VerifyPasswordAsync(request.Password, userEntity.PasswordHash, ct).ConfigureAwait(false);
        if (!verified)
        {
            var (newlyLocked, until) = await _lockoutService.RecordFailureAsync(user.Id, ct).ConfigureAwait(false);
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, request.Login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
            }

            return IdentityErrors.InvalidCredentials();
        }

        // Rehash on parameter upgrade (IDN §5, IDN-U-009)
        if (needsRehash)
        {
            var newHash = await _hasher.HashPasswordAsync(request.Password, ct).ConfigureAwait(false);
            userEntity.PasswordHash = newHash;
            await SaveBestEffortAsync(db, ct).ConfigureAwait(false);
        }

        // 5. Status checks: disabled -> disabled; expired -> expired; client kind not allowed -> client_not_allowed; workstation -> workstation_not_allowed
        if (!user.Enabled)
        {
            return IdentityErrors.Disabled();
        }

        var now = _clock.GetUtcNow();
        if ((user.ValidFrom.HasValue && now < user.ValidFrom.Value) ||
            (user.ValidUntil.HasValue && now > user.ValidUntil.Value))
        {
            return IdentityErrors.Expired();
        }

        if (user.AllowedClients.Count > 0 && !user.AllowedClients.Contains(request.Client.Kind))
        {
            return IdentityErrors.ClientNotAllowed();
        }

        if (user.AllowedWorkstations.Count > 0 &&
            (string.IsNullOrEmpty(request.Client.WorkstationId) || !user.AllowedWorkstations.Contains(request.Client.WorkstationId)))
        {
            return IdentityErrors.WorkstationNotAllowed();
        }

        // 6. ISessionAdmission hooks (all must allow)
        foreach (var admission in _admissions)
        {
            var admissionResult = await admission.AdmitAsync(user, request.Client, AdmissionPhase.SignIn, ct).ConfigureAwait(false);
            if (!admissionResult.IsSuccess)
            {
                return admissionResult.Error;
            }
        }

        // Check concurrent session limits (U1 / §6.1 / §6.3)
        if (user.SessionPolicy.MaxConcurrentSessions > 0)
        {
            var activeUserSessions = _sessionRegistry.ListActive(new SessionQuery(UserId: user.Id));
            if (activeUserSessions.Count >= user.SessionPolicy.MaxConcurrentSessions)
            {
                return Error.Create(ErrorKind.TooManyRequests, "identity.session.limit_reached");
            }
        }

        // 7. If MustChangePassword -> PasswordChangeRequired
        if (user.MustChangePassword)
        {
            return new SignInOutcome.PasswordChangeRequired(IssueChangeToken(user.Id, request.Client, now));
        }

        // 9. Create session and tokens; reset failure count; update LastSignInAt; audit signedIn
        var tokenPair = await IssueTokensAndCreateSessionAsync(user, request.Client, ct).ConfigureAwait(false);
        await _lockoutService.ResetFailuresAsync(user.Id, ct).ConfigureAwait(false);

        userEntity.LastSignInAt = now;
        await SaveBestEffortAsync(db, ct).ConfigureAwait(false);

        var updatedUser = user with { LastSignInAt = now };

        if (_logger is not null)
        {
            IdentityLog.UserSignedIn(_logger, user.Id.ToString(), request.Client.Kind.ToString(), tokenPair.Session.ToString());
        }

        return new SignInOutcome.Signed(tokenPair, updatedUser);
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
        return await _sessionRegistry.RevokeAsync(session, "Explicit sign-out", null, ct).ConfigureAwait(false);
    }

    public async Task<Result<TokenPair>> CompletePasswordChangeAsync(string changeToken, string newPassword, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (string.IsNullOrEmpty(changeToken) || changeToken.Length > 128)
        {
            return IdentityErrors.TokenInvalid();
        }

        // Single use: the token is taken out before anything else.
        var key = HashChangeToken(changeToken);
        if (!_changeTokens.TryRemove(key, out var entry) || entry.ExpiresAt < now)
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

        if (CheckStatus(user, entry.Client, now) is { } statusError)
        {
            return statusError;
        }

        foreach (var admission in _admissions)
        {
            var admissionResult = await admission.AdmitAsync(user, entry.Client, AdmissionPhase.SignIn, ct).ConfigureAwait(false);
            if (!admissionResult.IsSuccess)
            {
                return admissionResult.Error;
            }
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
                _changeTokens.TryAdd(key, entry);
            }

            return changeResult.Error;
        }

        // The password changed: every other change token of the user dies with the old password.
        RemoveChangeTokens(user.Id, now);

        if (!_userDirectory.Get(user.Id).TryGetValue(out var updatedUser, out var updatedError))
        {
            return updatedError;
        }

        // Complete sign-in (step 9)
        var tokens = await IssueTokensAndCreateSessionAsync(updatedUser, entry.Client, ct).ConfigureAwait(false);
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

        if (_logger is not null)
        {
            IdentityLog.UserSignedIn(_logger, user.Id.ToString(), entry.Client.Kind.ToString(), tokens.Session.ToString());
        }

        return tokens;
    }

    private string IssueChangeToken(UserId userId, ClientContext client, DateTimeOffset now)
    {
        RemoveChangeTokens(userId, now);
        var token = Base64UrlEncoder(RandomNumberGenerator.GetBytes(32));
        _changeTokens[HashChangeToken(token)] = (userId, client, now + _options.ChangeTokenLifetime);
        return token;
    }

    // Removes the tokens of a user and, in passing, every expired token (the map stays small).
    private void RemoveChangeTokens(UserId userId, DateTimeOffset now)
    {
        foreach (var pair in _changeTokens)
        {
            if (pair.Value.UserId == userId || pair.Value.ExpiresAt < now)
            {
                _changeTokens.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string HashChangeToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncoder(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Error? CheckStatus(User user, ClientContext client, DateTimeOffset now)
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

        if (user.AllowedClients.Count > 0 && !user.AllowedClients.Contains(client.Kind))
        {
            return IdentityErrors.ClientNotAllowed();
        }

        if (user.AllowedWorkstations.Count > 0 &&
            (string.IsNullOrEmpty(client.WorkstationId) || !user.AllowedWorkstations.Contains(client.WorkstationId)))
        {
            return IdentityErrors.WorkstationNotAllowed();
        }

        return null;
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
            null);

        await _sessionRegistry.RegisterNewSessionAsync(session, ct).ConfigureAwait(false);
        var (accessToken, accessExpiresAt) = _tokenService.IssueAccessToken(session, user);
        var (refreshToken, refreshExpiresAt) = await _refreshStore.CreateRefreshTokenAsync(session, ct).ConfigureAwait(false);

        return new TokenPair(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt, sessionId);
    }
}
