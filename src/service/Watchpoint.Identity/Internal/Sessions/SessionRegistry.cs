using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal.Sessions;

/// <summary>In-memory and DbContext-backed session registry (spec IDN §6.3).</summary>
internal sealed class SessionRegistry : ISessionRegistry
{
    private readonly IDbContextFactory<IdentityDbContext> _dbFactory;
    private readonly ISessionEventSink? _eventSink;
    private readonly TimeProvider _clock;
    private readonly ILogger<SessionRegistry>? _logger;

    // Fast in-memory lookup of active sessions
    private readonly ConcurrentDictionary<SessionId, Session> _activeSessions = new();

    // Fast in-memory set of revoked session IDs (O(1) checks per request, spec IDN §6.2)
    private readonly ConcurrentDictionary<SessionId, DateTimeOffset> _revokedSessions = new();

    // Throttling for Touch: last persisted instant per session
    private readonly ConcurrentDictionary<SessionId, DateTimeOffset> _lastPersistedTouch = new();

    public SessionRegistry(
        IDbContextFactory<IdentityDbContext> dbFactory,
        TimeProvider clock,
        ISessionEventSink? eventSink = null,
        ILogger<SessionRegistry>? logger = null)
    {
        _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _eventSink = eventSink;
        _logger = logger;
    }

    /// <summary>Registers an active session newly created by authentication.</summary>
    public async Task RegisterNewSessionAsync(Session session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        _activeSessions[session.Id] = session;

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Sessions.Add(new SessionEntity
        {
            Id = session.Id,
            UserId = session.UserId,
            ClientKind = session.ClientKind.ToString(),
            ClientInstanceId = session.ClientInstanceId,
            WorkstationId = session.WorkstationId,
            RemoteIp = session.RemoteIp,
            UserAgent = session.UserAgent,
            AppVersion = session.AppVersion,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.LastActivityAt,
            ExpiresAt = session.ExpiresAt,
            RevokedAt = session.RevokedAt,
            RevokedReason = session.RevokedReason,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<Session> ListActive(SessionQuery query)
    {
        var now = _clock.GetUtcNow();
        var result = new List<Session>();

        foreach (var session in _activeSessions.Values)
        {
            if (session.RevokedAt is not null || IsRevoked(session.Id) || session.ExpiresAt <= now)
            {
                continue;
            }

            if (query.UserId is { } uid && session.UserId != uid)
            {
                continue;
            }

            if (query.ClientKind is { } ck && session.ClientKind != ck)
            {
                continue;
            }

            result.Add(session);
        }

        return result;
    }

    public Result<Session> Get(SessionId id)
    {
        if (_activeSessions.TryGetValue(id, out var session))
        {
            return session;
        }

        // Try load from DB synchronously or fallback
        using var db = _dbFactory.CreateDbContext();
        var entity = db.Sessions.Find(id);
        if (entity is null)
        {
            return Error.NotFound("identity.session.not_found", ("sessionId", id.ToString()));
        }

        if (!Enum.TryParse<ClientKind>(entity.ClientKind, out var clientKind))
        {
            clientKind = ClientKind.Desktop;
        }

        var loaded = new Session(
            entity.Id,
            entity.UserId,
            clientKind,
            entity.ClientInstanceId,
            entity.WorkstationId,
            entity.RemoteIp,
            entity.UserAgent,
            entity.AppVersion,
            entity.CreatedAt,
            entity.LastActivityAt,
            entity.ExpiresAt,
            entity.RevokedAt,
            entity.RevokedReason);

        _activeSessions[id] = loaded;
        if (loaded.RevokedAt is not null)
        {
            _revokedSessions[id] = loaded.RevokedAt.Value;
        }

        return loaded;
    }

    public async Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        _revokedSessions[id] = now;

        Session? session = null;
        if (_activeSessions.TryGetValue(id, out var active))
        {
            session = active with { RevokedAt = now, RevokedReason = reason };
            _activeSessions[id] = session;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Sessions.FindAsync([id], ct).ConfigureAwait(false);
        if (entity is not null)
        {
            entity.RevokedAt = now;
            entity.RevokedReason = reason;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            if (session is null)
            {
                if (!Enum.TryParse<ClientKind>(entity.ClientKind, out var clientKind))
                {
                    clientKind = ClientKind.Desktop;
                }
                session = new Session(
                    entity.Id,
                    entity.UserId,
                    clientKind,
                    entity.ClientInstanceId,
                    entity.WorkstationId,
                    entity.RemoteIp,
                    entity.UserAgent,
                    entity.AppVersion,
                    entity.CreatedAt,
                    entity.LastActivityAt,
                    entity.ExpiresAt,
                    now,
                    reason);
            }
        }

        if (session is not null)
        {
            if (_logger is not null)
            {
                IdentityLog.SessionRevoked(_logger, id.ToString(), reason);
            }

            if (_eventSink is not null)
            {
                await _eventSink.PublishSessionRevokedAsync(id, session.UserId, reason, ct).ConfigureAwait(false);
            }
        }

        return Result.Success();
    }

    public async Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct)
    {
        var sessionsToRevoke = _activeSessions.Values
            .Where(s => s.UserId == user && s.RevokedAt is null && !IsRevoked(s.Id))
            .Select(s => s.Id)
            .ToList();

        // Also check DB for sessions
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var dbSessions = await db.Sessions
            .Where(s => s.UserId == user && s.RevokedAt == null)
            .Select(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var sid in dbSessions)
        {
            if (!sessionsToRevoke.Contains(sid))
            {
                sessionsToRevoke.Add(sid);
            }
        }

        var count = 0;
        foreach (var sid in sessionsToRevoke)
        {
            await RevokeAsync(sid, reason, actor, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public async Task<int> RevokeWhereAsync(Func<Session, bool> predicate, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var sessionsToRevoke = _activeSessions.Values
            .Where(s => s.RevokedAt is null && !IsRevoked(s.Id) && predicate(s))
            .Select(s => s.Id)
            .ToList();

        var count = 0;
        foreach (var sid in sessionsToRevoke)
        {
            await RevokeAsync(sid, reason, null, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public bool IsRevoked(SessionId id)
    {
        return _revokedSessions.ContainsKey(id);
    }

    public void Touch(SessionId id)
    {
        var now = _clock.GetUtcNow();

        if (_activeSessions.TryGetValue(id, out var session))
        {
            _activeSessions[id] = session with { LastActivityAt = now };
        }

        if (_lastPersistedTouch.TryGetValue(id, out var last) && (now - last) < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _lastPersistedTouch[id] = now;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
                var entity = await db.Sessions.FindAsync([id]).ConfigureAwait(false);
                if (entity is not null)
                {
                    entity.LastActivityAt = now;
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Throttled background persist failure is non-fatal
            }
        });
    }
}
