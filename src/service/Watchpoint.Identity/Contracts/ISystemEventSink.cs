namespace Watchpoint.Identity.Contracts;

/// <summary>Severity of a system event (EVT §6).</summary>
public enum IdentitySystemEventSeverity
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Alarm-able by default.</summary>
    Alarm,
}

/// <summary>A system event of the identity module published to <c>WP_SYS</c> (EVT §6): <c>identity.accountLocked</c>, <c>identity.breakGlassUsed</c>…</summary>
/// <param name="Kind">The kind, <c>identity.&lt;name&gt;</c>.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Payload">The payload (user id, until, client kind…); never secrets.</param>
/// <param name="At">When it happened.</param>
public sealed record IdentitySystemEvent(string Kind, IdentitySystemEventSeverity Severity, IReadOnlyDictionary<string, string?> Payload, DateTimeOffset At);

/// <summary>Publishes the system events of the identity module; implemented by the events module (EVT) through the pipeline.</summary>
public interface ISystemEventSink
{
    /// <summary>Publishes an event. Never throws for business reasons.</summary>
    /// <param name="systemEvent">The event.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask PublishAsync(IdentitySystemEvent systemEvent, CancellationToken ct);
}
