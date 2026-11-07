using Microsoft.Extensions.Time.Testing;

namespace Watchpoint.Testing.Time;

/// <summary>
/// Deterministic clock for tests: a <see cref="FakeTimeProvider"/> starting at <see cref="DefaultStart"/>
/// with auto-advance disabled. <see cref="FakeTimeProvider.Advance(TimeSpan)"/> fires due timers in order.
/// </summary>
public sealed class TestClock : FakeTimeProvider
{
    /// <summary>The default start instant: 2026-01-15 09:00:00 UTC.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Initializes a new instance of the <see cref="TestClock"/> class.</summary>
    /// <param name="start">The start instant (UTC); <see cref="DefaultStart"/> when <see langword="null"/>.</param>
    public TestClock(DateTimeOffset? start = null)
        : base(start ?? DefaultStart)
    {
        AutoAdvanceAmount = TimeSpan.Zero;
        SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    /// <summary>Advances the clock to <paramref name="utc"/>, firing due timers in order.</summary>
    /// <param name="utc">The target instant; must not be in the past.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="utc"/> is before the current time.</exception>
    public void AdvanceTo(DateTimeOffset utc)
    {
        var by = utc - GetUtcNow();
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero, nameof(utc));
        Advance(by);
    }
}
