namespace Watchpoint.Testing.Async;

/// <summary>Standard timeouts for tests that wait on real asynchronous infrastructure.</summary>
public static class TestTimeouts
{
    /// <summary>Gets the default timeout for <see cref="Eventually"/>: 5 s.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the timeout for container-backed tests: 30 s in CI (<c>CI=true</c>), 10 s locally.</summary>
    public static TimeSpan Container { get; } = IsCi ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);

    /// <summary>Gets the default polling interval of <see cref="Eventually"/>: 20 ms.</summary>
    public static TimeSpan PollInterval { get; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Gets a value indicating whether the tests run in CI.</summary>
    public static bool IsCi => string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
}
