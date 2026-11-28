namespace Watchpoint.Audit.Contracts;

/// <summary>Verifies the hash chain and its checkpoints (spec AUD §6.1).</summary>
public interface IAuditVerifier
{
    /// <summary>Verifies a segment of a chain.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="fromSequence">First sequence to verify.</param>
    /// <param name="toSequence">Last sequence, or <see langword="null"/> for the end of the chain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report.</returns>
    Task<VerificationReport> VerifyAsync(string chainId, long fromSequence, long? toSequence, CancellationToken ct);

    /// <summary>Gets the chains that exist.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chain ids.</returns>
    Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct);

    /// <summary>Gets the latest stored reports, one per chain.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The reports.</returns>
    Task<IReadOnlyList<VerificationReport>> LatestReportsAsync(CancellationToken ct);
}
