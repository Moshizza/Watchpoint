using Shouldly;
using Watchpoint.Audit.Contracts;
using Xunit;

namespace Watchpoint.Audit.Tests;

[Trait("layer", "U")]
[Trait("spec", "AUD")]
public sealed class AuditWriterTests
{
    [Fact]
    public async Task AUD_U_006_Writer_batches_by_size_and_by_delay_and_sequences_are_contiguous()
    {
        await using var h = new AuditHarness(s => s with { BatchSize = 3, BatchDelay = TimeSpan.FromMilliseconds(50) });
        for (var i = 0; i < 7; i++)
        {
            await h.WriteAsync(AuditHarness.Record());
        }

        await h.StartAsync();

        // Two full batches of 3 are written at once; the last record waits for the batch delay.
        await AuditHarness.UntilAsync(() => h.Store.Rows.Count == 6);
        await h.Clock.WaitForTimersAsync(1, TestContext.Current.CancellationToken);
        h.Store.Rows.Count.ShouldBe(6);
        h.Clock.Advance(TimeSpan.FromMilliseconds(50));
        await AuditHarness.UntilAsync(() => h.Store.Rows.Count == 7);

        h.Store.BatchSizes.ShouldBe([3, 3, 1]);
        h.Store.Rows.Select(r => r.Sequence).ShouldBe([1L, 2, 3, 4, 5, 6, 7]);
        h.Store.Rows.Select(r => r.ChainId).Distinct().ShouldBe(["node-1"]);
        (await h.Verifier.VerifyAsync("node-1", 1, null, TestContext.Current.CancellationToken)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task AUD_U_006_A_failed_batch_is_retried_without_gaps_or_duplicates_even_when_it_had_committed()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();
        await h.WriteStoredAsync(2);

        // First failure: nothing stored. Second failure: stored, then the connection drops.
        h.Store.FailAppends = 1;
        var failing = h.WriteAsync(AuditHarness.Record(severity: AuditSeverity.Critical)).AsTask();
        await AuditHarness.UntilAsync(() => h.Logs.WithEventId(2861).Any());
        await h.Clock.WaitForTimersAsync(2, TestContext.Current.CancellationToken);
        h.Store.CommitBeforeFailing = true;
        h.Store.FailAppends = 1;
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await AuditHarness.UntilAsync(() => h.Logs.WithEventId(2861).Count() >= 2);
        await h.Clock.WaitForTimersAsync(4, TestContext.Current.CancellationToken);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await failing;

        h.Store.Rows.Select(r => r.Sequence).ShouldBe([1L, 2, 3]);
        (await h.Verifier.VerifyAsync("node-1", 1, null, TestContext.Current.CancellationToken)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task AUD_U_007_Critical_records_are_flushed_immediately_and_logged_as_warning()
    {
        await using var h = new AuditHarness(s => s with { BatchDelay = TimeSpan.FromSeconds(30) });
        await h.StartAsync();

        await h.WriteAsync(AuditHarness.Record("identity.breakGlassUsed", AuditSeverity.Critical));

        // Stored without the clock advancing: no batch delay was waited.
        h.Store.Rows.ShouldHaveSingleItem().Action.ShouldBe("identity.breakGlassUsed");
        h.Logs.WithEventId(2863).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AUD_U_008_A_full_channel_writes_to_the_fallback_file_and_the_fallback_is_ingested_in_order_on_start()
    {
        await using var h = new AuditHarness(s => s with { ChannelCapacity = 2, ChannelFullWait = TimeSpan.FromSeconds(2), BatchDelay = TimeSpan.FromMilliseconds(10) });
        await h.WriteAsync(AuditHarness.Record("identity.first"));
        await h.WriteAsync(AuditHarness.Record("identity.second"));

        var third = h.WriteAsync(AuditHarness.Record("identity.third")).AsTask();
        await h.Clock.WaitForTimersAsync(1, TestContext.Current.CancellationToken);
        third.IsCompleted.ShouldBeFalse();
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await third;

        h.Logs.WithEventId(2860).ShouldHaveSingleItem();
        var files = Directory.GetFiles(h.FallbackDirectory, "fallback-*.jsonl");
        files.Length.ShouldBe(1);
        File.ReadAllLines(files[0]).Length.ShouldBe(1);
        h.Store.Rows.ShouldBeEmpty();

        await h.StartAsync();
        await AuditHarness.UntilAsync(() => h.Logs.WithEventId(2850).Any());
        h.Clock.Advance(TimeSpan.FromMilliseconds(10));
        await AuditHarness.UntilAsync(() => h.Store.Rows.Count == 3);

        // The fallback record comes first, then the queued ones; nothing is lost, the file is gone.
        h.Store.Rows.Select(r => r.Action).ShouldBe(["identity.third", "identity.first", "identity.second"]);
        h.Store.Rows.Select(r => r.Sequence).ShouldBe([1L, 2, 3]);
        Directory.GetFiles(h.FallbackDirectory, "fallback-*.jsonl").ShouldBeEmpty();
    }

    [Fact]
    public async Task AUD_U_009_Shutdown_drains_the_queue_and_moves_the_remainder_to_the_fallback_file()
    {
        // Drain: the queue empties within the timeout.
        await using (var h = new AuditHarness())
        {
            await h.StartAsync();
            for (var i = 0; i < 5; i++)
            {
                await h.WriteAsync(AuditHarness.Record());
            }

            await h.Writer.StopAsync(TestContext.Current.CancellationToken);
            h.Store.Rows.Count.ShouldBe(5);
            Directory.Exists(h.FallbackDirectory).ShouldBeFalse();
        }

        // Timeout: the store is down; the batch in flight and the queue go to the fallback file.
        await using var down = new AuditHarness(s => s with { ShutdownTimeout = TimeSpan.FromSeconds(10), BatchDelay = TimeSpan.FromMilliseconds(5) });
        down.Store.FailAppends = int.MaxValue;
        for (var i = 0; i < 3; i++)
        {
            await down.WriteAsync(AuditHarness.Record());
        }

        await down.StartAsync();
        await down.Clock.WaitForTimersAsync(1, TestContext.Current.CancellationToken);
        down.Clock.Advance(TimeSpan.FromMilliseconds(5));
        await AuditHarness.UntilAsync(() => down.Logs.WithEventId(2861).Any());
        await down.WriteAsync(AuditHarness.Record());
        await down.WriteAsync(AuditHarness.Record());

        var stop = down.Writer.StopAsync(TestContext.Current.CancellationToken);
        await AuditHarness.UntilAsync(() => down.Clock.TimersCreated >= 3);
        down.Clock.Advance(TimeSpan.FromSeconds(10));
        await stop;

        down.Store.Rows.ShouldBeEmpty();
        Directory.GetFiles(down.FallbackDirectory, "fallback-*.jsonl").SelectMany(File.ReadAllLines).Count().ShouldBe(5);
    }

    [Fact]
    public async Task AUD_U_006_Invalid_actions_are_rejected_in_debug_and_normalised_in_release()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();
        var invalid = AuditHarness.Record("Bad_Action", AuditSeverity.Critical);

#if DEBUG
        await Should.ThrowAsync<ArgumentException>(async () => await h.WriteAsync(invalid));
#else
        await h.WriteAsync(invalid);
        h.Store.Rows.ShouldHaveSingleItem().Action.ShouldBe("unknown.invalid");
        h.Logs.WithEventId(2864).ShouldHaveSingleItem();
#endif
    }
}
