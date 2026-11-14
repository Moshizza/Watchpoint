namespace Watchpoint.Hosting.Supervision;

/// <summary>State of a supervised process (spec HST §7.2).</summary>
public enum SupervisedProcessState
{
    /// <summary>The process is being started and its readiness probe has not succeeded yet.</summary>
    Starting,

    /// <summary>The readiness probe succeeded.</summary>
    Running,

    /// <summary>The process exited unexpectedly or did not become ready; a restart is scheduled.</summary>
    Backoff,

    /// <summary>Too many restarts within the window; only <see cref="ISupervisedProcessHandle.RestartAsync"/> starts it again.</summary>
    Failed,

    /// <summary>A stop was requested; the process is being stopped (graceful, then killed).</summary>
    Stopping,

    /// <summary>The process is stopped for good; the handle is no longer supervised.</summary>
    Stopped,
}
