using System.Text.Json;
using Watchpoint.Audit.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Verifies hash chains and checkpoints (spec AUD §6.1) and keeps the verification state.</summary>
internal sealed class AuditVerifier(IAuditStore store, IAuditSigner signer, AuditSettings settings, TimeProvider clock, AuditMetrics metrics) : IAuditVerifier
{
    public Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct) => store.ListChainsAsync(ct);

    public async Task<IReadOnlyList<VerificationReport>> LatestReportsAsync(CancellationToken ct)
    {
        List<VerificationReport> reports = [];
        foreach (var chain in await store.ListChainsAsync(ct).ConfigureAwait(false))
        {
            var state = await store.GetVerificationStateAsync(chain, ct).ConfigureAwait(false);
            if (state?.LastResultJson is { } json && JsonSerializer.Deserialize(json, AuditJsonContext.Default.VerificationReport) is { } report)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    public async Task<VerificationReport> VerifyAsync(string chainId, long fromSequence, long? toSequence, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(chainId);
        var issues = new List<VerificationIssue>();
        var checkpoints = await store.GetCheckpointsAsync(chainId, ct).ConfigureAwait(false);
        foreach (var checkpoint in checkpoints)
        {
            var payload = HashChain.CheckpointPayload(checkpoint.ChainId, checkpoint.Sequence, checkpoint.Hash, checkpoint.At);
            if (!await signer.VerifyAsync(checkpoint.KeyId, payload, checkpoint.Signature, ct).ConfigureAwait(false))
            {
                issues.Add(new VerificationIssue(VerificationIssueKind.CheckpointSignatureInvalid, checkpoint.Sequence, "The checkpoint signature is not valid."));
            }
        }

        var byChecksum = checkpoints.ToDictionary(c => c.Sequence);
        var head = await store.GetHeadAsync(chainId, ct).ConfigureAwait(false);
        var headSequence = head?.Sequence ?? 0;
        var last = checkpoints.Count > 0 ? checkpoints[^1] : null;
        if (last is not null && last.Sequence > headSequence)
        {
            issues.Add(new VerificationIssue(VerificationIssueKind.MissingAfterCheckpoint, last.Sequence, $"The chain ends at {headSequence}, before the checkpoint at {last.Sequence}."));
        }

        long checkedCount = 0;
        long first = 0;
        long lastSequence = 0;
        byte[]? previousHash = null;
        long expected = -1;
        var start = Math.Max(1, fromSequence - 1);
        var cursor = start;
        var anchored = false;
        while (true)
        {
            var batch = await store.ReadChainAsync(chainId, cursor, toSequence, settings.VerifyBatchSize, ct).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                if (!anchored)
                {
                    anchored = true;
                    if (fromSequence > 1 && row.Sequence == fromSequence - 1)
                    {
                        // Anchor: the record before the segment; its own hash is trusted here and checked by the segments before it.
                        previousHash = row.Hash;
                        expected = row.Sequence + 1;
                        continue;
                    }

                    AnchorFirst(row, chainId, byChecksum, issues);
                    first = row.Sequence;
                    expected = row.Sequence;
                    previousHash = row.PrevHash;
                }

                if (row.Sequence != expected)
                {
                    issues.Add(new VerificationIssue(VerificationIssueKind.SequenceGap, expected, $"Expected sequence {expected}, found {row.Sequence}."));
                }

                if (first == 0)
                {
                    first = row.Sequence;
                }

                if (!row.PrevHash.AsSpan().SequenceEqual(previousHash))
                {
                    issues.Add(new VerificationIssue(VerificationIssueKind.HashMismatch, row.Sequence, "The previous hash does not match the preceding record."));
                }

                var computed = HashChain.ComputeHash(row.PrevHash, row);
                if (!computed.AsSpan().SequenceEqual(row.Hash))
                {
                    issues.Add(new VerificationIssue(VerificationIssueKind.HashMismatch, row.Sequence, "The record does not match its hash."));
                }

                if (byChecksum.TryGetValue(row.Sequence, out var cp) && !cp.Hash.AsSpan().SequenceEqual(row.Hash))
                {
                    issues.Add(new VerificationIssue(VerificationIssueKind.CheckpointMismatch, row.Sequence, "The checkpoint hash does not match the record."));
                }

                previousHash = row.Hash;
                expected = row.Sequence + 1;
                lastSequence = row.Sequence;
                checkedCount++;
            }

            cursor = batch[^1].Sequence + 1;
        }

        var now = clock.GetUtcNow();
        var to = toSequence ?? lastSequence;
        var report = new VerificationReport(chainId, first == 0 ? fromSequence : first, Math.Max(to, lastSequence), checkedCount, issues.Count == 0, issues, now);
        await SaveStateAsync(report, fromSequence, ct).ConfigureAwait(false);
        metrics.Issues(issues.Count);
        return report;
    }

    private static void AnchorFirst(AuditRow row, string chainId, Dictionary<long, AuditCheckpointRow> checkpoints, List<VerificationIssue> issues)
    {
        if (row.Sequence == 1)
        {
            if (!row.PrevHash.AsSpan().SequenceEqual(HashChain.Genesis(chainId)))
            {
                issues.Add(new VerificationIssue(VerificationIssueKind.HashMismatch, 1, "The first record does not start at the chain genesis."));
            }

            return;
        }

        // After a retention purge the chain starts at the hash of the purge checkpoint.
        if (!checkpoints.TryGetValue(row.Sequence - 1, out var checkpoint) || !checkpoint.Hash.AsSpan().SequenceEqual(row.PrevHash))
        {
            issues.Add(new VerificationIssue(VerificationIssueKind.CheckpointMismatch, row.Sequence, "No checkpoint anchors the start of the remaining chain."));
        }
    }

    private async Task SaveStateAsync(VerificationReport report, long fromSequence, CancellationToken ct)
    {
        var previous = await store.GetVerificationStateAsync(report.ChainId, ct).ConfigureAwait(false);
        var verified = report.Valid && report.RecordsChecked > 0 ? Math.Max(previous?.LastVerifiedSequence ?? 0, report.To) : previous?.LastVerifiedSequence ?? 0;
        var full = report.Valid && fromSequence <= 1 ? report.At : previous?.LastFullVerificationAt;
        await store.SaveVerificationStateAsync(
            new AuditVerificationState(report.ChainId, verified, full, JsonSerializer.Serialize(report, AuditJsonContext.Default.VerificationReport)), ct).ConfigureAwait(false);
    }
}
