using Microsoft.Extensions.Time.Testing;

namespace Watchpoint.Kernel.Testing;

/// <summary>Deterministic time for tests.</summary>
public static class TestTime
{
    /// <summary>The default start instant of test clocks: 2026-01-05T08:00:00.000Z (a Monday).</summary>
    public static readonly DateTimeOffset Start = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a fake time provider at <see cref="Start"/> (or the given instant), with UTC as local zone.</summary>
    /// <param name="start">The start instant, or <see langword="null"/> for <see cref="Start"/>.</param>
    /// <returns>The provider; advance it with <see cref="FakeTimeProvider.Advance"/>.</returns>
    public static FakeTimeProvider CreateProvider(DateTimeOffset? start = null)
    {
        var provider = new FakeTimeProvider(start ?? Start);
        provider.SetLocalTimeZone(TimeZoneInfo.Utc);
        return provider;
    }
}
