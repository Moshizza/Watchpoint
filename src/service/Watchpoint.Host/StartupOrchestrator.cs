using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Watchpoint.Hosting;

namespace Watchpoint.Host;

/// <summary>
/// Startup orchestrator executing startup tasks by phase sequentially, and shutdown tasks in reverse order (spec HST §5.3).
/// </summary>
public sealed class StartupOrchestrator : IHostedLifecycleService
{
    private static readonly Meter HostMeter = new("Watchpoint.Host");
    private static readonly Histogram<double> StartupDuration = HostMeter.CreateHistogram<double>(
        "watchpoint.host.startup.duration",
        "ms",
        "Duration of startup tasks");

    private readonly IEnumerable<IStartupTask> _startupTasks;
    private readonly IEnumerable<IShutdownTask> _shutdownTasks;
    private readonly INodeReadiness _readiness;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly NodeSettings _settings;
    private readonly ILogger<StartupOrchestrator> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Exit code used on startup task failure (EX_SOFTWARE).</summary>
    public const int ExitCodeStartupFailure = 70;

    /// <summary>Exit code used when required dependency stayed unreachable (EX_TEMPFAIL).</summary>
    public const int ExitCodeTempFail = 75;

    /// <summary>Initializes a new instance of the <see cref="StartupOrchestrator"/> class.</summary>
    public StartupOrchestrator(
        IEnumerable<IStartupTask> startupTasks,
        IEnumerable<IShutdownTask> shutdownTasks,
        INodeReadiness readiness,
        IHostApplicationLifetime lifetime,
        IOptions<NodeSettings> settings,
        ILogger<StartupOrchestrator> logger,
        TimeProvider? timeProvider = null)
    {
        _startupTasks = startupTasks;
        _shutdownTasks = shutdownTasks;
        _readiness = readiness;
        _lifetime = lifetime;
        _settings = settings.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        // Execute startup tasks in background after Kestrel has started listening
        _ = Task.Run(() => RunStartupTasksAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>Runs all startup tasks sequentially across phases.</summary>
    public async Task RunStartupTasksAsync(CancellationToken cancellationToken)
    {
        var orchestratorStart = _timeProvider.GetTimestamp();

        var phases = _startupTasks
            .GroupBy(t => t.Phase)
            .OrderBy(g => (int)g.Key);

        foreach (var phaseGroup in phases)
        {
            var orderedTasks = phaseGroup.OrderBy(t => t.Order).ToList();
            foreach (var task in orderedTasks)
            {
                HostingLog.LogStartupTaskStarting(_logger, task.Name, task.Phase);
                var taskStart = _timeProvider.GetTimestamp();

                try
                {
                    await task.ExecuteAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    HostingLog.LogStartupTaskFailed(_logger, task.Name, ex);
                    if (Environment.ExitCode == 0)
                    {
                        Environment.ExitCode = ExitCodeStartupFailure;
                    }
                    _lifetime.StopApplication();
                    return;
                }

                var durationMs = (long)_timeProvider.GetElapsedTime(taskStart).TotalMilliseconds;
                StartupDuration.Record(durationMs, new KeyValuePair<string, object?>("task", task.Name));
                HostingLog.LogStartupTaskCompleted(_logger, task.Name, durationMs);
            }

            if (phaseGroup.Key == StartupPhase.Edge)
            {
                _readiness.SetReady();
                var totalDurationMs = (long)_timeProvider.GetElapsedTime(orchestratorStart).TotalMilliseconds;
                HostingLog.LogNodeReady(_logger, totalDurationMs);
                NotifySystemdReady();
            }
            else if ((int)phaseGroup.Key > (int)StartupPhase.Edge && !_readiness.IsReady)
            {
                // Ensure readiness is marked before running phases > Edge (e.g. Connections)
                _readiness.SetReady();
                var totalDurationMs = (long)_timeProvider.GetElapsedTime(orchestratorStart).TotalMilliseconds;
                HostingLog.LogNodeReady(_logger, totalDurationMs);
                NotifySystemdReady();
            }
        }

        // If no tasks ran up to Edge phase, or if tasks completed without throwing, ensure readiness if Edge passed
        if (!_readiness.IsReady)
        {
            _readiness.SetReady();
            var totalDurationMs = (long)_timeProvider.GetElapsedTime(orchestratorStart).TotalMilliseconds;
            HostingLog.LogNodeReady(_logger, totalDurationMs);
            NotifySystemdReady();
        }
    }

    private static void NotifySystemdReady() => NotifySystemd(Microsoft.Extensions.Hosting.Systemd.ServiceState.Ready);

    private static void NotifySystemd(Microsoft.Extensions.Hosting.Systemd.ServiceState state)
    {
        try
        {
            var notifier = new Microsoft.Extensions.Hosting.Systemd.SystemdNotifier();
            if (notifier.IsEnabled)
            {
                notifier.Notify(state);
            }
        }
        catch
        {
            // Ignore systemd notify exceptions outside Linux
        }
    }

    /// <inheritdoc />
    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        _readiness.SetDraining();
        NotifySystemd(Microsoft.Extensions.Hosting.Systemd.ServiceState.Stopping);

        var timeout = _settings.Shutdown.TimeoutDuration;
        var startTimestamp = _timeProvider.GetTimestamp();

        var phases = _shutdownTasks
            .GroupBy(t => t.Phase)
            .OrderByDescending(g => (int)g.Key);

        foreach (var phaseGroup in phases)
        {
            foreach (var task in phaseGroup)
            {
                var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                var remaining = timeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    HostingLog.LogShutdownTaskAbandoned(_logger, task.Name);
                    continue;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(remaining);

                var taskStart = _timeProvider.GetTimestamp();
                try
                {
                    await task.ExecuteAsync(cts.Token);
                    var durationMs = (long)_timeProvider.GetElapsedTime(taskStart).TotalMilliseconds;
                    HostingLog.LogShutdownTaskCompleted(_logger, task.Name, durationMs);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    HostingLog.LogShutdownTaskAbandoned(_logger, task.Name);
                }
                catch (Exception ex)
                {
                    HostingLog.LogShutdownTaskFailed(_logger, task.Name, ex);
                }
            }
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
