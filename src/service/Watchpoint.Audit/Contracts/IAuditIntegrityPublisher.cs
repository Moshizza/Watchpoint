namespace Watchpoint.Audit.Contracts;

/// <summary>Publishes the integrity state: the <c>audit.integrityFailed</c> system event and the <c>auditIntegrity</c> live message (spec AUD §6.1, §9).</summary>
public interface IAuditIntegrityPublisher
{
    /// <summary>Publishes the result of a verification.</summary>
    /// <param name="ok"><see langword="true"/> when the chain is intact.</param>
    /// <param name="lastCheckedAt">When the check ran.</param>
    /// <param name="issue">The first issue when not intact: chain, sequence and kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    ValueTask PublishAsync(bool ok, DateTimeOffset lastCheckedAt, (string ChainId, long Sequence, VerificationIssueKind Kind)? issue, CancellationToken ct);
}
