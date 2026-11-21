namespace Watchpoint.Kernel.Time;

/// <summary>
/// A gate for work that must run on one node of a cluster at a time (spec K §6.1), such as the registry outbox
/// publisher (PIP §5.5). The work awaits <see cref="WaitUntilHeldAsync"/> before each run and stops when the returned
/// token is cancelled. The cluster module implements it with its lease manager; hosts without it register
/// <see cref="AlwaysHeldLeaseGate"/>.
/// </summary>
public interface ILeaseGate
{
    /// <summary>Waits until this node holds <paramref name="lease"/>.</summary>
    /// <param name="lease">The lease name, e.g. <c>registry-outbox</c>.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A token that is cancelled when this node loses the lease.</returns>
    ValueTask<CancellationToken> WaitUntilHeldAsync(string lease, CancellationToken ct);
}
