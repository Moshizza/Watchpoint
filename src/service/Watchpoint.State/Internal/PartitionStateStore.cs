using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Writer-thread-only bookkeeping kept alongside an entity's published <see cref="DeviceState"/> (spec STA §6.1,
/// §6.4): the aggregate scopes it currently belongs to, and the flags and attributes the state summary evaluator
/// needs on every recompute (neither is part of the published, spec-fixed <see cref="DeviceState"/> shape).
/// </summary>
/// <param name="Scopes">The aggregate scopes the entity currently belongs to.</param>
/// <param name="Flags">The administrative flags (disabled, maintenance).</param>
/// <param name="Attributes">The device attributes used by state overrides (empty for a server).</param>
internal sealed record EntityMeta(IReadOnlyList<AggregateScope> Scopes, DeviceFlags Flags, IReadOnlyDictionary<string, WpValue> Attributes);

/// <summary>
/// The state of one partition (spec STA §6.1): a map of entity id to <see cref="DeviceState"/>, mutated only by the
/// partition's own worker thread (ingest records, and registry-driven work posted through the pipeline's
/// dispatcher) so no lock is needed; readers on any thread see a complete, immutable <see cref="DeviceState"/> per
/// entity.
/// </summary>
internal sealed class PartitionStateStore(int partition)
{
    private readonly ConcurrentDictionary<Guid, DeviceState> _entities = new();
    private readonly Dictionary<Guid, EntityMeta> _meta = [];
    private readonly Dictionary<(Guid Entity, PropertyPath Path), Quality> _preStaleQuality = [];

    /// <summary>Gets the partition number.</summary>
    public int Partition { get; } = partition;

    /// <summary>Gets the stale-property scheduler of this partition.</summary>
    public StaleTracker StaleTracker { get; } = new();

    /// <summary>Gets or sets the last event log sequence applied to this partition.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets the number of entities currently held.</summary>
    public int Count => _entities.Count;

    /// <summary>Attempts to get the state of an entity.</summary>
    public bool TryGet(Guid id, out DeviceState state) => _entities.TryGetValue(id, out state!);

    /// <summary>Replaces the state of an entity (the only mutation readers can observe).</summary>
    public void Set(Guid id, DeviceState state) => _entities[id] = state;

    /// <summary>Removes an entity and its bookkeeping; returns its last state, if any.</summary>
    public bool Remove(Guid id, out DeviceState state)
    {
        _meta.Remove(id);
        return _entities.TryRemove(id, out state!);
    }

    /// <summary>Gets the bookkeeping of an entity, or <see langword="null"/> when it has none (not tracked).</summary>
    public EntityMeta? GetMeta(Guid id) => _meta.GetValueOrDefault(id);

    /// <summary>Replaces the bookkeeping of an entity.</summary>
    public void SetMeta(Guid id, EntityMeta meta) => _meta[id] = meta;

    /// <summary>
    /// Remembers the quality a property had just before <see cref="StaleTracker"/> replaced it with
    /// <see cref="Quality.Stale"/>, so a later touch can restore it (spec STA §6.1, touch row).
    /// </summary>
    public void MarkStale(Guid id, PropertyPath path, Quality previousQuality) => _preStaleQuality[(id, path)] = previousQuality;

    /// <summary>Takes back the quality remembered by <see cref="MarkStale"/>, if any.</summary>
    public bool TryTakePreStaleQuality(Guid id, PropertyPath path, out Quality quality) => _preStaleQuality.Remove((id, path), out quality);

    /// <summary>Returns a weakly-consistent snapshot of every entity currently held, for queries and sweeps.</summary>
    public IEnumerable<DeviceState> Snapshot() => _entities.Values;
}
