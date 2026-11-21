using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// A lock-free ring of the last <c>State:RecentChanges</c> <see cref="StateChange"/>s (spec STA §6.5), shared by all
/// partitions. Assigns the change-feed sequence (distinct from the per-partition event log sequence of
/// <see cref="DeviceState.Sequence"/>): a single counter, so listeners can resynchronise regardless of which
/// partition produced a change.
/// </summary>
internal sealed class RecentChangesRing(int capacity)
{
    private readonly StateChange?[] _buffer = new StateChange?[capacity];
    private long _nextSequence = 1;

    /// <summary>Gets the capacity of the ring.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>Appends a change, assigning it the next sequence.</summary>
    public StateChange Append(Guid entityId, IReadOnlyList<PropertyPath> changedProperties, bool healthChanged, OverallState? newOverallState, DateTimeOffset at)
    {
        var sequence = Interlocked.Increment(ref _nextSequence) - 1;
        var change = new StateChange(entityId, sequence, changedProperties, healthChanged, newOverallState, at);
        _buffer[IndexOf(sequence)] = change;
        return change;
    }

    /// <summary>Gets the changes after a sequence (spec STA §6.5).</summary>
    /// <param name="sequence">The last sequence the caller already has.</param>
    /// <param name="max">The maximum number of changes to return.</param>
    /// <returns>The changes, oldest first, or <see langword="null"/> when the ring no longer holds that sequence.</returns>
    public IReadOnlyList<StateChange>? GetSince(long sequence, int max)
    {
        var last = Interlocked.Read(ref _nextSequence) - 1;
        var oldest = Math.Max(1, last - Capacity + 1);
        if (sequence < oldest - 1)
        {
            return null;
        }

        var result = new List<StateChange>(Math.Min(max, (int)Math.Min(int.MaxValue, last - sequence)));
        for (var s = sequence + 1; s <= last && result.Count < max; s++)
        {
            if (_buffer[IndexOf(s)] is { } change && change.Sequence == s)
            {
                result.Add(change);
            }
        }

        return result;
    }

    private int IndexOf(long sequence) => (int)((sequence - 1) % Capacity);
}
