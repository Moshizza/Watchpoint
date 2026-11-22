using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The commands of one partition that are not finished, or finished less than 60 s ago (spec CMD §6). Used only on the
/// partition's worker. Tracks what changed since the last snapshot collection (snapshot kind <c>command</c>).
/// </summary>
internal sealed class PendingCommandStore
{
    private readonly Dictionary<CommandId, Command> _commands = [];
    private readonly Dictionary<CommandId, DateTimeOffset> _finishedAt = [];
    private readonly HashSet<CommandId> _dirty = [];
    private readonly HashSet<CommandId> _removed = [];

    public int Count => _commands.Count;

    /// <summary>The last sequence applied to the store.</summary>
    public long Sequence { get; set; }

    /// <summary>Commands whose approval-expiry rejection was emitted but not applied yet.</summary>
    public HashSet<CommandId> Expiring { get; } = [];

    public IEnumerable<Command> Commands => _commands.Values;

    public Command? Find(CommandId id) => _commands.GetValueOrDefault(id);

    public void Put(Command command, DateTimeOffset at)
    {
        _commands[command.Id] = command;
        _dirty.Add(command.Id);
        _removed.Remove(command.Id);
        if (CommandStateMachine.IsTerminal(command))
        {
            _finishedAt.TryAdd(command.Id, at);
        }
    }

    /// <summary>The finished commands older than <paramref name="before"/>.</summary>
    public List<Command> FinishedBefore(DateTimeOffset before) =>
        [.. _finishedAt.Where(f => f.Value <= before).Select(f => _commands[f.Key])];

    public void Remove(CommandId id)
    {
        _commands.Remove(id);
        _finishedAt.Remove(id);
        Expiring.Remove(id);
        _dirty.Remove(id);
        _removed.Add(id);
    }

    /// <summary>The changes since the previous call: upserts and removals.</summary>
    public (List<Command> Upserts, List<CommandId> Removed) TakeChanges()
    {
        var upserts = _dirty.Select(id => _commands[id]).ToList();
        var removed = _removed.ToList();
        _dirty.Clear();
        _removed.Clear();
        return (upserts, removed);
    }
}
