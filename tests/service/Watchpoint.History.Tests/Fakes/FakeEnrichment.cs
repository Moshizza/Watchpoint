using Watchpoint.History.Internal;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>Enrichment from dictionaries.</summary>
internal sealed class FakeEnrichment : IHistoryEnrichment
{
    public Dictionary<DeviceId, HistorySource> Devices { get; } = [];

    public Dictionary<ServerId, HistorySource> Servers { get; } = [];

    public HistorySource? ForDevice(DeviceId device) => Devices.GetValueOrDefault(device);

    public HistorySource? ForServer(ServerId server) => Servers.GetValueOrDefault(server);
}
