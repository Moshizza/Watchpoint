using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.History.Internal;

/// <summary>The source of a record as it is when the record is written: names, location, tags and type (spec HIS §4, REG §5).</summary>
/// <param name="EntityId">The device or server.</param>
/// <param name="IsServer">Whether the entity is a server.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="ServerId">The server.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationIds">The location and its ancestors, root first.</param>
/// <param name="LocationNames">The names of the location and its ancestors, root first.</param>
/// <param name="TagKeys">The tag keys.</param>
/// <param name="TypeRef">The type, <c>id@version</c>.</param>
/// <param name="TypeId">The type id.</param>
internal sealed record HistorySource(
    Guid EntityId,
    bool IsServer,
    string DisplayName,
    Guid ServerId,
    Guid LocationId,
    IReadOnlyList<Guid> LocationIds,
    IReadOnlyList<string> LocationNames,
    IReadOnlyList<string> TagKeys,
    Watchpoint.Model.Contracts.TypeRef TypeRef,
    string TypeId);

/// <summary>Resolves the denormalised source of a record; <see langword="null"/> when the entity is unknown (deleted since).</summary>
internal interface IHistoryEnrichment
{
    HistorySource? ForDevice(DeviceId device);

    HistorySource? ForServer(ServerId server);
}

/// <summary>Enrichment from the in-memory registry.</summary>
internal sealed class RegistryHistoryEnrichment(IRegistryReader registry) : IHistoryEnrichment
{
    public HistorySource? ForDevice(DeviceId device)
    {
        if (!registry.GetDevice(device).TryGetValue(out var found, out _))
        {
            return null;
        }

        var e = registry.GetEnrichment(device);
        var ancestors = registry.GetAncestors(e.LocationId);
        List<Guid> ids = [.. ancestors.Select(l => l.Id.Value), e.LocationId.Value];
        return new HistorySource(
            device.Value, false, e.DisplayNameDefault, e.ServerId.Value, e.LocationId.Value, ids, e.LocationNames, e.TagKeys, found.Type, found.Type.Id.Value);
    }

    public HistorySource? ForServer(ServerId server)
    {
        if (!registry.GetServer(server).TryGetValue(out var found, out _))
        {
            return null;
        }

        var ancestors = registry.GetAncestors(found.LocationId);
        List<Guid> ids = [.. ancestors.Select(l => l.Id.Value), found.LocationId.Value];
        List<string> names = [];
        foreach (var id in ids)
        {
            if (registry.GetLocation(LocationId.From(id)).TryGetValue(out var location, out _))
            {
                names.Add(location.Name.Default);
            }
        }

        var tags = registry.GetTags().Where(t => found.Tags.Contains(t.Id)).Select(t => t.Key).ToList();
        return new HistorySource(
            server.Value, true, found.DisplayName?.Default ?? found.VendorName, server.Value, found.LocationId.Value, ids, names, tags, found.Type, found.Type.Id.Value);
    }
}
