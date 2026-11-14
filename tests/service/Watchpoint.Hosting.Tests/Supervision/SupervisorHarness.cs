using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Hosting.Supervision;
using Watchpoint.Kernel.Testing;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Hosting.Tests.Supervision;

/// <summary>A supervisor on a fake clock with fake processes; every wait is on a signal, bounded by a real-time limit.</summary>
internal sealed class SupervisorHarness : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;

    public SupervisorHarness(IReadinessProber? prober = null, Func<double>? jitter = null, int outputCapacity = 1024, FakeTimeProvider? clock = null)
    {
        Clock = clock ?? TestTime.CreateProvider();
        RunDirectory = Path.Combine(Path.GetTempPath(), "wp-sup-" + Guid.NewGuid().ToString("N"));
        _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        var options = new SupervisorOptions
        {
            RunDirectory = RunDirectory,
            Jitter = jitter ?? (() => 0.5),
            OutputQueueCapacity = outputCapacity,
        };
        Janitor = new ChildProcessJanitor(RunDirectory, _loggerFactory.CreateLogger("janitor"), Clock, Inspector, TimeSpan.FromSeconds(5));
        Supervisor = new Supervisor(options, _loggerFactory, Clock, Janitor, Launcher, prober);
    }

    /// <summary>An existing absolute file, as the supervisor checks the executable before starting.</summary>
    public static string Executable { get; } = typeof(SupervisorHarness).Assembly.Location;

    public FakeTimeProvider Clock { get; }

    public LogCollector Logs { get; } = new();

    public FakeLauncher Launcher { get; } = new();

    public FakeInspector Inspector { get; } = new();

    public ChildProcessJanitor Janitor { get; }

    public Supervisor Supervisor { get; }

    public string RunDirectory { get; }

    public static ProcessSpec Spec(string name = "child", ReadinessProbe? readiness = null, TimeSpan? grace = null) => new()
    {
        Name = name,
        Executable = Executable,
        Readiness = readiness ?? ReadinessProbe.None,
        StopGracePeriod = grace ?? TimeSpan.FromSeconds(10),
    };

    /// <summary>Starts supervising without waiting for the first outcome; the recorder sees every transition.</summary>
    public (Task<ISupervisedProcessHandle> Started, StateRecorder States) Begin(ProcessSpec spec, CancellationToken ct = default)
    {
        var states = new StateRecorder();
        var started = Supervisor.StartAsync(spec, states.Attach, ct);
        return (started, states);
    }

    /// <summary>Starts a process with no readiness probe and waits until it is Running.</summary>
    public async Task<(ISupervisedProcessHandle Handle, StateRecorder States, FakeChild Child)> StartRunningAsync(ProcessSpec? spec = null)
    {
        var (started, states) = Begin(spec ?? Spec());
        var handle = await started.WaitAsync(TestTimeouts.Default);
        return (handle, states, Launcher.Children[^1]);
    }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        Janitor.Dispose();
        _loggerFactory.Dispose();
        try
        {
            Directory.Delete(RunDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // No pid file was ever written.
        }
    }
}

/// <summary>Records the <c>StateChanged</c> events of one handle, in order.</summary>
internal sealed class StateRecorder
{
    private readonly Lock _gate = new();
    private readonly List<SupervisedProcessState> _states = [];
    private readonly List<(Func<IReadOnlyList<SupervisedProcessState>, bool> Condition, TaskCompletionSource Met)> _waiters = [];

    public IReadOnlyList<SupervisedProcessState> States
    {
        get
        {
            lock (_gate)
            {
                return [.. _states];
            }
        }
    }

    /// <summary>The recorded handle (available before the first transition).</summary>
    public ISupervisedProcessHandle Handle { get; private set; } = null!;

    public void Attach(ISupervisedProcessHandle handle)
    {
        Handle = handle;
        handle.StateChanged += (_, state) => Add(state);
    }

    /// <summary>Waits until <paramref name="state"/> has been entered <paramref name="times"/> times.</summary>
    public Task WaitForAsync(SupervisedProcessState state, int times = 1) =>
        WaitForAsync(states => states.Count(s => s == state) >= times);

    public async Task WaitForAsync(Func<IReadOnlyList<SupervisedProcessState>, bool> condition)
    {
        var met = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (condition(_states))
            {
                return;
            }

            _waiters.Add((condition, met));
        }

        await met.Task.WaitAsync(TestTimeouts.Default);
    }

    private void Add(SupervisedProcessState state)
    {
        lock (_gate)
        {
            _states.Add(state);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Condition(_states))
                {
                    _waiters[i].Met.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }
}

