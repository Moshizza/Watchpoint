namespace Watchpoint.Testing;

/// <summary>Thrown by the shared test helpers when an assertion fails; any test framework reports it as a failure.</summary>
public sealed class TestAssertionException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="TestAssertionException"/> class.</summary>
    public TestAssertionException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestAssertionException"/> class.</summary>
    /// <param name="message">The failure message.</param>
    public TestAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestAssertionException"/> class.</summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The cause.</param>
    public TestAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
