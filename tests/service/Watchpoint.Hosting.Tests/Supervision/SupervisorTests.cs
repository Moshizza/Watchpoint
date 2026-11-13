using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Hosting.Supervision;
using Watchpoint.Testing.Async;
using Xunit;
using static Watchpoint.Hosting.Supervision.SupervisedProcessState;

namespace Watchpoint.Hosting.Tests.Supervision;

/// <summary>The supervisor state machine (spec HST §7.1, §7.2) on a fake clock with fake processes.</summary>
[Trait("layer", "U")]
[Trait("spec", "HST")]
public sealed class SupervisorTests
{
    private static readonly RestartPolicy Policy = RestartPolicy.Default;

    /// <summary>Every transition of spec HST §7.2; anything else is a bug.</summary>
    private static readonly HashSet<(SupervisedProcessState From, SupervisedProcessState To)> Allowed =
    [
        (Starting, Running), (Starting, Backoff), (Starting, Stopping),
        (Running, Backoff), (Running, Stopping),
        (Backoff, Starting), (Backoff, Failed), (Backoff, Stopped),
        (Failed, Starting), (Failed, Stopped),
        (Stopping, Stopped), (Stopping, Starting),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void HST_U_016_Restart_delay_computation()
    {
        double[] expected = [1, 2, 4, 8, 16, 32, 60, 60];
        var random = new Random(20261113);
        for (var n = 1; n <= expected.Length; n++)
        {
            var nominal = expected[n - 1];
            Policy.ComputeDelay(n, 0.5).TotalSeconds.ShouldBe(nominal, 1e-9);
            Policy.ComputeDelay(n, 0.0).TotalSeconds.ShouldBe(nominal * 0.8, 1e-9);
            Policy.ComputeDelay(n, 1.0).TotalSeconds.ShouldBe(nominal * 1.2, 1e-9);
            for (var i = 0; i < 200; i++)
            {
                Policy.ComputeDelay(n, random.NextDouble()).TotalSeconds.ShouldBeInRange(nominal * 0.8, nominal * 1.2);
            }
        }

        // The cap holds for any restart number (the power overflows to infinity).
        Policy.ComputeDelay(5000, 0.5).ShouldBe(TimeSpan.FromSeconds(60));
        Should.Throw<ArgumentOutOfRangeException>(() => Policy.ComputeDelay(0, 0.5));
    }

    [Fact]
    public async Task HST_U_017_Restart_window_leads_to_Failed()
    {
        await using var h = new SupervisorHarness();
        var (handle, states, _) = await h.StartRunningAsync();
        var began = h.Clock.GetUtcNow();
        var expected = new List<SupervisedProcessState> { Starting, Running };
        for (var n = 1; n <= Policy.MaxRestartsInWindow; n++)
        {
            h.Launcher.Children[^1].Exit(1);
            await states.WaitForAsync(Backoff, n);
            handle.RestartCount.ShouldBe(n);
            h.Clock.Advance(Policy.ComputeDelay(n, 0.5));
            await states.WaitForAsync(Running, n + 1);
            expected.AddRange([Backoff, Starting, Running]);
        }

        // The 11th exit, 303 s after the first: more than 10 restarts within the 10 minute window.
        (h.Clock.GetUtcNow() - began).ShouldBeLessThan(Policy.Window);
        h.Launcher.Children[^1].Exit(1);
        await states.WaitForAsync(Failed);
        expected.AddRange([Backoff, Failed]);

        handle.State.ShouldBe(Failed);
        handle.RestartCount.ShouldBe(10);
        handle.Pid.ShouldBeNull();
        states.States.ShouldBe(expected);
        var failed = h.Logs.WithEventId(1152).ShouldHaveSingleItem();
        failed.Level.ShouldBe(LogLevel.Error);
        failed.Properties["Name"].ShouldBe("child");
        failed.Properties["Restarts"].ShouldBe(10);
        h.Logs.WithEventId(1151).Count().ShouldBe(10);

        // Failed is stable: no timer starts the child again.
        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Launcher.Attempts.ShouldBe(11);
        handle.State.ShouldBe(Failed);
    }

    [Fact]
    public async Task HST_U_018_Long_run_resets_restart_count()
    {
        await using var h = new SupervisorHarness();
        var (handle, states, _) = await h.StartRunningAsync();
        for (var n = 1; n <= 2; n++)
        {
            h.Launcher.Children[^1].Exit(1);
            await states.WaitForAsync(Backoff, n);
            h.Clock.Advance(Policy.ComputeDelay(n, 0.5));
            await states.WaitForAsync(Running, n + 1);
        }

        handle.RestartCount.ShouldBe(2);

        h.Clock.Advance(TimeSpan.FromMinutes(11));
        h.Launcher.Children[^1].Exit(1);
        await states.WaitForAsync(Backoff, 3);

        handle.RestartCount.ShouldBe(1);
        var last = h.Logs.WithEventId(1151).Last();
        last.Properties["Attempt"].ShouldBe(1);
        last.Properties["DelayMs"].ShouldBe(1000L);
        AssertValid(states);
    }

    [Fact]
    public async Task HST_U_019_Readiness_probe_timeout_leads_to_Backoff()
    {
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Watchpoint.Kernel.Testing.TestTime.CreateProvider();
        using var probes = new ReadinessProbes(clock, TimeSpan.FromMilliseconds(200), (_, _, _) =>
        {
            firstAttempt.TrySetResult();
            return Task.FromResult(false);
        });
        await using var h = new SupervisorHarness(new ClockedProber(probes), clock: clock);
        var (started, states) = h.Begin(SupervisorHarness.Spec(readiness: new ReadinessProbe.Tcp("127.0.0.1", 4222, TimeSpan.FromSeconds(5))), Ct);

        // The probe's deadline timer is armed before its first attempt.
        await firstAttempt.Task.WaitAsync(TestTimeouts.Default, Ct);
        h.Clock.Advance(TimeSpan.FromSeconds(4.9));
        states.Handle.State.ShouldBe(Starting);

        h.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await states.WaitForAsync(Backoff);

        states.States.ShouldBe([Starting, Backoff]);
        states.Handle.LastExitReason.ShouldBe("readiness probe timed out");
        started.IsCompleted.ShouldBeFalse();
        var child = h.Launcher.Children.ShouldHaveSingleItem();
        child.GracefulStops.ShouldBe(1);
        child.Exited.IsCompleted.ShouldBeTrue();
        File.Exists(h.Janitor.PidFilePath("child")).ShouldBeFalse();
    }

    [Fact]
    public async Task HST_U_020_Stop_is_graceful_then_kill()
    {
        await using var h = new SupervisorHarness();
        h.Launcher.Configure = (_, child) => child.IgnoresGracefulStop = true;
        var (handle, states, child) = await h.StartRunningAsync(SupervisorHarness.Spec(grace: TimeSpan.FromSeconds(10)));

        var stop = handle.StopAsync(Ct);
        await child.GracefulRequested;
        h.Clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(1));
        child.Kills.ShouldBe(0);
        handle.State.ShouldBe(Stopping);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await stop.WaitAsync(TestTimeouts.Default, Ct);

        child.GracefulStops.ShouldBe(1);
        child.Kills.ShouldBe(1);
        states.States.ShouldBe([Starting, Running, Stopping, Stopped]);
        handle.LastExitReason.ShouldBe("stopped");
    }

