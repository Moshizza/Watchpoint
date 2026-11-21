using Watchpoint.History.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>
/// Folds <c>command.*</c> records into one row per command (spec HIS §5). The request record carries the command; the
/// others move the state and set their timestamp once. A row built from a record other than the request has an empty path
/// and risk and priority -1 until the request is merged.
/// </summary>
internal sealed class CommandFolder(ILogRecordTypeRegistry registry, IHistoryEnrichment enrichment)
{
    public IReadOnlyList<HistoryCommandRow> Fold(IEnumerable<LogRecord> records, Action<LogRecord, string>? skipped = null)
    {
        var folded = new Dictionary<(Guid Id, DateTimeOffset Time), HistoryCommandRow>();
        foreach (var record in records)
        {
            if (record.Envelope.Dev is not { } device)
            {
                skipped?.Invoke(record, "a command record needs dev");
                continue;
            }

            CommandTransitionBody body;
            try
            {
                body = record.BodyAs<CommandTransitionBody>(registry);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                skipped?.Invoke(record, ex.Message);
                continue;
            }

            if (StateOf(record.Envelope.Kind) is not { } state)
            {
                skipped?.Invoke(record, "unknown command record kind");
                continue;
            }

            var row = ToRow(record, body, device.Value, state);
            folded[(row.Id, row.Time)] = folded.TryGetValue((row.Id, row.Time), out var current) ? Merge(current, row) : row;
        }

        return [.. folded.Values];
    }

    /// <summary>The state a record kind moves a command to; <see langword="null"/> for an unknown kind.</summary>
    public static CommandHistoryState? StateOf(string kind) => kind switch
    {
        "command.requested" => CommandHistoryState.Requested,
        "command.rejected" => CommandHistoryState.Rejected,
        "command.awaitingApproval" => CommandHistoryState.AwaitingApproval,
        "command.approved" => CommandHistoryState.Approved,
        "command.approvalRejected" => CommandHistoryState.ApprovalRejected,
        "command.queued" => CommandHistoryState.Queued,
        "command.dispatched" => CommandHistoryState.Dispatched,
        "command.succeeded" => CommandHistoryState.Succeeded,
        "command.failed" => CommandHistoryState.Failed,
        "command.timedOut" => CommandHistoryState.TimedOut,
        "command.expired" => CommandHistoryState.Expired,
        "command.cancelled" => CommandHistoryState.Cancelled,
        "command.verified" => CommandHistoryState.Verified,
        "command.unverified" => CommandHistoryState.Unverified,
        _ => null,
    };

    /// <summary>Merges the row of a newer or older record into the row so far.</summary>
    public static HistoryCommandRow Merge(HistoryCommandRow current, HistoryCommandRow next)
    {
        var newer = next.Seq > current.Seq;
        var requested = next.Path.Length > 0;
        return current with
        {
            Seq = Math.Max(current.Seq, next.Seq),
            State = newer ? next.State : current.State,
            GroupId = current.GroupId ?? next.GroupId,
            DeviceName = current.DeviceName ?? next.DeviceName,
            LocationIds = current.LocationIds ?? next.LocationIds,
            Path = requested ? next.Path : current.Path,
            ArgumentsJson = requested ? next.ArgumentsJson : current.ArgumentsJson,
            IssuerJson = requested ? next.IssuerJson : current.IssuerJson,
            IssuerUser = requested ? next.IssuerUser : current.IssuerUser,
            Reason = requested ? next.Reason : current.Reason,
            Risk = next.Risk >= 0 ? next.Risk : current.Risk,
            Priority = next.Priority >= 0 && (current.Priority < 0 || newer) ? next.Priority : current.Priority,
            FailureJson = newer ? next.FailureJson ?? current.FailureJson : current.FailureJson ?? next.FailureJson,
            QueuedAt = current.QueuedAt ?? next.QueuedAt,
            DispatchedAt = current.DispatchedAt ?? next.DispatchedAt,
            CompletedAt = current.CompletedAt ?? next.CompletedAt,
            VerifiedAt = current.VerifiedAt ?? next.VerifiedAt,
            Attempt = Math.Max(current.Attempt, next.Attempt),
            ApprovalJson = newer ? next.ApprovalJson ?? current.ApprovalJson : current.ApprovalJson ?? next.ApprovalJson,
            CorrelationJson = current.CorrelationJson ?? next.CorrelationJson,
        };
    }

    private HistoryCommandRow ToRow(LogRecord record, CommandTransitionBody body, Guid device, CommandHistoryState state)
    {
        var envelope = record.Envelope;
        var source = enrichment.ForDevice(Watchpoint.Kernel.Ids.DeviceId.From(device));
        var ts = envelope.Ts;
        var request = state == CommandHistoryState.Requested;
        var approval = state is CommandHistoryState.Approved or CommandHistoryState.ApprovalRejected
            ? HistoryJson.Serialize(new Dictionary<string, Watchpoint.Kernel.Values.WpValue>
            {
                ["decision"] = Watchpoint.Kernel.Values.WpValue.String(state == CommandHistoryState.Approved ? "approved" : "rejected"),
                ["by"] = Watchpoint.Kernel.Values.WpValue.String(body.By?.ToString("D") ?? string.Empty),
                ["reason"] = Watchpoint.Kernel.Values.WpValue.String(body.Reason ?? string.Empty),
            })
            : null;
        return new HistoryCommandRow(
            body.RequestedAt,
            body.Id,
            record.Sequence,
            body.GroupId,
            device,
            source?.DisplayName,
            source?.LocationIds,
            body.Path ?? string.Empty,
            HistoryJson.Text(body.Arguments, "{}")!,
            HistoryJson.Text(body.Issuer, "{}")!,
            body.IssuerUser,
            request ? body.Reason : null,
            body.Risk ?? -1,
            body.Priority ?? -1,
            (short)state,
            state is CommandHistoryState.Rejected or CommandHistoryState.Failed ? HistoryJson.Text(body.Failure, null) : null,
            state == CommandHistoryState.Queued ? ts : null,
            state == CommandHistoryState.Dispatched ? ts : null,
            state is CommandHistoryState.Rejected or CommandHistoryState.ApprovalRejected or CommandHistoryState.Succeeded or CommandHistoryState.Failed
                or CommandHistoryState.TimedOut or CommandHistoryState.Expired or CommandHistoryState.Cancelled ? ts : null,
            state == CommandHistoryState.Verified ? ts : null,
            body.Attempt ?? 0,
            approval,
            HistoryJson.CorrelationJson(envelope.Corr));
    }
}
