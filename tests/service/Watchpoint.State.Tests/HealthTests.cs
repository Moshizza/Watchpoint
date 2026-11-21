using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class HealthTests
{
    [Fact]
    public void STA_U_006_Health_record_replaces_health_and_sets_since()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var onlineAt = fixture.Time.GetUtcNow();
        fixture.Apply(fixture.Records.Health(device.Id, null, Connectivity.Online, at: onlineAt));

        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        var offlineAt = fixture.Time.GetUtcNow();
        fixture.Apply(fixture.Records.Health(device.Id, null, Connectivity.Offline, at: offlineAt));

        var health = fixture.Store.Get(device.Id.Value).Value.Health;
        health.Connectivity.ShouldBe(Connectivity.Offline);
        health.Since.ShouldBe(offlineAt);
    }

    [Fact]
    public void STA_U_007_Server_offline_marks_devices_server_unavailable()
    {
        var fixture = new StateFixture();
        var (server, devices) = SeedServerWithDevices(fixture, count: 8);
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online));

        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Offline));

        foreach (var device in devices)
        {
            fixture.Store.Get(device.Id.Value).Value.ServerUnavailable.ShouldBeTrue();
        }
    }

    [Fact]
    public void STA_U_008_Child_servers_propagate()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Server());
        fixture.Types.Add(TestTypes.Door());
        var connection = ConnectionId.New();
        var parent = TestEntities.Server(connectionId: connection);
        fixture.Registry.CreateServer(parent);
        var child = TestEntities.Server(connectionId: connection, parentId: parent.Id);
        fixture.Registry.CreateServer(child);
        var devicesOfChild = new List<Watchpoint.Registry.Contracts.Device>();
        for (var i = 0; i < 8; i++)
        {
            var device = TestEntities.Door(serverId: child.Id);
            fixture.Registry.CreateDevice(device);
            devicesOfChild.Add(device);
        }

        fixture.Apply(fixture.Records.Health(null, parent.Id, Connectivity.Online));
        fixture.Apply(fixture.Records.Health(null, parent.Id, Connectivity.Offline));

        foreach (var device in devicesOfChild)
        {
            fixture.Store.Get(device.Id.Value).Value.ServerUnavailable.ShouldBeTrue();
        }
    }

    [Fact]
    public void STA_U_009_Server_back_online_clears_flag()
    {
        var fixture = new StateFixture();
        var (server, devices) = SeedServerWithDevices(fixture, count: 8);
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online));
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Offline));
        devices.ForEach(d => fixture.Store.Get(d.Id.Value).Value.ServerUnavailable.ShouldBeTrue());

        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online));

        foreach (var device in devices)
        {
            fixture.Store.Get(device.Id.Value).Value.ServerUnavailable.ShouldBeFalse();
        }
    }

    private static (Watchpoint.Registry.Contracts.Server Server, List<Watchpoint.Registry.Contracts.Device> Devices) SeedServerWithDevices(StateFixture fixture, int count)
    {
        fixture.Types.Add(TestTypes.Server());
        fixture.Types.Add(TestTypes.Door());
        var server = TestEntities.Server();
        fixture.Registry.CreateServer(server);
        var devices = new List<Watchpoint.Registry.Contracts.Device>();
        for (var i = 0; i < count; i++)
        {
            var device = TestEntities.Door(serverId: server.Id);
            fixture.Registry.CreateDevice(device);
            devices.Add(device);
        }

        return (server, devices);
    }
}
