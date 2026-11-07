namespace Watchpoint.Testing.Async;

/// <summary>
/// Polls a condition until it holds or a timeout expires. For real asynchronous infrastructure only
/// (containers, background services); unit tests use <see cref="Time.TestClock"/> and direct calls.
/// </summary>
public static class Eventually
{
    /// <summary>Waits until <paramref name="condition"/> returns <see langword="true"/>.</summary>
    /// <param name="condition">The condition, evaluated immediately and then on every poll.</param>
    /// <param name="timeout">The timeout; <see cref="TestTimeouts.Default"/> when <see langword="null"/>.</param>
    /// <param name="because">Explanation included in the failure message.</param>
    /// <param name="pollInterval">The real-time polling interval; <see cref="TestTimeouts.PollInterval"/> when <see langword="null"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the condition holds.</returns>
    /// <exception cref="TestAssertionException">The condition did not hold within the timeout.</exception>
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        string? because = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        await UntilAsync(() => condition() ? string.Empty : null, timeout, because, pollInterval, ct).ConfigureAwait(false);
    }

    /// <summary>Waits until <paramref name="probe"/> returns a non-null value and returns it.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="probe">The probe, evaluated immediately and then on every poll.</param>
    /// <param name="timeout">The timeout; <see cref="TestTimeouts.Default"/> when <see langword="null"/>.</param>
    /// <param name="because">Explanation included in the failure message.</param>
    /// <param name="pollInterval">The real-time polling interval; <see cref="TestTimeouts.PollInterval"/> when <see langword="null"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The first non-null value.</returns>
    /// <exception cref="TestAssertionException">The probe returned <see langword="null"/> until the timeout.</exception>
    public static async Task<T> UntilAsync<T>(
        Func<T?> probe,
        TimeSpan? timeout = null,
        string? because = null,
        TimeSpan? pollInterval = null,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        var limit = timeout ?? TestTimeouts.Default;
        var interval = pollInterval ?? TestTimeouts.PollInterval;
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (probe() is { } value)
            {
                return value;
            }

            if (clock.GetElapsedTime(started) >= limit)
            {
                var reason = string.IsNullOrEmpty(because) ? "condition was not met" : because;
                throw new TestAssertionException($"Timed out after {limit.TotalMilliseconds:0} ms: {reason}.");
            }

            await Task.Delay(interval, clock, ct).ConfigureAwait(false);
        }
    }
}
