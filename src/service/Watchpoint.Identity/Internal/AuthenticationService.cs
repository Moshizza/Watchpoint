using System.Collections.Concurrent;
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

    // Temporary changeTokens for MustChangePassword flow: token -> (UserId, ClientContext, ExpiresAt)
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
        if (!_rateLimiter.TryAcquire(request.Client.RemoteIp, request.Login))
        {
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, request.Login, request.Client.RemoteIp?.ToString() ?? "", "common.rate_limited");
            }
            return IdentityErrors.RateLimited();
        }

        // 2. Find user; if not found / template / script -> dummy hash verify -> invalid_credentials
        var userResult = _userDirectory.FindByLogin(request.Login);
        if (!userResult.TryGetValue(out var user, out _))
        {
            await _hasher.VerifyDummyAsync(request.Password, ct).ConfigureAwait(false);
            if (_logger is not null)
            {
                IdentityLog.SignInFailed(_logger, request.Login, request.Client.RemoteIp?.ToString() ?? "", "identity.signin.invalid_credentials");
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
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
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
            var changeToken = Guid.NewGuid().ToString("N");
            _changeTokens[changeToken] = (user.Id, request.Client, now + _options.ChangeTokenLifetime);
            return new SignInOutcome.PasswordChangeRequired(changeToken);
        }

        // 9. Create session and tokens; reset failure count; update LastSignInAt; audit signedIn
        var tokenPair = await IssueTokensAndCreateSessionAsync(user, request.Client, ct).ConfigureAwait(false);
        await _lockoutService.ResetFailuresAsync(user.Id, ct).ConfigureAwait(false);

        userEntity.LastSignInAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

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

        var rotateResult = await _refreshStore.RotateRefreshTokenAsync(refreshToken, client, ct).ConfigureAwait(false);
        if (!rotateResult.TryGetValue(out var tuple, out var rotateError))
        {
            return rotateError;
        }

        var (newToken, newExpiresAt, session) = tuple;

        // Verify user is still active and not expired
        var userResult = _userDirectory.Get(session.UserId);
        if (!userResult.TryGetValue(out var user, out var userError))
        {
            return userError;
        }

        var now = _clock.GetUtcNow();
        if (!user.Enabled)
        {
            await _sessionRegistry.RevokeAsync(session.Id, "User disabled", null, ct).ConfigureAwait(false);
            return IdentityErrors.Disabled();
        }

        if ((user.ValidFrom.HasValue && now < user.ValidFrom.Value) ||
            (user.ValidUntil.HasValue && now > user.ValidUntil.Value))
        {
            await _sessionRegistry.RevokeAsync(session.Id, "User expired", null, ct).ConfigureAwait(false);
            return IdentityErrors.Expired();
        }

        // Admission hook check on refresh
        foreach (var admission in _admissions)
        {
            var admissionResult = await admission.AdmitAsync(user, client, AdmissionPhase.Refresh, ct).ConfigureAwait(false);
            if (!admissionResult.IsSuccess)
            {
                return admissionResult.Error;
            }
        }

        // Issue new access token
        var (accessToken, accessExpiresAt) = _tokenService.IssueAccessToken(session, user);
        _sessionRegistry.Touch(session.Id);

        return new TokenPair(accessToken, accessExpiresAt, newToken, newExpiresAt, session.Id);
    }

    public async Task<Result> SignOutAsync(SessionId session, CancellationToken ct)
    {
        return await _sessionRegistry.RevokeAsync(session, "Explicit sign-out", null, ct).ConfigureAwait(false);
    }

    public async Task<Result<TokenPair>> CompletePasswordChangeAsync(string changeToken, string newPassword, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!_changeTokens.TryRemove(changeToken, out var entry) || entry.ExpiresAt < now)
        {
            return IdentityErrors.TokenInvalid("Password change token is invalid or expired.");
        }

        var userResult = _userDirectory.Get(entry.UserId);
        if (!userResult.TryGetValue(out var user, out var userError))
        {
            return userError;
        }

        // Apply new password
        var changeResult = await _userDirectory.SetPasswordAsync(user.Id, newPassword, mustChange: false, new ChangeContext(user.Id, "changeToken"), ct).ConfigureAwait(false);
        if (!changeResult.IsSuccess)
        {
            return changeResult.Error;
        }

        var updatedUserResult = _userDirectory.Get(user.Id);
        var updatedUser = updatedUserResult.Value;

        // Complete sign-in
        var tokens = await IssueTokensAndCreateSessionAsync(updatedUser, entry.Client, ct).ConfigureAwait(false);
        return tokens;
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
