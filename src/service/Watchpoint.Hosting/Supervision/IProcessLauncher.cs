namespace Watchpoint.Hosting.Supervision;

/// <summary>Starts operating-system processes for the supervisor (replaced by a fake in unit tests).</summary>
internal interface IProcessLauncher
{
    /// <summary>Starts the process described by <paramref name="spec"/>; throws when it cannot be started.</summary>
    IChildProcess Start(ProcessSpec spec);
}

/// <summary>A started child process.</summary>
internal interface IChildProcess : IAsyncDisposable
{
    int Pid { get; }

    /// <summary>The child's stdout, or <see langword="null"/> when not captured.</summary>
    Stream? StandardOutput { get; }

    /// <summary>The child's stderr, or <see langword="null"/> when not captured.</summary>
    Stream? StandardError { get; }

    /// <summary>Completes (never faults) when the process has exited.</summary>
    Task<ChildExit> Exited { get; }

    /// <summary>Sends the polite stop signal; <see langword="false"/> when it cannot be delivered (the caller then kills).</summary>
    bool RequestGracefulStop();

    /// <summary>Kills the process and its descendants; no effect once it has exited.</summary>
    void Kill();
}

/// <summary>How a child ended.</summary>
/// <param name="ExitCode">The exit code, or <see langword="null"/> when unknown.</param>
/// <param name="Signal">The terminating signal (Unix), or <see langword="null"/>.</param>
internal sealed record ChildExit(int? ExitCode, int? Signal = null)
{
    public string Describe() => Signal is { } signal
        ? $"killed by signal {signal}"
        : ExitCode is { } code ? $"exited with code {code}" : "exited (exit code unknown)";
}
