using Watchpoint.Kernel.Results;

namespace Watchpoint.Hosting.Supervision;

/// <summary>Errors of the host (spec HST §10).</summary>
public static class HostErrors
{
    /// <summary>The code of <see cref="ProcessNotFound"/>.</summary>
    public const string ProcessNotFoundCode = "host.process.not_found";

    /// <summary>The code of <see cref="ExecutableMissing"/>.</summary>
    public const string ExecutableMissingCode = "host.process.executable_missing";

    /// <summary>The code of <see cref="ProcessFailed"/>.</summary>
    public const string ProcessFailedCode = "host.process.failed";

    /// <summary>The code of <see cref="NatsVersionUnsupported"/>.</summary>
    public const string NatsVersionUnsupportedCode = "host.nats.version_unsupported";

    /// <summary>The supervisor was asked about a process it does not (or no longer) supervise.</summary>
    /// <param name="name">The process name.</param>
    /// <returns>The error.</returns>
    public static Error ProcessNotFound(string name) => Error.NotFound(ProcessNotFoundCode, ("name", name));

    /// <summary>The executable of a process does not exist.</summary>
    /// <param name="name">The process name.</param>
    /// <param name="path">The executable path.</param>
    /// <returns>The error.</returns>
    public static Error ExecutableMissing(string name, string path) =>
        Error.Unprocessable(ExecutableMissingCode, ("name", name), ("path", path));

    /// <summary>A process did not reach <see cref="SupervisedProcessState.Running"/>: too many restarts within the window.</summary>
    /// <param name="name">The process name.</param>
    /// <returns>The error.</returns>
    public static Error ProcessFailed(string name) => Error.Unavailable(ProcessFailedCode, ("name", name));

    /// <summary>The bundled nats-server is older than the minimum version (ADR-001).</summary>
    /// <param name="found">The version found, or the unparsable output.</param>
    /// <param name="minimum">The minimum version.</param>
    /// <returns>The error.</returns>
    public static Error NatsVersionUnsupported(string found, string minimum) =>
        Error.Unprocessable(NatsVersionUnsupportedCode, ("found", found), ("minimum", minimum));
}
