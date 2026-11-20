using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Xunit;

namespace Watchpoint.Registry.Tests;

/// <summary>Transactional outbox and persisted revision (spec REG §6, §10; T1.09b).</summary>
public sealed partial class RegistryUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task REG_U_048_Revision_persisted_and_continued_after_restart()
    {
        (await _writer.ApplyAsync(SiteBatch("Europe/Paris"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await _writer.ApplyAsync(SiteBatch("UTC"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L, 2L]);

        // A new writer on the same database (node restart) continues the persisted revision: no restart at 1, no reuse.
        using var restarted = new RegistryWriter(_factory, _types, _outbox, _clock, NullLogger<RegistryWriter>.Instance, s => _snapshot = s, () => _snapshot);
        (await restarted.ApplyAsync(SiteBatch("Europe/Paris"), _context, Ct)).IsSuccess.ShouldBeTrue();

        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L, 2L, 3L]);
        await using var db = _factory.CreateDbContext();
        db.Revision.Single().Value.ShouldBe(3);
    }

    [Fact]
    public async Task REG_U_049_Rejected_batch_takes_no_revision_and_writes_no_record()
    {
        (await _writer.ApplyAsync(SiteBatch("Europe/Paris"), _context, Ct)).IsSuccess.ShouldBeTrue();

        var root = Root;
        var stale = new RegistryChangeBatch([new RegistryChange.UpsertLocation(root with { Notes = "stale" }, ExpectedVersion: 99)]);
        (await _writer.ApplyAsync(stale, _context, Ct)).IsSuccess.ShouldBeFalse();

        (await _writer.ApplyAsync(SiteBatch("UTC"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task REG_U_050_Outbox_read_in_revision_order_mark_sent_and_bounded_retention()
    {
        foreach (var tz in new[] { "Europe/Paris", "UTC", "Europe/Paris" })
        {
            (await _writer.ApplyAsync(SiteBatch(tz), _context, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await _outbox.ReadUnsentAsync(2, Ct)).Select(r => r.Revision).ShouldBe([1L, 2L]);

        await _outbox.MarkSentAsync(1, Ct);
        await _outbox.MarkSentAsync(2, Ct);
        await _outbox.MarkSentAsync(2, Ct); // idempotent
        await _outbox.MarkSentAsync(42, Ct); // unknown: nothing happens
        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([3L]);

        // Only sent records older than the threshold are deleted; the unsent one stays whatever its age.
        _clock.Advance(TimeSpan.FromHours(25));
        (await _outbox.DeleteSentBeforeAsync(_clock.GetUtcNow() - TimeSpan.FromHours(24), Ct)).ShouldBe(2);
        await _outbox.MarkSentAsync(3, Ct);
        (await _outbox.DeleteSentBeforeAsync(_clock.GetUtcNow() - TimeSpan.FromHours(24), Ct)).ShouldBe(0);
        (await _outbox.ReadUnsentAsync(10, Ct)).ShouldBeEmpty();
        await using var db = _factory.CreateDbContext();
        db.Outbox.Select(o => o.Revision).ShouldBe([3L]);
    }

    [Fact]
    public async Task REG_U_051_Commit_signals_a_waiting_publisher_once()
    {
        var wait = _outbox.WaitForRecordsAsync(Ct);
        await Task.Yield();
        wait.IsCompleted.ShouldBeFalse();

        (await _writer.ApplyAsync(SiteBatch("Europe/Paris"), _context, Ct)).IsSuccess.ShouldBeTrue();
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Two commits before the next wait complete it once (a signal, not a queue); nothing more is pending after that.
        (await _writer.ApplyAsync(SiteBatch("UTC"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await _writer.ApplyAsync(SiteBatch("Europe/Paris"), _context, Ct)).IsSuccess.ShouldBeTrue();
        await _outbox.WaitForRecordsAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var pending = _outbox.WaitForRecordsAsync(Ct);
        await Task.Yield();
        pending.IsCompleted.ShouldBeFalse();
    }

    private RegistryChangeBatch SiteBatch(string timeZone) =>
        new([new RegistryChange.UpdateSite(_reader.Site with { DefaultTimezone = timeZone })]);
}
