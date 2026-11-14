using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Identity.Internal.Tokens;

/// <summary>Manages refresh token generation, hashing (SHA-256), rotation, and reuse detection (spec IDN §6.3).</summary>
internal sealed class RefreshTokenStore : IDisposable
{
    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly ISessionRegistry _sessionRegistry;
    private readonly TimeProvider _clock;
    private readonly RefreshTokenOptions _options;
    private readonly ILogger<RefreshTokenStore>? _logger;

    // One rotation at a time per presented token in this process; the rotated_at concurrency token covers other nodes.
    private readonly SemaphoreSlim[] _rotationLocks = [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1))];

    public RefreshTokenStore(
        IDbContextFactory<IdentityDbContext> dbFactory,
        ISessionRegistry sessionRegistry,
        TimeProvider clock,
        RefreshTokenOptions? options = null,
        ILogger<RefreshTokenStore>? logger = null)
    {
        _logger = logger;
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new RefreshTokenOptions(
            IdleLifetime: TimeSpan.FromHours(12),
            WebMaxSessionLength: TimeSpan.FromHours(12),
            DesktopMaxSessionLength: TimeSpan.FromDays(7),
            MobileMaxSessionLength: TimeSpan.FromDays(30));
    }

    /// <summary>Creates and persists an initial refresh token for a newly created session.</summary>
    public async Task<(string RefreshToken, DateTimeOffset ExpiresAt)> CreateRefreshTokenAsync(Session session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = Base64UrlEncode(tokenBytes);
        var hash = ComputeHash(token);

        var now = _clock.GetUtcNow();
        var expiresAt = CalculateExpiration(now, session);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            TokenHash = hash,
            SessionId = session.Id,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (token, expiresAt);
    }

    /// <summary>Rotates an existing refresh token, issuing a new one and detecting reuse (spec IDN §6.3).</summary>
    public async Task<Result<(string NewToken, DateTimeOffset NewExpiresAt, Session Session)>> RotateRefreshTokenAsync(
        string presentedToken,
        ClientContext client,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return IdentityErrors.RefreshInvalid();
        }

        var presentedHash = ComputeHash(presentedToken);

        // Two refreshes with the same token must not both rotate it (that would fork the token family and hide a
        // theft): the second one sees rotated_at set and takes the reuse path (spec IDN §6.3).
        var gate = _rotationLocks[(int)((uint)string.GetHashCode(presentedHash, StringComparison.Ordinal) % (uint)_rotationLocks.Length)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RotateCoreAsync(presentedHash, client, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<(string NewToken, DateTimeOffset NewExpiresAt, Session Session)>> RotateCoreAsync(
        string presentedHash,
        ClientContext client,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tokenEntity = await db.RefreshTokens.FirstOrDefaultAsync(r => r.TokenHash == presentedHash, ct).ConfigureAwait(false);

        if (tokenEntity is null)
        {
            return IdentityErrors.RefreshInvalid();
        }

        // Reuse detection: already rotated token presented -> revoke the whole session
        if (tokenEntity.RotatedAt is not null)
        {
            return await ReuseDetectedAsync(tokenEntity.SessionId, ct).ConfigureAwait(false);
        }

        // Check token expiration
        if (now > tokenEntity.ExpiresAt)
        {
            return IdentityErrors.RefreshInvalid();
        }

        // Check session exists and is not revoked
        var sessionResult = _sessionRegistry.Get(tokenEntity.SessionId);
        if (!sessionResult.TryGetValue(out var session, out var sessionError))
        {
            return sessionError;
        }

        if (session.RevokedAt is not null || _sessionRegistry.IsRevoked(session.Id))
        {
            return IdentityErrors.TokenRevoked();
        }

        // Check binding: same ClientInstanceId and ClientKind
        if (!string.Equals(session.ClientInstanceId, client.ClientInstanceId, StringComparison.Ordinal) ||
            session.ClientKind != client.Kind)
        {
            return IdentityErrors.RefreshInvalid();
        }

        // Check absolute session expiration
        if (now > session.ExpiresAt)
        {
            return IdentityErrors.RefreshInvalid();
        }

        // Generate new token
        var newTokenBytes = RandomNumberGenerator.GetBytes(32);
        var newToken = Base64UrlEncode(newTokenBytes);
        var newHash = ComputeHash(newToken);
        var newExpiresAt = CalculateExpiration(now, session);

        // Mark old token rotated and link to replacement
        tokenEntity.RotatedAt = now;
        tokenEntity.ReplacedByHash = newHash;

        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            TokenHash = newHash,
            SessionId = session.Id,
            CreatedAt = now,
            ExpiresAt = newExpiresAt,
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (StorageException ex) when (ex.Error.Code.Value == "storage.concurrency.conflict")
        {
            // rotated_at is a concurrency token: another node rotated this token between the read and the write.
            return await ReuseDetectedAsync(tokenEntity.SessionId, ct).ConfigureAwait(false);
        }

        return (newToken, newExpiresAt, session);
    }

    private async Task<Error> ReuseDetectedAsync(SessionId sessionId, CancellationToken ct)
    {
        await _sessionRegistry.RevokeAsync(sessionId, "Refresh token reuse detected", null, ct).ConfigureAwait(false);
        if (_logger is not null)
        {
            IdentityLog.RefreshTokenReuse(_logger, sessionId.ToString());
        }

        return IdentityErrors.RefreshReused();
    }

    public DateTimeOffset CalculateSessionExpiresAt(DateTimeOffset createdAt, ClientKind clientKind, SessionPolicy? userPolicy)
    {
        var maxSession = userPolicy?.MaxSessionLength > TimeSpan.Zero
            ? userPolicy.MaxSessionLength
            : clientKind switch
            {
                ClientKind.Web => _options.WebMaxSessionLength,
                ClientKind.Desktop => _options.DesktopMaxSessionLength,
                ClientKind.Mobile => _options.MobileMaxSessionLength,
                _ => _options.DesktopMaxSessionLength,
            };

        return createdAt + maxSession;
    }

    // Sliding idle lifetime, never beyond the absolute end of the session (which includes the user's policy).
    private DateTimeOffset CalculateExpiration(DateTimeOffset now, Session session)
    {
        var sliding = now + _options.IdleLifetime;
        return sliding < session.ExpiresAt ? sliding : session.ExpiresAt;
    }

    private static string ComputeHash(string token)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        var base64 = Convert.ToBase64String(input);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public void Dispose()
    {
        foreach (var gate in _rotationLocks)
        {
            gate.Dispose();
        }
    }
}
