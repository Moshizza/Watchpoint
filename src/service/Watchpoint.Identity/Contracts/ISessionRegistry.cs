using System.Diagnostics.CodeAnalysis;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Identity.Contracts;

/// <summary>Active session record (spec IDN §6.3).</summary>
public sealed record Session(
    SessionId Id,
    UserId UserId,
    ClientKind ClientKind,
    string ClientInstanceId,
    string? WorkstationId,
    string? RemoteIp,
    string? UserAgent,
    string? AppVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevokedReason);

/// <summary>Query filters for active sessions.</summary>
/// <param name="UserId">Optional user id filter.</param>
/// <param name="ClientKind">Optional client kind filter.</param>
public sealed record SessionQuery(UserId? UserId = null, ClientKind? ClientKind = null);

/// <summary>Registry and manager for active client sessions (spec IDN §6.3).</summary>
public interface ISessionRegistry
{
    /// <summary>Lists active sessions matching the query.</summary>
    /// <param name="query">Filter query.</param>
    /// <returns>Active sessions.</returns>
    IReadOnlyList<Session> ListActive(SessionQuery query);

    /// <summary>Gets a session by id.</summary>
    /// <param name="id">The session id.</param>
    /// <returns>The session if found.</returns>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Method name fixed by spec IDN §6.3.")]
    Result<Session> Get(SessionId id);

    /// <summary>Revokes a specific session.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="reason">The revocation reason.</param>
    /// <param name="actor">The actor performing revocation, or null for system.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or error.</returns>
    Task<Result> RevokeAsync(SessionId id, string reason, UserId? actor, CancellationToken ct);

    /// <summary>Revokes all sessions belonging to a user.</summary>
    /// <param name="user">The user identifier.</param>
    /// <param name="reason">The revocation reason.</param>
    /// <param name="actor">The actor performing revocation, or null for system.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of revoked sessions.</returns>
    Task<int> RevokeAllAsync(UserId user, string reason, UserId? actor, CancellationToken ct);

    /// <summary>Revokes sessions matching an arbitrary predicate.</summary>
    /// <param name="predicate">Filter predicate.</param>
    /// <param name="reason">Revocation reason.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The count of revoked sessions.</returns>
    Task<int> RevokeWhereAsync(Func<Session, bool> predicate, string reason, CancellationToken ct);

    /// <summary>Checks whether a session has been revoked.</summary>
    /// <param name="id">The session id.</param>
    /// <returns>True if revoked.</returns>
    bool IsRevoked(SessionId id);

    /// <summary>Touches session activity; throttled to persist at most every 60s.</summary>
    /// <param name="id">The session id.</param>
    void Touch(SessionId id);
}
