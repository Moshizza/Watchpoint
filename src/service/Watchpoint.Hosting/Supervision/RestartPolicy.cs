namespace Watchpoint.Hosting.Supervision;

/// <summary>Restart back-off of a supervised process (spec HST §7.2).</summary>
/// <param name="InitialDelay">The delay before the first restart.</param>
/// <param name="MaxDelay">The cap of the delay (before jitter).</param>
/// <param name="Multiplier">The factor applied for each further restart.</param>
/// <param name="MaxRestartsInWindow">The number of restarts tolerated within <paramref name="Window"/>; one more makes the child <see cref="SupervisedProcessState.Failed"/>.</param>
/// <param name="Window">The sliding window of <paramref name="MaxRestartsInWindow"/>; a child that stayed running longer resets its restart count.</param>
public sealed record RestartPolicy(TimeSpan InitialDelay, TimeSpan MaxDelay, double Multiplier, int MaxRestartsInWindow, TimeSpan Window)
{
    /// <summary>The jitter applied to every delay: ±20 %.</summary>
    public const double JitterFraction = 0.2;

    /// <summary>1 s doubling up to 60 s; failed after more than 10 restarts within 10 minutes.</summary>
    public static readonly RestartPolicy Default = new(
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 2.0, MaxRestartsInWindow: 10, Window: TimeSpan.FromMinutes(10));

    /// <summary>
    /// Computes the delay before restart <paramref name="restart"/>:
    /// <c>min(InitialDelay × Multiplier^(restart-1), MaxDelay)</c> scaled by <c>1 ± 20 %</c>.
    /// </summary>
    /// <param name="restart">The restart number, 1 for the first restart.</param>
    /// <param name="jitter">A uniform sample in [0, 1): 0 gives −20 %, 0.5 no jitter, values near 1 +20 %.</param>
    /// <returns>The delay.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="restart"/> is below 1 or <paramref name="jitter"/> is outside [0, 1].</exception>
    public TimeSpan ComputeDelay(int restart, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(restart, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1.0);

        // Math.Pow overflows to infinity for large exponents; Math.Min then yields the cap.
        var seconds = Math.Min(InitialDelay.TotalSeconds * Math.Pow(Multiplier, restart - 1), MaxDelay.TotalSeconds);
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = MaxDelay.TotalSeconds;
        }

        var factor = 1.0 + (JitterFraction * ((2.0 * jitter) - 1.0));
        return TimeSpan.FromSeconds(seconds * factor);
    }
}
