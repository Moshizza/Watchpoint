using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The registry fields of a device or server that the state module needs (spec STA §6.1, §6.3), read from
/// <see cref="Watchpoint.Registry.Contracts.IRegistryReader"/> so the rest of the module does not depend on which
/// entity kind it is.
/// </summary>
/// <param name="Id">The device or server id.</param>
/// <param name="Kind">Whether this is a device or a server.</param>
/// <param name="Type">The type.</param>
/// <param name="LocationId">The location.</param>
/// <param name="ServerId">
/// The hosting server: a device's own server, or the server itself (for a <see cref="EntityKind.Server"/> entity, so
/// its own server chain starts at itself).
/// </param>
/// <param name="Tags">The tags.</param>
/// <param name="Enabled">Whether the entity is administratively enabled.</param>
/// <param name="Maintenance">Whether the entity is in maintenance.</param>
/// <param name="Attributes">The device attributes used by state overrides, or empty for a server.</param>
internal sealed record EntityInfo(
    Guid Id,
    EntityKind Kind,
    TypeRef Type,
    LocationId LocationId,
    ServerId ServerId,
    IReadOnlyList<TagId> Tags,
    bool Enabled,
    bool Maintenance,
    IReadOnlyDictionary<string, WpValue> Attributes)
{
    /// <summary>Builds an <see cref="EntityInfo"/> from a registered device.</summary>
    public static EntityInfo Of(Device device) => new(
        device.Id.Value,
        EntityKind.Device,
        device.Type,
        device.LocationId,
        device.ServerId,
        device.Tags,
        device.Enabled,
        device.Maintenance,
        device.Attributes);

    /// <summary>Builds an <see cref="EntityInfo"/> from a registered server.</summary>
    public static EntityInfo Of(Server server) => new(
        server.Id.Value,
        EntityKind.Server,
        server.Type,
        server.LocationId,
        server.Id,
        server.Tags,
        server.Enabled,
        server.Maintenance,
        _emptyAttributes);

    private static readonly IReadOnlyDictionary<string, WpValue> _emptyAttributes = new Dictionary<string, WpValue>();
}
