using System.Text;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Internal;
using Watchpoint.State.Tests.Support;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.State.Tests;

/// <summary>The snapshotter (spec STA §8.1).</summary>
public sealed class SnapshotTests
{
    [Fact]
    public async Task STA_U_036_Snapshot_collects_only_dirty_entities()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var devices = Enumerable.Range(0, 100).Select(_ => TestEntities.Door()).ToList();
        devices.ForEach(fixture.Registry.CreateDevice);
        (await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None)).ShouldBeTrue();
        fixture.Persistence.Batches.Clear();

        foreach (var device in devices.Take(3))
        {
            fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("open")));
        }

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        var written = fixture.Persistence.Batches.SelectMany(b => b.Upserts).Select(i => i.Key).ToList();
        written.Count.ShouldBe(3);
        written.ShouldBe(devices.Take(3).Select(d => d.Id.Value), ignoreOrder: true);
    }

    [Fact]
    public async Task STA_U_037_Snapshot_checkpoint_is_partition_sequence_at_collection()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        LogRecord last = null!;
        for (var i = 0; i < 5; i++)
        {
            last = fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum(i % 2 == 0 ? "open" : "closed"));
            fixture.Apply(last);
        }

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        var partition = Partitioning.Of(device.Id);
        var batch = fixture.Persistence.Batches.Single(b => b.Partition == partition);
        batch.Consumer.ShouldBe("state");
        batch.Sequence.ShouldBe(last.Sequence);
        batch.Sequence.ShouldBe(fixture.Store.PartitionStore(partition).Sequence);
        fixture.Persistence.CheckpointOf("state", partition).ShouldBe(last.Sequence);
    }

    [Fact]
    public async Task STA_U_038_Snapshot_split_into_batches_with_checkpoint_in_last()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var now = fixture.Time.GetUtcNow();
        foreach (var id in IdsInPartition(0, 12_000))
        {
            fixture.Store.InitializeEntity(EntityInfo.Of(TestEntities.Door(id: DeviceId.From(id))), now);
        }

        fixture.Store.PartitionStore(0).Sequence = 42;

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        var batches = fixture.Persistence.Batches.Where(b => b.Partition == 0).ToList();
        batches.Count.ShouldBe(3);
        batches.ShouldAllBe(b => b.Upserts.Count + b.Deletes.Count <= IStateStore.MaxBatchItems);
        batches.Sum(b => b.Upserts.Count).ShouldBe(12_000);
        batches[0].Sequence.ShouldBe(0);
        batches[1].Sequence.ShouldBe(0);
        batches[2].Sequence.ShouldBe(42);
    }

    [Fact]
    public async Task STA_U_039_Failed_snapshot_retried_with_merged_dirty_set()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var first = TestEntities.Door(id: DeviceId.From(IdsInPartition(5, 1).Single()));
        fixture.Registry.CreateDevice(first);
        fixture.Persistence.FailNext = 1;

        (await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None)).ShouldBeFalse();
        fixture.Persistence.Items.ShouldBeEmpty();

        var second = TestEntities.Door(id: DeviceId.From(IdsInPartition(5, 2).Last()));
        fixture.Registry.CreateDevice(second);
        fixture.Apply(fixture.Records.Property(second.Id, null, TestTypes.Position, WpValue.Enum("open")));

        (await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None)).ShouldBeTrue();

        fixture.Persistence.Items.Select(i => i.Key).ShouldBe([first.Id.Value, second.Id.Value], ignoreOrder: true);
        fixture.Persistence.CheckpointOf("state", 5).ShouldBe(fixture.Store.PartitionStore(5).Sequence);
    }

    [Fact]
    public async Task STA_U_040_Snapshot_payload_excludes_derived_fields()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Server());
        fixture.Types.Add(TestTypes.Door(staleAfter: TimeSpan.FromMinutes(10)));
        var server = TestEntities.Server();
        fixture.Registry.CreateServer(server);
        var device = TestEntities.Door(serverId: server.Id);
        fixture.Registry.CreateDevice(device);
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Online));
        fixture.Apply(fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("forced"), Quality.Good));
        fixture.Apply(fixture.Records.Health(null, server.Id, Connectivity.Offline));
        fixture.Time.Advance(TimeSpan.FromMinutes(11));
        fixture.Tick(Partitioning.Of(device.Id));
        var live = fixture.Store.Get(device.Id.Value).Value;
        live.ServerUnavailable.ShouldBeTrue();
        live.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Stale);

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        var item = fixture.Persistence.Items.Single(i => i.Key == device.Id.Value);
        var json = Encoding.UTF8.GetString(item.PayloadJson.Span);
        json.ShouldNotContain("summary", Case.Insensitive);
        json.ShouldNotContain("serverUnavailable", Case.Insensitive);
        json.ShouldNotContain("overall", Case.Insensitive);
        var persisted = StateSerializer.Deserialize(item.PayloadJson.Span);
        persisted.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Good);
        persisted.Properties[TestTypes.Position].Value.ShouldBe(WpValue.Enum("forced"));
    }

    [Fact]
    public async Task STA_U_041_Shutdown_writes_final_snapshot()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        fixture.Snapshotter.Start();
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var record = fixture.Records.Property(device.Id, null, TestTypes.Position, WpValue.Enum("open"));
        fixture.Apply(record);
        fixture.Persistence.Batches.ShouldBeEmpty();

        await fixture.Recovery.StopAsync(CancellationToken.None);

        fixture.Persistence.Items.Single().Key.ShouldBe(device.Id.Value);
        fixture.Persistence.CheckpointOf("state", Partitioning.Of(device.Id)).ShouldBe(record.Sequence);
        fixture.Snapshotter.Lag.ShouldBe(0);
    }

    [Fact]
    public async Task STA_U_046_Snapshot_participant_collected_and_written_with_own_checkpoint()
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
        participant.Pending[partition] = [new StateItem("thing", key, partition, "{\"a\":1}"u8.ToArray(), record.Sequence)];
        fixture.Persistence.FailNext = 1;
        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        await fixture.Snapshotter.SnapshotAsync(onWorkers: true, CancellationToken.None);

        participant.CollectedPartitions.Count.ShouldBe(2 * Partitioning.PartitionCount);
        fixture.Persistence.Items.ShouldContain(i => i.Kind == "thing" && i.Key == key);
        fixture.Persistence.CheckpointOf("things", partition).ShouldBe(record.Sequence);
    }

    internal static List<Guid> IdsInPartition(int partition, int count)
    {
        var result = new List<Guid>(count);
        while (result.Count < count)
        {
            var id = Guid.NewGuid();
            if (Partitioning.Of(id) == partition)
            {
                result.Add(id);
            }
        }

        return result;
    }
}

/// <summary>A snapshot participant that hands out what a test put in <see cref="Pending"/> and records restores.</summary>
internal sealed class TestParticipant : ISnapshotParticipant
{
    public string Consumer => "things";

    public string Kind => "thing";

    public Dictionary<int, List<StateItem>> Pending { get; } = [];

    public List<int> CollectedPartitions { get; } = [];

    public Dictionary<int, IReadOnlyList<StateItem>> Restored { get; } = [];

    public Dictionary<int, long> RestoredCheckpoints { get; } = [];

    public SnapshotContribution Collect(int partition)
    {
        CollectedPartitions.Add(partition);
        return Pending.Remove(partition, out var items) ? new SnapshotContribution(items, []) : new SnapshotContribution([], []);
    }

    public void Restore(int partition, IReadOnlyList<StateItem> items) => Restored[partition] = items;

    public void RestoreCheckpoint(int partition, long checkpoint) => RestoredCheckpoints[partition] = checkpoint;
}
