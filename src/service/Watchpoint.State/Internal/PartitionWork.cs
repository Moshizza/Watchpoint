using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// Posts work to the pipeline's real partition dispatcher (spec PIP §7.3). Resolves <see cref="IPartitionDispatcher"/>
/// lazily, on first use, rather than taking it as a constructor dependency: the dispatcher's own implementation
/// (<c>PartitionRouter</c>) takes every registered <see cref="IPartitionSubscriber"/> as a constructor dependency,
/// and <see cref="StateSubscriber"/> (one of them) depends, through <see cref="ServerAvailabilityCascade"/>, on
/// this class — a constructor-time dependency on <see cref="IPartitionDispatcher"/> here would complete the cycle
/// and deadlock the container while building the singleton.
/// </summary>
internal sealed class DispatcherPartitionWork(IServiceProvider services) : IPartitionWork
{
    private IPartitionDispatcher? _dispatcher;

    public Task RunAsync(int partition, Action work, CancellationToken ct) =>
        (_dispatcher ??= services.GetRequiredService<IPartitionDispatcher>()).PostAsync(
            partition,
            _ =>
            {
                work();
                return ValueTask.CompletedTask;
            },
            ct).AsTask();
}