    [Fact]
    public async Task HST_U_028_Start_and_stop_lifecycle()
    {
        await using var h = new SupervisorHarness();
        var (handle, states, child) = await h.StartRunningAsync(SupervisorHarness.Spec("nats"));

        handle.Name.ShouldBe("nats");
        handle.State.ShouldBe(Running);
        handle.Pid.ShouldBe(child.Pid);
        handle.RestartCount.ShouldBe(0);
        handle.LastStartedAt.ShouldBe(h.Clock.GetUtcNow());
        h.Supervisor.Processes.ShouldHaveSingleItem().ShouldBeSameAs(handle);
        var pidFile = h.Janitor.PidFilePath("nats");
        (await File.ReadAllTextAsync(pidFile, Ct)).ShouldBe($"{child.Pid}\n{SupervisorHarness.Executable}\n");
        var startedLog = h.Logs.WithEventId(1150).ShouldHaveSingleItem();
        startedLog.Properties["Name"].ShouldBe("nats");
        startedLog.Properties["Pid"].ShouldBe(child.Pid);

        await handle.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Running, Stopping, Stopped]);
        handle.State.ShouldBe(Stopped);
        handle.Pid.ShouldBeNull();
        child.GracefulStops.ShouldBe(1);
        child.Kills.ShouldBe(0);
        child.Disposed.ShouldBeTrue();
        File.Exists(pidFile).ShouldBeFalse();
        h.Supervisor.Processes.ShouldBeEmpty();
        h.Logs.WithEventId(1153).ShouldHaveSingleItem().Properties["Name"].ShouldBe("nats");

        // Stopping again is a no-op, and a stopped process cannot be restarted.
        await handle.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);
        var error = await Should.ThrowAsync<SupervisorException>(() => handle.RestartAsync(Ct));
        error.Error.Code.Value.ShouldBe("host.process.not_found");
        states.States.Count.ShouldBe(4);
    }

    [Fact]
    public async Task HST_U_029_Crash_during_start_backs_off_for_the_exact_delay()
    {
        var prober = new ManualProber();
        await using var h = new SupervisorHarness(prober);
        var (started, states) = h.Begin(SupervisorHarness.Spec(), Ct);
        await prober.WaitForProbeAsync(1);

        h.Launcher.Children[0].Exit(3);
        await states.WaitForAsync(Backoff);
        started.IsCompleted.ShouldBeFalse();
        states.Handle.LastExitReason.ShouldBe("exited with code 3 before it was ready");
        states.Handle.Pid.ShouldBeNull();

        // Not one millisecond early.
        h.Clock.Advance(TimeSpan.FromMilliseconds(999));
        h.Launcher.Attempts.ShouldBe(1);
        states.Handle.State.ShouldBe(Backoff);

        h.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await prober.WaitForProbeAsync(2);
        prober.Complete(2, ready: true);
        var handle = await started.WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Backoff, Starting, Running]);
        handle.RestartCount.ShouldBe(1);
        var log = h.Logs.WithEventId(1151).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Properties["ExitCode"].ShouldBe(3);
        log.Properties["Attempt"].ShouldBe(1);
        log.Properties["DelayMs"].ShouldBe(1000L);
    }

    [Fact]
    public async Task HST_U_030_Start_failure_is_retried_with_backoff()
    {
        await using var h = new SupervisorHarness();
        h.Launcher.Configure = (attempt, _) =>
        {
            if (attempt == 1)
            {
                throw new IOException("boom");
            }
        };
        var (started, states) = h.Begin(SupervisorHarness.Spec(), Ct);

        await states.WaitForAsync(Backoff);
        states.Handle.LastExitReason.ShouldBe("start failed: boom");
        h.Clock.Advance(Policy.ComputeDelay(1, 0.5));
        await started.WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Backoff, Starting, Running]);
        h.Launcher.Attempts.ShouldBe(2);
        h.Launcher.Children.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task HST_U_031_Stop_requested_while_starting()
    {
        var prober = new ManualProber();
        await using var h = new SupervisorHarness(prober);
        var (started, states) = h.Begin(SupervisorHarness.Spec(), Ct);
        await prober.WaitForProbeAsync(1);

        await states.Handle.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Stopping, Stopped]);
        var child = h.Launcher.Children.ShouldHaveSingleItem();
        child.GracefulStops.ShouldBe(1);
        child.Exited.IsCompleted.ShouldBeTrue();
        (await started.WaitAsync(TestTimeouts.Default, Ct)).State.ShouldBe(Stopped);
        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Launcher.Attempts.ShouldBe(1);
        h.Logs.WithEventId(1151).ShouldBeEmpty();
    }

    [Fact]
    public async Task HST_U_032_Stop_requested_during_Backoff_or_Failed()
    {
        await using var h = new SupervisorHarness();
        var (backingOff, states, child) = await h.StartRunningAsync(SupervisorHarness.Spec("a"));
        child.Exit(1);
        await states.WaitForAsync(Backoff);

        await backingOff.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Running, Backoff, Stopped]);

        // Failed at the first exit with a policy that tolerates no restart.
        var noRestart = SupervisorHarness.Spec("b") with { Restart = Policy with { MaxRestartsInWindow = 0 } };
        var (failed, failedStates, failedChild) = await h.StartRunningAsync(noRestart);
        failedChild.Exit(1);
        await failedStates.WaitForAsync(Failed);

        await failed.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        failedStates.States.ShouldBe([Starting, Running, Backoff, Failed, Stopped]);

        // The cancelled back-off timer starts nothing.
        h.Clock.Advance(TimeSpan.FromHours(1));
        h.Launcher.Attempts.ShouldBe(2);
        h.Supervisor.Processes.ShouldBeEmpty();
    }

    [Fact]
    public async Task HST_U_033_Manual_restart_from_Running_Backoff_and_Failed()
    {
        var prober = new ManualProber();
        await using var h = new SupervisorHarness(prober);
        var spec = SupervisorHarness.Spec() with { Restart = Policy with { MaxRestartsInWindow = 1 } };
        var (started, states) = h.Begin(spec, Ct);
        await prober.WaitForProbeAsync(1);
        prober.Complete(1, ready: true);
        var handle = await started.WaitAsync(TestTimeouts.Default, Ct);

        // Running: three concurrent calls are one restart.
        var restarts = new[] { handle.RestartAsync(Ct), handle.RestartAsync(Ct), handle.RestartAsync(Ct) };
        await prober.WaitForProbeAsync(2);
        restarts.ShouldAllBe(r => !r.IsCompleted);
        prober.Complete(2, ready: true);
        await Task.WhenAll(restarts).WaitAsync(TestTimeouts.Default, Ct);
        states.States.ShouldBe([Starting, Running, Stopping, Starting, Running]);
        h.Launcher.Attempts.ShouldBe(2);
        h.Launcher.Children[0].GracefulStops.ShouldBe(1);

        // Backoff: the restart does not wait for the delay and resets the count.
        h.Launcher.Children[1].Exit(1);
        await states.WaitForAsync(Backoff);
        handle.RestartCount.ShouldBe(1);
        var fromBackoff = handle.RestartAsync(Ct);
        await prober.WaitForProbeAsync(3);
        prober.Complete(3, ready: true);
        await fromBackoff.WaitAsync(TestTimeouts.Default, Ct);
        handle.RestartCount.ShouldBe(0);

        // Failed (two exits with MaxRestartsInWindow = 1): only a manual restart starts it again.
        h.Launcher.Children[2].Exit(1);
        await states.WaitForAsync(Backoff, 2);
        h.Clock.Advance(Policy.ComputeDelay(1, 0.5));
        await prober.WaitForProbeAsync(4);
        h.Launcher.Children[3].Exit(1);
        await states.WaitForAsync(Failed);
        var fromFailed = handle.RestartAsync(Ct);
        await prober.WaitForProbeAsync(5);
        prober.Complete(5, ready: true);
        await fromFailed.WaitAsync(TestTimeouts.Default, Ct);

        handle.State.ShouldBe(Running);
        handle.RestartCount.ShouldBe(0);
        states.States.ShouldBe(
        [
            Starting, Running, Stopping, Starting, Running,
            Backoff, Starting, Running,
            Backoff, Starting, Backoff, Failed, Starting, Running,
        ]);
    }

    [Fact]
    public async Task HST_U_034_Concurrent_stop_and_restart_calls()
    {
        for (var round = 0; round < 25; round++)
        {
            await using var h = new SupervisorHarness();
            var (handle, states, _) = await h.StartRunningAsync();
            using var go = new ManualResetEventSlim();
            var calls = Enumerable.Range(0, 16).Select(i => Task.Run(
                async () =>
                {
                    go.Wait(Ct);
                    try
                    {
                        await (i % 2 == 0 ? handle.RestartAsync(Ct) : handle.StopAsync(Ct));
                    }
                    catch (SupervisorException ex) when (ex.Error.Code.Value == "host.process.not_found")
                    {
                        // The restart lost against a stop.
                    }
                },
                Ct)).ToArray();
            go.Set();
            await Task.WhenAll(calls).WaitAsync(TestTimeouts.Default, Ct);

            // Stop wins, exactly once, and every transition on the way is a legal one.
            handle.State.ShouldBe(Stopped);
            AssertValid(states);
            states.States.Count(s => s == Stopped).ShouldBe(1);
            states.States[^1].ShouldBe(Stopped);
            states.States.Count(s => s == Starting).ShouldBe(h.Launcher.Attempts);
            h.Launcher.Children.ShouldAllBe(c => c.Exited.IsCompleted && c.Disposed && c.GracefulStops == 1 && c.Kills == 0);
            h.Supervisor.Processes.ShouldBeEmpty();
            h.Logs.WithEventId(1151).ShouldBeEmpty();
            h.Logs.WithEventId(1159).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task HST_U_035_Stop_never_hangs()
    {
        await using var h = new SupervisorHarness();
        h.Launcher.Configure = (attempt, child) =>
        {
            child.IgnoresGracefulStop = true;
            child.Unkillable = attempt == 2;
        };

        // A cancelled token skips the grace period: killed at once, no clock involved.
        var (impatient, _, first) = await h.StartRunningAsync(SupervisorHarness.Spec("a"));
        await impatient.StopAsync(new CancellationToken(canceled: true)).WaitAsync(TestTimeouts.Default, Ct);
        first.GracefulStops.ShouldBe(0);
        first.Kills.ShouldBe(1);
        impatient.State.ShouldBe(Stopped);

        // A child that survives the kill is given up after the kill timeout.
        var (stubborn, states, second) = await h.StartRunningAsync(SupervisorHarness.Spec("b"));
        var stop = stubborn.StopAsync(Ct);
        await second.GracefulRequested;
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        await second.KillRequested;
        stop.IsCompleted.ShouldBeFalse();
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TestTimeouts.Default, Ct);

        states.States.ShouldBe([Starting, Running, Stopping, Stopped]);
        stubborn.LastExitReason.ShouldBe("stop requested; the process could not be killed");
        h.Logs.WithEventId(1157).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task HST_U_036_Start_errors_and_cancelled_start()
    {
        var prober = new ManualProber();
        await using var h = new SupervisorHarness(prober);
        var missingPath = Path.Combine(h.RunDirectory, "missing", "nats-server");

        var missing = await Should.ThrowAsync<SupervisorException>(
            () => h.Supervisor.StartAsync(SupervisorHarness.Spec() with { Executable = missingPath }, Ct));
        missing.Error.Code.Value.ShouldBe("host.process.executable_missing");
        missing.Error.Kind.ShouldBe(Watchpoint.Kernel.Results.ErrorKind.Unprocessable);
        await Should.ThrowAsync<ArgumentException>(() => h.Supervisor.StartAsync(SupervisorHarness.Spec("../evil"), Ct));
        await Should.ThrowAsync<ArgumentException>(() => h.Supervisor.StartAsync(SupervisorHarness.Spec() with { Executable = "nats-server" }, Ct));
        await Should.ThrowAsync<NotSupportedException>(
            () => h.Supervisor.StartAsync(SupervisorHarness.Spec() with { Limits = new ResourceLimits(MemoryBytes: 1) }, Ct));
        h.Launcher.Attempts.ShouldBe(0);
        h.Supervisor.Processes.ShouldBeEmpty();

        // Cancelling the start stops the child: the caller got no handle, so nothing may stay behind.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (started, states) = h.Begin(SupervisorHarness.Spec(), cts.Token);
        await prober.WaitForProbeAsync(1);
        await Should.ThrowAsync<InvalidOperationException>(() => h.Supervisor.StartAsync(SupervisorHarness.Spec(), Ct));
        await cts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => started.WaitAsync(TestTimeouts.Default, Ct));
        states.States.ShouldBe([Starting, Stopping, Stopped]);
        var child = h.Launcher.Children.ShouldHaveSingleItem();
        child.Kills.ShouldBe(1);
        child.GracefulStops.ShouldBe(0);
        h.Supervisor.Processes.ShouldBeEmpty();

        // The name is free again.
        var (again, _) = h.Begin(SupervisorHarness.Spec(), Ct);
        await prober.WaitForProbeAsync(2);
        prober.Complete(2, ready: true);
        (await again.WaitAsync(TestTimeouts.Default, Ct)).State.ShouldBe(Running);
    }

    [Fact]
    public async Task HST_U_037_Shutdown_stops_children_in_reverse_start_order()
    {
        await using var h = new SupervisorHarness();
        var stopped = new List<int>();
        h.Launcher.Configure = (attempt, child) => child.OnGracefulStop = () =>
        {
            lock (stopped)
            {
                stopped.Add(attempt);
            }
        };
        foreach (var name in (string[])["a", "b", "c"])
        {
            await h.StartRunningAsync(SupervisorHarness.Spec(name));
        }

        h.Supervisor.Processes.Select(p => p.Name).ShouldBe(["a", "b", "c"]);

        await h.Supervisor.StopAllAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        stopped.ShouldBe([3, 2, 1]);
        h.Supervisor.Processes.ShouldBeEmpty();
        await Should.ThrowAsync<InvalidOperationException>(() => h.Supervisor.StartAsync(SupervisorHarness.Spec("d"), Ct));

        // Disposing a supervisor that was not shut down kills what is left, without a grace period.
        var other = new SupervisorHarness();
        other.Launcher.Configure = (_, child) => child.IgnoresGracefulStop = true;
        var (_, _, leftover) = await other.StartRunningAsync();
        await other.DisposeAsync().AsTask().WaitAsync(TestTimeouts.Default, Ct);
        leftover.Kills.ShouldBe(1);
        leftover.GracefulStops.ShouldBe(0);
    }

    private static void AssertValid(StateRecorder recorder)
    {
        var states = recorder.States;
        states[0].ShouldBe(Starting);
        for (var i = 1; i < states.Count; i++)
        {
            Allowed.ShouldContain((states[i - 1], states[i]), $"transition {i} in [{string.Join(", ", states)}]");
        }
    }
}
