using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Internal;
using Watchpoint.State.Tests.Support;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.State.Tests;

/// <summary>The recovery: snapshot + replay (spec STA §8.2).</summary>
public sealed class RecoveryTests
{
    [Fact]
    public async Task STA_U_042_Recovery_from_snapshot_and_replay()
    {
        var fixture = new StateFixture();
        var (server, devices) = Seed(fixture, 6);
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online));
        var values = new[] { "open", "closed", "forced" };
        var i = 0;
        while (fixture.Log.Count < 100)
        {
            var device = devices[i % devices.Count];
            fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum(values[i % 3])));
            i++;
        }

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);
        while (fixture.Log.Count < 149)
        {
            var device = devices[i % devices.Count];
            fixture.Apply(i % 7 == 0
                ? fixture.Records.Event(device.Id, null, TestTypes.ForcedEvent, Severity.Alarm)
                : fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum(values[i % 3])));
            i++;
        }

        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Offline));
        fixture.Log[^1].Sequence.ShouldBe(150);
        var expected = Normalised(fixture, devices.Select(d => d.Id.Value).Append(server.Id.Value));
        var expectedCounts = fixture.Store.Get(new AggregateScope(AggregateScopeKind.Server, server.Id.Value));

        var restarted = fixture.Restart();
        await restarted.RecoverAsync();

        Normalised(restarted, devices.Select(d => d.Id.Value).Append(server.Id.Value)).ShouldBe(expected);
        restarted.Store.Get(new AggregateScope(AggregateScopeKind.Server, server.Id.Value)).ShouldBeEquivalentTo(expectedCounts);
        restarted.Store.Get(devices[0].Id.Value).Value.ServerUnavailable.ShouldBeTrue();
        restarted.Recovery.Recovered.ShouldBeTrue();
        restarted.Store.CurrentSequence.ShouldBe(150);
    }

    [Fact]
    public async Task STA_U_043_Recovery_discards_entities_removed_from_registry()
    {
        var fixture = new StateFixture();
        var (_, devices) = Seed(fixture, 2);
        fixture.Apply(fixture.Records.Property(devices[0].Id, null, TestTypes.Position, WpValue.Enum("open")));
        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);
        fixture.Persistence.Items.Count(i => i.Kind == "device").ShouldBe(3);

        var restarted = fixture.Restart();
        restarted.Registry.DeleteDevice(devices[0].Id);
        await restarted.RecoverAsync();

        restarted.Store.Get(devices[0].Id.Value).IsSuccess.ShouldBeFalse();
        restarted.Store.Get(devices[1].Id.Value).IsSuccess.ShouldBeTrue();
        await restarted.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);
        restarted.Persistence.Items.ShouldNotContain(i => i.Key == devices[0].Id.Value);
    }

    [Fact]
    public async Task STA_U_044_Recovery_initialises_entities_missing_from_snapshot()
    {
        var fixture = new StateFixture();
        var (server, _) = Seed(fixture, 1);
        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);
        var before = fixture.Store.Get(new AggregateScope(AggregateScopeKind.Server, server.Id.Value)).Total;

        var restarted = fixture.Restart();
        var added = TestEntities.Door(serverId: server.Id);
        restarted.Tracker.Unsubscribe();
        restarted.Registry.CreateDevice(added);
        await restarted.RecoverAsync();

        var state = restarted.Store.Get(added.Id.Value).Value;
        state.Properties[TestTypes.Position].Value.ShouldBeNull();
        state.Summary.State.ShouldBe(OverallState.Unknown);
        restarted.Store.Get(new AggregateScope(AggregateScopeKind.Server, server.Id.Value)).Total.ShouldBe(before + 1);
    }

    [Fact]
    public async Task STA_U_045_Recovery_provides_last_values_to_pipeline_cache()
    {
        var fixture = new StateFixture();
        var (server, devices) = Seed(fixture, 2);
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online, faults: ["f1"]));
        fixture.Apply(fixture.Records.Property(devices[0].Id, null, TestTypes.Position, WpValue.Enum("open"), Quality.Good));
        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);
        fixture.Apply(fixture.Records.Property(devices[1].Id, null, TestTypes.Position, WpValue.Enum("forced"), Quality.Uncertain));

        var restarted = fixture.Restart();
        var sink = new RecordingSink();
        await restarted.RecoverAsync(sink);

        sink.Properties.ShouldContain((devices[0].Id.Value, TestTypes.Position, WpValue.Enum("open"), Quality.Good));
        sink.Properties.ShouldContain((devices[1].Id.Value, TestTypes.Position, WpValue.Enum("forced"), Quality.Uncertain));
        sink.Properties.Count.ShouldBe(2);
        sink.Health.ShouldHaveSingleItem().Target.ShouldBe(server.Id.Value);
        sink.Health[0].Health.Connectivity.ShouldBe(Connectivity.Online);
        sink.Health[0].Health.Faults.ShouldBe(["f1"]);
    }

    [Fact]
    public async Task STA_U_047_Recovery_restores_snapshot_participants()
    {
        var participant = new TestParticipant();
        var fixture = new StateFixture(participants: [participant]);
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var record = fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("open"));
        fixture.Apply(record);
        var partition = Partitioning.Of(device.Id);
        var key = Guid.NewGuid();
        participant.Pending[partition] = [new StateItem("thing", key, partition, "{}"u8.ToArray(), record.Sequence)];
        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        var restoredParticipant = new TestParticipant();
        var restarted = fixture.Restart([restoredParticipant]);
        await restarted.RecoverAsync();

        restoredParticipant.Restored.Count.ShouldBe(Partitioning.PartitionCount);
        restoredParticipant.Restored[partition].ShouldHaveSingleItem().Key.ShouldBe(key);
        restoredParticipant.RestoredCheckpoints[partition].ShouldBe(record.Sequence);
    }

    private static (Server Server, List<Device> Devices) Seed(StateFixture fixture, int count)
    {
        fixture.Types.Add(TestTypes.Server());
        fixture.Types.Add(TestTypes.Door());
        var server = TestEntities.Server();
        fixture.Registry.CreateServer(server);
        var devices = new List<Device>();
        for (var i = 0; i < count; i++)
        {
            var device = TestEntities.Door(serverId: server.Id, locationId: server.LocationId);
            fixture.Registry.CreateDevice(device);
            devices.Add(device);
        }

        return (server, devices);
    }

    private static List<string> Normalised(StateFixture fixture, IEnumerable<Guid> ids) =>
        [.. ids.Select(id => DeviceStateJson.ToJson(fixture.Store.Get(id).Value with { UpdatedAt = default }))];

    private sealed class RecordingSink : ILastValueSink
    {
        public List<(Guid Target, PropertyPath Path, WpValue Value, Quality Quality)> Properties { get; } = [];

        public List<(Guid Target, HealthRecordBody Health)> Health { get; } = [];

        public void SetProperty(Guid target, PropertyPath path, WpValue value, Quality quality) => Properties.Add((target, path, value, quality));

        public void SetHealth(Guid target, HealthRecordBody health) => Health.Add((target, health));
    }
}
