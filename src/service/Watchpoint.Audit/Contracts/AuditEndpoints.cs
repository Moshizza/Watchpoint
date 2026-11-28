using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Audit.Contracts;

/// <summary>The audit endpoints (spec AUD §9) on the API framework; all need <see cref="ViewPermission"/>.</summary>
public static class AuditEndpoints
{
    /// <summary>The permission of every audit endpoint.</summary>
    public const string ViewPermission = "admin.audit.view";

    /// <summary>Maps the audit endpoints.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var g = endpoints.MapGroup("/api/v1/audit").WithTags("Audit").RequirePermission(ViewPermission);
        g.MapGet("/", QueryAsync).WithPaging().WithName("Audit_Query").WithSummary("Query the audit trail");
        g.MapGet("/export", ExportAsync).Heavy().WithName("Audit_Export").WithSummary("Export the audit trail as CSV (audited)");
        g.MapGet("/verification", LatestVerificationAsync).WithName("Audit_GetVerification").WithSummary("Latest verification report per chain");
        g.MapPost("/verification", StartVerification).WithName("Audit_StartVerification").WithSummary("Verify a chain on demand (job)");
        g.MapGet("/{id:long}", GetAsync).WithName("Audit_Get").WithSummary("One audit entry with details");
        return endpoints;
    }

    internal sealed record VerificationBody(string? ChainId, long? From, long? To);

    internal sealed record EntryDto(
        long Id, string ChainId, long Sequence, DateTimeOffset OccurredAt, DateTimeOffset RecordedAt, string Action, string ActionLabel, string Outcome, string Severity,
        AuditActor Actor, IReadOnlyList<AuditObjectRef> Objects, string? Reason, JsonElement? Details, Guid? CorrelationId);

    internal static EntryDto ToDto(AuditEntry e) =>
        new(e.Id, e.ChainId, e.Sequence, e.OccurredAt, e.RecordedAt, e.Action.Value, e.ActionLabel, Camel(e.Outcome.ToString()), Camel(e.Severity.ToString()), e.Actor, e.Objects,
            e.Reason, e.Details, e.CorrelationId);

    internal static Result<AuditQuery> Bind(HttpContext http)
    {
        var q = http.Request.Query;
        List<FieldError> errors = [];
        var from = Time(q, "from", errors);
        var to = Time(q, "to", errors);
        var actors = Many(q, "actor").Select(a => Guid.TryParse(a, out var g) ? g : AddError(errors, "actor")).ToList();
        var prefixes = Many(q, "action");
        AuditOutcome? outcome = null;
        if (q.TryGetValue("outcome", out var outcomeText) && !StringValues.IsNullOrEmpty(outcomeText))
        {
            if (Enum.TryParse<AuditOutcome>(outcomeText, ignoreCase: true, out var o) && Enum.IsDefined(o) && !int.TryParse(outcomeText, out _))
            {
                outcome = o;
            }
            else
            {
                errors.Add(FieldError.Create("/query/outcome", CommonErrorCodes.ValueNotAllowed, ("value", outcomeText.ToString())));
            }
        }

        AuditSeverity? severity = null;
        if (q.TryGetValue("minSeverity", out var severityText) && !StringValues.IsNullOrEmpty(severityText))
        {
            if (Enum.TryParse<AuditSeverity>(severityText, ignoreCase: true, out var sv) && Enum.IsDefined(sv) && !int.TryParse(severityText, out _))
            {
                severity = sv;
            }
            else
            {
                errors.Add(FieldError.Create("/query/minSeverity", CommonErrorCodes.ValueNotAllowed, ("value", severityText.ToString())));
            }
        }

        var objectId = OptionalGuid(q, "objectId", errors);
        var correlation = OptionalGuid(q, "correlationId", errors);
        if (errors.Count > 0 || from is null || to is null)
        {
            return CommonErrors.ValidationFailed(errors);
        }

        return new AuditQuery(
            from.Value, to.Value, actors.Count == 0 ? null : actors, prefixes.Count == 0 ? null : prefixes,
            q.TryGetValue("objectType", out var type) && !StringValues.IsNullOrEmpty(type) ? type.ToString() : null, objectId, outcome, severity, correlation,
            q.TryGetValue("text", out var text) && !StringValues.IsNullOrEmpty(text) ? text.ToString() : null,
            q.TryGetValue("sort", out var sort) && string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase) ? SortDirection.Ascending : SortDirection.Descending);
    }

    private static async Task<IResult> QueryAsync(HttpContext http, [FromServices] IAuditQuery audit)
    {
        var bound = Bind(http);
        var paging = http.Paging();
        if (!bound.TryGetValue(out var query, out var error) || !paging.TryGetValue(out var p, out error))
        {
            return HttpResults.Problem(error);
        }

        var page = await audit.QueryAsync(query, p.Page, http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(page, x => ApiResults.Page([.. x.Items.Select(ToDto)], x.NextCursor));
    }

    private static async Task<IResult> GetAsync(long id, HttpContext http, [FromServices] IAuditQuery audit)
    {
        var entry = await audit.GetAsync(id, http.RequestAborted).ConfigureAwait(false);
        return entry is null ? HttpResults.Problem(AuditErrors.EntryNotFound(id)) : ApiResults.Ok(ToDto(entry));
    }

    private static IResult ExportAsync(HttpContext http, [FromServices] IAuditExporter exporter)
    {
        var bound = Bind(http);
        if (!bound.TryGetValue(out var query, out var error))
        {
            return HttpResults.Problem(error);
        }

        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(366))
        {
            return HttpResults.Problem(AuditErrors.RangeTooLarge(366));
        }

        if (Query(http, "format") is { } format && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/query/format", CommonErrorCodes.ValueNotAllowed, ("value", format))]));
        }

        var user = http.CurrentUser();
        var actor = AuditActor.User(user.UserId.Value, "user", null, null, http.Connection.RemoteIpAddress?.ToString());
        var name = "audit-" + query.From.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv";
        return Results.Stream(
            async stream => await exporter.ExportCsvAsync(query, stream, user.Language, actor, http.RequestAborted).ConfigureAwait(false),
            "text/csv; charset=utf-8", name);
    }

    private static async Task<IResult> LatestVerificationAsync(HttpContext http, [FromServices] IAuditVerifier verifier) =>
        ApiResults.Page(await verifier.LatestReportsAsync(http.RequestAborted).ConfigureAwait(false), null);

    private static IResult StartVerification(VerificationBody? body, HttpContext http, [FromServices] IAuditVerifier verifier, [FromServices] IJobRegistry jobs)
    {
        var user = http.CurrentUser();
        var job = jobs.Start("audit.verification", user.UserId, async (_, ct) =>
        {
            var chains = body?.ChainId is { } one ? [one] : await verifier.ListChainsAsync(ct).ConfigureAwait(false);
            if (body?.ChainId is not null && !(await verifier.ListChainsAsync(ct).ConfigureAwait(false)).Contains(body.ChainId))
            {
                return AuditErrors.ChainNotFound(body.ChainId);
            }

            List<VerificationReport> reports = [];
            foreach (var chain in chains)
            {
                reports.Add(await verifier.VerifyAsync(chain, body?.From ?? 1, body?.To, ct).ConfigureAwait(false));
            }

            return Result.Success<JsonElement?>(JsonSerializer.SerializeToElement(reports, ApiJson.Options));
        });
        return ApiResults.Accepted(job);
    }

    private static string? Query(HttpContext http, string name) => http.Request.Query.TryGetValue(name, out var v) && !StringValues.IsNullOrEmpty(v) ? v.ToString() : null;

    private static string Camel(string text) => char.ToLowerInvariant(text[0]) + text[1..];

    private static List<string> Many(IQueryCollection q, string name) =>
        [.. q[name].SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))];

    private static Guid AddError(List<FieldError> errors, string name)
    {
        errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat));
        return Guid.Empty;
    }

    private static Guid? OptionalGuid(IQueryCollection q, string name, List<FieldError> errors)
    {
        if (!q.TryGetValue(name, out var text) || StringValues.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Guid.TryParse(text, out var value))
        {
            return value;
        }

        errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat));
        return null;
    }

    private static DateTimeOffset? Time(IQueryCollection q, string name, List<FieldError> errors)
    {
        if (!q.TryGetValue(name, out var text) || StringValues.IsNullOrEmpty(text))
        {
            errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueRequired));
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
        {
            return value;
        }

        errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat));
        return null;
    }
}
