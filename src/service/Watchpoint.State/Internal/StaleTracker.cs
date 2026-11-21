using Watchpoint.Kernel.Ids;

namespace Watchpoint.State.Internal;

/// <summary>
/// Schedules when a property becomes stale (spec STA §6.2): a min-heap of deadlines per partition, touched only by
/// the partition's worker thread. A property may be rescheduled many times (every refresh pushes a new deadline);
/// an expired entry is checked against the property's current deadline before it is acted on, so a stale entry left
/// over from an earlier, superseded schedule is a harmless no-op.
/// </summary>
internal sealed class StaleTracker
{
    private readonly PriorityQueue<(Guid Entity, PropertyPath Path, DateTimeOffset Deadline), DateTimeOffset> _queue = new();

    /// <summary>Schedules a property to go stale at a deadline.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="path">The property.</param>
    /// <param name="deadline">When the property becomes stale unless refreshed.</param>
    public void Schedule(Guid entity, PropertyPath path, DateTimeOffset deadline) =>
        _queue.Enqueue((entity, path, deadline), deadline);

    /// <summary>Pops every entry whose deadline is at or before <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The expired entries, each with the deadline it was scheduled for.</returns>
    public IEnumerable<(Guid Entity, PropertyPath Path, DateTimeOffset Deadline)> PopExpired(DateTimeOffset now)
    {
        while (_queue.TryPeek(out _, out var priority) && priority <= now)
        {
            yield return _queue.Dequeue();
        }
    }
}
