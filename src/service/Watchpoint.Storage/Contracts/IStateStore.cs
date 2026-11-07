using System.Text.RegularExpressions;

namespace Watchpoint.Storage.Contracts;

/// <summary>Snapshots of in-memory state (spec STO §7.2): items per kind and partition, with the consumer's checkpoints.</summary>
public interface IStateStore
{
    /// <summary>Maximum items (upserts + deletes) of one batch.</summary>
    const int MaxBatchItems = 5000;

    /// <summary>Writes a batch of item upserts/deletes and the consumer checkpoint for one partition, atomically.</summary>
    /// <param name="batch">The batch (≤ <see cref="MaxBatchItems"/> items).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="StorageException"><c>storage.state.batch_too_large</c>.</exception>
    /// <exception cref="ArgumentException">A kind is invalid (lowercase, ≤ 32 characters).</exception>
    Task WriteSnapshotAsync(StateSnapshotBatch batch, CancellationToken ct);

    /// <summary>Reads all items of a kind for the given partitions (recovery), streamed.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="partitions">The partitions.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The items, in no particular order.</returns>
    IAsyncEnumerable<StateItem> ReadItemsAsync(string kind, IReadOnlyCollection<int> partitions, CancellationToken ct);

    /// <summary>Reads the checkpoints of a consumer: partition → event-log sequence.</summary>
    /// <param name="consumer">The consumer, e.g. <c>state</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The checkpoints.</returns>
    Task<IReadOnlyDictionary<int, long>> ReadCheckpointsAsync(string consumer, CancellationToken ct);
}

/// <summary>A snapshot batch: item changes of one partition and the consumer's checkpoint after them.</summary>
/// <param name="Consumer">Owner of the checkpoint, e.g. <c>state</c>, <c>alarms</c>.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Sequence">The checkpoint: event-log sequence reflected by the batch.</param>
/// <param name="Upserts">Items to insert or replace.</param>
/// <param name="Deletes">Items to delete.</param>
public sealed record StateSnapshotBatch(string Consumer, int Partition, long Sequence,
    IReadOnlyList<StateItem> Upserts, IReadOnlyList<StateItemKey> Deletes);

/// <summary>A state item.</summary>
/// <param name="Kind">Kind, e.g. <c>device</c>, <c>alarm</c> (lowercase, ≤ 32 characters).</param>
/// <param name="Key">The item key.</param>
/// <param name="Partition">The partition.</param>
/// <param name="PayloadJson">UTF-8 JSON document.</param>
/// <param name="Sequence">Event-log sequence of the last change reflected.</param>
public sealed record StateItem(string Kind, Guid Key, int Partition, ReadOnlyMemory<byte> PayloadJson, long Sequence);

/// <summary>Key of a state item.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Key">The key.</param>
public sealed record StateItemKey(string Kind, Guid Key);

/// <summary>Validation shared by state store implementations.</summary>
public static partial class StateStoreRules
{
    /// <summary>Maximum length of a kind and of a consumer.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Validates a batch before writing.</summary>
    /// <param name="batch">The batch.</param>
    /// <exception cref="StorageException"><c>storage.state.batch_too_large</c>.</exception>
    /// <exception cref="ArgumentException">A kind or the consumer is invalid.</exception>
    public static void Validate(StateSnapshotBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var count = batch.Upserts.Count + batch.Deletes.Count;
        if (count > IStateStore.MaxBatchItems)
        {
            throw new StorageException(StorageErrors.StateBatchTooLarge(count, IStateStore.MaxBatchItems));
        }

        ValidateName(batch.Consumer, nameof(batch.Consumer));
        foreach (var item in batch.Upserts)
        {
            ValidateName(item.Kind, nameof(StateItem.Kind));
            if (item.Partition != batch.Partition)
            {
                throw new ArgumentException($"Item {item.Kind}/{item.Key} is in partition {item.Partition}, the batch in {batch.Partition}.", nameof(batch));
            }
        }

        foreach (var key in batch.Deletes)
        {
            ValidateName(key.Kind, nameof(StateItemKey.Kind));
        }
    }

    /// <summary>Validates a kind or consumer: lowercase letters, digits and hyphens, ≤ 32 characters.</summary>
    /// <param name="name">The name.</param>
    /// <param name="parameter">Parameter name for the exception.</param>
    /// <exception cref="ArgumentException">Invalid.</exception>
    public static void ValidateName(string name, string parameter)
    {
        if (name is null || name.Length > MaxNameLength || !NamePattern().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' must be lowercase letters, digits or hyphens, at most {MaxNameLength} characters.", parameter);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
