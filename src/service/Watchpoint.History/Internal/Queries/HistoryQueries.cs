using System.Globalization;
using System.Text.Json;
using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Queries;

/// <summary>The queries of history (spec HIS §7): validates, restricts to the caller's scope, pages by keyset, maps rows.</summary>
internal sealed class HistoryQueries(IHistoryStore store, IHistoryTagKeys tagKeyLookup, HistorySettings settings) : IHistoryQueries
{
    /// <summary>The longest range of an events query.</summary>
    public const int MaxEventDays = 31;

    /// <summary>The longest range of a count query.</summary>
    public const int MaxCountDays = 366;

    /// <summary>The most raw points of a series.</summary>
    public const int MaxSeriesPoints = 10_000;

    public Task<Result<Page<JournalEvent>>> QueryEventsAsync(EventQuery query, PageRequest page, CancellationToken ct) => GuardedAsync<Page<JournalEvent>>(
        async () =>
        {
            if (Check(page, query.From, query.To, MaxEventDays) is { } invalid)
            {
                return invalid;
            }

            if (!HistoryCursor.Decode(page.Cursor).TryGetValue(out var after, out var cursorError))
            {
                return cursorError;
            }

            var scope = ScopeSelector.Select(query.Scope, out var nothingVisible);
            IReadOnlyList<string>? tagKeys = null;
            if (query.Tags is { Count: > 0 })
            {
                var keys = tagKeyLookup.KeysOf([.. query.Tags]);
                nothingVisible |= keys.Count == 0;
                tagKeys = keys;
            }

            if (nothingVisible)
            {
                return new Page<JournalEvent>([], null);
            }

            List<string> exact = [];
            List<string> prefixes = [];
            foreach (var kind in query.Kinds ?? [])
            {
                if (kind.EndsWith(".*", StringComparison.Ordinal) && kind.Length > 2)
                {
                    prefixes.Add(kind[..^1]);
                }
                else if (kind.Contains('*', StringComparison.Ordinal) || kind.Length == 0)
                {
                    return HistoryErrors.InvalidQuery("kinds", "pattern");
                }
                else
                {
                    exact.Add(kind);
                }
            }

            var filter = new HistoryEventFilter(
                query.From, query.To,
                query.Categories?.Select(c => (short)c).ToList(),
                exact.Count == 0 ? null : exact,
                prefixes.Count == 0 ? null : prefixes,
                query.MinSeverity is { } s ? (short)s : null,
                query.LocationsSubtree?.Select(l => l.Value).ToList(),
                query.Servers?.Select(x => x.Value).ToList(),
                query.Entities,
                tagKeys,
                query.PersonRef,
                query.Operator?.Value,
                query.Text,
                scope,
                query.Descending,
                after);
            var rows = await store.QueryEventsAsync(filter, page.Limit + 1, settings.QueryTimeout, ct).ConfigureAwait(false);
            return HistoryCursor.ToPage(rows, page.Limit, r => r.Time, r => r.Id, ToJournalEvent);
        });

    public Task<Result<IReadOnlyList<AlarmTimelineEntry>>> GetAlarmTimelineAsync(AlarmId id, CancellationToken ct) => GuardedAsync<IReadOnlyList<AlarmTimelineEntry>>(
        async () =>
        {
            var rows = await store.GetAlarmTimelineAsync(id.Value, settings.QueryTimeout, ct).ConfigureAwait(false);
            return rows.Select(r => new AlarmTimelineEntry(r.Time, r.RecordId, r.Kind, r.ActorUser, r.ActorSession, r.ClientKind, HistoryJson.Parse(r.BodyJson))).ToList();
        });

    public Task<Result<Page<AlarmHistoryRow>>> QueryAlarmsAsync(AlarmHistoryQuery query, PageRequest page, CancellationToken ct) => GuardedAsync<Page<AlarmHistoryRow>>(
        async () =>
        {
            if (Check(page, query.From, query.To, MaxCountDays) is { } invalid)
            {
                return invalid;
            }

            if (!HistoryCursor.Decode(page.Cursor).TryGetValue(out var after, out var cursorError))
            {
                return cursorError;
            }

            var scope = ScopeSelector.Select(query.Scope, out var nothingVisible);
            if (nothingVisible)
            {
                return new Page<AlarmHistoryRow>([], null);
            }

            var filter = new HistoryAlarmFilter(
                query.From, query.To, query.RuleIds, query.RuleKeys, query.Sources, query.States?.Select(x => (short)x).ToList(), query.MinPriority,
                query.FalseAlarm, query.LocationsSubtree?.Select(l => l.Value).ToList(), scope, query.Descending, after);
            var rows = await store.QueryAlarmsAsync(filter, page.Limit + 1, settings.QueryTimeout, ct).ConfigureAwait(false);
            return HistoryCursor.ToPage(rows, page.Limit, r => r.Time, r => r.Id, ToAlarmRow);
        });

