using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting;

/// <summary>
/// Event IDs and structured logging definitions for the Host module (spec HST §11).
/// </summary>
public static partial class HostingLog
{
    /// <summary>1101: Invalid node settings.</summary>
    public static readonly EventId InvalidNodeSettings = new(1101, nameof(InvalidNodeSettings));

    /// <summary>1102: Node starting.</summary>
    public static readonly EventId NodeStarting = new(1102, nameof(NodeStarting));

    /// <summary>1103: Privileged process refused in Production.</summary>
    public static readonly EventId ElevatedRefused = new(1103, nameof(ElevatedRefused));

    /// <summary>1104: Privileged process allowed in Production by <c>--allow-elevated</c>.</summary>
    public static readonly EventId ElevatedAllowed = new(1104, nameof(ElevatedAllowed));

    /// <summary>1105: The generated node id could not be written to <c>watchpoint.json</c>.</summary>
    public static readonly EventId NodeIdPersistFailed = new(1105, nameof(NodeIdPersistFailed));

    /// <summary>1110: Startup task starting.</summary>
    public static readonly EventId StartupTaskStarting = new(1110, nameof(StartupTaskStarting));

    /// <summary>1111: Startup task completed.</summary>
    public static readonly EventId StartupTaskCompleted = new(1111, nameof(StartupTaskCompleted));

    /// <summary>1112: Startup task failed.</summary>
    public static readonly EventId StartupTaskFailed = new(1112, nameof(StartupTaskFailed));

    /// <summary>1120: Node ready.</summary>
    public static readonly EventId NodeReady = new(1120, nameof(NodeReady));

    /// <summary>1121: systemd watchdog enabled.</summary>
    public static readonly EventId SystemdWatchdogEnabled = new(1121, nameof(SystemdWatchdogEnabled));

    /// <summary>1130: Shutdown task completed.</summary>
    public static readonly EventId ShutdownTaskCompleted = new(1130, nameof(ShutdownTaskCompleted));

    /// <summary>1131: Shutdown task abandoned after timeout.</summary>
    public static readonly EventId ShutdownTaskAbandoned = new(1131, nameof(ShutdownTaskAbandoned));

    /// <summary>1132: Shutdown task failed with exception.</summary>
    public static readonly EventId ShutdownTaskFailed = new(1132, nameof(ShutdownTaskFailed));

    /// <summary>1140: Development seed failed.</summary>
    public static readonly EventId DevSeedFailed = new(1140, nameof(DevSeedFailed));

    /// <summary>1150: Child process started.</summary>
    public static readonly EventId ChildStarted = new(1150, nameof(ChildStarted));

    /// <summary>1151: Child process exited unexpectedly.</summary>
    public static readonly EventId ChildExitedUnexpectedly = new(1151, nameof(ChildExitedUnexpectedly));

    /// <summary>1152: Child process failed.</summary>
    public static readonly EventId ChildFailed = new(1152, nameof(ChildFailed));

    /// <summary>1153: Child process stopped.</summary>
    public static readonly EventId ChildStopped = new(1153, nameof(ChildStopped));

    /// <summary>1154: Killed leftover child process.</summary>
    public static readonly EventId ChildKilledLeftover = new(1154, nameof(ChildKilledLeftover));

    [LoggerMessage(EventId = 1101, Level = LogLevel.Critical, Message = "Invalid node settings: {Problems}")]
    public static partial void LogInvalidNodeSettings(ILogger logger, string problems);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "Node {NodeId} ({NodeName}) starting, version {Version}, environment {Environment}")]
    public static partial void LogNodeStarting(ILogger logger, Guid nodeId, string nodeName, string version, NodeEnvironment environment);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Critical, Message = "Refusing to start as a privileged process (root/Administrator) in Production without --allow-elevated")]
    public static partial void LogElevatedRefused(ILogger logger);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning, Message = "Running as a privileged process (root/Administrator) in Production (--allow-elevated)")]
    public static partial void LogElevatedAllowed(ILogger logger);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning, Message = "Could not persist Node:Id to {Path}")]
    public static partial void LogNodeIdPersistFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Information, Message = "Startup task {Task} (phase {Phase}) starting")]
    public static partial void LogStartupTaskStarting(ILogger logger, string task, StartupPhase phase);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Information, Message = "Startup task {Task} completed in {DurationMs} ms")]
    public static partial void LogStartupTaskCompleted(ILogger logger, string task, long durationMs);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Critical, Message = "Startup task {Task} failed")]
    public static partial void LogStartupTaskFailed(ILogger logger, string task, Exception exception);

    [LoggerMessage(EventId = 1120, Level = LogLevel.Information, Message = "Node ready in {DurationMs} ms")]
    public static partial void LogNodeReady(ILogger logger, long durationMs);

    [LoggerMessage(EventId = 1121, Level = LogLevel.Information, Message = "systemd watchdog enabled, ping every {IntervalMs} ms")]
    public static partial void LogSystemdWatchdogEnabled(ILogger logger, long intervalMs);

    [LoggerMessage(EventId = 1130, Level = LogLevel.Information, Message = "Shutdown task {Task} completed in {DurationMs} ms")]
    public static partial void LogShutdownTaskCompleted(ILogger logger, string task, long durationMs);

    [LoggerMessage(EventId = 1131, Level = LogLevel.Warning, Message = "Shutdown task {Task} abandoned after timeout")]
    public static partial void LogShutdownTaskAbandoned(ILogger logger, string task);

    [LoggerMessage(EventId = 1132, Level = LogLevel.Error, Message = "Shutdown task {Task} failed with exception")]
    public static partial void LogShutdownTaskFailed(ILogger logger, string task, Exception exception);

    [LoggerMessage(EventId = 1140, Level = LogLevel.Error, Message = "Development seed failed at {Step}: {Error}")]
    public static partial void LogDevSeedFailed(ILogger logger, string step, string error);
}
