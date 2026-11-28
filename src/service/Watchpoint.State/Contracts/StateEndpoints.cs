using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Internal;

namespace Watchpoint.State.Contracts;

/// <summary>The state endpoints (STA §7) on the API framework; JSON of a state is STA §7.1.</summary>
public static class StateEndpoints
{
    /// <summary>The maximum ids of <c>POST /api/v1/states/batch</c>.</summary>
    public const int MaxBatchIds = 1_000;

    /// <summary>The maximum scopes of <c>POST /api/v1/aggregates</c>.</summary>
    public const int MaxScopes = 500;

    /// <summary>Maps the state endpoints.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapStateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var g = endpoints.MapGroup("/api/v1").WithTags("States").RequirePermission(RegistryEndpoints.ViewPermission);
        g.MapGet("/devices/{id:guid}/state", GetAsync).WithName("States_GetDeviceState").WithSummary("Device state (STA §7.1)");
        g.MapPost("/states/batch", BatchAsync).WithName("States_GetStates").WithSummary("States of up to 1,000 devices");
        g.MapGet("/states", QueryAsync).WithPaging().WithName("States_QueryStates")
            .WithSummary("Paged states: REG device filters, state=alarm,fault, where=path:value");
        g.MapPost("/aggregates", AggregatesAsync).WithName("States_GetAggregates").WithSummary("Counts by overall state for up to 500 scopes");
        return endpoints;
    }

    internal sealed record BatchBody(IReadOnlyList<Guid>? Ids);

    internal sealed record ScopeBody(string? Kind, Guid Id);

    internal sealed record AggregatesBody(IReadOnlyList<ScopeBody>? Scopes);

    private static async Task<IVisibleScope> ScopeAsync(HttpContext http) =>
        await http.CurrentUser().GetVisibleScopeAsync(PermissionCode.FromTrusted(RegistryEndpoints.ViewPermission), http.RequestAborted).ConfigureAwait(false);

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, [FromServices] IStateReader states)
    {
        var scope = await ScopeAsync(http).ConfigureAwait(false);
        if (!scope.IsEverything && scope.Devices?.Contains(DeviceId.From(id)) != true)
        {
            return HttpResults.Problem(StateErrors.EntityNotFound(id));
        }

        return HttpResults.From(states.Get(id), s => Json(DeviceStateJson.ToJson(s)));
    }

    private static async Task<IResult> BatchAsync(BatchBody body, HttpContext http, [FromServices] IStateReader states)
    {
        if (body.Ids is null)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/ids", CommonErrorCodes.ValueRequired)]));
        }

        if (body.Ids.Count > MaxBatchIds)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/ids", CommonErrorCodes.ValueOutOfRange, ("min", 0L), ("max", (long)MaxBatchIds))]));
        }

        var scope = await ScopeAsync(http).ConfigureAwait(false);
        var ids = scope.IsEverything ? body.Ids : [.. body.Ids.Where(i => scope.Devices?.Contains(DeviceId.From(i)) == true)];
        return Json(Page(states.GetMany([.. ids.Distinct()]), null));
    }

    private static async Task<IResult> QueryAsync(HttpContext http, [FromServices] IStateReader states)
    {
        var scope = await ScopeAsync(http).ConfigureAwait(false);
        var devices = RegistryEndpoints.BindDeviceQuery(http, scope);
        var paging = http.Paging();
        var errors = new List<FieldError>();
        if (!devices.IsSuccess)
        {
            errors.AddRange(devices.Error.FieldErrors);
        }

        if (!paging.IsSuccess)
        {
            errors.AddRange(paging.Error.FieldErrors);
        }

        var q = http.Request.Query;
        List<OverallState>? filter = null;
        if (q.TryGetValue("state", out var stateText) && !StringValues.IsNullOrEmpty(stateText))
        {
            filter = [];
            foreach (var part in stateText.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<OverallState>(part, ignoreCase: true, out var s) && Enum.IsDefined(s) && !int.TryParse(part, out _))
                {
                    filter.Add(s);
                }
                else
                {
                    errors.Add(FieldError.Create("/query/state", CommonErrorCodes.ValueNotAllowed, ("value", part)));
                }
            }
        }

        PropertyCondition? where = null;
        if (q.TryGetValue("where", out var whereText) && !StringValues.IsNullOrEmpty(whereText))
        {
            where = Condition(whereText.ToString());
            if (where is null)
            {
                errors.Add(FieldError.Create("/query/where", CommonErrorCodes.ValueInvalidFormat, ("expected", "path:value")));
            }
        }

        if (errors.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(errors));
        }

        var page = states.Query(new StateQuery(devices.Value, filter, where), paging.Value.Page);
        return HttpResults.From(page, p => Json(Page(p.Items, p.NextCursor)));
    }

    private static IResult AggregatesAsync(AggregatesBody body, [FromServices] IAggregateReader aggregates)
    {
        if (body.Scopes is null)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/scopes", CommonErrorCodes.ValueRequired)]));
        }

        if (body.Scopes.Count > MaxScopes)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/scopes", CommonErrorCodes.ValueOutOfRange, ("min", 0L), ("max", (long)MaxScopes))]));
        }

        var scopes = new List<AggregateScope>();
        var errors = new List<FieldError>();
        for (var i = 0; i < body.Scopes.Count; i++)
        {
            var kind = body.Scopes[i].Kind;
            if (Enum.TryParse<AggregateScopeKind>(kind, ignoreCase: true, out var k) && Enum.IsDefined(k) && !int.TryParse(kind, out _))
            {
                scopes.Add(new AggregateScope(k, body.Scopes[i].Id));
            }
            else
            {
                errors.Add(FieldError.Create($"/scopes/{i}/kind", CommonErrorCodes.ValueNotAllowed));
            }
        }

        if (errors.Count > 0)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(errors));
        }

        var counts = aggregates.GetMany(scopes);
        var items = scopes.Select(s => new AggregateDto(new ScopeDto(Camel(s.Kind.ToString()), s.Id), counts.TryGetValue(s, out var c) ? c.Total : 0,
            counts.TryGetValue(s, out var byState) ? byState.ByState.ToDictionary(b => Camel(b.Key.ToString()), b => b.Value) : [])).ToList();
        return ApiResults.Ok(new { items });
    }

    internal sealed record ScopeDto(string Kind, Guid Id);

    internal sealed record AggregateDto(ScopeDto Scope, int Total, IReadOnlyDictionary<string, int> ByState);

    private static string Camel(string name) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name);

    /// <summary><c>path:value</c>: <c>true</c>/<c>false</c> → bool, an integer → int, anything else → enum code.</summary>
    private static PropertyCondition? Condition(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1 || !PropertyPath.Parse(text[..colon]).TryGetValue(out var path, out _))
        {
            return null;
        }

        var raw = text[(colon + 1)..];
        var value = bool.TryParse(raw, out var b) ? WpValue.Bool(b)
            : long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? WpValue.Int(n)
            : WpValue.Enum(raw);
        return new PropertyCondition(path, value);
    }

    private static string Page(IReadOnlyList<DeviceState> items, string? nextCursor)
    {
        var json = new StringBuilder("{\"items\":[");
        for (var i = 0; i < items.Count; i++)
        {
            json.Append(i == 0 ? string.Empty : ",").Append(DeviceStateJson.ToJson(items[i]));
        }

        json.Append("],\"nextCursor\":").Append(nextCursor is null ? "null" : System.Text.Json.JsonSerializer.Serialize(nextCursor, ApiJson.Options)).Append('}');
        return json.ToString();
    }

    private static IResult Json(string json) => Results.Content(json, "application/json", Encoding.UTF8);
}
