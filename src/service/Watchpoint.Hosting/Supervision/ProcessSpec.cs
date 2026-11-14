using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>Describes a helper process started and kept alive by the <see cref="ISupervisor"/> (spec HST §7.1).</summary>
public sealed record ProcessSpec
{
    /// <summary>Gets the unique name of the child (e.g. <c>nats</c>): lowercase letters, digits, <c>-</c>, <c>_</c> and <c>.</c>; used for the pid file and in logs.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the absolute path of the executable.</summary>
    public required string Executable { get; init; }

    /// <summary>Gets the command-line arguments (passed without shell interpretation).</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Gets the environment variables added to (or overriding) the host's environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the working directory; the host's when <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the probe that decides when the child is <see cref="SupervisedProcessState.Running"/>.</summary>
    public ReadinessProbe Readiness { get; init; } = ReadinessProbe.None;

    /// <summary>Gets the restart policy applied after an unexpected exit or a failed start.</summary>
    public RestartPolicy Restart { get; init; } = RestartPolicy.Default;

    /// <summary>Gets how long the child may take to exit after the graceful stop signal before it is killed.</summary>
    public TimeSpan StopGracePeriod { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the logger category the child's output lines are written to.</summary>
    public string LogCategory { get; init; } = "Watchpoint.Child";

    /// <summary>
    /// Gets the level of the child's stderr lines (stdout lines are always Information). Default Warning; set it to
    /// Information for children that write their normal log to stderr (nats-server does).
    /// </summary>
    public LogLevel StandardErrorLevel { get; init; } = LogLevel.Warning;

    /// <summary>Gets the resource limits (used by IHO/SCR from M4). Not supported before T4.01: a spec with limits is refused.</summary>
    public ResourceLimits? Limits { get; init; }
}
