using Watchpoint.Kernel.Results;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// A failure of the event log: an infrastructure failure (<c>pipeline.log.unavailable</c>,
/// <c>pipeline.stream.incompatible</c>) or a record that can never be appended (<c>pipeline.record.invalid</c>,
/// <c>pipeline.record.too_large</c>).
/// </summary>
public sealed class PipelineException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PipelineException"/> class.</summary>
    /// <param name="error">The error.</param>
    /// <param name="innerException">The cause, if any.</param>
    public PipelineException(Error error, Exception? innerException = null)
        : base(Describe(error), innerException)
    {
        Error = error;
    }

    /// <summary>Gets the error.</summary>
    public Error Error { get; }

    private static string Describe(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.DeveloperDetail is null ? error.Code.Value : $"{error.Code.Value}: {error.DeveloperDetail}";
    }
}
