using Microsoft.Extensions.Hosting.Systemd;
using Watchpoint.Hosting;

namespace Watchpoint.Host;

/// <summary>
/// Sends <c>WATCHDOG=1</c> to systemd every <c>WATCHDOG_USEC / 2</c> when the service manager
/// enabled the watchdog for this process (spec HST §6.1).
/// </summary>
/// <remarks>
/// Enabled only when <c>NOTIFY_SOCKET</c> and a positive <c>WATCHDOG_USEC</c> are set and, if
/// <c>WATCHDOG_PID</c> is present, it equals the current process id. The first ping is sent
/// immediately: systemd arms the watchdog when the process starts, not after <c>READY=1</c>.
/// </remarks>
public sealed class SystemdWatchdog : BackgroundService
{
    /// <summary>The notification sent on every ping.</summary>
    public static readonly ServiceState WatchdogState = new("WATCHDOG=1");

    private readonly ISystemdNotifier _notifier;
    private readonly ILogger<SystemdWatchdog> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly int _processId;

    /// <summary>Initializes a new instance of the <see cref="SystemdWatchdog"/> class.</summary>
    public SystemdWatchdog(ISystemdNotifier notifier, ILogger<SystemdWatchdog> logger, TimeProvider? timeProvider = null)
        : this(notifier, logger, timeProvider ?? TimeProvider.System, Environment.GetEnvironmentVariable, Environment.ProcessId)
    {
    }

    /// <summary>Initializes a new instance with an explicit environment and process id (tests).</summary>
    public SystemdWatchdog(
        ISystemdNotifier notifier,
        ILogger<SystemdWatchdog> logger,
        TimeProvider timeProvider,
        Func<string, string?> getEnvironmentVariable,
        int processId)
    {
        _notifier = notifier;
        _logger = logger;
        _timeProvider = timeProvider;
        _getEnvironmentVariable = getEnvironmentVariable;
        _processId = processId;
    }

    /// <summary>
    /// Gets the ping interval (<c>WATCHDOG_USEC / 2</c>), or <see langword="null"/> when the
    /// watchdog is not enabled for this process.
    /// </summary>
    public TimeSpan? GetInterval()
    {
        if (string.IsNullOrEmpty(_getEnvironmentVariable("NOTIFY_SOCKET")))
        {
            return null;
        }

        if (!long.TryParse(_getEnvironmentVariable("WATCHDOG_USEC"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var usec) || usec <= 0)
        {
            return null;
        }

        var pid = _getEnvironmentVariable("WATCHDOG_PID");
        if (!string.IsNullOrEmpty(pid)
            && (!int.TryParse(pid, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var watchdogPid) || watchdogPid != _processId))
        {
            return null;
        }

        // 1 µs = 10 ticks; keep at least 1 ms so the timer period is valid.
        var ticks = Math.Max(usec / 2 * 10, TimeSpan.TicksPerMillisecond);
        return TimeSpan.FromTicks(ticks);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (GetInterval() is not { } interval || !_notifier.IsEnabled)
        {
            return;
        }

        HostingLog.LogSystemdWatchdogEnabled(_logger, (long)interval.TotalMilliseconds);

        using var timer = new PeriodicTimer(interval, _timeProvider);
        do
        {
            Ping();
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Ping()
    {
        try
        {
            _notifier.Notify(WatchdogState);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or PlatformNotSupportedException)
        {
            // A missed ping is reported by systemd itself; never crash the host for it.
        }
    }
}