    public Task<Result<Page<CommandHistoryRow>>> QueryCommandsAsync(CommandHistoryQuery query, PageRequest page, CancellationToken ct) => GuardedAsync<Page<CommandHistoryRow>>(
        async () =>
        {
            if (Check(page, query.From, query.To, MaxCountDays) is { } invalid)
            {
                return invalid;
            }

            if (!HistoryCursor.Decode(page.Cursor).TryGetValue(out var after, out var cursorError))
            {
                return cursorError;
            }

            var scope = ScopeSelector.Select(query.Scope, out var nothingVisible);
            if (nothingVisible)
            {
                return new Page<CommandHistoryRow>([], null);
            }

            var filter = new HistoryCommandFilter(
                query.From, query.To, query.Devices?.Select(d => d.Value).ToList(), query.IssuerUser?.Value, query.States?.Select(x => (short)x).ToList(),
                query.Path, query.MinRisk, query.LocationsSubtree?.Select(l => l.Value).ToList(), scope, query.Descending, after);
            var rows = await store.QueryCommandsAsync(filter, page.Limit + 1, settings.QueryTimeout, ct).ConfigureAwait(false);
            return HistoryCursor.ToPage(rows, page.Limit, r => r.Time, r => r.Id, ToCommandRow);
        });

    public Task<Result<IReadOnlyList<SeriesPoint>>> QuerySeriesAsync(SeriesQuery query, CancellationToken ct) => GuardedAsync<IReadOnlyList<SeriesPoint>>(
        async () =>
        {
            if (query.To <= query.From)
            {
                return HistoryErrors.InvalidQuery("to", "range");
            }

            if (query.Bucket is { } bucket && bucket < TimeSpan.FromSeconds(1))
            {
                return HistoryErrors.InvalidQuery("bucket", "too_small");
            }

            var request = new HistorySeriesRequest(query.Device.Value, query.Path.Value, query.From, query.To, query.Bucket, MaxSeriesPoints + 1);
            var rows = await store.QuerySeriesAsync(request, settings.QueryTimeout, ct).ConfigureAwait(false);
            if (rows.Count > MaxSeriesPoints)
            {
                return HistoryErrors.TooManyPoints(MaxSeriesPoints);
            }

            return rows.Select(ToPoint).ToList();
        });

    public Task<Result<IReadOnlyList<BucketCount>>> CountAsync(CountQuery query, CancellationToken ct) => GuardedAsync<IReadOnlyList<BucketCount>>(
        async () =>
        {
            if (query.To <= query.From)
            {
                return HistoryErrors.InvalidQuery("to", "range");
            }

            if (query.To - query.From > TimeSpan.FromDays(MaxCountDays))
            {
                return HistoryErrors.RangeTooLarge(MaxCountDays);
            }

            if (query.Bucket < TimeSpan.FromSeconds(1))
            {
                return HistoryErrors.InvalidQuery("bucket", "too_small");
            }

            if (!HistoryCountColumns.TryGetColumns(query.Table ?? string.Empty, out var columns))
            {
                return HistoryErrors.InvalidQuery("table", "not_whitelisted");
            }

            foreach (var column in query.GroupBy)
            {
                if (!columns.ContainsKey(column))
                {
                    return HistoryErrors.InvalidQuery("groupBy", "not_whitelisted");
                }
            }

            if (!ParseFilters(query.Filters, columns).TryGetValue(out var filters, out var filterError))
            {
                return filterError;
            }

            var scope = ScopeSelector.Select(query.Scope, out var nothingVisible);
            if (nothingVisible)
            {
                return Array.Empty<BucketCount>();
            }

            var request = new HistoryCountRequest(query.Table!, query.From, query.To, query.Bucket, query.GroupBy, filters, scope);
            var rows = await store.CountAsync(request, settings.QueryTimeout, ct).ConfigureAwait(false);
            return rows.Select(r => new BucketCount(r.Bucket, r.Group, r.Count)).ToList();
        });

