using System.Net.Sockets;
using Polly.CircuitBreaker;
using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk.Resilience;
using Watchpoint.Integration.Sdk.Scheduling;
using Watchpoint.Kernel.Testing;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task INT_U_033_ResiliencePresets_Command_no_retry_when_not_idempotent()
    {
        var clock = TestTime.CreateProvider();
        var nonIdempotentPipeline = ResiliencePresets.CreateCommandPipeline(isIdempotent: false, timeProvider: clock);

        var callsNonIdempotent = 0;
        await Should.ThrowAsync<SocketException>(async () =>
        {
            await nonIdempotentPipeline.ExecuteAsync(async ct =>
            {
                callsNonIdempotent++;
                await Task.Yield();
                throw new SocketException((int)SocketError.ConnectionRefused);
            }, TestContext.Current.CancellationToken);
        });

        // Exactly 1 call: no retry when not idempotent
        callsNonIdempotent.ShouldBe(1);

        // For idempotent command, it should retry twice (total 3 attempts)
        var idempotentPipeline = ResiliencePresets.CreateCommandPipeline(isIdempotent: true, timeProvider: clock);
        var callsIdempotent = 0;
        var retryTask = Task.Run(async () =>
        {
            await Should.ThrowAsync<SocketException>(async () =>
            {
                await idempotentPipeline.ExecuteAsync(async ct =>
                {
                    callsIdempotent++;
                    await Task.Yield();
                    throw new SocketException((int)SocketError.ConnectionRefused);
                }, TestContext.Current.CancellationToken);
            });
        }, TestContext.Current.CancellationToken);

        // Advance fake time for the 2 retries
        for (var i = 0; i < 50 && !retryTask.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        await retryTask;
        callsIdempotent.ShouldBe(3);
    }

    [Fact]
    public async Task INT_U_034_ResiliencePresets_Poll_circuit_opens_after_5_failures_half_open_after_30s()
    {
        var clock = TestTime.CreateProvider();
        var stateProvider = new CircuitBreakerStateProvider();
        var pollPipeline = ResiliencePresets.CreatePollPipeline(
            failureThreshold: 5,
            breakDuration: TimeSpan.FromSeconds(30),
            timeProvider: clock,
            stateProvider: stateProvider);

        // 5 consecutive failures
        for (var i = 0; i < 5; i++)
        {
            await Should.ThrowAsync<InvalidOperationException>(async () =>
            {
                await pollPipeline.ExecuteAsync(ct => throw new InvalidOperationException("Failure"), TestContext.Current.CancellationToken);
            });
        }

        // Circuit should now be open
        stateProvider.CircuitState.ShouldBe(CircuitState.Open);

        // Calling when open immediately throws BrokenCircuitException
        await Should.ThrowAsync<BrokenCircuitException>(async () =>
        {
            await pollPipeline.ExecuteAsync(ct => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        });

        // Advance past 30 s break duration
        clock.Advance(TimeSpan.FromSeconds(31));

        // Successful execution closes the circuit
        await pollPipeline.ExecuteAsync(ct => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        stateProvider.CircuitState.ShouldBe(CircuitState.Closed);
    }

    [Fact]
    public async Task INT_U_035_RequestGate_priority_ordering()
    {
        // Saturated gate (concurrency 1), queued Background x 10 then Critical -> Critical runs next
        using var gate = new RequestGate(maxConcurrency: 1);

        var firstRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 1. Sature the gate
        var runningTask = Task.Run(async () =>
        {
            await gate.RunAsync(CommandPriority.Normal, async ct =>
            {
                firstRequestStarted.TrySetResult();
                await firstRequestRelease.Task.WaitAsync(ct);
                return 0;
            }, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await firstRequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var executionOrder = new List<string>();

        // 2. Queue 10 background tasks
        var backgroundTasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            backgroundTasks.Add(Task.Run(async () =>
            {
                await gate.RunAsync(CommandPriority.Background, ct =>
                {
                    lock (executionOrder)
                    {
                        executionOrder.Add($"Background-{index}");
                    }
                    return ValueTask.FromResult(0);
                }, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken));
        }

        // Give them a moment to enqueue
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

        // 3. Queue 1 critical task
        var criticalTask = Task.Run(async () =>
        {
            await gate.RunAsync(CommandPriority.Critical, ct =>
            {
                lock (executionOrder)
                {
                    executionOrder.Add("Critical");
                }
                return ValueTask.FromResult(0);
            }, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);

        // 4. Release the first request slot
        firstRequestRelease.TrySetResult();
        await runningTask;

        // Wait for all to complete
        await criticalTask;
        await Task.WhenAll(backgroundTasks);

        // Critical must run first among queued
        executionOrder[0].ShouldBe("Critical");
        executionOrder.Count.ShouldBe(11);
    }

    [Fact]
    public async Task INT_U_036_RequestGate_concurrency_limit_respected()
    {
        const int maxConcurrency = 3;
        using var gate = new RequestGate(maxConcurrency: maxConcurrency);

        var activeCount = 0;
        var maxObserved = 0;
        var lockObj = new object();

        var tasks = new List<Task>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await gate.RunAsync(CommandPriority.Normal, async ct =>
                {
                    var count = Interlocked.Increment(ref activeCount);
                    lock (lockObj)
                    {
                        if (count > maxObserved)
                        {
                            maxObserved = count;
                        }
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, ct);
                    Interlocked.Decrement(ref activeCount);
                    return 0;
                }, TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken));
        }

        await Task.WhenAll(tasks);
        maxObserved.ShouldBeLessThanOrEqualTo(maxConcurrency);
        maxObserved.ShouldBe(maxConcurrency);
    }
}
