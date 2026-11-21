using Watchpoint.History.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Writers;

/// <summary>
/// Folds <c>alarm.*</c> records into one row per alarm and one timeline row per record (spec HIS §5). Rows merge
/// monotonically: state, final priority, report, owner and procedure follow the newest record sequence, timestamps are set
/// once, the count and the escalation level only grow. The database applies the same rules when a row already exists.
/// </summary>
internal sealed class AlarmFolder(ILogRecordTypeRegistry registry, IHistoryEnrichment enrichment)
{
    public (IReadOnlyList<HistoryAlarmRow> Alarms, IReadOnlyList<HistoryAlarmTimelineRow> Timeline) Fold(IEnumerable<LogRecord> records, Action<LogRecord, string>? skipped = null)
    {
        var folded = new Dictionary<(Guid Id, DateTimeOffset Time), HistoryAlarmRow>();
        var timeline = new List<HistoryAlarmTimelineRow>();
        foreach (var record in records)
        {
            AlarmTransitionBody body;
            try
            {
                body = record.BodyAs<AlarmTransitionBody>(registry);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                skipped?.Invoke(record, ex.Message);
                continue;
            }

            var row = ToRow(record, body);
            folded[(row.Id, row.Time)] = folded.TryGetValue((row.Id, row.Time), out var current) ? Merge(current, row) : row;
            timeline.Add(new HistoryAlarmTimelineRow(
                record.Envelope.Ts,
                body.Id,
                record.Envelope.Id,
                record.Envelope.Kind,
                body.Actor?.User,
                body.Actor?.Session,
                body.Actor?.ClientKind,
                HistoryJson.Serialize(new HistoryAlarmTimelineBody(body.State, body.Priority, body.Count, body.Detail))));
        }

        return ([.. folded.Values], timeline);
    }

    /// <summary>Merges the row of a newer or older record into the row so far (the rules above).</summary>
    public static HistoryAlarmRow Merge(HistoryAlarmRow current, HistoryAlarmRow next)
    {
        var newer = next.Seq > current.Seq;
        T? Latest<T>(T? fromCurrent, T? fromNext)
            where T : class => newer ? fromNext ?? fromCurrent : fromCurrent ?? fromNext;
        T? LatestValue<T>(T? fromCurrent, T? fromNext)
            where T : struct => newer ? fromNext ?? fromCurrent : fromCurrent ?? fromNext;
        return current with
        {
            Seq = Math.Max(current.Seq, next.Seq),
            State = newer ? next.State : current.State,
            PriorityFinal = newer ? next.PriorityFinal : current.PriorityFinal,
            Count = Math.Max(current.Count, next.Count),
            LastOccurrenceAt = current.LastOccurrenceAt >= next.LastOccurrenceAt ? current.LastOccurrenceAt : next.LastOccurrenceAt,
            AcknowledgedBy = current.AcknowledgedAt is null ? next.AcknowledgedBy : current.AcknowledgedBy,
            AcknowledgedAt = current.AcknowledgedAt ?? next.AcknowledgedAt,
            ResolvedAt = current.ResolvedAt ?? next.ResolvedAt,
            ClosedBy = current.ClosedAt is null ? next.ClosedBy : current.ClosedBy,
            ClosedAt = current.ClosedAt ?? next.ClosedAt,
            Report = Latest(current.Report, next.Report),
            OwnerJson = Latest(current.OwnerJson, next.OwnerJson),
            EscalationLevel = Math.Max(current.EscalationLevel, next.EscalationLevel),
            ProcedureId = LatestValue(current.ProcedureId, next.ProcedureId),
            ProcedureVersion = LatestValue(current.ProcedureVersion, next.ProcedureVersion),
            FalseAlarm = LatestValue(current.FalseAlarm, next.FalseAlarm),
        };
    }

    private HistoryAlarmRow ToRow(LogRecord record, AlarmTransitionBody body)
    {
        var envelope = record.Envelope;
        var source = body.SourceId is { } id
            ? (body.SourceKind == 1 ? enrichment.ForServer(Watchpoint.Kernel.Ids.ServerId.From(id)) : enrichment.ForDevice(Watchpoint.Kernel.Ids.DeviceId.From(id)))
            : envelope.Dev is { } device ? enrichment.ForDevice(device)
            : envelope.Srv is { } server ? enrichment.ForServer(server) : null;
        var sourceId = body.SourceId ?? envelope.Dev?.Value ?? envelope.Srv?.Value;
        return new HistoryAlarmRow(
            body.RaisedAt,
            body.Id,
            record.Sequence,
            body.RuleId,
            body.RuleKey,
            body.RuleVersion,
            sourceId,
            body.SourceKind ?? (sourceId is null ? null : (short)(envelope.Dev is null && envelope.Srv is not null ? 1 : 0)),
            source?.DisplayName,
            source?.LocationId,
            source?.LocationIds,
            source?.TypeId,
            source?.TagKeys,
            (short)body.State,
            body.InitialPriority,
            body.Priority,
            body.Count,
            body.LastOccurrenceAt,
            body.AcknowledgedAt,
            body.AcknowledgedBy,
            body.ResolvedAt,
            body.ClosedAt,
            body.ClosedBy,
            body.Report,
            HistoryJson.Text(body.Owner, null),
            body.EscalationLevel,
            body.ProcedureId,
            body.ProcedureVersion,
            body.FalseAlarm);
    }
}
