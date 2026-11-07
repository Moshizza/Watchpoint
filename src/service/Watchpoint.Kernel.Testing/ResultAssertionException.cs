namespace Watchpoint.Kernel.Testing;

/// <summary>Thrown by <see cref="ResultAssertions"/> when an assertion fails.</summary>
public sealed class ResultAssertionException : Exception
{
    /// <summary>Initialises the exception.</summary>
    public ResultAssertionException()
    {
    }

    /// <summary>Initialises the exception.</summary>
    /// <param name="message">The failure message.</param>
    public ResultAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises the exception.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The cause.</param>
    public ResultAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
