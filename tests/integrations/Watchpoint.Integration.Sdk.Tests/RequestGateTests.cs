using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk.Scheduling;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

[Trait("layer", "U")]
[Trait("spec", "INT")]
public sealed class RequestGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task INT_U_041_Gate_starts_waiting_requests_by_priority_and_first_come_first_served_within_one()
    {
        using var gate = new RequestGate(maxConcurrency: 1);
        var release = new TaskCompletionSource();
        var order = new List<string>();

        // One request occupies the only slot; the others queue up in a mixed order.
        var running = gate.RunAsync(CommandPriority.Normal, async _ => { await release.Task; return 0; }, Ct).AsTask();
        var queued = new List<Task>();
        foreach (var (name, priority) in new[]
        {
            ("background-1", CommandPriority.Background), ("normal-1", CommandPriority.Normal), ("high-1", CommandPriority.High), ("critical-1", CommandPriority.Critical),
            ("high-2", CommandPriority.High), ("normal-2", CommandPriority.Normal), ("critical-2", CommandPriority.Critical), ("background-2", CommandPriority.Background),
        })
        {
            queued.Add(gate.RunAsync(priority, _ =>
            {
                lock (order)
                {
                    order.Add(name);
                }

                return ValueTask.FromResult(0);
            }, Ct).AsTask());
        }

        order.ShouldBeEmpty("one at a time");
        release.SetResult();
        await Task.WhenAll([running, .. queued]).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Critical, then High, then Normal, then Background; the order of arrival decides within a priority.
        order.ShouldBe(["critical-1", "critical-2", "high-1", "high-2", "normal-1", "normal-2", "background-1", "background-2"]);

        // A waiting request that is cancelled gives up its place without blocking the others.
        var blocker = new TaskCompletionSource();
        var first = gate.RunAsync(CommandPriority.Normal, async _ => { await blocker.Task; return 0; }, Ct).AsTask();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelled = gate.RunAsync(CommandPriority.High, _ => ValueTask.FromResult(0), cts.Token).AsTask();
        var after = gate.RunAsync(CommandPriority.Background, _ => ValueTask.FromResult(1), Ct).AsTask();
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        blocker.SetResult();
        (await after.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe(1);
        await first;
    }
}
