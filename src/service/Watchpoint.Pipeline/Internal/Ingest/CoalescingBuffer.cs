using Watchpoint.Kernel.Ids;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>
/// What a connection sent above its quota (spec PIP §8), in arrival order. A property observation replaces the waiting
/// one of the same target and property and keeps its place in the queue, so a property that changes all the time is
/// not starved; events and health observations are never replaced. Not thread-safe: guarded by its
/// <see cref="ConnectionQuota"/>.
/// </summary>
internal sealed class CoalescingBuffer
{
    private readonly LinkedList<IngestItem> _items = new();
    private readonly Dictionary<(Guid Target, PropertyPath Property), LinkedListNode<IngestItem>> _properties = [];

    public int Count => _items.Count;

    /// <summary>The events and health observations waiting (the part that cannot be coalesced).</summary>
    public int FifoCount => _items.Count - _properties.Count;

    /// <summary>Adds an observation; returns the property observation it replaced, if any.</summary>
    public IngestItem? Add(IngestItem item)
    {
        if (item.Kind != ObservationKind.Property)
        {
            _items.AddLast(item);
            return null;
        }

        var key = (item.Target, item.Property);
        if (_properties.TryGetValue(key, out var node))
        {
            var replaced = node.Value;
            node.Value = item;
            return replaced;
        }

        _properties[key] = _items.AddLast(item);
        return null;
    }

    public bool TryTake(out IngestItem item)
    {
        if (_items.First is not { } node)
        {
            item = null!;
            return false;
        }

        item = node.Value;
        _items.RemoveFirst();
        if (item.Kind == ObservationKind.Property)
        {
            _properties.Remove((item.Target, item.Property));
        }

        return true;
    }
}
