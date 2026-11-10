using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// Cache of device metadata and enrichment fields for fast real-time access.
/// </summary>
internal sealed class DeviceEnrichmentCache
{
    private readonly ConcurrentDictionary<DeviceId, DeviceEnrichmentRecord> _cache = new();

    /// <summary>
    /// Gets or sets an enrichment record.
    /// </summary>
    public DeviceEnrichmentRecord? Get(DeviceId id) => _cache.TryGetValue(id, out var r) ? r : null;

    /// <summary>
    /// Updates or inserts an enrichment record.
    /// </summary>
    public void Put(DeviceEnrichmentRecord record) => _cache[record.Id] = record;

    /// <summary>
    /// Removes an enrichment record.
    /// </summary>
    public void Remove(DeviceId id) => _cache.TryRemove(id, out _);
}

/// <summary>
/// Enriched device record for streaming pipelines.
/// </summary>
internal sealed record DeviceEnrichmentRecord(
    DeviceId Id,
    ServerId ServerId,
    LocationId LocationId,
    string LocationPath,
    IReadOnlyList<TagId> Tags,
    IReadOnlyDictionary<string, WpValue> Attributes,
    IReadOnlyDictionary<CustomFieldId, WpValue> CustomFields);
