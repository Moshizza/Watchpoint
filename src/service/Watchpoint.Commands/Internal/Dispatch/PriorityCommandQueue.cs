using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Internal.Dispatch;

/// <summary>
/// The queued commands of one connection (spec CMD §7): four FIFO lanes by priority. Not thread-safe: the owning
/// <see cref="ConnectionDispatcher"/> uses it under its lock. A command is in the queue at most once.
/// </summary>
internal sealed class PriorityCommandQueue
{
    private readonly LinkedList<Command>[] _lanes = [.. Enumerable.Range(0, 4).Select(_ => new LinkedList<Command>())];
    private readonly Dictionary<CommandId, LinkedListNode<Command>> _index = [];

    public int Count => _index.Count;

    /// <summary>The number of queued commands of a priority.</summary>
    public int CountOf(CommandPriority priority) => _lanes[(int)priority].Count;

    /// <summary>Adds a command at the end of its lane; <see langword="false"/> when it is already queued.</summary>
    public bool Enqueue(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (_index.ContainsKey(command.Id))
        {
            return false;
        }

        _index[command.Id] = _lanes[(int)command.Priority].AddLast(command);
        return true;
    }

    /// <summary>
    /// Removes and returns the first command, highest priority first and in arrival order within a priority, that
    /// <paramref name="eligible"/> accepts; commands it refuses stay where they are (CMD §7 step 1).
    /// </summary>
    public Command? TakeFirst(Func<Command, bool> eligible)
    {
        ArgumentNullException.ThrowIfNull(eligible);
        foreach (var lane in _lanes)
        {
            for (var node = lane.First; node is not null; node = node.Next)
            {
                if (eligible(node.Value))
                {
                    lane.Remove(node);
                    _index.Remove(node.Value.Id);
                    return node.Value;
                }
            }
        }

        return null;
    }

    /// <summary>Removes a command; <see langword="false"/> when it is not queued.</summary>
    public bool Remove(CommandId id)
    {
        if (!_index.Remove(id, out var node))
        {
            return false;
        }

        node.List!.Remove(node);
        return true;
    }

    /// <summary>Every queued command, highest priority first, in arrival order within a priority.</summary>
    public List<Command> ToList() => [.. _lanes.SelectMany(l => l)];
}
