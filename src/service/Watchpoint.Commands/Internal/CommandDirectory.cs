using System.Collections.Concurrent;
using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The node-wide view of commands, readable from any thread (spec CMD §5 <c>Get</c>, step 1): the current value of every
/// active command (written only by the partition that owns it), finished commands for 24 h, and the idempotency keys.
/// </summary>
internal sealed class CommandDirectory(CommandsOptions options)
{
    private static readonly TimeSpan _pruneEvery = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<CommandId, Command> _active = new();
    private readonly ConcurrentDictionary<CommandId, (Command Command, DateTimeOffset Until)> _recent = new();
    private readonly ConcurrentDictionary<(UserId Identity, string Key), (CommandId Id, DateTimeOffset At)> _keys = new();
    private long _lastPruneTicks = long.MinValue;

    public Command? Find(CommandId id, DateTimeOffset now)
    {
        if (_active.TryGetValue(id, out var active))
        {
            return active;
        }

        return _recent.TryGetValue(id, out var recent) && recent.Until > now ? recent.Command : null;
    }

    public void Publish(Command command) => _active[command.Id] = command;

    /// <summary>The command left its pending store: it stays readable for 24 h.</summary>
    public void Retire(Command command, DateTimeOffset now)
    {
        _recent[command.Id] = (command, now + CommandsOptions.ReadCacheRetention);
        _active.TryRemove(command.Id, out _);
    }

    public CommandId? FindKey(UserId identity, string key, DateTimeOffset now) =>
        _keys.TryGetValue((identity, key), out var entry) && entry.At + options.IdempotencyWindow > now ? entry.Id : null;

    /// <summary>Reserves a key for <paramref name="id"/>; returns the command that holds it already, if any.</summary>
    public CommandId? Reserve(UserId identity, string key, CommandId id, DateTimeOffset at)
    {
        while (true)
        {
            if (_keys.TryGetValue((identity, key), out var entry))
            {
                if (entry.At + options.IdempotencyWindow > at)
                {
                    return entry.Id == id ? null : entry.Id;
                }

                if (_keys.TryUpdate((identity, key), (id, at), entry))
                {
                    return null;
                }

                continue;
            }

            if (_keys.TryAdd((identity, key), (id, at)))
            {
                return null;
            }
        }
    }

    /// <summary>Drops finished commands older than 24 h and keys older than the idempotency window (at most once a minute).</summary>
    public void Prune(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (last != long.MinValue && now.UtcTicks - last < _pruneEvery.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        foreach (var (id, recent) in _recent)
        {
            if (recent.Until <= now)
            {
                _recent.TryRemove(id, out _);
            }
        }

        foreach (var (key, entry) in _keys)
        {
            if (entry.At + options.IdempotencyWindow <= now)
            {
                _keys.TryRemove(key, out _);
            }
        }
    }
}
