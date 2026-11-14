using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Hosting.Supervision;
using Watchpoint.Kernel.Testing;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Hosting.Tests.Supervision;

/// <summary>Output capture, readiness probes, leftover cleanup and metrics of the supervisor (spec HST §7.3, §11).</summary>
[Trait("layer", "U")]
[Trait("spec", "HST")]
public sealed class SupervisionPartsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HST_U_021_Output_lines_logged_with_child_name()
    {
        await using var h = new SupervisorHarness();
        h.Launcher.Configure = (_, child) =>
        {
            child.StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes("hello\r\n\nsecond line"));
            child.StandardError = new MemoryStream(Encoding.UTF8.GetBytes("oops\n"));
        };
        var (handle, _, _) = await h.StartRunningAsync(SupervisorHarness.Spec("nats") with { LogCategory = "Watchpoint.Child.Nats" });

        // Stopping waits until the output is read and logged.
        await handle.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        var lines = h.Logs.Entries.Where(e => e.Category == "Watchpoint.Child.Nats").ToList();
        lines.ShouldAllBe(e => e.EventId == 1156 && (string?)e.Properties["Child"] == "nats");
        lines.Where(e => e.Level == LogLevel.Information).Select(e => e.Properties["Line"]).ShouldBe(["hello", "second line"]);
        var error = lines.Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        error.Properties["Line"].ShouldBe("oops");
        error.RenderedMessage.ShouldBe("nats: oops");
    }

    [Fact]
    public async Task HST_U_022_Long_lines_truncated()
    {
        await using var h = new SupervisorHarness();
        var longLine = new string('a', 20 * 1024);
        h.Launcher.Configure = (_, child) => child.StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(longLine + "\nshort\n"));
        var (handle, _, _) = await h.StartRunningAsync();

        await handle.StopAsync(Ct).WaitAsync(TestTimeouts.Default, Ct);

        var lines = h.Logs.WithEventId(1156).Select(e => (string)e.Properties["Line"]!).ToList();
        lines.Count.ShouldBe(2);
        lines[0].ShouldBe(new string('a', 8192) + " …[truncated]");
        lines[1].ShouldBe("short");
    }

    [Fact]
    public async Task HST_U_038_Chatty_child_is_drained_with_bounded_memory()
    {
        // A logger that blocks: the child writes far more than the queue holds while nothing is logged.
        using var release = new ManualResetEventSlim();
        var slow = new BlockingLogger(release);
        var host = new LogCollector();
        var stream = new SignallingStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"))));
        using var pump = new ChildOutputPump("chatty", slow, host.CreateLogger("host"), LogLevel.Warning, capacity: 8);

        pump.Start(stream, null);

        // The whole stream is read although the logger is stuck: the child never blocks on a full pipe.
        await stream.EndReached.WaitAsync(TestTimeouts.Default, Ct);
        slow.Lines.Count.ShouldBeLessThanOrEqualTo(1);
        release.Set();
        await pump.CompleteAsync(TimeSpan.FromSeconds(5), TimeProvider.System).WaitAsync(TestTimeouts.Default, Ct);

        // At most the queue capacity (plus the line in the logger's hands) was kept; the rest is counted.
        slow.Lines.Count.ShouldBeInRange(1, 9);
        slow.Lines[0].ShouldBe("line 0");
        var dropped = host.WithEventId(1155).Sum(e => (long)e.Properties["Dropped"]!);
        (slow.Lines.Count + dropped).ShouldBe(5000);
        host.WithEventId(1155).ShouldAllBe(e => e.Level == LogLevel.Warning && (string?)e.Properties["Name"] == "chatty");
    }

    [Fact]
    public async Task HST_U_039_Leftover_cleanup_kills_only_the_recorded_executable()
    {
        await using var h = new SupervisorHarness();
        Directory.CreateDirectory(h.RunDirectory);
        var nats = Path.Combine(h.RunDirectory, "nats-server");
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("nats"), $"4242\n{nats}\n", Ct);
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("reused"), $"4343\n{nats}\n", Ct);
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("gone"), $"4444\n{nats}\n", Ct);
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("self"), $"{Environment.ProcessId}\n{nats}\n", Ct);
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("garbage"), "not a pid", Ct);
        h.Inspector.Run(4242, nats);
        h.Inspector.Run(4343, "/usr/bin/unrelated");
        h.Inspector.Run(Environment.ProcessId, nats);

        var killed = await h.Janitor.CleanupAllAsync(Ct);

        // Only the process that still runs the recorded executable is killed; a reused pid is left alone.
        killed.ShouldBe(1);
        h.Inspector.Killed.ShouldBe([4242]);
        var log = h.Logs.WithEventId(1154).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Warning);
        log.Properties["Pid"].ShouldBe(4242);
        log.Properties["Name"].ShouldBe("nats");
        Directory.EnumerateFiles(h.RunDirectory).ShouldBeEmpty();

        // Starting a child cleans the leftover of its own name first.
        await File.WriteAllTextAsync(h.Janitor.PidFilePath("child"), $"5555\n{SupervisorHarness.Executable}\n", Ct);
        h.Inspector.Run(5555, SupervisorHarness.Executable);
        await h.StartRunningAsync();
        h.Inspector.Killed.ShouldBe([4242, 5555]);
        h.Logs.WithEventId(1154).Count().ShouldBe(2);
    }

    [Fact]
    public async Task HST_U_040_Readiness_probes()
    {
        var clock = TestTime.CreateProvider();
        var attempts = new List<TaskCompletionSource>();
        TaskCompletionSource Attempt(int number)
        {
            lock (attempts)
            {
                while (attempts.Count < number)
                {
                    attempts.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                return attempts[number - 1];
            }
        }

        var calls = 0;
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.NoContent]);
        using var probes = new ReadinessProbes(
            clock,
            TimeSpan.FromMilliseconds(200),
            (_, _, _) =>
            {
                var call = Interlocked.Increment(ref calls);
                Attempt(call).TrySetResult();
                return call < 3 ? throw new System.Net.Sockets.SocketException(111) : Task.FromResult(true);
            },
            new StubHandler(() => statuses.Dequeue()));
        using var pump = new ChildOutputPump("p", NullLogger.Instance, NullLogger.Instance, LogLevel.Warning, 16);

        // None: ready at once.
        (await probes.WaitReadyAsync(ReadinessProbe.None, pump, Ct)).ShouldBeTrue();

        // Tcp: refused twice, then connected; one attempt per interval.
        var tcp = probes.WaitReadyAsync(new ReadinessProbe.Tcp("127.0.0.1", 4222, TimeSpan.FromSeconds(30)), pump, Ct);
        await Attempt(1).Task.WaitAsync(TestTimeouts.Default, Ct);
        for (var next = 2; next <= 3; next++)
        {
            await Eventually.UntilAsync(
                () =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(200));
                    return Attempt(next).Task.IsCompleted;
                },
                ct: Ct);
        }

        (await tcp.WaitAsync(TestTimeouts.Default, Ct)).ShouldBeTrue();

        // Http: 503 is not ready, 2xx is.
        var http = probes.WaitReadyAsync(new ReadinessProbe.Http(new Uri("http://127.0.0.1:8222/healthz"), TimeSpan.FromSeconds(30)), pump, Ct);
        await Eventually.UntilAsync(
            () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(200));
                return http.IsCompleted;
            },
            ct: Ct);
        (await http).ShouldBeTrue();
        statuses.ShouldBeEmpty();

        // StdoutLine: times out without the line, succeeds when a stdout line contains the text.
        var missing = probes.WaitReadyAsync(new ReadinessProbe.StdoutLine("Server is ready", TimeSpan.FromSeconds(5)), pump, Ct);
        clock.Advance(TimeSpan.FromSeconds(5));
        (await missing.WaitAsync(TestTimeouts.Default, Ct)).ShouldBeFalse();
        var seen = probes.WaitReadyAsync(new ReadinessProbe.StdoutLine("Server is ready", TimeSpan.FromSeconds(5)), pump, Ct);
        pump.Start(new MemoryStream(Encoding.UTF8.GetBytes("starting\n[INF] Server is ready\n")), null);
        (await seen.WaitAsync(TestTimeouts.Default, Ct)).ShouldBeTrue();
        await pump.CompleteAsync(TimeSpan.FromSeconds(5), TimeProvider.System).WaitAsync(TestTimeouts.Default, Ct);
    }

    [Fact]
    public async Task HST_U_045_Ready_line_printed_at_once_is_not_missed()
    {
        // The whole output, ready line included, is available before the supervisor looks at it.
        for (var round = 0; round < 50; round++)
        {
            var clock = TestTime.CreateProvider();
            using var probes = new ReadinessProbes(clock, TimeSpan.FromMilliseconds(200));
            await using var h = new SupervisorHarness(new ClockedProber(probes), clock: clock);
            h.Launcher.Configure = (_, child) => child.StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes("ready\n"));

            var (started, states) = h.Begin(
                SupervisorHarness.Spec(readiness: new ReadinessProbe.StdoutLine("ready", TimeSpan.FromSeconds(30))), Ct);
            var handle = await started.WaitAsync(TestTimeouts.Default, Ct);

            handle.State.ShouldBe(SupervisedProcessState.Running);
            states.States.ShouldBe([SupervisedProcessState.Starting, SupervisedProcessState.Running]);
        }
    }

    [Fact]
    public async Task HST_U_041_Child_metrics()
    {
        var restarts = 0L;
        var states = new Dictionary<string, int>();
        var name = "m" + Guid.NewGuid().ToString("N")[..8];
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Watchpoint.Host" && instrument.Name.StartsWith("watchpoint.host.child.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "watchpoint.host.child.restarts" && Tag(tags, "child") == name)
            {
                instrument.Unit.ShouldBe("{restarts}");
                Interlocked.Add(ref restarts, value);
            }
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "watchpoint.host.child.state" && Tag(tags, "child") == name)
            {
                states[Tag(tags, "state")!] = value;
            }
        });
        listener.Start();

        await using var h = new SupervisorHarness();
        var (handle, recorder, child) = await h.StartRunningAsync(SupervisorHarness.Spec(name));
        listener.RecordObservableInstruments();
        states.Where(s => s.Value == 1).Select(s => s.Key).ShouldBe(["Running"]);
        states.Count.ShouldBe(6);

        child.Exit(1);
        await recorder.WaitForAsync(SupervisedProcessState.Backoff);
        states.Clear();
        listener.RecordObservableInstruments();

        Interlocked.Read(ref restarts).ShouldBe(1);
        states.Where(s => s.Value == 1).Select(s => s.Key).ShouldBe(["Backoff"]);
        handle.State.ShouldBe(SupervisedProcessState.Backoff);
    }

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == key)
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private sealed class StubHandler(Func<HttpStatusCode> status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status()));
    }

    /// <summary>A stream that tells when its end was read.</summary>
    private sealed class SignallingStream(byte[] content) : MemoryStream(content)
    {
        private readonly TaskCompletionSource _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EndReached => _end.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                _end.TrySetResult();
            }

            return read;
        }
    }

    private sealed class BlockingLogger(ManualResetEventSlim release) : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = ((IEnumerable<KeyValuePair<string, object?>>)state!).First(p => p.Key == "Line").Value!.ToString()!;
            lock (_lines)
            {
                _lines.Add(line);
            }

            release.Wait();
        }
    }
}