    private static Result<IReadOnlyList<HistoryCountFilter>> ParseFilters(JsonElement filters, IReadOnlyDictionary<string, HistoryCountColumns.ColumnType> columns)
    {
        if (filters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Array.Empty<HistoryCountFilter>();
        }

        if (filters.ValueKind != JsonValueKind.Object)
        {
            return HistoryErrors.InvalidQuery("filters", "not_an_object");
        }

        List<HistoryCountFilter> result = [];
        foreach (var member in filters.EnumerateObject())
        {
            if (!columns.TryGetValue(member.Name, out var type))
            {
                return HistoryErrors.InvalidQuery("filters." + member.Name, "not_whitelisted");
            }

            List<string> values = [];
            var items = member.Value.ValueKind == JsonValueKind.Array ? member.Value.EnumerateArray().ToList() : [member.Value];
            foreach (var item in items)
            {
                var text = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Number => item.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (text is null || (type == HistoryCountColumns.ColumnType.Uuid && !Guid.TryParse(text, out _))
                    || (type is HistoryCountColumns.ColumnType.ShortInteger or HistoryCountColumns.ColumnType.Number && !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
                {
                    return HistoryErrors.InvalidQuery("filters." + member.Name, "value");
                }

                values.Add(type == HistoryCountColumns.ColumnType.Uuid ? Guid.Parse(text).ToString("D") : text);
            }

            if (values.Count == 0)
            {
                return HistoryErrors.InvalidQuery("filters." + member.Name, "value");
            }

            result.Add(new HistoryCountFilter(member.Name, values));
        }

        return result;
    }

    private static Error? Check(PageRequest page, DateTimeOffset from, DateTimeOffset to, int maxDays)
    {
        if (page.Limit is < 1 or > PageRequest.MaxLimit)
        {
            return CommonErrors.OutOfRange(1, PageRequest.MaxLimit);
        }

        if (to <= from)
        {
            return HistoryErrors.InvalidQuery("to", "range");
        }

        return to - from > TimeSpan.FromDays(maxDays) ? HistoryErrors.RangeTooLarge(maxDays) : null;
    }

    private static async Task<Result<T>> GuardedAsync<T>(Func<Task<Result<T>>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (HistoryQueryTimeoutException)
        {
            return HistoryErrors.Timeout();
        }
    }

    private static JournalEvent ToJournalEvent(HistoryEventRow r)
    {
        var correlation = r.CorrelationJson is null
            ? RecordCorrelation.None
            : JsonSerializer.Deserialize(r.CorrelationJson, HistoryJson.Info<HistoryCorrelation>()) is { } c
                ? new RecordCorrelation(c.Command is { } cmd ? CommandId.From(cmd) : null, c.Alarm is { } alarm ? AlarmId.From(alarm) : null, c.AutomationRun is { } run ? AutomationRunId.From(run) : null)
                : RecordCorrelation.None;
        var source = r.EntityId is { } entity
            ? new JournalEventSource(entity, r.EntityKind == 1, r.SourceName, r.TypeId, r.LocationId, r.LocationNames ?? [], r.ServerId, r.TagKeys ?? [])
            : null;
        return new JournalEvent(
            r.Id, r.Seq, r.Kind, (EventCategory)r.Category, (Severity)r.Severity, r.SourceTime, r.Time, source,
            new RecordOrigin((RecordOriginType)r.OriginType, r.OriginId), HistoryJson.Parse(r.PayloadJson), HistoryJson.ParseOrNull(r.PersonalJson),
            r.TitleKey, HistoryJson.Parse(r.TitleArgsJson), correlation);
    }

    private static AlarmHistoryRow ToAlarmRow(HistoryAlarmRow r) => new(
        r.Id, r.Time, r.RuleId, r.RuleKey, r.RuleVersion, r.SourceId, r.SourceName, r.LocationId, r.TypeId, (AlarmHistoryState)r.State, r.PriorityInitial,
        r.PriorityFinal, r.Count, r.LastOccurrenceAt, r.AcknowledgedAt, r.AcknowledgedBy, r.ResolvedAt, r.ClosedAt, r.ClosedBy, r.Report, r.EscalationLevel, r.FalseAlarm);

    private static CommandHistoryRow ToCommandRow(HistoryCommandRow r) => new(
        r.Id, r.Time, r.GroupId, r.DeviceId, r.DeviceName, r.Path, HistoryJson.Parse(r.ArgumentsJson), HistoryJson.Parse(r.IssuerJson), r.IssuerUser, r.Reason,
        r.Risk, r.Priority, (CommandHistoryState)r.State, HistoryJson.ParseOrNull(r.FailureJson), r.QueuedAt, r.DispatchedAt, r.CompletedAt, r.VerifiedAt,
        r.Attempt, HistoryJson.ParseOrNull(r.ApprovalJson));

    private static SeriesPoint ToPoint(HistorySeriesRow r)
    {
        WpValue? value = r.VNum is { } n ? WpValue.Decimal(n)
            : r.VText is { } t ? WpValue.String(t)
            : r.VBool is { } b ? WpValue.Bool(b)
            : r.VJson is { } j ? JsonSerializer.Deserialize(j, HistoryJson.Info<WpValue>())
            : null;
        return new SeriesPoint(r.Time, value, r.Min, r.Avg, r.Max, r.Count);
    }
}
