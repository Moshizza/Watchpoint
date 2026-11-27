using System.Text.Json;
using Watchpoint.Commands.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The pending store as snapshot items (spec CMD §6, STA §8.1 <c>ISnapshotParticipant</c>: consumer <c>commands</c>, kind
/// <c>command</c>, one item per command with the full <see cref="Command"/> as JSON). STA's snapshotter calls
/// <see cref="Collect"/> on the partition worker and, at recovery, <see cref="Restore"/> then the checkpoint before the
/// replay; the subscriber reports the restored sequence as its recovery checkpoint (STA §8).
/// </summary>
internal sealed class PendingCommandSnapshot(CommandProcessor processor, TimeProvider time) : ISnapshotParticipant
{
    public const string Consumer = "commands";

    public const string Kind = "command";

    /// <summary>The commands changed since the previous call, and the ones that left the store.</summary>
    public (IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes) Collect(int partition)
    {
        var store = processor.Store(partition);
        var (upserts, removed) = store.TakeChanges();
        var items = upserts
            .Select(c => new StateItem(Kind, c.Id.Value, partition, JsonSerializer.SerializeToUtf8Bytes(c, CommandRecordCodec.Info<Command>()), store.Sequence))
            .ToList();
        return (items, [.. removed.Select(id => new StateItemKey(Kind, id.Value))]);
    }

    public void Restore(int partition, IReadOnlyList<StateItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var commands = items
            .Where(i => i.Kind == Kind && i.Partition == partition)
            .Select(i => JsonSerializer.Deserialize(i.PayloadJson.Span, CommandRecordCodec.Info<Command>())
                ?? throw new JsonException($"Snapshot item {i.Key} is empty."));
        processor.Restore(partition, commands, time.GetUtcNow());
        var sequence = items.Where(i => i.Partition == partition).Select(i => i.Sequence).DefaultIfEmpty(0).Max();
        processor.Store(partition).Sequence = Math.Max(processor.Store(partition).Sequence, sequence);
    }

    string ISnapshotParticipant.Consumer => Consumer;

    string ISnapshotParticipant.Kind => Kind;

    SnapshotContribution ISnapshotParticipant.Collect(int partition)
    {
        var (upserts, deletes) = Collect(partition);
        return new SnapshotContribution(upserts, deletes);
    }

    /// <summary>The persisted checkpoint: the replay starts after it (<see cref="CommandSubscriber.GetRecoveryCheckpoint"/>).</summary>
    void ISnapshotParticipant.RestoreCheckpoint(int partition, long checkpoint)
    {
        var store = processor.Store(partition);
        store.Sequence = Math.Max(store.Sequence, checkpoint);
    }
}
