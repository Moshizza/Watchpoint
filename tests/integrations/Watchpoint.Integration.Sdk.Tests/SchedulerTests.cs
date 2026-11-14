using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk.Paging;
using Watchpoint.Integration.Sdk.Polling;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class SchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Waits until the code under test has armed its <paramref name="count"/>-th timer on <paramref name="clock"/>.
    /// Advancing the fake clock before that loses the advance (the timer is armed later, at the advanced time).
    /// </summary>
    private static Task TimersArmedAsync(TestClock clock, long count) =>
        clock.WaitForTimersAsync(count, Ct).WaitAsync(TestTimeouts.Default, TimeProvider.System, Ct);
    private sealed class TrackingObservationSink : IObservationSink
    {
        public int FlushCount { get; private set; }

        public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null) { }
        public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null) { }
        public void ReportInventoryChanged(InventorySnapshot partialOrComplete) { }
        public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null) { }
        public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null) { }
        public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null) { }

        public ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct)
        {
            FlushCount++;
            return ValueTask.FromResult(new SinkFlushResult(0, 0, 0));
        }
    }

    /// <summary>A sink whose flushes stay pending until the test completes them, one at a time.</summary>
    private sealed class GatedObservationSink : IObservationSink
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource> _started = [];
        private TaskCompletionSource<SinkFlushResult>? _pending;
        private int _flushCount;
        private int _flushCalls;

        public int FlushCount => Volatile.Read(ref _flushCount);

        public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null) { }
        public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null) { }
        public void ReportInventoryChanged(InventorySnapshot partialOrComplete) { }
        public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null) { }
        public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null) { }
        public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null) { }

        /// <summary>Completes when the <paramref name="number"/>-th flush (1-based) has been called.</summary>
        public Task FlushStarted(int number)
        {
            lock (_gate)
            {
                return Started(number).Task;
            }
        }

        /// <summary>Completes the pending flush.</summary>
        public void CompleteFlush()
        {
            TaskCompletionSource<SinkFlushResult> pending;
            lock (_gate)
            {
                pending = _pending ?? throw new InvalidOperationException("No flush is pending.");
                _pending = null;
            }

            Interlocked.Increment(ref _flushCount);
            pending.SetResult(new SinkFlushResult(0, 0, 0));
        }

        public ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct)
        {
            TaskCompletionSource started;
            TaskCompletionSource<SinkFlushResult> pending;
            lock (_gate)
            {
                pending = new TaskCompletionSource<SinkFlushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = pending;
                started = Started(++_flushCalls);
            }

            started.TrySetResult();
            return new ValueTask<SinkFlushResult>(pending.Task.WaitAsync(ct));
        }

        private TaskCompletionSource Started(int number)
        {
            while (_started.Count < number)
            {
                _started.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _started[number - 1];
        }
    }

    [Fact]
    public void INT_U_017_PollingLoop_interval_and_jitter_bounds()
    {
        var baseInterval = TimeSpan.FromSeconds(10);
        var jitter = 0.2; // ±20%

        var minExpected = TimeSpan.FromSeconds(8);
        var maxExpected = TimeSpan.FromSeconds(12);

        for (var i = 0; i < 50; i++)
        {
            var delay = PollingLoop.ComputeNextDelay(baseInterval, jitter);
            delay.ShouldBeGreaterThanOrEqualTo(minExpected);
            delay.ShouldBeLessThanOrEqualTo(maxExpected);
        }
    }

    [Fact]
    public async Task INT_U_018_PollingLoop_no_overlap()
    {
        var clock = TestTime.CreateProvider();
        var sink = new TrackingObservationSink();
        var executionCount = 0;
        var inFlight = 0;
        var maxInFlight = 0;

        using var loop = new PollingLoop(
            "test-no-overlap",
            TimeSpan.FromSeconds(1),
            async ct =>
            {
                var current = Interlocked.Increment(ref inFlight);
                lock (sink)
                {
                    if (current > maxInFlight)
                    {
                        maxInFlight = current;
                    }
                }

                // Simulate work that takes 2.5s (longer than the 1s interval)
                await Task.Delay(TimeSpan.FromSeconds(2.5), clock, ct);

                Interlocked.Decrement(ref inFlight);
                Interlocked.Increment(ref executionCount);
            },
            sink,
            clock);

        // Advance time in steps
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        maxInFlight.ShouldBe(1);
        executionCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void INT_U_019_PollingLoop_error_back_off_doubling_to_max_reset_on_success()
    {
        var clock = TestTime.CreateProvider();
        var sink = new TrackingObservationSink();
        var options = new PollingOptions(Jitter: 0.0, MaxBackoff: TimeSpan.FromSeconds(16));

        using var loop = new PollingLoop(
            "test-backoff",
            TimeSpan.FromSeconds(1),
            ct => ValueTask.CompletedTask,
            sink,
            clock,
            options);

        // attempt 1: 1s, attempt 2: 2s, attempt 3: 4s, attempt 4: 8s, attempt 5: 16s, attempt 6: 16s
        loop.ComputeErrorBackoff(1).ShouldBe(TimeSpan.FromSeconds(1));
        loop.ComputeErrorBackoff(2).ShouldBe(TimeSpan.FromSeconds(2));
        loop.ComputeErrorBackoff(3).ShouldBe(TimeSpan.FromSeconds(4));
        loop.ComputeErrorBackoff(4).ShouldBe(TimeSpan.FromSeconds(8));
        loop.ComputeErrorBackoff(5).ShouldBe(TimeSpan.FromSeconds(16));
        loop.ComputeErrorBackoff(6).ShouldBe(TimeSpan.FromSeconds(16));
        loop.ComputeErrorBackoff(0).ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task INT_U_020_PollingLoop_flush_awaited_each_iteration()
    {
        var clock = new TestClock();
        var sink = new GatedObservationSink();
        var iterations = 0;

        using var loop = new PollingLoop(
            "test-flush",
            TimeSpan.FromSeconds(1),
            ct =>
            {
                Interlocked.Increment(ref iterations);
                return ValueTask.CompletedTask;
            },
            sink,
            clock,
            new PollingOptions(Jitter: 0.0));

        for (var iteration = 1; iteration <= 3; iteration++)
        {
            // While the flush is pending, the loop waits for it: no delay is armed, no new iteration starts.
            await sink.FlushStarted(iteration).WaitAsync(TestTimeouts.Default, TimeProvider.System, Ct);
            Volatile.Read(ref iterations).ShouldBe(iteration);
            clock.TimersCreated.ShouldBe(iteration - 1);

            // The flush completes: the loop arms the interval delay, and only then is the clock advanced.
            sink.CompleteFlush();
            await TimersArmedAsync(clock, iteration);
            Volatile.Read(ref iterations).ShouldBe(iteration);
            sink.FlushCount.ShouldBe(iteration);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task INT_U_021_PollingLoop_paused_when_disconnected_unless_allowed()
    {
        var clock = new TestClock();
        var sink = new TrackingObservationSink();
        var normalRuns = 0;
        var allowedRuns = 0;

        using var normalLoop = new PollingLoop(
            "normal",
            TimeSpan.FromSeconds(1),
            ct =>
            {
                Interlocked.Increment(ref normalRuns);
                return ValueTask.CompletedTask;
            },
            sink,
            clock,
            new PollingOptions(Jitter: 0.0, RunWhenDisconnected: false));

        using var allowedLoop = new PollingLoop(
            "allowed",
            TimeSpan.FromSeconds(1),
            ct =>
            {
                Interlocked.Increment(ref allowedRuns);
                return ValueTask.CompletedTask;
            },
            sink,
            clock,
            new PollingOptions(Jitter: 0.0, RunWhenDisconnected: true));

        // 1st run of each loop, then each arms its interval delay (one timer per loop and iteration).
        await TimersArmedAsync(clock, 2);
        Volatile.Read(ref normalRuns).ShouldBe(1);
        Volatile.Read(ref allowedRuns).ShouldBe(1);

        // Pause both (simulating disconnection): after the next interval, both loops have been through an
        // iteration (timers 3 and 4), and only the allowed one ran its work.
        normalLoop.Pause();
        allowedLoop.Pause();
        clock.Advance(TimeSpan.FromSeconds(1));
        await TimersArmedAsync(clock, 4);
        Volatile.Read(ref normalRuns).ShouldBe(1);
        Volatile.Read(ref allowedRuns).ShouldBe(2);

        // Resume normal
        normalLoop.Resume();
        clock.Advance(TimeSpan.FromSeconds(1));
        await TimersArmedAsync(clock, 6);
        Volatile.Read(ref normalRuns).ShouldBe(2);
        Volatile.Read(ref allowedRuns).ShouldBe(3);
    }

    [Fact]
    public async Task INT_U_022_BoundedPager_stops_on_short_page()
    {
        var clock = TestTime.CreateProvider();
        var page1 = new PageResult<string>(["a", "b", "c"], "cursor-2");
        var page2 = new PageResult<string>(["d"], "cursor-3"); // short page (< 3 items)
        var page3 = new PageResult<string>(["e", "f", "g"], null);

        var calls = 0;
        ValueTask<PageResult<string>> Fetch(string? cursor, CancellationToken ct)
        {
            calls++;
            return cursor switch
            {
                null => ValueTask.FromResult(page1),
                "cursor-2" => ValueTask.FromResult(page2),
                _ => ValueTask.FromResult(page3),
            };
        }

        var results = new List<string>();
        await foreach (var item in BoundedPager.ReadAllAsync(Fetch, expectedPageSize: 3, clock, ct: TestContext.Current.CancellationToken))
        {
            results.Add(item);
        }

        results.ShouldBe(["a", "b", "c", "d"]);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task INT_U_023_BoundedPager_delay_on_empty_page()
    {
        var clock = new TestClock();

        ValueTask<PageResult<string>> Fetch(string? cursor, CancellationToken ct) =>
            ValueTask.FromResult(new PageResult<string>([]));

        var pagerTask = Task.Run(async () =>
        {
            var items = 0;
            await foreach (var item in BoundedPager.ReadAllAsync(
                Fetch,
                expectedPageSize: 10,
                clock,
                emptyPageDelay: TimeSpan.FromSeconds(1),
                ct: Ct))
            {
                items++;
            }

            return items;
        }, Ct);

        // The empty page arms the delay; the pager does not complete before the delay has elapsed.
        await TimersArmedAsync(clock, 1);
        pagerTask.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(999));
        pagerTask.IsCompleted.ShouldBeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        (await pagerTask.WaitAsync(TestTimeouts.Default, TimeProvider.System, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task INT_U_024_BoundedPager_repeated_cursor_throws_exception()
    {
        var clock = TestTime.CreateProvider();
        var page1 = new PageResult<string>(["a", "b"], "same-cursor");
        var page2 = new PageResult<string>(["c", "d"], "same-cursor");

        ValueTask<PageResult<string>> Fetch(string? cursor, CancellationToken ct)
        {
            return cursor switch
            {
                null => ValueTask.FromResult(page1),
                _ => ValueTask.FromResult(page2),
            };
        }

        await Should.ThrowAsync<PagingLoopDetectedException>(async () =>
        {
            await foreach (var _ in BoundedPager.ReadAllAsync(Fetch, expectedPageSize: 2, clock, ct: TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task INT_U_025_BoundedPager_max_pages_enforced()
    {
        var clock = TestTime.CreateProvider();
        var count = 0;

        ValueTask<PageResult<int>> Fetch(string? cursor, CancellationToken ct)
        {
            count++;
            return ValueTask.FromResult(new PageResult<int>([count, count], $"cursor-{count}"));
        }

        var items = new List<int>();
        await foreach (var item in BoundedPager.ReadAllAsync(Fetch, expectedPageSize: 2, clock, maxPages: 3, ct: TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        count.ShouldBe(3);
        items.Count.ShouldBe(6);
    }
}
