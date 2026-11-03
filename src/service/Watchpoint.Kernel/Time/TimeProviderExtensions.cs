namespace Watchpoint.Kernel.Time;

/// <summary>Helpers on <see cref="TimeProvider"/> (inject <see cref="TimeProvider"/>; tests use <c>FakeTimeProvider</c>).</summary>
public static class TimeProviderExtensions
{
    /// <summary>
    /// Returns the current UTC time truncated to milliseconds, with offset zero (storage precision everywhere is
    /// milliseconds).
    /// </summary>
    /// <param name="timeProvider">The time provider.</param>
    /// <returns>The current time.</returns>
    public static DateTimeOffset UtcNowMs(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var ticks = timeProvider.GetUtcNow().UtcTicks;
        return new DateTimeOffset(ticks - (ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}
