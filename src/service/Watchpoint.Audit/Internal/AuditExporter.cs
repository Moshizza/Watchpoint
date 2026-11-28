using System.Globalization;
using System.Text;
using System.Text.Json;
using Watchpoint.Audit.Contracts;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Audit.Internal;

/// <summary>Streams audit entries as CSV with localised action labels; the export is itself audited as <c>audit.exported</c> (spec AUD §9).</summary>
internal sealed class AuditExporter(AuditQueryService queries, IAuditSink sink) : IAuditExporter
{
    private const int PageSize = 1_000;

    private static readonly string[] _header =
        ["occurredAt", "recordedAt", "chain", "sequence", "action", "actionLabel", "outcome", "severity", "actorKind", "actorId", "actor", "objects", "reason", "correlationId", "details"];

    public async Task<Result<long>> ExportCsvAsync(AuditQuery query, Stream output, string language, AuditActor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(actor);
        var first = await queries.QueryRowsAsync(query, new PageRequest(PageSize), ct).ConfigureAwait(false);
        if (!first.IsSuccess)
        {
            return first.Error;
        }

        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', _header)).ConfigureAwait(false);
        long count = 0;
        var page = first.Value;
        while (true)
        {
            foreach (var entry in await queries.ToEntriesAsync(page.Rows, language, ct).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(Line(entry)).ConfigureAwait(false);
                count++;
            }

            if (page.NextCursor is null)
            {
                break;
            }

            var next = await queries.QueryRowsAsync(query, new PageRequest(PageSize, page.NextCursor), ct).ConfigureAwait(false);
            if (!next.IsSuccess)
            {
                return next.Error;
            }

            page = next.Value;
        }

        await writer.FlushAsync(ct).ConfigureAwait(false);
        await sink.WriteAsync(
            new AuditRecord(
                new AuditAction("audit.exported"), AuditOutcome.Success, actor, [], null,
                JsonSerializer.SerializeToElement(new { format = "csv", rows = count, from = query.From, to = query.To }), AuditSeverity.Sensitive),
            ct).ConfigureAwait(false);
        return count;
    }

    private static string Line(AuditEntry e) => string.Join(
        ',',
        Field(e.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        Field(e.RecordedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
        Field(e.ChainId),
        Field(e.Sequence.ToString(CultureInfo.InvariantCulture)),
        Field(e.Action.Value),
        Field(e.ActionLabel),
        Field(e.Outcome.ToString()),
        Field(e.Severity.ToString()),
        Field(e.Actor.Kind.ToString()),
        Field(e.Actor.Id?.ToString("D")),
        Field(e.Actor.DisplayName),
        Field(string.Join("; ", e.Objects.Select(o => o.Type + ":" + o.Id?.ToString("D") + (o.Label is null ? string.Empty : " (" + o.Label + ")")))),
        Field(e.Reason),
        Field(e.CorrelationId?.ToString("D")),
        Field(e.Details?.GetRawText()));

    /// <summary>Quotes a field; text that a spreadsheet would read as a formula is prefixed with an apostrophe.</summary>
    internal static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
