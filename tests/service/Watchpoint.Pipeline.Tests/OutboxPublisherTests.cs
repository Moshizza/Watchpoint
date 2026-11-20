using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Kernel.Testing;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Tests.Fakes;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>The registry outbox publisher on the in-memory broker and an in-memory outbox (spec PIP §5.5, §14; T1.09b).</summary>
[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class OutboxPublisherTests : IDisposable
{
    private readonly TestClock _clock = new(TestTime.Start);
    private readonly PipelineHarness _harness;
    private readonly FakeRegistryOutbox _outbox;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _running = [];

    public OutboxPublisherTests()
    {
        _harness = new PipelineHarness(time: _clock);
        _outbox = new FakeRegistryOutbox(_harness.Time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CfgStream => _harness.Naming.StreamName(LogStream.Cfg);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public async Task PIP_U_051_Outbox_published_in_revision_order_with_revision_dedup_id()
    {
        // Written out of order (the fake lets us), unsent: the publisher sends them by revision, one at a time.
        var r3 = _outbox.Add(3);
        var r1 = _outbox.Add(1, user: "0192a9d0-0000-7000-8000-000000000001");
        var r2 = _outbox.Add(2);
        var publisher = Start();

        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 3);

        var stored = _harness.Broker.Messages(CfgStream);
        stored.Select(m => m.Headers["Nats-Msg-Id"]).ShouldBe(Enumerable.Range(1, 3).Select(r => RegistryOutboxPublisher.RecordId(r).ToString("D")));
        stored.ShouldAllBe(m => m.Headers["Wp-Kind"] == RecordKinds.RegistryChanged && m.Subject == _harness.Options.SubjectPrefix + ".cfg.registry.changed");
        _harness.Broker.PublishAttempts.Select(a => a.MessageId).ShouldBe(stored.Select(m => m.Headers["Nats-Msg-Id"]));

        // The record is the registry.changed body of PIP §5.3; a user id in the context becomes the origin.
        var records = await _harness.Reader.ReadGlobalAsync(LogStream.Cfg, 1, Ct).ToListAsync();
        records.Select(r => r.Envelope.Id).ShouldBe(new[] { r1, r2, r3 }.Select(r => RegistryOutboxPublisher.RecordId(r.Revision)));
        var body = records[0].BodyAs<RegistryChangedBody>(_harness.Registry);
        body.Revision.ShouldBe(1);
        body.Context.ShouldBe(new RegistryChangedContext(r1.Context.Actor!.Value.Value.ToString(), r1.Context.Origin, null));
        body.Changes.ShouldBe([new RegistryChangedEntity("device", r1.Changes[0].Id, "updated")]);
        records[0].Envelope.Origin.ShouldBe(new RecordOrigin(RecordOriginType.User, r1.Context.Actor!.Value.Value));
        records[0].Envelope.Ts.ShouldBe(r1.CreatedAt);
        records[1].Envelope.Origin.ShouldBe(RecordOrigin.System);

        // The id is a function of the revision (UUID v8), so a repeated publish is a duplicate for the log.
        RegistryOutboxPublisher.RecordId(7).ShouldBe(RegistryOutboxPublisher.RecordId(7));
        RegistryOutboxPublisher.RecordId(7).ShouldNotBe(RegistryOutboxPublisher.RecordId(8));
        RegistryOutboxPublisher.RecordId(7).Version.ShouldBe(8);
        RegistryOutboxPublisher.RecordId(7).Variant.ShouldBeInRange(8, 11); // RFC 4122 variant (bits 10xx)

        // A record committed later wakes the idle publisher without waiting for the poll interval.
        _outbox.Add(4);
        await SettleAsync(() => _outbox.SentRevisions.Count == 4);
        _outbox.SentRevisions.ShouldBe([1L, 2L, 3L, 4L]);
        _harness.Broker.Messages(CfgStream).Count.ShouldBe(4);
        _harness.Logs.WithEventId(1612).ShouldHaveSingleItem().Properties["Records"].ShouldBe(3); // the backlog; a single record logs nothing
        await StopAsync(publisher);
    }

    [Fact]
    public async Task PIP_U_052_Outbox_retries_with_back_off_while_the_log_is_down_then_catches_up()
    {
        _outbox.Add(1);
        _outbox.Add(2);
        _harness.Broker.PublishFault = (_, _) => PublishFault.Lost;
        var publisher = Start();

        // Every attempt fails after Log:AppendTimeout; the publisher waits 1 s, 2 s, 4 s … (TimeProvider) and nothing is marked.
        await _harness.PumpUntilAsync(() => _harness.Logs.WithEventId(1611).Count() >= 4, TimeSpan.FromSeconds(1));
        _harness.Logs.WithEventId(1611).Take(4).Select(l => (TimeSpan)l.Properties["Delay"]!)
            .ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)]);
        _harness.Logs.WithEventId(1611).ShouldAllBe(l => (long)l.Properties["Revision"]! == 1L && l.Level == LogLevel.Warning);
        _outbox.SentRevisions.ShouldBeEmpty();
        _harness.Broker.Messages(CfgStream).ShouldBeEmpty();

        // The log is back: both records are published, in order, once; the recovery is logged.
        _harness.Broker.PublishFault = null;
        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 2, TimeSpan.FromSeconds(1));
        _harness.Broker.Messages(CfgStream).Select(m => m.Headers["Nats-Msg-Id"])
            .ShouldBe([RegistryOutboxPublisher.RecordId(1).ToString("D"), RegistryOutboxPublisher.RecordId(2).ToString("D")]);
        var caughtUp = _harness.Logs.WithEventId(1612).ShouldHaveSingleItem();
        caughtUp.Properties["First"].ShouldBe(1L);
        caughtUp.Properties["Last"].ShouldBe(2L);
        ((int)caughtUp.Properties["FailedAttempts"]!).ShouldBeGreaterThanOrEqualTo(4);
        await StopAsync(publisher);
    }

    [Fact]
    public async Task PIP_U_053_Outbox_publisher_resumes_from_the_oldest_unsent_record()
    {
        // Records 1…3 were sent by the node before it stopped; 4 and 5 were committed while the log was down.
        foreach (var revision in new[] { 1L, 2L, 3L })
        {
            _outbox.Add(revision, sent: true);
        }

        _outbox.Add(4);
        _outbox.Add(5);

        var publisher = Start();
        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 5);

        _harness.Broker.Messages(CfgStream).Select(m => m.Headers["Nats-Msg-Id"])
            .ShouldBe([RegistryOutboxPublisher.RecordId(4).ToString("D"), RegistryOutboxPublisher.RecordId(5).ToString("D")]);
        _harness.Broker.PublishAttempts.Count.ShouldBe(2);
        await StopAsync(publisher);
    }

    [Fact]
    public async Task PIP_U_054_Outbox_record_marked_sent_only_after_ack_and_duplicate_after_crash_stored_once()
    {
        // The acknowledgement is held back: the record is in the log but not yet marked.
        var ackGate = new TaskCompletionSource();
        _harness.Broker.AckGate = ackGate.Task;
        _outbox.Add(1);
        var publisher = Start();
        await _harness.PumpUntilAsync(() => _harness.Broker.Messages(CfgStream).Count == 1);
        for (var i = 0; i < 1000; i++)
        {
            await Task.Yield();
        }

        _outbox.SentRevisions.ShouldBeEmpty();

        ackGate.SetResult();
        _harness.Broker.AckGate = null;
        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 1);
        await StopAsync(publisher);

        // The node crashed after the log acknowledged revision 2 but before the mark was stored: after the restart the
        // record is published again with the same id, and the log keeps one copy.
        var r2 = _outbox.Add(2);
        var envelope = Publisher().Envelope(r2);
        (await _harness.Writer.AppendGlobalAsync(LogStream.Cfg, envelope, Ct)).ShouldBe(2L);
        _outbox.Unmark(2);

        var restarted = Start();
        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 2);
        _harness.Broker.PublishAttempts.Count(a => a.MessageId == envelope.Id.ToString("D")).ShouldBe(2);
        _harness.Broker.Messages(CfgStream).Select(m => m.Sequence).ShouldBe([1L, 2L]);
        await StopAsync(restarted);
    }

    [Fact]
    public async Task PIP_U_055_Outbox_record_refused_by_the_log_is_skipped_and_logged()
    {
        // 15,000 changes make a body above the 1 MB message limit: the log can never store it.
        _outbox.Add(1, changes: 15_000);
        _outbox.Add(2);
        var publisher = Start();

        await _harness.PumpUntilAsync(() => _outbox.SentRevisions.Count == 2);
        _harness.Broker.Messages(CfgStream).Select(m => m.Headers["Nats-Msg-Id"]).ShouldBe([RegistryOutboxPublisher.RecordId(2).ToString("D")]);
        var skipped = _harness.Logs.WithEventId(1613).ShouldHaveSingleItem();
        skipped.Level.ShouldBe(LogLevel.Error);
        skipped.Properties["Revision"].ShouldBe(1L);
        skipped.Properties["Code"].ShouldBe(PipelineErrors.RecordTooLargeCode);
        await StopAsync(publisher);
    }

    [Fact]
    public async Task PIP_U_056_Outbox_sent_records_pruned_after_retention_once_per_interval_and_database_faults_retried()
    {
        // The clock is advanced by exactly one poll interval at a time, once the publisher armed its poll timer: a longer
        // advance would also fire the timer armed by the round it triggered.
        var poll = _harness.Options.OutboxPollInterval = TimeSpan.FromMinutes(5);
        var start = _clock.GetUtcNow();
        _outbox.Add(1, sent: true);
        var publisher = Start();

        // An idle round is one read followed by one armed poll timer, so the timer of the last read is timer number Reads.
        async Task PollAsync(TimeSpan? advance = null)
        {
            var reads = _outbox.Reads;
            await _clock.WaitForTimersAsync(reads, Ct);
            _clock.Advance(advance ?? poll);

            // The round is over once the next poll timer is armed (after the read and the prune check).
            await SettleAsync(() => _outbox.Reads > reads && _clock.TimersCreated >= _outbox.Reads, () => $"reads {reads}, timers {_clock.TimersCreated}, now {_clock.GetUtcNow():O}");
        }

        // Idle: the first round prunes sent records older than the retention (24 h): nothing to delete yet.
        await SettleAsync(() => _outbox.PruneThresholds.Count == 1);
        _outbox.PruneThresholds[0].ShouldBe(start - _harness.Options.OutboxSentRetention);
        _outbox.Revisions.ShouldBe([1L]);

        // Polls keep running, but the next prune waits for the prune interval (1 h).
        for (var round = 1; round <= 11; round++)
        {
            await PollAsync();
            _outbox.PruneThresholds.Count.ShouldBe(1, $"round {round}");
        }

        await PollAsync();
        _clock.GetUtcNow().ShouldBe(start + _harness.Options.OutboxPruneInterval);
        _outbox.PruneThresholds.Count.ShouldBe(2);
        _outbox.PruneThresholds[1].ShouldBe(_clock.GetUtcNow() - _harness.Options.OutboxSentRetention);
        _outbox.Revisions.ShouldBe([1L]);

        // 25 h after it was sent, the record is deleted.
        _harness.Options.OutboxPollInterval = TimeSpan.FromHours(24);
        await PollAsync(poll);
        await PollAsync(TimeSpan.FromHours(24));
        _outbox.PruneThresholds.Count.ShouldBe(3);
        _outbox.Revisions.ShouldBeEmpty();

        // A database fault is logged and retried with back-off (1 s, 2 s, …); the publisher survives and goes on when the
        // database is back.
        _outbox.Fault = new InvalidOperationException("injected: database down");
        var reads = _outbox.Reads;
        await _clock.WaitForTimersAsync(reads, Ct);
        _clock.Advance(TimeSpan.FromHours(24));
        await SettleAsync(() => _harness.Logs.WithEventId(1614).Count() == 1);
        await _clock.WaitForTimersAsync(reads + 1, Ct); // the retry delay
        _clock.Advance(TimeSpan.FromSeconds(1));
        await SettleAsync(() => _harness.Logs.WithEventId(1614).Count() == 2);
        await _clock.WaitForTimersAsync(reads + 2, Ct);
        _harness.Logs.WithEventId(1614).Select(l => (TimeSpan)l.Properties["Delay"]!).ShouldBe([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);
        _harness.Logs.WithEventId(1614).ShouldAllBe(l => l.Level == LogLevel.Warning && l.Exception is InvalidOperationException);

        _outbox.Fault = null;
        _outbox.Add(2);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await SettleAsync(() => _outbox.SentRevisions.Contains(2));
        _harness.Broker.Messages(CfgStream).Count.ShouldBe(1);
        await StopAsync(publisher);
    }

    /// <summary>Lets the publisher's continuations run (no time passes) until <paramref name="done"/> holds.</summary>
    private static async Task SettleAsync(Func<bool> done, Func<string>? describe = null)
    {
        for (var i = 0; i < 100_000; i++)
        {
            if (done())
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException("The condition was not met without advancing the clock. " + describe?.Invoke());
    }

    private RegistryOutboxPublisher Publisher() => new(
        _outbox,
        _harness.Writer,
        _harness.Registry,
        _harness.Options,
        _harness.Time,
        _harness.Metrics,
        _harness.Logger<RegistryOutboxPublisher>());

    private (RegistryOutboxPublisher Publisher, Task Run, CancellationTokenSource Stop) Start()
    {
        var publisher = Publisher();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var run = publisher.RunAsync(stop.Token);
        _running.Add(run);
        return (publisher, run, stop);
    }

    private static async Task StopAsync((RegistryOutboxPublisher Publisher, Task Run, CancellationTokenSource Stop) running)
    {
        await running.Stop.CancelAsync();
        await running.Run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        running.Stop.Dispose();
        running.Publisher.Dispose();
    }
}
