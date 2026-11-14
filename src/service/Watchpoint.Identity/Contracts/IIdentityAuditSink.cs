using Watchpoint.Kernel.Ids;

namespace Watchpoint.Identity.Contracts;

/// <summary>Outcome of an audited action.</summary>
public enum IdentityAuditOutcome
{
    /// <summary>The action succeeded.</summary>
    Success,

    /// <summary>The action failed (wrong credentials, invalid factor…).</summary>
    Failure,

    /// <summary>The action was refused by a rule (permission, policy).</summary>
    Denied,
}

/// <summary>Severity of an audited action (AUD §4).</summary>
public enum IdentityAuditSeverity
{
    /// <summary>Routine.</summary>
    Normal,

    /// <summary>Sensitive (MFA reset, unlock, session revoked by an administrator).</summary>
    Sensitive,

    /// <summary>Critical (break-glass use and account changes).</summary>
    Critical,
}

/// <summary>
/// An audit record of the identity module (spec IDN §14, AUD §4). Never carries a password, a token, a secret or a code.
/// </summary>
/// <param name="Action">The action, <c>identity.&lt;verb&gt;</c>: <c>signedIn</c>, <c>signInFailed</c>, <c>accountLocked</c>, <c>accountUnlocked</c>, <c>mfaEnrolled</c>, <c>mfaVerifyFailed</c>, <c>mfaReset</c>, <c>sessionRevoked</c>, <c>breakGlassUsed</c>, <c>breakGlassAccountChanged</c>…</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Actor">The user who acted; <see langword="null"/> for the system or an anonymous caller.</param>
/// <param name="Subject">The user the action is about, if any.</param>
/// <param name="Reason">A user-entered reason when one is required (break-glass).</param>
/// <param name="Severity">The severity.</param>
/// <param name="Details">Action-specific details (client kind, method, session id, remote IP…); values are plain text, never secrets.</param>
/// <param name="At">When it happened.</param>
public sealed record IdentityAuditRecord(
    string Action,
    IdentityAuditOutcome Outcome,
    UserId? Actor,
    UserId? Subject,
    string? Reason,
    IdentityAuditSeverity Severity,
    IReadOnlyDictionary<string, string?> Details,
    DateTimeOffset At);

/// <summary>
/// Receives the audit records of the identity module. The audit module (AUD, T3.06) replaces the default implementation,
/// which writes the records as structured log events (2790–2792); callers do not change.
/// </summary>
public interface IIdentityAuditSink
{
    /// <summary>Records an action. Never throws for business reasons.</summary>
    /// <param name="record">The record.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask RecordAsync(IdentityAuditRecord record, CancellationToken ct);
}
