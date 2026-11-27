using System.Collections.Concurrent;

namespace Watchpoint.Pipeline.Internal.Partitions;

/// <summary>
/// The soft budget of subscriber calls (spec PIP §7.3): a call longer than <see cref="Limit"/> is reported at most once per
/// minute per subscriber, across all partitions, and never interrupted. Shared by the workers, so thread-safe.
/// </summary>
internal sealed class SubscriberBudget(TimeSpan limit, TimeProvider time)
{
    /// <summary>The minimum time between two reports of the same subscriber.</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, long> _lastReported = new(StringComparer.Ordinal);

    /// <summary>Gets the budget of one call.</summary>
    public TimeSpan Limit { get; } = limit;

    /// <summary>Returns whether a call of <paramref name="elapsed"/> is over budget and due to be reported now.</summary>
    public bool ShouldLog(string subscriber, TimeSpan elapsed)
    {
        if (elapsed <= Limit)
        {
            return false;
        }

        var now = time.GetUtcNow().UtcTicks;
        while (true)
        {
            if (!_lastReported.TryGetValue(subscriber, out var last))
            {
                if (_lastReported.TryAdd(subscriber, now))
                {
                    return true;
                }

                continue;
            }

            if (now - last < ReportInterval.Ticks)
            {
                return false;
            }

            if (_lastReported.TryUpdate(subscriber, now, last))
            {
                return true;
            }
        }
    }
}
