using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.SideEffects;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>Connection quota, coalescing, flooding (spec PIP §8) and the side-effect queue (spec PIP §9).</summary>
[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class QuotaAndSideEffectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PIP_U_039_Quota_coalescing_keeps_latest_per_property()
    {
        var state = new RecordingSubscriber("state", 100);
        await using var harness = new IngestHarness(
            o =>
            {
                o.ConnectionRate = 1;
                o.ConnectionBurst = 1;
            },
            state);
        await harness.StartAsync();

        // 100 updates of one property at once, with a quota of one per second.
        var updates = Enumerable.Range(1, 100)
            .Select(i => harness.Property(harness.Door, IngestHarness.Temperature, WpValue.Decimal(i)))
            .ToArray();
        var call = harness.IngestAsync(updates);
        var result = await harness.PumpAsync(call, TimeSpan.FromMilliseconds(100));

        // The first passes with the token of the bucket; of the 99 that wait only the latest survives.
        result.Counts().ShouldBe((2, 0, 98, 0));
        var values = harness.Records().Select(r => harness.PropertyBody(r).Value.AsDecimal()).ToList();
        values.ShouldBe([1m, 100m]);
        harness.Counter("watchpoint.pipeline.observations", "outcome", "coalesced").ShouldBe(98);
        harness.Logs.WithEventId(1630).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task PIP_U_040_Quota_events_never_coalesced()
    {
        await using var harness = new IngestHarness(o =>
        {
            o.ConnectionRate = 10;
            o.ConnectionBurst = 1;
        });
        await harness.StartAsync();

        // 100 identical events and, between them, updates of one property.
        var batch = new List<Observation>();
        for (var i = 0; i < 100; i++)
        {
            batch.Add(harness.Event(harness.Door));
            batch.Add(harness.Property(harness.Door, IngestHarness.Temperature, WpValue.Decimal(i)));
        }

        var result = await harness.PumpAsync(harness.IngestAsync([.. batch]), TimeSpan.FromMilliseconds(50));

        // Every event is in the log, in the order it was reported; only property values were coalesced.
        var events = batch.Where(o => o.Kind == ObservationKind.Event).Select(o => o.Id).ToList();
        var stored = harness.Records().Where(r => r.Envelope.Kind == RecordKinds.Event).Select(r => r.Envelope.Id).ToList();
        stored.Count.ShouldBe(100);
        stored.SequenceEqual(events).ShouldBeTrue("events must reach the log in the order they were reported");
        result.Rejected.ShouldBeEmpty();
        result.Coalesced.ShouldBe(99, "the first event took the only token; of the 100 property values that waited, the latest survives");
        result.Accepted.ShouldBe(101);
    }

    [Fact]
    public async Task PIP_U_041_Flooding_fault()
    {
        await using var harness = new IngestHarness(o =>
        {
            o.ConnectionRate = 1;
            o.ConnectionBurst = 1;
            o.FloodingHigh = 20;
            o.FloodingLow = 10;
            o.FloodingAfter = TimeSpan.FromHours(1);
        });
        await harness.StartAsync();

        // Three concurrent batches of ten events with a quota of one per second: 29 wait, more than the limit of 20.
        var calls = Enumerable.Range(0, 3)
            .Select(_ => harness.IngestAsync([.. Enumerable.Range(0, 10).Select(_ => harness.Event(harness.Door))]))
            .ToList();

        // The calls run on their own threads when the machine is busy: wait until the quota exists and all 29 events wait
        // (the fake clock does not move, so nothing leaves the buffer meanwhile) before looking at it.
        await IngestHarness.UntilAsync(() => harness.Ingest.QuotaOf(harness.Connection)?.PendingEvents == 29, "the 29 events did not reach the buffer");
        var quota = harness.Ingest.QuotaOf(harness.Connection)!;
        quota.Flooding.ShouldBeTrue();
        harness.Flooding.Signals.ShouldBe([(harness.Connection, true, 21)]);
        harness.Logs.WithEventId(1631).ShouldHaveSingleItem();

        // The next ingest waits: it adds nothing while 10 or more events wait.
        var late = harness.IngestAsync([.. Enumerable.Range(0, 5).Select(_ => harness.Event(harness.Door))]);
        late.IsCompleted.ShouldBeFalse();
        quota.PendingEvents.ShouldBe(29);

        // One event per second leaves the buffer; below 10 the waiting ingest goes on.
        for (var i = 0; i < 300 && quota.PendingEvents >= 10; i++)
        {
            quota.PendingEvents.ShouldBeGreaterThanOrEqualTo(10);
            late.IsCompleted.ShouldBeFalse();
            var before = quota.PendingEvents;
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            await IngestHarness.UntilAsync(() => quota.PendingEvents != before, "the buffer did not drain");
        }

        await IngestHarness.UntilAsync(() => quota.PendingEvents >= 13, "the waiting ingest did not continue below the low mark");
        quota.Flooding.ShouldBeTrue("the fault stays until the connection is within its quota again");

        var results = await harness.PumpAsync(Task.WhenAll(calls.Append(late)), TimeSpan.FromSeconds(1));
        results.Sum(r => r.Accepted).ShouldBe(35);
        harness.Records().Count.ShouldBe(35);
        await IngestHarness.UntilAsync(() => !quota.Flooding, "the flooding fault was not cleared");
        harness.Flooding.Signals.ShouldBe([(harness.Connection, true, 21), (harness.Connection, false, 0)]);
    }

    [Fact]
    public async Task PIP_U_044_Side_effect_classes_isolated()
    {
        using var logs = new LogCollector();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        using var metrics = new PipelineMetrics();
        var options = new IngestOptions();
        options.SideEffectCapacity[SideEffectClass.Background] = 2;
        options.SideEffectWorkers[SideEffectClass.Background] = 1;
        await using var queue = new SideEffectQueue(options, Watchpoint.Kernel.Testing.TestTime.CreateProvider(), metrics, loggers.CreateLogger<SideEffectQueue>());
        using var blocked = new SemaphoreSlim(0);
        using var started = new SemaphoreSlim(0);
        SideEffect Background() => new(Guid.CreateVersion7(), SideEffectClass.Background, "report", async ct =>
        {
            started.Release();
            await blocked.WaitAsync(ct);
        }, TimeSpan.FromHours(1));

        // One background effect runs and never ends; two more fill the queue; the fourth is refused.
        queue.TryEnqueue(Background()).ShouldBeTrue();
        await started.WaitAsync(Ct);
        queue.TryEnqueue(Background()).ShouldBeTrue();
        queue.TryEnqueue(Background()).ShouldBeTrue();
        queue.TryEnqueue(Background()).ShouldBeFalse();
        logs.WithEventId(1661).ShouldHaveSingleItem();

        // Alarm actions have their own queue and workers: they still run.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue(new SideEffect(Guid.CreateVersion7(), SideEffectClass.AlarmAction, "siren", _ =>
        {
            ran.SetResult();
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await IngestHarness.UntilAsync(() => queue.GetStats().Classes[SideEffectClass.AlarmAction].Completed == 1, "the alarm action did not complete");
        var background = queue.GetStats().Classes[SideEffectClass.Background];
        background.ShouldBe(new SideEffectClassStats(Queued: 2, Running: 1, Completed: 0, Failed: 0, Rejected: 1, Duplicates: 0));
    }

    [Fact]
    public async Task PIP_U_045_Side_effect_timeout_and_retry()
    {
        using var logs = new LogCollector();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        using var meters = new TestMeterFactory();
        using var metrics = new PipelineMetrics(meters);
        var time = Watchpoint.Kernel.Testing.TestTime.CreateProvider();
        await using var queue = new SideEffectQueue(new IngestOptions(), time, metrics, loggers.CreateLogger<SideEffectQueue>());
        var runs = new List<DateTimeOffset>();
        var cancelled = 0;
        var effect = new SideEffect(Watchpoint.Testing.Ids.TestIds.Uuid(77), SideEffectClass.Webhook, "hook", async ct =>
        {
            lock (runs)
            {
                runs.Add(time.GetUtcNow());
            }

            try
            {
                // Hangs until its timeout cancels it.
                await Task.Delay(Timeout.InfiniteTimeSpan, time, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
        }, TimeSpan.FromSeconds(5), MaxAttempts: 3);
        int Runs()
        {
            lock (runs)
            {
                return runs.Count;
            }
        }

        queue.TryEnqueue(effect).ShouldBeTrue();

        // The same id again (a record processed twice): accepted, but it does not run a second time.
        queue.TryEnqueue(effect with { Kind = "duplicate" }).ShouldBeTrue();

        // Run 1 at t0, cancelled at its timeout (5 s); back-off 1 s; run 2, cancelled; back-off 2 s; run 3, cancelled.
        var start = time.GetUtcNow();
        await IngestHarness.UntilAsync(() => Runs() == 1, "the first run did not start");
        foreach (var (advance, expectedRuns, expectedCancelled) in new[] { (5, 1, 1), (1, 2, 1), (5, 2, 2), (2, 3, 2), (5, 3, 3) })
        {
            time.Advance(TimeSpan.FromSeconds(advance) - TimeSpan.FromMilliseconds(1));
            Runs().ShouldBe(expectedRuns - (advance <= 2 ? 1 : 0));
            time.Advance(TimeSpan.FromMilliseconds(1));
            await IngestHarness.UntilAsync(
                () => Runs() == expectedRuns && Volatile.Read(ref cancelled) == expectedCancelled,
                $"expected {expectedRuns} runs and {expectedCancelled} cancellations");
        }

        await IngestHarness.UntilAsync(() => queue.GetStats().Classes[SideEffectClass.Webhook].Failed == 1, "the effect was not given up");
        runs.ShouldBe([start, start.AddSeconds(6), start.AddSeconds(13)]);

        // Given up after three attempts: logged (1660), counted, and never run again.
        var failure = logs.WithEventId(1660).ShouldHaveSingleItem();
        failure.RenderedMessage.ShouldContain("hook (Webhook) failed after 3 attempts");
        meters.Sum("watchpoint.pipeline.sideeffects.failed", "kind", "hook").ShouldBe(1);
        time.Advance(TimeSpan.FromMinutes(10));
        var stats = queue.GetStats().Classes[SideEffectClass.Webhook];
        stats.ShouldBe(new SideEffectClassStats(Queued: 0, Running: 0, Completed: 0, Failed: 1, Rejected: 0, Duplicates: 1));
        Runs().ShouldBe(3);
    }
}
