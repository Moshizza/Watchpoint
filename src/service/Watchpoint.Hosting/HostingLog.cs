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

    /// <summary>1155: Child output lines dropped because the logger was slower than the child.</summary>
    public static readonly EventId ChildOutputDropped = new(1155, nameof(ChildOutputDropped));

    /// <summary>1156: A line of child output.</summary>
    public static readonly EventId ChildOutput = new(1156, nameof(ChildOutput));

    /// <summary>1157: A child could not be killed.</summary>
    public static readonly EventId ChildKillFailed = new(1157, nameof(ChildKillFailed));

    /// <summary>1158: A pid file could not be read, written or removed.</summary>
    public static readonly EventId PidFileFailed = new(1158, nameof(PidFileFailed));

    /// <summary>1159: The supervision of a child stopped on an internal error.</summary>
    public static readonly EventId SupervisorLoopFailed = new(1159, nameof(SupervisorLoopFailed));

    /// <summary>1160: A <c>StateChanged</c> handler threw.</summary>
    public static readonly EventId StateHandlerFailed = new(1160, nameof(StateHandlerFailed));

    [LoggerMessage(EventId = 1101, Level = LogLevel.Critical, Message = "Invalid node settings: {Problems}")]
    public static partial void LogInvalidNodeSettings(ILogger logger, string problems);

    [LoggerMessage(EventId = 1102, Message = "Node {NodeId} ({NodeName}) starting, version {Version}, environment {Environment}")]
    public static partial void LogNodeStarting(ILogger logger, LogLevel level, Guid nodeId, string nodeName, string version, NodeEnvironment environment);

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

    [LoggerMessage(EventId = 1150, Level = LogLevel.Information, Message = "Child {Name} started with pid {Pid}")]
    public static partial void LogChildStarted(ILogger logger, string name, int pid);

    [LoggerMessage(EventId = 1151, Level = LogLevel.Warning, Message = "Child {Name} exited unexpectedly ({Reason}) with code {ExitCode}; restart {Attempt} in {DelayMs} ms")]
    public static partial void LogChildExitedUnexpectedly(ILogger logger, string name, string reason, int? exitCode, int attempt, long delayMs);

    [LoggerMessage(EventId = 1152, Level = LogLevel.Error, Message = "Child {Name} failed: {Restarts} restarts within {Window}")]
    public static partial void LogChildFailed(ILogger logger, string name, int restarts, TimeSpan window);

    [LoggerMessage(EventId = 1153, Level = LogLevel.Information, Message = "Child {Name} stopped")]
    public static partial void LogChildStopped(ILogger logger, string name);

    [LoggerMessage(EventId = 1154, Level = LogLevel.Warning, Message = "Killed leftover process {Pid} for child {Name}")]
    public static partial void LogChildKilledLeftover(ILogger logger, int pid, string name);

    [LoggerMessage(EventId = 1155, Level = LogLevel.Warning, Message = "Child {Name} output: {Dropped} lines dropped (logging slower than the child)")]
    public static partial void LogChildOutputDropped(ILogger logger, string name, long dropped);

    [LoggerMessage(EventId = 1156, Message = "{Child}: {Line}")]
    public static partial void LogChildOutput(ILogger logger, LogLevel level, string child, string line);

    [LoggerMessage(EventId = 1157, Level = LogLevel.Error, Message = "Child {Name} (pid {Pid}) could not be killed")]
    public static partial void LogChildKillFailed(ILogger logger, string name, int pid, Exception? exception);

    [LoggerMessage(EventId = 1158, Level = LogLevel.Warning, Message = "Pid file of child {Name} could not be read, written or removed")]
    public static partial void LogPidFileFailed(ILogger logger, string name, Exception exception);

    [LoggerMessage(EventId = 1159, Level = LogLevel.Critical, Message = "Supervision of child {Name} stopped on an internal error")]
    public static partial void LogSupervisorLoopFailed(ILogger logger, string name, Exception exception);

    [LoggerMessage(EventId = 1160, Level = LogLevel.Error, Message = "StateChanged handler of child {Name} failed")]
    public static partial void LogStateHandlerFailed(ILogger logger, string name, Exception exception);
}
