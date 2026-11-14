using Watchpoint.Kernel.Results;

namespace Watchpoint.Hosting.Supervision;

/// <summary>A supervisor operation failed with a host error (spec HST §10).</summary>
public sealed class SupervisorException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SupervisorException"/> class.</summary>
    /// <param name="error">The error.</param>
    /// <param name="message">The developer message.</param>
    public SupervisorException(Error error, string message)
        : base(message)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    /// <summary>Gets the error (<c>host.process.*</c>, <c>host.nats.*</c>).</summary>
    public Error Error { get; }
}
