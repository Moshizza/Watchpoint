using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal.Writers;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Xunit;

namespace Watchpoint.History.Tests;

[Trait("layer", "U")]
[Trait("spec", "HIS")]
public sealed class HistoryRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs a consumer on fake time until <paramref name="done"/> holds: time moves in small steps, with no real waiting.</summary>
    private static async Task RunUntilAsync(HistoryHarness h, HistoryConsumerDefinition definition, Func<bool> done, TimeSpan? step = null, int maxSteps = 400_000)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = h.Runner.RunAsync(definition, cts.Token);
        var advance = step ?? TimeSpan.FromMilliseconds(5);
        for (var i = 0; i < maxSteps && !done(); i++)
        {
            h.Time.Advance(advance);
            await Task.Yield();
        }

        var reached = done();
        await cts.CancelAsync();
        await run;
        reached.ShouldBeTrue("the consumer did not reach the expected state");
    }

    private static HistoryConsumerDefinition Definition(HistoryHarness h, string name) => h.Writers.Create().Single(d => d.Name == name);

    [Fact]
    public async Task HIS_U_014_Batches_are_bounded_by_size_and_time_and_unreadable_records_are_skipped()
    {
        using var h = new HistoryHarness();
        var definitions = h.Writers.Create();

        // The five consumers of spec HIS §5, each with its stream and kind filter.
        definitions.Select(d => (d.Name, d.Stream, string.Join(",", d.Kinds), d.Table)).ShouldBe(
        [
            ("his-events", LogStream.Log, "event,sysevent,operator.note", "events"),
            ("his-events-sys", LogStream.Sys, "sysevent,operator.note", "events"),
            ("his-alarms", LogStream.Log, "alarm.*", "alarms"),
            ("his-commands", LogStream.Log, "command.*", "commands"),
            ("his-properties", LogStream.Log, "property", "property_changes"),
        ]);

        // A consumer asks for at most 5,000 records, or what arrived within one second, and gives JetStream time to wait for the commit.
        var good = h.Property(1, "t.temp", WpValue.Int(1));
        var unknown = good with { SchemaVersion = 99 };
        h.Consumers.Enqueue(unknown, good);
        await RunUntilAsync(h, Definition(h, "his-properties"), () => h.Consumers.Acked.Count == 1);
        var created = h.Consumers.Created.ShouldHaveSingleItem();
        created.Name.ShouldBe("his-properties");
        created.Stream.ShouldBe(LogStream.Log);
        created.Kinds.ShouldBe(["property"]);
        created.Options.MaxBatch.ShouldBe(5000);
        created.Options.MaxWait.ShouldBe(TimeSpan.FromSeconds(1));
        created.Options.AckWait.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.FromMinutes(1));

        // The record this node cannot read is left out of the batch; the rest is written and acknowledged.
        h.Store.PropertyWrites.ShouldHaveSingleItem().Single().RecordId.ShouldBe(good.Envelope.Id);
        h.Logs.WithEventId(3205).ShouldHaveSingleItem();

        // A batch with nothing readable is acknowledged without a write.
        h.Consumers.Enqueue(unknown);
        await RunUntilAsync(h, Definition(h, "his-properties"), () => h.Consumers.Acked.Count == 2);
        h.Store.PropertyWrites.Count.ShouldBe(1);

        // Settings: the batch size is 1 to 10,000 (the consumer's limit), the default is 5,000.
        HistoryModule.ReadSettings(new ConfigurationBuilder().Build()).BatchSize.ShouldBe(5000);
        HistoryModule.ReadSettings(Config(("History:BatchSize", "250"), ("History:LagWarning", "PT10M"), ("History:QueryTimeout", "PT5S"))).ShouldBe(
            new HistorySettings { BatchSize = 250, LagWarning = TimeSpan.FromMinutes(10), QueryTimeout = TimeSpan.FromSeconds(5) });
        foreach (var bad in new[] { "0", "10001", "-1", "many" })
        {
            Should.Throw<InvalidOperationException>(() => HistoryModule.ReadSettings(Config(("History:BatchSize", bad)))).Message.ShouldContain("History:BatchSize");
        }

        Should.Throw<InvalidOperationException>(() => HistoryModule.ReadSettings(Config(("History:LagWarning", "5 minutes")))).Message.ShouldContain("History:LagWarning");
        Should.Throw<InvalidOperationException>(() => HistoryModule.ReadSettings(Config(("History:QueryTimeout", "PT0S")))).Message.ShouldContain("History:QueryTimeout");
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public async Task HIS_U_015_Nothing_is_acknowledged_before_the_commit_and_a_failing_write_backs_off_without_reading_on()
    {
        using var h = new HistoryHarness();
        var attempts = new List<DateTimeOffset>();
        h.Store.OnAttempt = () => attempts.Add(h.Time.GetUtcNow());
        h.Store.FailNext(8);
        h.Consumers.Enqueue(h.Property(1, "t.temp", WpValue.Int(1)));
        h.Consumers.Enqueue(h.Property(2, "t.temp", WpValue.Int(2)));
        var ackedBeforeSuccess = false;
        h.Consumers.OnAcked = _ => ackedBeforeSuccess |= h.Store.PropertyWrites.Count == 0;

        await RunUntilAsync(h, Definition(h, "his-properties"), () => h.Consumers.Acked.Count == 2);

        // Eight failures, then the batch is written once and acknowledged; the second batch follows.
        attempts.Count.ShouldBe(10);
        ackedBeforeSuccess.ShouldBeFalse();
        h.Store.PropertyWrites.Count.ShouldBe(2);
        h.Consumers.Acked.ShouldBe([1, 2]);

        // Back-off 1 s, 2 s, 4 s … doubling, capped at 60 s; never earlier than the delay.
        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => b - a).Take(8).ToList();
        for (var i = 0; i < 6; i++)
        {
            var delay = TimeSpan.FromSeconds(1 << i);
            gaps[i].ShouldBeGreaterThanOrEqualTo(delay);
            gaps[i].ShouldBeLessThan(delay * 2);
        }

        gaps[6].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        gaps[7].ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(60));
        gaps[7].ShouldBeLessThan(TimeSpan.FromSeconds(100));

        // While the write failed the consumer was not asked for another batch: each batch was delivered once, so nothing piles up.
        h.Consumers.Deliveries.ShouldBe([(1, 1), (2, 1)]);
        h.Logs.WithEventId(3203).Count().ShouldBe(8);
        h.Logs.WithEventId(3203).ShouldAllBe(l => l.Level == LogLevel.Error);

        await AcknowledgementFailuresAreRepeatedWithoutWritingAgainAsync();
        await ASlowDatabaseHoldsTheLogBackAndBatchesStayInOrderAsync();
    }

    private static async Task AcknowledgementFailuresAreRepeatedWithoutWritingAgainAsync()
    {
        using var h = new HistoryHarness();
        h.Consumers.AckFailures = 2;
        h.Consumers.Enqueue(h.Property(1, "t.temp", WpValue.Int(1)));

        await RunUntilAsync(h, Definition(h, "his-properties"), () => h.Consumers.Acked.Count == 1);

        h.Consumers.AckAttempts.ShouldBe(3);
        h.Store.WriteAttempts.ShouldBe(1);
    }

    private static async Task ASlowDatabaseHoldsTheLogBackAndBatchesStayInOrderAsync()
    {
        using var h = new HistoryHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.Gate = release.Task;
        for (var n = 1; n <= 3; n++)
        {
            h.Consumers.Enqueue(h.Property(n, "t.temp", WpValue.Int(n)));
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = h.Runner.RunAsync(Definition(h, "his-properties"), cts.Token);
        for (var i = 0; i < 2000 && h.Store.WriteAttempts == 0; i++)
        {
            await Task.Yield();
        }

        // The first write is stuck: one batch is held, the others are not even delivered, and nothing is acknowledged.
        h.Store.WriteAttempts.ShouldBe(1);
        for (var i = 0; i < 2000; i++)
        {
            await Task.Yield();
        }

        h.Consumers.Deliveries.ShouldBe([(1, 1)]);
        h.Consumers.Acked.ShouldBeEmpty();

        release.SetResult();
        for (var i = 0; i < 200_000 && h.Consumers.Acked.Count < 3; i++)
        {
            await Task.Yield();
        }

        await cts.CancelAsync();
        await run;
        h.Consumers.Acked.ShouldBe([1, 2, 3]);
        h.Consumers.Deliveries.ShouldBe([(1, 1), (2, 1), (3, 1)]);
        h.Store.PropertyWrites.Select(w => w.Single().Time).ShouldBe(h.Store.PropertyWrites.Select(w => w.Single().Time).Order());
    }

    [Fact]
    public async Task HIS_U_016_A_consumer_that_falls_behind_raises_one_lag_warning_system_event_per_period()
    {
        using var h = new HistoryHarness();
        var consumer = "his-events";
        var now = h.Time.GetUtcNow();

        await h.Lag.ObserveAsync(consumer, now.AddMinutes(-4), Ct);
        h.EventLog.Global.ShouldBeEmpty();

        await h.Lag.ObserveAsync(consumer, now.AddMinutes(-6), Ct);
        var (stream, record) = h.EventLog.Global.ShouldHaveSingleItem();
        stream.ShouldBe(LogStream.Sys);
        record.Kind.ShouldBe(HistoryRecordKinds.SysEvent);
        record.P.ShouldBe(0);
        record.Origin.ShouldBe(RecordOrigin.System);
        var body = h.Registry.DeserializeBody<SysEventBody>(HistoryRecordKinds.SysEvent, record.Body);
        body.Kind.ShouldBe("history.lagWarning");
        body.Severity.ShouldBe(Severity.Warning);
        body.Payload["consumer"].AsString().ShouldBe(consumer);
        body.Payload["lagSeconds"].AsInt().ShouldBe(360);
        var logged = h.Logs.WithEventId(3202).ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogLevel.Warning);
        logged.Properties["LagSeconds"].ShouldBe(360L);
        logged.Properties["Consumer"].ShouldBe(consumer);

        // Still behind, same period: no second event. Another period: the warning is repeated.
        await h.Lag.ObserveAsync(consumer, now.AddMinutes(-6), Ct);
        h.EventLog.Global.Count.ShouldBe(1);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Lag.ObserveAsync(consumer, now.AddMinutes(-6), Ct);
        h.EventLog.Global.Count.ShouldBe(2);

        // Caught up: the next time it falls behind warns at once.
        h.Lag.Processed(consumer, h.Time.GetUtcNow());
        await h.Lag.ObserveAsync(consumer, h.Time.GetUtcNow().AddMinutes(-9), Ct);
        h.EventLog.Global.Count.ShouldBe(3);
        await h.Lag.ObserveAsync("his-alarms", h.Time.GetUtcNow().AddMinutes(-2), Ct);
        h.EventLog.Global.Count.ShouldBe(3);

        await TheLagGrowsAndIsReportedWhileTheDatabaseIsDownAsync();
    }

    private static async Task TheLagGrowsAndIsReportedWhileTheDatabaseIsDownAsync()
    {
        using var h = new HistoryHarness();
        h.Store.FailNext(1_000_000);
        h.Consumers.Enqueue(h.Property(1, "t.temp", WpValue.Int(1), ts: h.Time.GetUtcNow().AddMinutes(-1)));

        await RunUntilAsync(h, Definition(h, "his-properties"), () => h.EventLog.Global.Count >= 1, step: TimeSpan.FromMilliseconds(50));

        // The warning is raised from the retry loop, with the batch still unacknowledged and unwritten.
        h.Consumers.Acked.ShouldBeEmpty();
        h.Store.PropertyWrites.ShouldBeEmpty();
        h.Logs.WithEventId(3202).ShouldNotBeEmpty();
        h.EventLog.Global.First().Record.Kind.ShouldBe(HistoryRecordKinds.SysEvent);
    }
}
