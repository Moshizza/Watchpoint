using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Tests.Fakes;
using Watchpoint.Testing.Ids;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>Partition workers, subscribers, dispatcher, ticks, recovery and shutdown (spec PIP §7.3, §7.4, §8).</summary>
[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class PartitionWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PIP_U_034_Subscribers_called_in_order_with_records_in_sequence_order()
    {
        // Registered in the wrong order: the Order decides.
        var journal = new List<string>();
        var alarms = new RecordingSubscriber("alarms", 300) { Journal = journal };
        var state = new RecordingSubscriber("state", 100) { Journal = journal };
        await using var harness = new IngestHarness(null, alarms, state);

        // Three observations wait for the worker and make one batch; a fourth follows.
        var batch = harness.IngestAsync(
            harness.PositionOf(harness.Door, "open"), harness.Event(harness.Door), harness.PositionOf(harness.Door, "closed"));
        await harness.StartAsync();
        await batch;
        await harness.IngestAsync(harness.PositionOf(harness.Door, "forced"));

        journal.ShouldBe(["state:1,2,3", "alarms:1,2,3", "state:4", "alarms:4"]);
        state.Records.Select(r => r.Sequence).ShouldBe([1, 2, 3, 4]);
        state.Records.Select(r => r.Envelope.Kind).ShouldBe([RecordKinds.Property, RecordKinds.Event, RecordKinds.Property, RecordKinds.Property]);
        alarms.Records.ShouldBe(state.Records);
    }

    [Fact]
    public async Task PIP_U_035_Follow_up_records_appended_before_new_input()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var alarms = new RecordingSubscriber("alarms", 300);
        await using var harness = new IngestHarness(null, alarms);
        var followUpId = TestIds.Uuid(900);
        alarms.OnRecordsHook = (context, records) =>
        {
            if (records[0].Envelope.Kind != RecordKinds.Property || records[0].Sequence != 1)
            {
                return;
            }

            // The worker is held inside the first batch while the next observation arrives.
            entered.Release();
            release.Wait(Ct);
            context.Emit(harness.Log.Envelope(1, harness.Door, PipelineHarness.AlarmRaised) with { Id = followUpId });
        };
        await harness.StartAsync();

        var first = harness.IngestAsync(harness.PositionOf(harness.Door, "forced"));
        await entered.WaitAsync(Ct);
        var second = harness.IngestAsync(harness.PositionOf(harness.Door, "closed"));
        release.Release();
        await first;
        await second;

        var records = harness.Records();
        records.Select(r => r.Envelope.Kind).ShouldBe([RecordKinds.Property, PipelineHarness.AlarmRaised, RecordKinds.Property]);
        records[1].Envelope.Id.ShouldBe(followUpId);

        // Subscribers saw the follow-up as a record too, before the next input.
        alarms.Records.Select(r => r.Sequence).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task PIP_U_036_Follow_up_depth_limit()
    {
        // A rule loop: every record makes the subscriber emit another one.
        var loop = new RecordingSubscriber("loop", 300);
        await using var harness = new IngestHarness(null, loop);
        var n = 0;
        loop.OnRecordsHook = (context, _) => context.Emit(harness.Log.Envelope(100 + Interlocked.Increment(ref n), harness.Door, PipelineHarness.AlarmRaised));
        await harness.StartAsync();

        (await harness.IngestAsync(harness.PositionOf(harness.Door, "forced"))).Accepted.ShouldBe(1);

        // The input record and four levels of follow-ups; the fifth level is dropped.
        var records = harness.Records();
        records.Count.ShouldBe(5);
        records.Skip(1).ShouldAllBe(r => r.Envelope.Kind == PipelineHarness.AlarmRaised);
        var dropped = harness.Logs.WithEventId(1640).ShouldHaveSingleItem();
        dropped.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Error);

        // The worker goes on.
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "closed"))).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task PIP_U_037_Faulty_subscriber_isolated()
    {
        var faulty = new RecordingSubscriber("faulty", 100) { OnRecordsHook = (_, _) => throw new InvalidOperationException("bug") };
        var healthy = new RecordingSubscriber("healthy", 300);
        await using var harness = new IngestHarness(null, faulty, healthy);
        await harness.StartAsync();

        var first = harness.PositionOf(harness.Door, "open");
        (await harness.IngestAsync(first)).Accepted.ShouldBe(1);
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "closed"))).Accepted.ShouldBe(1);

        healthy.Records.Count.ShouldBe(2);
        var logs = harness.Logs.WithEventId(1641).ToList();
        logs.Count.ShouldBe(2);
        logs[0].RenderedMessage.ShouldContain("faulty");
        logs[0].RenderedMessage.ShouldContain(first.Id.ToString());
        harness.Counter("watchpoint.pipeline.subscriber.errors", "subscriber", "faulty").ShouldBe(2);
        harness.Counter("watchpoint.pipeline.subscriber.errors", "subscriber", "healthy").ShouldBe(0);
    }

    [Fact]
    public async Task PIP_U_038_Partition_queue_back_pressure()
    {
        await using var harness = new IngestHarness(o => o.PartitionQueueCapacity = 10);
        var worker = harness.Router.Worker(Partitioning.Of(harness.Door));

        // The worker is not running yet: ten observations fill its queue.
        var calls = new List<Task<IngestResult>>();
        for (var i = 0; i < 11; i++)
        {
            calls.Add(harness.IngestAsync(harness.Event(harness.Door)));
        }

        // The eleventh waits for room; nothing is dropped and nothing grows beyond the capacity.
        worker.Queued.ShouldBe(10);
        calls.ShouldAllBe(c => !c.IsCompleted);

        await harness.StartAsync();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        results.ShouldAllBe(r => r.Accepted == 1);
        harness.Records().Count.ShouldBe(11);
    }

    [Fact]
    public async Task PIP_U_042_Recovery_replays_from_minimum_checkpoint()
    {
        var state = new RecordingSubscriber("state", 100);
        var alarms = new RecordingSubscriber("alarms", 300);
        var stateless = new RecordingSubscriber("stateless", 400);
        await using var harness = new IngestHarness(null, state, alarms, stateless);
        var partition = Partitioning.Of(harness.Door);
        var other = harness.AddDoor(1, (partition + 1) % Partitioning.PartitionCount);

        // 60 records of the door's partition, the last property record says "open"; 5 of another partition.
        var registry = harness.Log.Registry;
        for (var i = 1; i <= 60; i++)
        {
            var body = registry.SerializeBody(RecordKinds.Property, new PropertyRecordBody(
                IngestHarness.Position.Value, WpValue.Enum(i % 2 == 0 ? "open" : "closed"), Quality.Good, null));
            await harness.Log.Writer.AppendAsync([harness.Log.Envelope(i, harness.Door, RecordKinds.Property) with { Body = body }], Ct);
        }

        await harness.Log.Writer.AppendAsync([.. Enumerable.Range(61, 5).Select(i => harness.Log.Envelope(i, other))], Ct);
        state.Checkpoints[partition] = 50;
        alarms.Checkpoints[partition] = 40;
        state.Checkpoints[Partitioning.Of(other)] = 65;

        await harness.Router.RecoverAsync(Ct);

        // Replay from min(50, 40) + 1 = 41; each subscriber only gets what is after its own checkpoint.
        alarms.Records.Select(r => r.Sequence).ShouldBe(Enumerable.Range(41, 20).Select(i => (long)i));
        state.Records.Select(r => r.Sequence).ShouldBe(Enumerable.Range(51, 10).Select(i => (long)i));
        stateless.Records.ShouldBeEmpty("a subscriber without checkpoint needs no replay");
        state.Recovered.Count.ShouldBe(Partitioning.PartitionCount);
        state.Recovered.ShouldAllBe(r => r.LastSequence == 65);
        harness.Logs.WithEventId(1610).ShouldHaveSingleItem().RenderedMessage.ShouldContain("from sequence 41 to 65 (20 records)");

        // The change detection continues from the replayed values: no false "unchanged", no false change.
        await harness.StartAsync();
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "open"))).Counts().ShouldBe((0, 1, 0, 0));
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "closed"))).Counts().ShouldBe((1, 0, 0, 0));
        harness.PropertyBody(harness.Records()[^1]).Prev.ShouldBe(WpValue.Enum("open"));
    }

    [Fact]
    public async Task PIP_U_043_Recovery_after_retention()
    {
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(null, state);
        var partition = Partitioning.Of(harness.Door);
        for (var i = 1; i <= 20; i++)
        {
            await harness.Log.Writer.AppendAsync([harness.Log.Envelope(i, harness.Door)], Ct);
        }

        // Retention removed 1…12; the subscriber's checkpoint is older.
        harness.Log.Broker.Purge(harness.Log.LogStreamName, 12);
        state.Checkpoints[partition] = 5;

        await harness.Router.RecoverAsync(Ct);

        var log = harness.Logs.WithEventId(1650).ShouldHaveSingleItem();
        log.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Error);
        log.RenderedMessage.ShouldContain("wanted 6, first available 13");
        state.Records.Select(r => r.Sequence).ShouldBe(Enumerable.Range(13, 8).Select(i => (long)i));
        state.Recovered.ShouldContain((partition, 20L));
    }

    [Fact]
    public async Task PIP_U_046_Posted_work_runs_on_the_partition_worker_before_queued_input()
    {
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(null, state);
        var partition = Partitioning.Of(harness.Door);

        // 100 observations are queued, then work is posted; the worker starts afterwards.
        var inputs = Enumerable.Range(0, 100).Select(_ => harness.IngestAsync(harness.Event(harness.Door))).ToList();
        var recordsSeenByWork = -1;
        var posted = harness.Router.InvokeAsync(
            partition,
            context =>
            {
                recordsSeenByWork = harness.Records().Count;
                context.Emit(harness.Log.Envelope(7, harness.Door, PipelineHarness.AlarmCleared));
                return ValueTask.FromResult(context.Partition);
            },
            Ct).AsTask();
        await harness.StartAsync();

        (await posted.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe(partition);
        await Task.WhenAll(inputs).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // The work ran first; what it emitted is in the log before any queued observation.
        recordsSeenByWork.ShouldBe(0);
        var records = harness.Records();
        records.Count.ShouldBe(101);
        records[0].Envelope.Kind.ShouldBe(PipelineHarness.AlarmCleared);
        state.Records[0].Sequence.ShouldBe(1);

        // Serialised with record processing: work posted from many threads never overlaps a subscriber call.
        var busy = 0;
        var overlaps = 0;
        state.OnRecordsHook = (_, _) =>
        {
            if (Interlocked.Exchange(ref busy, 1) == 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            Thread.SpinWait(2000);
            Volatile.Write(ref busy, 0);
        };
        var mixed = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            mixed.Add(harness.IngestAsync(harness.Event(harness.Door)));
            mixed.Add(Task.Run(
                () => harness.Router.PostAsync(
                    partition,
                    _ =>
                    {
                        if (Interlocked.Exchange(ref busy, 1) == 1)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        Thread.SpinWait(2000);
                        Volatile.Write(ref busy, 0);
                        return ValueTask.CompletedTask;
                    },
                    Ct).AsTask(),
                Ct));
        }

        await Task.WhenAll(mixed).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        overlaps.ShouldBe(0);

        // An exception of posted work goes to the poster; the worker goes on.
        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Router.PostAsync(partition, _ => throw new InvalidOperationException("posted"), Ct).AsTask());
        (await harness.IngestAsync(harness.Event(harness.Door))).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task PIP_U_047_OnTick_cadence()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var inBatch = 0;
        var concurrent = 0;
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(null, state);
        var partition = Partitioning.Of(harness.Door);
        var ticks = 0;
        state.OnTickHook = context =>
        {
            if (context.Partition == partition)
            {
                if (Volatile.Read(ref inBatch) == 1)
                {
                    Interlocked.Increment(ref concurrent);
                }

                Interlocked.Increment(ref ticks);
            }
        };
        state.OnRecordsHook = (_, _) =>
        {
            Volatile.Write(ref inBatch, 1);
            entered.Release();
            release.Wait(Ct);
            Volatile.Write(ref inBatch, 0);
        };
        await harness.StartAsync();

        // Idle worker: one tick per interval (1 s).
        for (var second = 1; second <= 5; second++)
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            var expected = second;
            await IngestHarness.UntilAsync(() => Volatile.Read(ref ticks) == expected, $"tick {expected} did not come");
        }

        harness.Time.Advance(TimeSpan.FromMilliseconds(900));
        Volatile.Read(ref ticks).ShouldBe(5);

        // A long batch: three intervals pass while the subscriber works. No tick runs meanwhile.
        var ingest = harness.IngestAsync(harness.PositionOf(harness.Door, "open"));
        await entered.WaitAsync(Ct);
        harness.Time.Advance(TimeSpan.FromSeconds(3));
        Volatile.Read(ref ticks).ShouldBe(5);
        release.Release();
        await ingest;

        // The missed intervals give one tick after the batch, not three.
        await IngestHarness.UntilAsync(() => Volatile.Read(ref ticks) == 6, "the tick after the batch did not come");
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await IngestHarness.UntilAsync(() => Volatile.Read(ref ticks) == 7, "the next tick did not come");
        concurrent.ShouldBe(0);
        Volatile.Read(ref ticks).ShouldBe(7);
    }

    [Fact]
    public async Task PIP_U_049_Log_failure_keeps_cache_subscribers_and_callers_consistent_with_the_log()
    {
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(null, null, logTimeouts: true, state);
        await harness.StartAsync();
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "closed"))).Accepted.ShouldBe(1);

        // The log stores the first record of the next batch and then stops answering.
        var open = harness.PositionOf(harness.Door, "open");
        var forced = harness.PositionOf(harness.Door, "forced");
        var again = harness.PositionOf(harness.Door, "forced");
        harness.Log.Broker.PublishFault = (message, _) => message.MessageId == open.Id.ToString("D") ? PublishFault.None : PublishFault.Lost;
        var failing = harness.IngestAsync(open, forced, again);
        var result = await harness.PumpAsync(failing, TimeSpan.FromSeconds(1));

        // What the log holds is accepted; the rest is rejected, including the "unchanged" one that depended on it.
        result.Accepted.ShouldBe(1);
        result.Rejected.Select(r => r.ObservationId).ShouldBe([forced.Id, again.Id]);
        result.Rejected.ShouldAllBe(r => r.Error.Code.Value == PipelineErrors.LogUnavailableCode);
        state.Records.Select(r => r.Envelope.Id).ShouldContain(open.Id);
        state.Records.Select(r => r.Envelope.Id).ShouldNotContain(forced.Id);

        // The log is back. "forced" was never stored, so it is a change (no false "unchanged"), with the right prev.
        harness.Log.Broker.PublishFault = null;
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "open"))).Counts().ShouldBe((0, 1, 0, 0));
        (await harness.IngestAsync(harness.PositionOf(harness.Door, "forced"))).Counts().ShouldBe((1, 0, 0, 0));
        var records = harness.Records();
        records.Select(r => harness.PropertyBody(r).Value.AsEnum()).ShouldBe(["closed", "open", "forced"]);
        harness.PropertyBody(records[2]).Prev.ShouldBe(WpValue.Enum("open"));
        state.Records.Select(r => r.Sequence).ShouldBe(records.Select(r => r.Sequence));
    }

    [Fact]
    public async Task PIP_U_050_Shutdown_drains_within_a_bounded_time()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(o => o.MaxBatch = 1, state);
        var stuckDoor = harness.AddDoor(1, (Partitioning.Of(harness.Door) + 1) % Partitioning.PartitionCount);
        state.OnRecordsHook = (context, _) =>
        {
            if (context.Partition == Partitioning.Of(stuckDoor))
            {
                entered.Release();
                release.Wait(Ct);
            }
        };
        await harness.StartAsync();

        // One partition is stuck in a subscriber with two more observations queued; another has queued work.
        var stuck = harness.IngestAsync(harness.Event(stuckDoor));
        await entered.WaitAsync(Ct);
        var behind = Enumerable.Range(0, 2).Select(_ => harness.IngestAsync(harness.Event(stuckDoor))).ToList();
        var healthy = Enumerable.Range(0, 20).Select(_ => harness.IngestAsync(harness.Event(harness.Door))).ToList();

        var stop = harness.Router.StopAsync(Ct);

        // The healthy partition drains completely although another one is stuck.
        (await Task.WhenAll(healthy).WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldAllBe(r => r.Accepted == 1);
        stop.IsCompleted.ShouldBeFalse();

        // After the drain time the stuck worker is given up: its queue is rejected, never dropped silently.
        harness.Time.Advance(harness.Options.DrainTimeout);
        var rejected = await Task.WhenAll(behind).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        rejected.ShouldAllBe(r => r.Rejected.Count == 1 && r.Rejected[0].Error.Code.Value == PipelineErrors.LogUnavailableCode);
        release.Release();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        (await stuck).Accepted.ShouldBe(1);
        harness.Logs.WithEventId(1644).ShouldHaveSingleItem();

        // Stopped: new observations are refused at once.
        var late = await harness.IngestAsync(harness.Event(harness.Door));
        late.Rejected.Single().Error.Code.Value.ShouldBe(PipelineErrors.LogUnavailableCode);
        harness.Router.Running.ShouldBeFalse();
    }
}