internal sealed class FakeLauncher : IProcessLauncher
{
    private readonly Lock _gate = new();
    private readonly List<FakeChild> _children = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];
    private int _attempts;

    /// <summary>Configures each new child (attempt number from 1); may throw to simulate a start failure.</summary>
    public Action<int, FakeChild>? Configure { get; set; }

    public IReadOnlyList<FakeChild> Children
    {
        get
        {
            lock (_gate)
            {
                return [.. _children];
            }
        }
    }

    /// <summary>Start attempts, including those that threw.</summary>
    public int Attempts => Volatile.Read(ref _attempts);

    public IChildProcess Start(ProcessSpec spec)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        var child = new FakeChild(1000 + attempt);
        try
        {
            Configure?.Invoke(attempt, child);
        }
        finally
        {
            Signal(attempt);
        }

        lock (_gate)
        {
            _children.Add(child);
        }

        return child;
    }

    /// <summary>Waits until <paramref name="count"/> start attempts were made.</summary>
    public async Task WaitForAttemptsAsync(int count)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_attempts >= count)
            {
                return;
            }

            _waiters.Add((count, reached));
        }

        await reached.Task.WaitAsync(TestTimeouts.Default);
    }

    private void Signal(int attempt)
    {
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (attempt >= _waiters[i].Count)
                {
                    _waiters[i].Reached.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }
}

internal sealed class FakeChild(int pid) : IChildProcess
{
    private readonly TaskCompletionSource<ChildExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gracefulRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _killRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _gracefulStops;
    private int _kills;

    public int Pid { get; } = pid;

    public Stream? StandardOutput { get; set; }

    public Stream? StandardError { get; set; }

    public Task<ChildExit> Exited => _exited.Task;

    /// <summary>The child does not react to the graceful stop signal.</summary>
    public bool IgnoresGracefulStop { get; set; }

    /// <summary>The graceful stop signal cannot be delivered.</summary>
    public bool GracefulStopUndeliverable { get; set; }

    /// <summary>The child survives a kill.</summary>
    public bool Unkillable { get; set; }

    public int GracefulStops => Volatile.Read(ref _gracefulStops);

    public int Kills => Volatile.Read(ref _kills);

    public bool Disposed { get; private set; }

    /// <summary>Called when the graceful stop signal arrives.</summary>
    public Action? OnGracefulStop { get; set; }

    public Task GracefulRequested => _gracefulRequested.Task.WaitAsync(TestTimeouts.Default);

    public Task KillRequested => _killRequested.Task.WaitAsync(TestTimeouts.Default);

    public void Exit(int code) => _exited.TrySetResult(new ChildExit(code));

    public bool RequestGracefulStop()
    {
        Interlocked.Increment(ref _gracefulStops);
        OnGracefulStop?.Invoke();
        if (!GracefulStopUndeliverable && !IgnoresGracefulStop)
        {
            Exit(0);
        }

        _gracefulRequested.TrySetResult();
        return !GracefulStopUndeliverable;
    }

    public void Kill()
    {
        Interlocked.Increment(ref _kills);
        if (!Unkillable)
        {
            _exited.TrySetResult(new ChildExit(137, 9));
        }

        _killRequested.TrySetResult();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A prober completed by the test: one pending probe per start attempt.</summary>
internal sealed class ManualProber : IReadinessProber
{
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource<bool>> _probes = [];
    private readonly List<(int Count, TaskCompletionSource Reached)> _waiters = [];

    public int Probes
    {
        get
        {
            lock (_gate)
            {
                return _probes.Count;
            }
        }
    }

    public Task<bool> WaitReadyAsync(ReadinessProbe probe, ChildOutputPump output, CancellationToken ct)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => pending.TrySetCanceled(ct));
        lock (_gate)
        {
            _probes.Add(pending);
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_probes.Count >= _waiters[i].Count)
                {
                    _waiters[i].Reached.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }

        return pending.Task;
    }

    /// <summary>Waits until the probe of start attempt <paramref name="attempt"/> runs: the child is then Starting.</summary>
    public async Task WaitForProbeAsync(int attempt)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_probes.Count >= attempt)
            {
                return;
            }

            _waiters.Add((attempt, reached));
        }

        await reached.Task.WaitAsync(TestTimeouts.Default);
    }

    public void Complete(int attempt, bool ready)
    {
        lock (_gate)
        {
            _probes[attempt - 1].TrySetResult(ready);
        }
    }
}

internal sealed class FakeInspector : IProcessInspector
{
    private readonly Dictionary<int, string> _running = [];

    public List<int> Killed { get; } = [];

    public void Run(int pid, string executable) => _running[pid] = executable;

    public string? GetExecutablePath(int pid) => _running.GetValueOrDefault(pid);

    public bool IsAlive(int pid) => _running.ContainsKey(pid);

    public void Kill(int pid)
    {
        Killed.Add(pid);
        _running.Remove(pid);
    }
}

/// <summary>Wraps a prober owned by the test (the harness does not dispose it).</summary>
internal sealed class ClockedProber(IReadinessProber inner) : IReadinessProber
{
    public Task<bool> WaitReadyAsync(ReadinessProbe probe, ChildOutputPump output, CancellationToken ct) => inner.WaitReadyAsync(probe, output, ct);
}
