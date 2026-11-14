using Microsoft.Extensions.Time.Testing;

namespace Watchpoint.Testing.Time;

/// <summary>
/// Deterministic clock for tests: a <see cref="FakeTimeProvider"/> starting at <see cref="DefaultStart"/>
/// with auto-advance disabled. <see cref="FakeTimeProvider.Advance(TimeSpan)"/> fires due timers in order.
/// </summary>
/// <remarks>
/// <see cref="FakeTimeProvider.Advance(TimeSpan)"/> only fires timers that already exist: advancing before code
/// under test has armed its timer (for example <c>Task.Delay(delay, clock, ct)</c> on another thread) loses the
/// advance. Await <see cref="WaitForTimersAsync"/> first, so the advance is applied to an armed timer.
/// </remarks>
public sealed class TestClock : FakeTimeProvider
{
    /// <summary>The default start instant: 2026-01-15 09:00:00 UTC.</summary>
    public static readonly DateTimeOffset DefaultStart = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private readonly List<(long Count, TaskCompletionSource Armed)> _waiters = [];
    private long _timersCreated;

    /// <summary>Initializes a new instance of the <see cref="TestClock"/> class.</summary>
    /// <param name="start">The start instant (UTC); <see cref="DefaultStart"/> when <see langword="null"/>.</param>
    public TestClock(DateTimeOffset? start = null)
        : base(start ?? DefaultStart)
    {
        AutoAdvanceAmount = TimeSpan.Zero;
        SetLocalTimeZone(TimeZoneInfo.Utc);
    }

    /// <summary>Gets the number of timers created on this clock so far (including <c>Task.Delay</c> timers).</summary>
    public long TimersCreated
    {
        get
        {
            lock (_gate)
            {
                return _timersCreated;
            }
        }
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

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // The timer is registered with its due time before the waiters are released, so an advance made by a
        // released waiter fires it.
        var timer = base.CreateTimer(callback, state, dueTime, period);
        List<TaskCompletionSource>? armed = null;
        lock (_gate)
        {
            _timersCreated++;
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Count <= _timersCreated)
                {
                    (armed ??= []).Add(_waiters[i].Armed);
                    _waiters.RemoveAt(i);
                }
            }
        }

        armed?.ForEach(static a => a.TrySetResult());
        return timer;
    }

    /// <summary>
    /// Completes once at least <paramref name="count"/> timers have been created on this clock since it was made.
    /// </summary>
    /// <param name="count">The total number of timers to wait for (1 for the first timer).</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <returns>A task that completes when the timer count is reached.</returns>
    public Task WaitForTimersAsync(long count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        lock (_gate)
        {
            if (_timersCreated >= count)
            {
                return Task.CompletedTask;
            }

            var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, armed));
            return armed.Task.WaitAsync(ct);
        }
    }
}
