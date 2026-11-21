using Watchpoint.History.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>
/// Folds <c>command.*</c> records into one row per command (spec HIS §5). The request record carries the command; the
/// others move the state and set their timestamp once. A row built from a record other than the request has an empty path
/// and risk and priority -1 until the request is merged. The others carry no request time, which is the row's key: it comes from
/// the request in the same batch, or from the stored row.
/// </summary>
internal sealed class CommandFolder(IHistoryEnrichment enrichment)
{
    public async Task<IReadOnlyList<HistoryCommandRow>> FoldAsync(
        IEnumerable<LogRecord> records,
        Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, DateTimeOffset>>> storedTimes,
        Action<LogRecord, string>? skipped,
        CancellationToken ct)
    {
        List<(LogRecord Record, ParsedCommandRecord Parsed, Guid Device)> parsed = [];
        foreach (var record in records)
        {
            if (record.Envelope.Dev is not { } device)
            {
                skipped?.Invoke(record, "a command record needs dev");
                continue;
            }

            if (CommandRecordReader.Read(record, out var problem) is not { } read)
            {
                skipped?.Invoke(record, problem ?? "unreadable");
                continue;
            }

            parsed.Add((record, read, device.Value));
        }

        var requested = parsed.Where(p => p.Parsed is { Request: true, RequestedAt: not null }).ToDictionary(p => p.Parsed.Id, p => p.Parsed.RequestedAt!.Value);
        var unknown = parsed.Where(p => !requested.ContainsKey(p.Parsed.Id)).Select(p => p.Parsed.Id).Distinct().ToList();
        IReadOnlyDictionary<Guid, DateTimeOffset> stored = unknown.Count == 0 ? new Dictionary<Guid, DateTimeOffset>() : await storedTimes(unknown, ct).ConfigureAwait(false);

        var folded = new Dictionary<(Guid Id, DateTimeOffset Time), HistoryCommandRow>();
        foreach (var (record, read, device) in parsed)
        {
            DateTimeOffset time;
            if (requested.TryGetValue(read.Id, out var inBatch))
            {
                time = inBatch;
            }
            else if (stored.TryGetValue(read.Id, out var fromStore))
            {
                time = fromStore;
            }
            else
            {
                skipped?.Invoke(record, "the request of the command is not in history");
                continue;
            }

            var row = ToRow(record, read, device, time);
            folded[(row.Id, row.Time)] = folded.TryGetValue((row.Id, row.Time), out var current) ? Merge(current, row) : row;
        }

        return [.. folded.Values];
    }

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

    private HistoryCommandRow ToRow(LogRecord record, ParsedCommandRecord read, Guid device, DateTimeOffset time)
    {
        var envelope = record.Envelope;
        var source = enrichment.ForDevice(Watchpoint.Kernel.Ids.DeviceId.From(device));
        var ts = envelope.Ts;
        var state = read.State;
        var kind = envelope.Kind;
        return new HistoryCommandRow(
            time,
            read.Id,
            record.Sequence,
            read.GroupId,
            device,
            source?.DisplayName,
            source?.LocationIds,
            read.Path ?? string.Empty,
            read.ArgumentsJson ?? "{}",
            read.IssuerJson ?? "{}",
            read.IssuerUser,
            read.Reason,
            read.Risk ?? -1,
            read.Priority ?? -1,
            (short)state,
            read.FailureJson,
            state == CommandHistoryState.Queued ? ts : null,
            state == CommandHistoryState.Dispatched ? ts : null,
            state is CommandHistoryState.Rejected or CommandHistoryState.Succeeded or CommandHistoryState.Failed or CommandHistoryState.TimedOut
                or CommandHistoryState.Expired or CommandHistoryState.Cancelled ? ts : null,
            kind == "command.verified" ? ts : null,
            read.Attempt ?? 0,
            read.ApprovalJson,
            HistoryJson.CorrelationJson(envelope.Corr));
    }
}
