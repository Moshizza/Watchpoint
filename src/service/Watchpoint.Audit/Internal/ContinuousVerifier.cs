using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Watchpoint.Audit.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Verifies new segments every 5 minutes and the whole chain weekly (spec AUD §6.1); any issue raises a Critical audit record, a system event and a degraded health check.</summary>
internal sealed class ContinuousVerifier(
    IAuditStore store, AuditVerifier verifier, IAuditSink sink, IAuditIntegrityPublisher publisher, AuditIntegrityState state,
    AuditSettings settings, TimeProvider clock, ILogger<ContinuousVerifier> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.VerifyInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AuditLog.UpkeepFailed(logger, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>Verifies every chain once: the new segment, or the whole chain when the weekly verification is due.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var ok = true;
        (string ChainId, long Sequence, VerificationIssueKind Kind)? firstIssue = null;
        foreach (var chain in await store.ListChainsAsync(ct).ConfigureAwait(false))
        {
            var previous = await store.GetVerificationStateAsync(chain, ct).ConfigureAwait(false);
            var fullDue = previous?.LastFullVerificationAt is not { } last || now - last >= settings.FullVerifyInterval;
            var from = fullDue ? 1 : previous!.LastVerifiedSequence + 1;
            var report = await verifier.VerifyAsync(chain, from, null, ct).ConfigureAwait(false);
            if (report.Valid)
            {
                continue;
            }

            ok = false;
            var issue = report.Issues[0];
            firstIssue ??= (chain, issue.Sequence, issue.Kind);
            AuditLog.VerificationFailed(logger, chain, issue.Sequence, issue.Kind);
            await sink.WriteAsync(
                new AuditRecord(
                    new AuditAction("audit.chainVerificationFailed"),
                    AuditOutcome.Failure,
                    AuditActor.System("audit"),
                    [],
                    null,
                    JsonSerializer.SerializeToElement(new { chainId = chain, sequence = issue.Sequence, kind = issue.Kind.ToString(), issues = report.Issues.Count }),
                    AuditSeverity.Critical),
                ct).ConfigureAwait(false);
        }

        state.Set(ok, now, firstIssue is { } f ? $"{f.ChainId}:{f.Sequence} {f.Kind}" : null);
        await publisher.PublishAsync(ok, now, firstIssue, ct).ConfigureAwait(false);
    }
}
