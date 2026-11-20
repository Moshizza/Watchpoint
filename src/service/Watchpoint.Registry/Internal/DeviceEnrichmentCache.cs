using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// Cache of <see cref="DeviceEnrichment"/> for fast real-time access (REG §7.2). <see cref="RegistryReader.GetEnrichment"/>
/// fills it on a miss; the writer invalidates the entries of devices affected by a committed batch.
/// </summary>
internal sealed class DeviceEnrichmentCache
{
    private readonly ConcurrentDictionary<DeviceId, DeviceEnrichment> _cache = new();

    /// <summary>Gets a cached enrichment, or <see langword="null"/> on a miss.</summary>
    public DeviceEnrichment? Get(DeviceId id) => _cache.TryGetValue(id, out var r) ? r : null;

    /// <summary>Inserts or replaces a cached enrichment.</summary>
    public void Put(DeviceEnrichment enrichment) => _cache[enrichment.Id] = enrichment;

    /// <summary>Removes a cached enrichment so the next access recomputes it.</summary>
    public void Remove(DeviceId id) => _cache.TryRemove(id, out _);
}
