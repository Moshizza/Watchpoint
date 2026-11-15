using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Pipeline.Internal.Ingest;

/// <summary>What the ingest needs to know about a device or server (spec PIP §7.1, steps 2–4).</summary>
/// <param name="Connection">The connection the target belongs to (for a device: the connection of its server).</param>
/// <param name="Enabled">Whether the target is enabled.</param>
/// <param name="Type">The type of the target, or <see langword="null"/> when the catalogue does not hold it.</param>
/// <param name="UnsupportedCapabilities">Optional capabilities the device does not support.</param>
internal sealed record IngestTarget(
    ConnectionId Connection,
    bool Enabled,
    DeviceTypeDefinition? Type,
    IReadOnlySet<CapabilityId> UnsupportedCapabilities);

/// <summary>Looks up the targets of observations: the registry and the type catalogue behind one small interface.</summary>
internal interface IIngestDirectory
{
    IngestTarget? FindDevice(DeviceId id);

    IngestTarget? FindServer(ServerId id);
}

/// <summary>The directory over the in-memory registry snapshot (REG) and the type catalogue (MOD); both are lock-free reads.</summary>
internal sealed class RegistryIngestDirectory(IRegistryReader registry, ITypeCatalog catalog) : IIngestDirectory
{
    private static readonly IReadOnlySet<CapabilityId> _none = new HashSet<CapabilityId>();

    public IngestTarget? FindDevice(DeviceId id)
    {
        var device = registry.GetDevice(id);
        if (!device.IsSuccess)
        {
            return null;
        }

        var server = registry.GetServer(device.Value.ServerId);
        if (!server.IsSuccess)
        {
            return null;
        }

        return new IngestTarget(server.Value.ConnectionId, device.Value.Enabled, TypeOf(device.Value.Type), device.Value.UnsupportedCaps);
    }

    public IngestTarget? FindServer(ServerId id)
    {
        var server = registry.GetServer(id);
        return server.IsSuccess
            ? new IngestTarget(server.Value.ConnectionId, server.Value.Enabled, TypeOf(server.Value.Type), _none)
            : null;
    }

    private DeviceTypeDefinition? TypeOf(TypeRef type)
    {
        var definition = catalog.Get(type);
        return definition.IsSuccess ? definition.Value : null;
    }
}
