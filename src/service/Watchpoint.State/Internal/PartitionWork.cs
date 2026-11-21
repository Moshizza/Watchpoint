using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Runs work serialised with a partition's own worker thread (spec STA §6.1, §6.3): registry-driven mutations
/// (new/deleted/changed entities, §6.3) and the server-unavailable cascade (§6.1) must never run concurrently with
/// the ingest records of the same partition, so they are posted the same way a subscriber's own records are
/// applied. The abstraction keeps <see cref="MembershipTracker"/> and <see cref="ServerAvailabilityCascade"/>
/// testable without a running pipeline: tests supply a synchronous implementation.
/// </summary>
internal interface IPartitionWork
{
    /// <summary>Runs <paramref name="work"/> serialised with the partition's worker.</summary>
    /// <param name="partition">The partition, 0 to 63.</param>
    /// <param name="work">The work; must not do I/O.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the work ran (callers typically do not await it: the caller is a synchronous registry-change or record-processing handler).</returns>
    Task RunAsync(int partition, Action work, CancellationToken ct);
}

/// <summary>Posts work to the pipeline's real partition dispatcher (spec PIP §7.3).</summary>
internal sealed class DispatcherPartitionWork(IPartitionDispatcher dispatcher) : IPartitionWork
{
    public Task RunAsync(int partition, Action work, CancellationToken ct) =>
        dispatcher.PostAsync(
            partition,
            _ =>
            {
                work();
                return ValueTask.CompletedTask;
            },
            ct).AsTask();
}
