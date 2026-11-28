using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using Watchpoint.Audit.Contracts;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>Audit queries (spec AUD §7): range limit 366 days, keyset paging on <c>(occurred_at, id)</c>, labels resolved at query time.</summary>
internal sealed class AuditQueryService(IAuditStore store, IAuditLabelResolver labels) : IAuditQuery
{
    public const int MaxRangeDays = 366;

    public async Task<Result<Page<AuditEntry>>> QueryAsync(AuditQuery query, PageRequest page, CancellationToken ct)
    {
        var rows = await QueryRowsAsync(query, page, ct).ConfigureAwait(false);
        if (!rows.IsSuccess)
        {
            return rows.Error;
        }

        var entries = await ToEntriesAsync(rows.Value.Rows, "en", ct).ConfigureAwait(false);
        return new Page<AuditEntry>(entries, rows.Value.NextCursor);
    }

    public async Task<Result<(IReadOnlyList<AuditRow> Rows, string? NextCursor)>> QueryRowsAsync(AuditQuery query, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(MaxRangeDays))
        {
            return AuditErrors.RangeTooLarge(MaxRangeDays);
        }

        AuditCursorPosition? after = null;
        if (page.Cursor is not null && !TryDecode(page.Cursor, out after))
        {
            return CommonErrors.ValidationFailed([FieldError.Create("/cursor", CommonErrorCodes.ValueInvalidFormat)]);
        }

        var limit = Math.Clamp(page.Limit, 1, PageRequest.MaxLimit);
        var filter = new AuditRowFilter(
            query.From, query.To, query.Actors, query.ActionPrefixes, query.ObjectType, query.ObjectId,
            query.Outcome is { } o ? (short)o : null, query.MinSeverity is { } s ? (short)s : null, query.CorrelationId, query.Text, query.Sort == SortDirection.Descending);
        var rows = await store.QueryAsync(filter, after, limit + 1, ct).ConfigureAwait(false);
        var hasMore = rows.Count > limit;
        IReadOnlyList<AuditRow> pageRows = hasMore ? [.. rows.Take(limit)] : rows;
        return (pageRows, hasMore ? Encode(pageRows[^1]) : null);
    }

    public async Task<AuditEntry?> GetAsync(long id, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct).ConfigureAwait(false);
        return row is null ? null : (await ToEntriesAsync([row], "en", ct).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<AuditEntry>> ToEntriesAsync(IReadOnlyList<AuditRow> rows, string language, CancellationToken ct)
    {
        var parsed = rows.Select(r => (Row: r, Objects: JsonSerializer.Deserialize(r.ObjectsJson, AuditJsonContext.Default.StoredObjectArray) ?? [])).ToList();
        var wanted = parsed.SelectMany(p => p.Objects).Where(o => o.Id.HasValue).Select(o => (o.Type, o.Id!.Value)).Distinct().ToList();
        var resolved = wanted.Count == 0
            ? new Dictionary<(string Type, Guid Id), string>()
            : await labels.ResolveAsync(wanted, ct).ConfigureAwait(false);
        List<AuditEntry> result = new(parsed.Count);
        foreach (var (row, objects) in parsed)
        {
            var action = new AuditAction(row.Action);
            JsonElement? details = null;
            if (row.DetailsJson is not null)
            {
                using var document = JsonDocument.Parse(row.DetailsJson);
                details = document.RootElement.Clone();
            }

            result.Add(new AuditEntry(
                row.Id, row.ChainId, row.Sequence, row.OccurredAt, row.RecordedAt, action, labels.ActionLabel(action, language),
                (AuditOutcome)row.Outcome, (AuditSeverity)row.Severity,
                JsonSerializer.Deserialize(row.ActorJson, AuditJsonContext.Default.AuditActor)!,
                [.. objects.Select(o => new AuditObjectRef(o.Type, o.Id, o.Id is { } id && resolved.TryGetValue((o.Type, id), out var label) ? label : null))],
                row.Reason, details, row.CorrelationId));
        }

        return result;
    }

    internal static string Encode(AuditRow row) =>
        Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{row.OccurredAt.UtcTicks}:{row.Id}")));

    internal static bool TryDecode(string cursor, out AuditCursorPosition? position)
    {
        position = null;
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var parts = text.Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && ticks <= DateTime.MaxValue.Ticks)
            {
                position = new AuditCursorPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
            // Not a cursor.
        }

        return false;
    }
}
