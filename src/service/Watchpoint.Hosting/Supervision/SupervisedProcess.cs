using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// State machine of one supervised process (spec HST §7.2). One loop (<see cref="RunAsync"/>) owns the state and
/// performs every transition, so transitions are neither lost nor doubled; <see cref="StopAsync"/> and
/// <see cref="RestartAsync"/> only record a request under <see cref="_gate"/> and wake the loop. Timers are armed
/// before the state that waits on them is published, so an observer that sees the state can advance a fake clock.
/// </summary>
internal sealed class SupervisedProcess : ISupervisedProcessHandle
{
    private readonly ProcessSpec _spec;
    private readonly SupervisorRuntime _runtime;
    private readonly ILogger _logger;
    private readonly Action<SupervisedProcess> _onStopped;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _restartTimes = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _killNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstOutcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _restartRequest;
    private bool _restartPending;
    private bool _stopRequested;
    private bool _announced;
    private SupervisedProcessState _state = SupervisedProcessState.Starting;
    private IChildProcess? _child;
    private int? _pid;
    private int _restartCount;
    private DateTimeOffset? _lastStartedAt;
    private string? _lastExitReason;

    public SupervisedProcess(ProcessSpec spec, SupervisorRuntime runtime, Action<SupervisedProcess> onStopped)
    {
        _spec = spec;
        _runtime = runtime;
        _logger = runtime.Logger;
        _onStopped = onStopped;
    }

    public event EventHandler<SupervisedProcessState>? StateChanged;

    private enum Command
    {
        None,
        Stop,
        Restart,
    }

    private enum StartOutcome
    {
        Ready,
        Exited,
        ProbeTimeout,
        Stop,
        Restart,
    }

    public string Name => _spec.Name;

    public SupervisedProcessState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public int? Pid
    {
        get
        {
            lock (_gate)
            {
                return _pid;
            }
        }
    }

    public int RestartCount
    {
        get
        {
            lock (_gate)
            {
                return _restartCount;
            }
        }
    }

    public DateTimeOffset? LastStartedAt
    {
        get
        {
            lock (_gate)
            {
                return _lastStartedAt;
            }
        }
    }

    public string? LastExitReason
    {
        get
        {
            lock (_gate)
            {
                return _lastExitReason;
            }
        }
    }

    /// <summary>Completes when the process is Running for the first time, Failed or Stopped.</summary>
    public Task FirstOutcome => _firstOutcome.Task;

