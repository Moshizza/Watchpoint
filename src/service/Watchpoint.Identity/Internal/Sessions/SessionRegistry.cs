using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Identity.Contracts;
using Watchpoint.Identity.Internal.Storage;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Internal.Sessions;

/// <summary>In-memory and DbContext-backed session registry (spec IDN §6.3).</summary>
internal sealed class SessionRegistry : ISessionRegistry, IDisposable
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

    // Serialises the database part of revocations, so that one session is revoked (and published) once.
    private readonly SemaphoreSlim _revokeGate = new(1, 1);
    private readonly Lock _loadGate = new();
    private volatile bool _loaded;
    private int _registrations;

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

        EnsureLoaded();
        _activeSessions[session.Id] = session;
        if (Interlocked.Increment(ref _registrations) % 256 == 0)
        {
            Prune();
        }

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

    /// <summary>
    /// Loads the sessions that have not ended from the database, once: after a restart the revocation set and the
    /// active list would otherwise be empty, so a revoked session's access token would be accepted again and
    /// concurrent-session limits and <see cref="RevokeWhereAsync"/> would miss sessions (spec IDN §6.2, §6.3).
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_loadGate)
        {
            if (_loaded)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            using var db = _dbFactory.CreateDbContext();

            // Filtered in memory: the SQLite provider of the unit tests cannot compare DateTimeOffset.
            foreach (var entity in db.Sessions.AsNoTracking().AsEnumerable().Where(s => s.ExpiresAt > now))
            {
                var session = Map(entity);
                _activeSessions.TryAdd(session.Id, session);
                if (session.RevokedAt is { } revokedAt)
                {
                    _revokedSessions.TryAdd(session.Id, revokedAt);
                }
            }

            _loaded = true;
        }
    }

    // Forgets sessions that ended more than a day ago, so the in-memory sets do not grow for ever.
    private void Prune()
    {
        var limit = _clock.GetUtcNow() - TimeSpan.FromDays(1);
        foreach (var session in _activeSessions.Values)
        {
            if (session.ExpiresAt < limit)
            {
                _activeSessions.TryRemove(session.Id, out _);
                _revokedSessions.TryRemove(session.Id, out _);
                _lastPersistedTouch.TryRemove(session.Id, out _);
            }
        }
    }

    private static Session Map(SessionEntity entity)
    {
        if (!Enum.TryParse<ClientKind>(entity.ClientKind, out var clientKind))
        {
            clientKind = ClientKind.Desktop;
        }

        return new Session(
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
    }

    public IReadOnlyList<Session> ListActive(SessionQuery query)
    {
        EnsureLoaded();
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

        // Never replace a newer in-memory value (e.g. one revoked meanwhile) by the row just read.
        var loaded = _activeSessions.GetOrAdd(id, Map(entity));
        if (loaded.RevokedAt is not null)
        {
            _revokedSessions.TryAdd(id, loaded.RevokedAt.Value);
        }

        return loaded;
    }

    public async Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct)
    {
        EnsureLoaded();
        var now = _clock.GetUtcNow();

        // In memory first: the session is refused from this instant, whatever happens to the database write.
        _revokedSessions.TryAdd(id, now);
        while (_activeSessions.TryGetValue(id, out var current) && current.RevokedAt is null &&
               !_activeSessions.TryUpdate(id, current with { RevokedAt = now, RevokedReason = reason }, current))
        {
        }

        Session? revoked = null;
        await _revokeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var entity = await db.Sessions.FindAsync([id], ct).ConfigureAwait(false);

            // Revoking twice keeps the first instant and reason and is published once.
            if (entity is not null && entity.RevokedAt is null)
            {
                entity.RevokedAt = now;
                entity.RevokedReason = reason;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                revoked = Map(entity);
            }
        }
        finally
        {
            _revokeGate.Release();
        }

        if (revoked is not null)
        {
            if (_logger is not null)
            {
                IdentityLog.SessionRevoked(_logger, id.ToString(), reason);
            }

            if (_eventSink is not null)
            {
                await _eventSink.PublishSessionRevokedAsync(id, revoked.UserId, reason, ct).ConfigureAwait(false);
            }
        }

        return Result.Success();
    }

    public async Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct)
    {
        EnsureLoaded();
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

        EnsureLoaded();
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
        EnsureLoaded();
        return _revokedSessions.ContainsKey(id);
    }

    public void Touch(SessionId id)
    {
        var now = _clock.GetUtcNow();

        // Compare-and-swap: a plain write could put back a copy read before a concurrent revocation.
        while (_activeSessions.TryGetValue(id, out var session) &&
               !_activeSessions.TryUpdate(id, session with { LastActivityAt = now }, session))
        {
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

    public void Dispose()
    {
        _revokeGate.Dispose();
    }
}
