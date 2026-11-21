namespace Watchpoint.Kernel.Time;

/// <summary>
/// The <see cref="ILeaseGate"/> of a single node (M1, hosts without the cluster module): every lease is held at once
/// and never lost (spec K §6.1).
/// </summary>
public sealed class AlwaysHeldLeaseGate : ILeaseGate
{
    /// <summary>Completes synchronously with a token that is never cancelled.</summary>
    /// <param name="lease">The lease name (not empty).</param>
    /// <param name="ct">Cancels the wait; an already cancelled token cancels the call.</param>
    /// <returns><see cref="CancellationToken.None"/>.</returns>
    public ValueTask<CancellationToken> WaitUntilHeldAsync(string lease, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lease);
        return ct.IsCancellationRequested
            ? ValueTask.FromCanceled<CancellationToken>(ct)
            : ValueTask.FromResult(CancellationToken.None);
    }
}
