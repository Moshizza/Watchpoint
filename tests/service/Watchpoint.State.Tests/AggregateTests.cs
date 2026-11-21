using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class AggregateTests
{
    [Fact]
    public void STA_U_019_Aggregates_per_location_subtree()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var building = TestEntities.Location(path: "/b/", depth: 1);
        var floor = TestEntities.Location(parentId: building.Id, path: "/b/f/", depth: 2);
        fixture.Registry.SeedLocation(building);
        fixture.Registry.SeedLocation(floor);

        var forced = TestEntities.Door(locationId: floor.Id);
        var closed1 = TestEntities.Door(locationId: floor.Id);
        var closed2 = TestEntities.Door(locationId: floor.Id);
        fixture.Registry.CreateDevice(forced);
        fixture.Registry.CreateDevice(closed1);
        fixture.Registry.CreateDevice(closed2);
        fixture.Apply(fixture.Records.Property(forced.Id, null, TestTypes.Position, WpValue.Enum("forced")));
        fixture.Apply(fixture.Records.Property(closed1.Id, null, TestTypes.Position, WpValue.Enum("closed")));
        fixture.Apply(fixture.Records.Property(closed2.Id, null, TestTypes.Position, WpValue.Enum("closed")));

        foreach (var locationId in new[] { building.Id, floor.Id })
        {
            var counts = fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, locationId.Value));
            counts.Total.ShouldBe(3);
            counts.ByState[OverallState.Alarm].ShouldBe(1);
            counts.ByState[OverallState.Normal].ShouldBe(2);
        }
    }

    [Fact]
    public void STA_U_020_Aggregates_move_on_location_change()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var buildingA = TestEntities.Location(path: "/a/");
        var buildingB = TestEntities.Location(path: "/b/");
        fixture.Registry.SeedLocation(buildingA);
        fixture.Registry.SeedLocation(buildingB);
        var device = TestEntities.Door(locationId: buildingA.Id);
        fixture.Registry.CreateDevice(device);

        fixture.Registry.UpdateDevice(device with { LocationId = buildingB.Id });

        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, buildingA.Id.Value)).Total.ShouldBe(0);
        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, buildingB.Id.Value)).Total.ShouldBe(1);
    }

    [Fact]
    public void STA_U_021_Aggregates_per_tag_and_server()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var tag = TagId.New();
        var server = ServerId.New();
        var device = TestEntities.Door(serverId: server, tags: [tag]);
        fixture.Registry.CreateDevice(device);

        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Tag, tag.Value)).Total.ShouldBe(1);
        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Server, server.Value)).Total.ShouldBe(1);
        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Site, fixture.Registry.Site.Id.Value)).Total.ShouldBe(1);
    }

    [Fact]
    public void STA_U_022_Aggregate_consistency_job_finds_no_drift()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var locations = Enumerable.Range(0, 5).Select(i => TestEntities.Location(path: $"/l{i}/")).ToArray();
        foreach (var location in locations)
        {
            fixture.Registry.SeedLocation(location);
        }

        var random = new Random(42);
        var devices = new List<Watchpoint.Registry.Contracts.Device>();
        for (var i = 0; i < 2000; i++)
        {
            var action = random.Next(4);
            if (devices.Count == 0 || action == 0)
            {
                var device = TestEntities.Door(locationId: locations[random.Next(locations.Length)].Id);
                fixture.Registry.CreateDevice(device);
                devices.Add(device);
            }
            else if (action == 1 && devices.Count > 0)
            {
                var device = devices[random.Next(devices.Count)];
                fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, random.Next(2) == 0 ? WpValue.Enum("forced") : WpValue.Enum("closed")));
            }
            else if (action == 2 && devices.Count > 0)
            {
                var index = random.Next(devices.Count);
                var updated = devices[index] with { LocationId = locations[random.Next(locations.Length)].Id };
                fixture.Registry.UpdateDevice(updated);
                devices[index] = updated;
            }
            else if (devices.Count > 0)
            {
                var index = random.Next(devices.Count);
                fixture.Registry.DeleteDevice(devices[index].Id);
                devices.RemoveAt(index);
            }
        }

        var drifted = fixture.Store.RunAggregateConsistencyCheck(NullLogger.Instance);

        drifted.ShouldBeFalse();
    }

    [Fact]
    public void STA_U_023_Aggregate_drift_corrected()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var scope = new AggregateScope(AggregateScopeKind.Site, fixture.Registry.Site.Id.Value);
        fixture.Store.Get(scope).Total.ShouldBe(1);

        fixture.Aggregates.Increment(scope, OverallState.Alarm); // corrupt: the site now (wrongly) counts 2 entities

        var drifted = fixture.Store.RunAggregateConsistencyCheck(NullLogger.Instance);

        drifted.ShouldBeTrue();
        fixture.Store.Get(scope).Total.ShouldBe(1);
    }

    [Fact]
    public void STA_U_035_Membership_change_and_state_change_in_same_batch()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var buildingA = TestEntities.Location(path: "/a/");
        var buildingB = TestEntities.Location(path: "/b/");
        fixture.Registry.SeedLocation(buildingA);
        fixture.Registry.SeedLocation(buildingB);
        var device = TestEntities.Door(locationId: buildingA.Id);
        fixture.Registry.CreateDevice(device);
        fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, buildingA.Id.Value)).ByState[OverallState.Unknown].ShouldBe(1);

        // A single update moves the device to another building (membership) and disables it (state), together.
        fixture.Registry.UpdateDevice(device with { LocationId = buildingB.Id, Enabled = false });

        var fromCounts = fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, buildingA.Id.Value));
        var toCounts = fixture.Store.Get(new AggregateScope(AggregateScopeKind.Location, buildingB.Id.Value));
        fromCounts.Total.ShouldBe(0);
        toCounts.Total.ShouldBe(1);
        toCounts.ByState[OverallState.Disabled].ShouldBe(1);
    }
}