    /// <summary>Starts the supervision loop.</summary>
    public void Start() => _ = Task.Run(RunAsync);

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _stopRequested = true;
            _wake.TrySetResult();
        }

        // Cancellation does not abandon the child: it skips the grace period, and the kill is bounded by KillTimeout.
        using var registration = ct.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), _killNow);
        await _stopped.Task.ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        TaskCompletionSource request;
        lock (_gate)
        {
            if (_stopRequested || _state == SupervisedProcessState.Stopped)
            {
                throw new SupervisorException(HostErrors.ProcessNotFound(Name), $"Process '{Name}' is stopped and no longer supervised.");
            }

            if (_restartRequest is null)
            {
                // Calls made while a restart is in progress join it instead of queueing a second one.
                _restartRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _restartPending = true;
                _wake.TrySetResult();
            }

            request = _restartRequest;
        }

        await request.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Kills the child now, without waiting (process exit hook).</summary>
    public void KillNow()
    {
        IChildProcess? child;
        lock (_gate)
        {
            child = _child;
        }

        try
        {
            child?.Kill();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Best effort while the process is exiting.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await RunOnceAsync().ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A bug in the loop must not leave a child behind or a caller waiting.
            HostingLog.LogSupervisorLoopFailed(_logger, Name, ex);
            KillNow();
            lock (_gate)
            {
                _lastExitReason = "supervisor error: " + ex.GetType().Name;
            }

            _runtime.Janitor.RemovePidFile(Name);
        }

        Finish();
    }

    /// <summary>One start attempt and what follows it. Returns <see langword="false"/> when the process is stopped for good.</summary>
    private async Task<bool> RunOnceAsync()
    {
        SetState(SupervisedProcessState.Starting);
        string failure;
        var ranFor = TimeSpan.Zero;
        IChildProcess child;
        try
        {
            child = _runtime.Launcher.Start(_spec);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await HandleFailureAsync("start failed: " + ex.Message, null, TimeSpan.Zero).ConfigureAwait(false);
        }

        using var pump = new ChildOutputPump(
            Name, _runtime.LoggerFactory.CreateLogger(_spec.LogCategory), _logger, _spec.StandardErrorLevel, _runtime.Options.OutputQueueCapacity);
        ChildExit? exit = null;
        try
        {
            lock (_gate)
            {
                _child = child;
                _pid = child.Pid;
                _lastStartedAt = _runtime.TimeProvider.GetUtcNow();
            }

            _runtime.Janitor.WritePidFile(Name, child.Pid, _spec.Executable);
            HostingLog.LogChildStarted(_logger, Name, child.Pid);
            pump.Start(child.StandardOutput, child.StandardError);

            switch (await WaitReadyAsync(child, pump).ConfigureAwait(false))
            {
                case StartOutcome.Ready:
                    var runningSince = _runtime.TimeProvider.GetTimestamp();
                    SetState(SupervisedProcessState.Running);
                    CompleteRestartRequest(null);
                    _firstOutcome.TrySetResult();
                    var command = await WaitWhileRunningAsync(child).ConfigureAwait(false);
                    if (command == Command.None)
                    {
                        exit = await child.Exited.ConfigureAwait(false);
                        ranFor = _runtime.TimeProvider.GetElapsedTime(runningSince);
                        failure = exit.Describe();
                        break;
                    }

                    SetState(SupervisedProcessState.Stopping);
                    await TerminateAsync(child).ConfigureAwait(false);
                    return ContinueAfterStop(command);

                case StartOutcome.Exited:
                    exit = await child.Exited.ConfigureAwait(false);
                    failure = exit.Describe() + " before it was ready";
                    break;

                case StartOutcome.ProbeTimeout:
                    await TerminateAsync(child).ConfigureAwait(false);
                    failure = "readiness probe timed out";
                    break;

                case StartOutcome.Stop:
                    SetState(SupervisedProcessState.Stopping);
                    await TerminateAsync(child).ConfigureAwait(false);
                    return ContinueAfterStop(Command.Stop);

                default:
                    // Restart requested while starting: replace this attempt without counting a failure.
                    SetState(SupervisedProcessState.Stopping);
                    await TerminateAsync(child).ConfigureAwait(false);
                    return ContinueAfterStop(Command.Restart);
            }
        }
        finally
        {
            await ReleaseChildAsync(child, pump).ConfigureAwait(false);
        }

        return await HandleFailureAsync(failure, exit, ranFor).ConfigureAwait(false);
    }

    private bool ContinueAfterStop(Command command)
    {
        if (command == Command.Stop)
        {
            return false;
        }

        ResetRestarts();
        return true;
    }

    /// <summary>Unexpected exit, failed start or probe timeout: Backoff then a new attempt, or Failed.</summary>
    private async Task<bool> HandleFailureAsync(string reason, ChildExit? exit, TimeSpan ranFor)
    {
        var policy = _spec.Restart;
        var now = _runtime.TimeProvider.GetUtcNow();
        int restart;
        bool failed;
        lock (_gate)
        {
            _lastExitReason = reason;
            if (ranFor > policy.Window)
            {
                _restartCount = 0;
                _restartTimes.Clear();
            }

            _restartTimes.Enqueue(now);
            while (_restartTimes.Count > 0 && now - _restartTimes.Peek() > policy.Window)
            {
                _restartTimes.Dequeue();
            }

            // RestartCount counts restarts performed: it does not grow when this exit makes the child Failed.
            failed = _restartTimes.Count > policy.MaxRestartsInWindow;
            restart = failed ? _restartCount : ++_restartCount;
        }

        if (failed)
        {
            HostingLog.LogChildFailed(_logger, Name, restart, policy.Window);
            // Failed is entered through Backoff (spec HST §7.2), without a delay.
            SetState(SupervisedProcessState.Backoff);

            // Before Failed is published: a restart requested once Failed is visible is a new request.
            CompleteRestartRequest(new SupervisorException(HostErrors.ProcessFailed(Name), $"Process '{Name}' failed: {reason}."));
            SetState(SupervisedProcessState.Failed);
            _firstOutcome.TrySetResult();
            if (await WaitForCommandAsync(null).ConfigureAwait(false) == Command.Stop)
            {
                return false;
            }

            ResetRestarts();
            return true;
        }

        var delay = policy.ComputeDelay(restart, Math.Clamp(_runtime.Options.Jitter(), 0.0, 1.0));
        using var delayCts = new CancellationTokenSource();
        var delayTask = Task.Delay(delay, _runtime.TimeProvider, delayCts.Token);
        _runtime.Restarts.Add(1, new KeyValuePair<string, object?>("child", Name));
        HostingLog.LogChildExitedUnexpectedly(_logger, Name, reason, exit?.ExitCode, restart, (long)delay.TotalMilliseconds);
        SetState(SupervisedProcessState.Backoff);
        var command = await WaitForCommandAsync(delayTask).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
        switch (command)
        {
            case Command.Stop:
                return false;
            case Command.Restart:
                ResetRestarts();
                return true;
            default:
                return true;
        }
    }

    private async Task<StartOutcome> WaitReadyAsync(IChildProcess child, ChildOutputPump pump)
    {
        using var probeCts = new CancellationTokenSource();
        var probe = ProbeAsync(pump, probeCts.Token);
        try
        {
            while (true)
            {
                Task wake;
                lock (_gate)
                {
                    wake = _wake.Task;
                }

                await Task.WhenAny(child.Exited, probe, wake).ConfigureAwait(false);
                switch (TakeCommand())
                {
                    case Command.Stop:
                        return StartOutcome.Stop;
                    case Command.Restart:
                        return StartOutcome.Restart;
                }

                if (child.Exited.IsCompleted)
                {
                    return StartOutcome.Exited;
                }

                if (probe.IsCompleted)
                {
                    return await probe.ConfigureAwait(false) ? StartOutcome.Ready : StartOutcome.ProbeTimeout;
                }
            }
        }
        finally
        {
            await probeCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> ProbeAsync(ChildOutputPump pump, CancellationToken ct)
    {
        try
        {
            return await _runtime.Prober.WaitReadyAsync(_spec.Readiness, pump, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cancelled (the outcome is then decided by the exit or the command) or a failing probe: not ready.
            return false;
        }
    }

    /// <summary>Returns the command that ends Running, or <see cref="Command.None"/> when the child exited.</summary>
    private async Task<Command> WaitWhileRunningAsync(IChildProcess child)
    {
        while (true)
        {
            Task wake;
            lock (_gate)
            {
                wake = _wake.Task;
            }

            await Task.WhenAny(child.Exited, wake).ConfigureAwait(false);
            var command = TakeCommand();
            if (command != Command.None || child.Exited.IsCompleted)
            {
                return command;
            }
        }
    }

    /// <summary>Waits for a stop or restart request, or for <paramref name="elapsed"/> (returns <see cref="Command.None"/>).</summary>
    private async Task<Command> WaitForCommandAsync(Task? elapsed)
    {
        while (true)
        {
            Task wake;
            lock (_gate)
            {
                wake = _wake.Task;
            }

            await (elapsed is null ? wake : Task.WhenAny(elapsed, wake)).ConfigureAwait(false);
            var command = TakeCommand();
            if (command != Command.None || elapsed is { IsCompleted: true })
            {
                return command;
            }
        }
    }

    /// <summary>Takes the pending request (stop wins over restart) or re-arms the wake signal.</summary>
    private Command TakeCommand()
    {
        lock (_gate)
        {
            if (_stopRequested)
            {
                return Command.Stop;
            }

            if (_restartPending)
            {
                _restartPending = false;
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return Command.Restart;
            }

            if (_wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return Command.None;
        }
    }

    /// <summary>Graceful stop signal, kill after the grace period (or at once when asked), bounded wait for the exit.</summary>
    private async Task TerminateAsync(IChildProcess child)
    {
        if (!child.Exited.IsCompleted)
        {
            using var graceCts = new CancellationTokenSource();
            var grace = Task.Delay(_spec.StopGracePeriod, _runtime.TimeProvider, graceCts.Token);
            bool delivered;
            try
            {
                delivered = !_killNow.Task.IsCompleted && child.RequestGracefulStop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                delivered = false;
            }

            if (delivered)
            {
                await Task.WhenAny(child.Exited, grace, _killNow.Task).ConfigureAwait(false);
            }

            await graceCts.CancelAsync().ConfigureAwait(false);
        }

        if (!child.Exited.IsCompleted)
        {
            using var killCts = new CancellationTokenSource();
            var killTimeout = Task.Delay(_runtime.Options.KillTimeout, _runtime.TimeProvider, killCts.Token);
            try
            {
                child.Kill();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HostingLog.LogChildKillFailed(_logger, Name, child.Pid, ex);
            }

            await Task.WhenAny(child.Exited, killTimeout).ConfigureAwait(false);
            await killCts.CancelAsync().ConfigureAwait(false);
            if (!child.Exited.IsCompleted)
            {
                HostingLog.LogChildKillFailed(_logger, Name, child.Pid, null);
            }
        }

        lock (_gate)
        {
            _lastExitReason = child.Exited.IsCompleted ? "stopped" : "stop requested; the process could not be killed";
        }
    }

    private async Task ReleaseChildAsync(IChildProcess child, ChildOutputPump pump)
    {
        if (!child.Exited.IsCompleted)
        {
            // Only reached when the loop failed: never leave the child running.
            try
            {
                child.Kill();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HostingLog.LogChildKillFailed(_logger, Name, child.Pid, ex);
            }
        }

        await pump.CompleteAsync(_runtime.Options.OutputDrainTimeout, _runtime.TimeProvider).ConfigureAwait(false);
        _runtime.Janitor.RemovePidFile(Name);
        lock (_gate)
        {
            _child = null;
            _pid = null;
        }

        try
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing to release.
        }
    }

    private void ResetRestarts()
    {
        lock (_gate)
        {
            _restartCount = 0;
            _restartTimes.Clear();
        }
    }

    private void CompleteRestartRequest(Exception? error)
    {
        TaskCompletionSource? request;
        lock (_gate)
        {
            if (error is null && _restartPending)
            {
                // Requested after the probe succeeded and not taken yet: this Running does not answer it.
                return;
            }

            request = _restartRequest;
            _restartRequest = null;
            _restartPending = false;
        }

        if (error is null)
        {
            request?.TrySetResult();
        }
        else
        {
            request?.TrySetException(error);
        }
    }

    private void Finish()
    {
        SetState(SupervisedProcessState.Stopped);
        HostingLog.LogChildStopped(_logger, Name);
        CompleteRestartRequest(new SupervisorException(HostErrors.ProcessNotFound(Name), $"Process '{Name}' was stopped."));
        _onStopped(this);
        _firstOutcome.TrySetResult();
        _stopped.TrySetResult();
    }

    private void SetState(SupervisedProcessState state)
    {
        lock (_gate)
        {
            if (_announced && _state == state)
            {
                return;
            }

            _announced = true;
            _state = state;
        }

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HostingLog.LogStateHandlerFailed(_logger, Name, ex);
        }
    }
}

/// <summary>What every <see cref="SupervisedProcess"/> of a <see cref="Supervisor"/> shares.</summary>
internal sealed record SupervisorRuntime(
    IProcessLauncher Launcher,
    IReadinessProber Prober,
    ChildProcessJanitor Janitor,
    TimeProvider TimeProvider,
    SupervisorOptions Options,
    ILogger Logger,
    ILoggerFactory LoggerFactory,
    Counter<long> Restarts);
