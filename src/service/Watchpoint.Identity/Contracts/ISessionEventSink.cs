using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Contracts;

/// <summary>Sink for publishing session lifecycle events to the system event pipeline (spec IDN §6.3, PIP WP_SYS).</summary>
public interface ISessionEventSink
{
    /// <summary>Publishes a <c>session.revoked</c> event record.</summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="reason">The revocation reason.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PublishSessionRevokedAsync(SessionId sessionId, UserId userId, string reason, CancellationToken ct);
}
