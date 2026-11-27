using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Registry.Contracts;
using Xunit;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Pipeline.Tests;

/// <summary>Purge of deleted targets (spec PIP §7.2) and the subscriber budget (spec PIP §7.3), T1.10b.</summary>
public sealed class PurgeAndBudgetTests
{
    [Fact]
    public async Task PIP_U_057_Deleted_targets_are_purged_from_the_last_value_cache()
    {
        await using var harness = new IngestHarness();

        // Its own id: the harness's first door and server share a number, and a cache key does not tell devices from servers.
        var door = harness.AddDoor(7, partition: 5);
        await harness.StartAsync();
        (await harness.IngestAsync(harness.PositionOf(door, "open"), harness.Health(door, Connectivity.Online))).Accepted.ShouldBe(2);
        (await harness.IngestAsync(harness.PositionOf(door, "open"), harness.Health(door, Connectivity.Online))).Accepted.ShouldBe(0, "both values are cached");
        var target = harness.Directory.Devices[door];
        var purge = new DeletedTargetPurge(null, harness.Router, harness.Directory, harness.LoggerFactory.CreateLogger<DeletedTargetPurge>());

        // The registry deleted the door: the purge posted to its partition removes both entries.
        harness.Directory.Devices.Remove(door);
        await purge.Handle(new RegistryChangeBatch([new RegistryChange.DeleteDevice(door, DeleteChildren: false)]), TestContext.Current.CancellationToken);

        var rejected = await harness.IngestAsync(harness.PositionOf(door, "open"));
        rejected.Rejected.ShouldHaveSingleItem().Error.Code.Value.ShouldBe("pipeline.target.unknown");

        // Nothing came back: a target known again (same id) starts from an empty cache, so the same values are changes.
        harness.Directory.Devices[door] = target;
        (await harness.IngestAsync(harness.PositionOf(door, "open"), harness.Health(door, Connectivity.Online))).Counts().ShouldBe((2, 0, 0, 0));
    }

    [Fact]
    public async Task PIP_U_061_Cascading_delete_sweeps_every_partition_and_keeps_existing_targets()
    {
        await using var harness = new IngestHarness();
        var other = harness.AddDoor(2, partition: 17);
        await harness.StartAsync();
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "open"), harness.PositionOf(other, "open"))).Accepted.ShouldBe(2);
        var purge = new DeletedTargetPurge(null, harness.Router, harness.Directory, harness.LoggerFactory.CreateLogger<DeletedTargetPurge>());

        // A server deleted with its devices: only the server is named; the door of partition 17 went with it.
        var door = harness.Directory.Devices[other];
        harness.Directory.Devices.Remove(other);
        await purge.Handle(new RegistryChangeBatch([new RegistryChange.DeleteServer(harness.Server, DeleteDevices: true)]), TestContext.Current.CancellationToken);
        harness.Directory.Devices[other] = door;

        var after = await harness.IngestAsync(harness.PositionOf(harness.Door, "open"), harness.PositionOf(other, "open"));
        after.Accepted.ShouldBe(1, "the swept door starts again from an empty cache");
        after.Unchanged.ShouldBe(1, "the door that still exists keeps its cache");
    }

    [Fact]
    public async Task PIP_U_058_Slow_subscriber_logged_against_the_budget()
    {
        IngestHarness? harness = null;
        var slow = new RecordingSubscriber("slow", 100);
        harness = new IngestHarness(o => o.SubscriberBudget = TimeSpan.FromMilliseconds(5), slow);
        await using var _ = harness;
        slow.OnRecordsHook = (_, _) => harness.Time.Advance(TimeSpan.FromMilliseconds(8));
        await harness.StartAsync();

        string[] codes = ["open", "closed"];
        for (var i = 0; i < 5; i++)
        {
            await harness.IngestAsync(harness.PositionOf(harness.Door, codes[i % 2]));
        }

        var logged = harness.Logs.WithEventId(1646).ShouldHaveSingleItem("at most once per minute per subscriber");
        logged.Level.ShouldBe(LogLevel.Warning);
        logged.Properties["Subscriber"].ShouldBe("slow");

        harness.Time.Advance(TimeSpan.FromMinutes(1));
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "closed"))).Accepted.ShouldBe(1);

        harness.Logs.WithEventId(1646).Count().ShouldBe(2);
        slow.Records.Count.ShouldBe(6, "a slow subscriber is never interrupted");
    }
}
