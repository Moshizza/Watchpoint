using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Xunit;

namespace Watchpoint.Audit.Tests;

[Trait("layer", "U")]
[Trait("spec", "AUD")]
public sealed class AuditVerificationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AUD_U_004_Verification_detects_a_modified_field_a_deleted_record_an_inserted_record_and_reordered_records()
    {
        // Modified field.
        await using (var h = await ChainAsync(6))
        {
            h.Store.Rows[2] = h.Store.Rows[2] with { Reason = "tampered" };
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.HashMismatch && i.Sequence == 3);
        }

        // Deleted record: a gap, and the next record no longer links.
        await using (var h = await ChainAsync(6))
        {
            h.Store.Rows.RemoveAt(2);
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.SequenceGap && i.Sequence == 3);
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.HashMismatch && i.Sequence == 4);
        }

        // Inserted record: properly sealed after record 2, but the chain behind it is not re-hashed.
        await using (var h = await ChainAsync(6))
        {
            var forged = HashChain.Seal(h.Store.Rows[1] with { Sequence = 3, Reason = "forged", Hash = [] }, h.Store.Rows[1].Hash);
            h.Store.Rows.Insert(2, forged with { Id = 999 });
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.ShouldNotBeEmpty();
        }

        // Reordered records: contents of 2 and 3 swapped under the same sequences.
        await using (var h = await ChainAsync(6))
        {
            var a = h.Store.Rows[1];
            var b = h.Store.Rows[2];
            h.Store.Rows[1] = b with { Sequence = a.Sequence, Id = a.Id };
            h.Store.Rows[2] = a with { Sequence = b.Sequence, Id = b.Id };
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.Select(i => i.Sequence).ShouldContain(2);
        }

        // An untouched chain is valid, also verified in segments.
        await using var clean = await ChainAsync(6);
        var whole = await clean.Verifier.VerifyAsync("node-1", 1, null, Ct);
        (whole.Valid, whole.RecordsChecked, whole.From, whole.To).ShouldBe((true, 6L, 1L, 6L));
        (await clean.Verifier.VerifyAsync("node-1", 4, 5, Ct)).RecordsChecked.ShouldBe(2);
        (await clean.Verifier.VerifyAsync("node-1", 4, null, Ct)).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task AUD_U_005_Checkpoints_are_signed_and_detect_a_tampered_checkpoint_a_truncation_and_a_wholesale_rewrite()
    {
        // Valid checkpoint.
        await using (var h = await ChainAsync(4))
        {
            var checkpoint = (await h.Checkpoints.CreateAsync(Ct)).ShouldNotBeNull();
            checkpoint.Sequence.ShouldBe(4);
            checkpoint.KeyId.ShouldBe("test-key");
            (await h.Verifier.VerifyAsync("node-1", 1, null, Ct)).Valid.ShouldBeTrue();
            (await h.Checkpoints.CreateAsync(Ct)).ShouldBeNull("the head is already checkpointed");
        }

        // Tampered checkpoint: the signature no longer matches.
        await using (var h = await ChainAsync(4))
        {
            await h.Checkpoints.CreateAsync(Ct);
            h.Store.Checkpoints[0] = h.Store.Checkpoints[0] with { Hash = new byte[32] };
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.CheckpointSignatureInvalid);
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.CheckpointMismatch);
        }

        // Truncation after the checkpoint: the chain ends before it.
        await using (var h = await ChainAsync(4))
        {
            await h.Checkpoints.CreateAsync(Ct);
            h.Store.Rows.RemoveAll(r => r.Sequence > 2);
            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.ShouldContain(i => i.Kind == VerificationIssueKind.MissingAfterCheckpoint && i.Sequence == 4);
        }

        // Wholesale rewrite by someone with database access but without the key: a consistent chain that differs from the signed checkpoint.
        await using (var h = await ChainAsync(4))
        {
            await h.Checkpoints.CreateAsync(Ct);
            var prev = HashChain.Genesis("node-1");
            for (var i = 0; i < h.Store.Rows.Count; i++)
            {
                h.Store.Rows[i] = HashChain.Seal(h.Store.Rows[i] with { Reason = "rewritten" }, prev);
                prev = h.Store.Rows[i].Hash;
            }

            var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
            report.Valid.ShouldBeFalse();
            report.Issues.Select(i => i.Kind).ShouldBe([VerificationIssueKind.CheckpointMismatch]);
        }
    }

    [Fact]
    public async Task AUD_U_015_Retention_purges_up_to_a_checkpoint_and_verification_continues_from_the_purge_checkpoint()
    {
        await using var h = new AuditHarness(s => s with { RetentionDays = 365 });
        await h.StartAsync();
        await h.WriteStoredAsync(6);
        var checkpoint = (await h.Checkpoints.CreateAsync(Ct)).ShouldNotBeNull();
        await h.WriteStoredAsync(3);
        h.Clock.Advance(TimeSpan.FromDays(366));
        var retention = new AuditRetention(h.Store, h.Writer, h.Settings, h.Clock, NullLogger<AuditRetention>.Instance);

        var purged = await retention.PurgeAsync(Ct);
        await h.WriteStoredAsync(1);

        purged.ShouldBe(checkpoint.Sequence);
        h.Store.Rows.Min(r => r.Sequence).ShouldBe(checkpoint.Sequence + 1);
        h.Store.Rows.First().PrevHash.ShouldBe(checkpoint.Hash);
        h.Store.Rows.ShouldContain(r => r.Action == "audit.retentionPurged" && r.Severity == (short)AuditSeverity.Sensitive);
        var report = await h.Verifier.VerifyAsync("node-1", 1, null, Ct);
        report.Valid.ShouldBeTrue(string.Join("; ", report.Issues.Select(i => i.Message)));
        report.From.ShouldBe(checkpoint.Sequence + 1);

        // Tampering after the purge is still detected; losing the purge checkpoint leaves the chain without an anchor.
        h.Store.Rows[1] = h.Store.Rows[1] with { Reason = "tampered" };
        (await h.Verifier.VerifyAsync("node-1", 1, null, Ct)).Valid.ShouldBeFalse();
        h.Store.Rows[1] = h.Store.Rows[1] with { Reason = null };
        h.Store.Checkpoints.Clear();
        (await h.Verifier.VerifyAsync("node-1", 1, null, Ct)).Issues.ShouldContain(i => i.Kind == VerificationIssueKind.CheckpointMismatch);
    }

    [Fact]
    public async Task AUD_U_017_The_continuous_verifier_raises_a_critical_audit_a_system_event_and_a_degraded_health_check_on_failure()
    {
        await using var h = await ChainAsync(5);
        var publisher = new RecordingPublisher();
        var state = new AuditIntegrityState();
        var verifier = new ContinuousVerifier(h.Store, h.Verifier, h.Writer, publisher, state, h.Settings, h.Clock, h.LoggerFor<ContinuousVerifier>());
        var health = new AuditHealthCheck(state);

        await verifier.RunOnceAsync(Ct);
        state.Current.Ok.ShouldBeTrue();
        publisher.Calls.ShouldBe([(true, null)]);
        h.Store.States["node-1"].LastVerifiedSequence.ShouldBe(5);
        (await health.CheckHealthAsync(null!, Ct)).Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);

        // New records are verified incrementally: a change to an old record is only found by the weekly full verification.
        await h.WriteStoredAsync(2);
        h.Store.Rows[1] = h.Store.Rows[1] with { Reason = "tampered" };
        await verifier.RunOnceAsync(Ct);
        state.Current.Ok.ShouldBeTrue();
        h.Clock.Advance(TimeSpan.FromDays(8));
        await verifier.RunOnceAsync(Ct);

        state.Current.Ok.ShouldBeFalse();
        (await health.CheckHealthAsync(null!, Ct)).Status.ShouldBe(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded);
        publisher.Calls.Last().ShouldBe((false, (VerificationIssueKind?)VerificationIssueKind.HashMismatch));
        var critical = h.Store.Rows.Last();
        critical.Action.ShouldBe("audit.chainVerificationFailed");
        critical.Severity.ShouldBe((short)AuditSeverity.Critical);
        JsonDocument.Parse(critical.DetailsJson!).RootElement.GetProperty("sequence").GetInt64().ShouldBe(2);
        h.Logs.WithEventId(2871).ShouldNotBeEmpty();
    }

    private static async Task<AuditHarness> ChainAsync(int records)
    {
        var h = new AuditHarness();
        await h.StartAsync();
        await h.WriteStoredAsync(records);
        return h;
    }

    private sealed class RecordingPublisher : IAuditIntegrityPublisher
    {
        public List<(bool Ok, VerificationIssueKind? Kind)> Calls { get; } = [];

        public ValueTask PublishAsync(bool ok, DateTimeOffset lastCheckedAt, (string ChainId, long Sequence, VerificationIssueKind Kind)? issue, CancellationToken ct)
        {
            Calls.Add((ok, issue?.Kind));
            return ValueTask.CompletedTask;
        }
    }
}
