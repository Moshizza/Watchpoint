using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;
using Watchpoint.Registry.Internal.Api;

namespace Watchpoint.Registry.Contracts;

/// <summary>The registry endpoints (REG §11) on the API framework.</summary>
public static class RegistryEndpoints
{
    /// <summary>Permission of the reads (M1 fallback: any authenticated user; M3: scopes via RGT).</summary>
    public const string ViewPermission = "operate.devices.view";

    /// <summary>Permission of the writes (M1–M2: administrators).</summary>
    public const string EditPermission = "config.registry.edit";

    /// <summary>Maps the registry endpoints.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns><paramref name="endpoints"/>.</returns>
    public static IEndpointRouteBuilder MapRegistryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/v1");

        var site = api.MapGroup(string.Empty).WithTags("Site").RequirePermission(ViewPermission);
        site.MapGet("/site", ([FromServices] IRegistryReader r) => HttpResults.Versioned(r.Site, r.Site.Version)).WithName("Site_GetSite");

        var locations = api.MapGroup("/locations").WithTags("Locations").RequirePermission(ViewPermission);
        locations.MapGet("/", ([FromServices] IRegistryReader r) => ApiResults.Page(r.GetLocationTree(), null)).WithName("Locations_ListLocations")
            .WithSummary("Whole tree, flat, ordered by path");
        locations.MapGet("/{id:guid}", (Guid id, [FromServices] IRegistryReader r) =>
            HttpResults.From(r.GetLocation(LocationId.From(id)), l => HttpResults.Versioned(new { location = l, ancestors = r.GetAncestors(l.Id) }, l.Version)))
            .WithName("Locations_GetLocation").WithSummary("One location with its ancestors");

        var tags = api.MapGroup(string.Empty).WithTags("Tags").RequirePermission(ViewPermission);
        tags.MapGet("/tags", ([FromServices] IRegistryReader r) => ApiResults.Page(r.GetTags(), null)).WithName("Tags_ListTags");
        tags.MapGet("/tag-families", ([FromServices] IRegistryReader r) => ApiResults.Page(r.GetTagFamilies(), null)).WithName("Tags_ListTagFamilies");

        var servers = api.MapGroup("/servers").WithTags("Servers").RequirePermission(ViewPermission);
        servers.MapGet("/", ListServers).WithPaging().WithName("Servers_ListServers");
        servers.MapGet("/{id:guid}", (Guid id, HttpContext http, [FromServices] IRegistryReader r) =>
            HttpResults.From(r.GetServer(ServerId.From(id)), s => HttpResults.Versioned(ServerDto.Of(s, http.CurrentUser().Language), s.Version)))
            .WithName("Servers_GetServer");

        var devices = api.MapGroup("/devices").WithTags("Devices");
        var read = devices.MapGroup(string.Empty).RequirePermission(ViewPermission);
        read.MapGet("/", ListDevicesAsync).WithPaging().WithName("Devices_ListDevices").WithSummary("Devices matching the filters (location = subtree)");
        read.MapGet("/{id:guid}", GetDeviceAsync).WithName("Devices_GetDevice").WithSummary("One device, displayName in the caller's culture");
        read.MapGet("/{id:guid}/children", (Guid id, HttpContext http, [FromServices] IRegistryReader r) =>
            ApiResults.Page(r.GetChildren(DeviceId.From(id)).Select(d => DeviceDto.Of(d, http.CurrentUser().Language)).ToList(), null))
            .WithName("Devices_ListChildren");
        read.MapGet("/{id:guid}/links", (Guid id, [FromServices] IRegistryReader r) => ApiResults.Page(r.GetLinks(DeviceId.From(id)), null))
            .WithName("Devices_ListLinks");
        devices.MapPatch("/{id:guid}", PatchDeviceAsync).RequirePermission(EditPermission).ConfigurationEndpoint().Versioned()
            .WithName("Devices_PatchDevice").WithSummary("JSON Merge Patch of name, locationId, tags, enabled, maintenance, attributes (If-Match)");

        var connections = api.MapGroup("/connections/{id:guid}/discovered").WithTags("Discovered");
        connections.MapGet("/", ListDiscoveredAsync).RequirePermission(ViewPermission).WithName("Discovered_ListDiscovered");
        connections.MapPost("/import", ImportAsync).RequirePermission(EditPermission).ConfigurationEndpoint().WithName("Discovered_ImportDiscovered");
        connections.MapPost("/ignore", IgnoreAsync).RequirePermission(EditPermission).ConfigurationEndpoint().WithName("Discovered_IgnoreDiscovered");

        api.MapPost("/registry/batch", ApplyBatchAsync).WithTags("Registry").RequirePermission(EditPermission).ConfigurationEndpoint().Idempotent()
            .WithName("Registry_ApplyBatch").WithSummary("Apply a registry change batch");
        return endpoints;
    }

    /// <summary>
    /// Binds the device filters of <c>GET /api/v1/devices</c> (REG §11): <c>server</c>, <c>location</c> (subtree),
    /// <c>type</c>, <c>category</c>, <c>capability</c>, <c>tag</c> (all), <c>anyTag</c>, <c>text</c>, <c>enabled</c>,
    /// <c>maintenance</c>, <c>orphaned</c>, <c>parent</c>, <c>roots</c>, <c>sort</c>; list filters repeat or are comma-separated.
    /// The visible scope of the caller becomes <see cref="DeviceQuery.Within"/>.
    /// </summary>
    /// <param name="http">The HTTP context.</param>
    /// <param name="scope">The caller's visible scope.</param>
    /// <returns>The query, or field errors on <c>/query/…</c>.</returns>
    public static Result<DeviceQuery> BindDeviceQuery(HttpContext http, IVisibleScope scope)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(scope);
        var q = http.Request.Query;
        var errors = new List<FieldError>();
        var query = new DeviceQuery(
            Servers: Guids(q, "server", errors, ServerId.From),
            LocationsSubtree: Guids(q, "location", errors, LocationId.From),
            Types: Strings(q, "type", errors, TypeId.Parse),
            Categories: Strings(q, "category", errors, c => Enum.TryParse<Category>(c, ignoreCase: true, out var v) && Enum.IsDefined(v) && !int.TryParse(c, out _)
                ? Result.Success(v) : (Result<Category>)CommonErrors.InvalidFormat("category")),
            Capabilities: Strings(q, "capability", errors, CapabilityId.Parse),
            TagsAll: Guids(q, "tag", errors, TagId.From),
            TagsAny: Guids(q, "anyTag", errors, TagId.From),
            Text: q.TryGetValue("text", out var text) && !StringValues.IsNullOrEmpty(text) ? text.ToString() : null,
            Enabled: Bool(q, "enabled", errors),
            Maintenance: Bool(q, "maintenance", errors),
            Orphaned: Bool(q, "orphaned", errors),
            Parent: Guids(q, "parent", errors, DeviceId.From) is [var parent] ? parent : null,
            RootsOnly: Bool(q, "roots", errors) ?? false,
            Within: scope.IsEverything ? null : scope.Devices);
        return errors.Count > 0 ? CommonErrors.ValidationFailed(errors) : query;
    }

    private static async Task<IResult> ListDevicesAsync(HttpContext http, [FromServices] IRegistryReader registry)
    {
        var user = http.CurrentUser();
        var scope = await user.GetVisibleScopeAsync(PermissionCode.FromTrusted(ViewPermission), http.RequestAborted).ConfigureAwait(false);
        var bound = BindDeviceQuery(http, scope);
        var paging = http.Paging(DeviceQuery.SortFields, DeviceQuery.DefaultSort);
        if (!bound.TryGetValue(out var query, out var error) || !paging.TryGetValue(out var p, out error))
        {
            return HttpResults.Problem(Merge(bound, paging) ?? error);
        }

        var page = registry.QueryDevices(query with { Sort = p.Sort }, p.Page);
        return ApiResults.Page(page.Items.Select(d => DeviceDto.Of(d, user.Language)).ToList(), page.NextCursor);
    }

    private static async Task<IResult> GetDeviceAsync(Guid id, HttpContext http, [FromServices] IRegistryReader registry)
    {
        var user = http.CurrentUser();
        var deviceId = DeviceId.From(id);
        var scope = await user.GetVisibleScopeAsync(PermissionCode.FromTrusted(ViewPermission), http.RequestAborted).ConfigureAwait(false);
        if (!scope.IsEverything && scope.Devices?.Contains(deviceId) != true)
        {
            return HttpResults.Problem(RegistryErrors.DeviceNotFound(id.ToString()));
        }

        return HttpResults.From(registry.GetDevice(deviceId), d => HttpResults.Versioned(DeviceDto.Of(d, user.Language), d.Version));
    }

    private static async Task<IResult> PatchDeviceAsync(Guid id, HttpContext http, [FromServices] IRegistryReader registry, [FromServices] IRegistryWriter writer)
    {
        using var body = await ReadJsonAsync(http).ConfigureAwait(false);
        if (body is null)
        {
            return HttpResults.Problem(ApiErrors.RequestInvalid());
        }

        var errors = new List<FieldError>();
        var patch = RegistryJson.ParseDevicePatch(body.RootElement, string.Empty, errors);
        if (patch is null)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed(errors));
        }

        var deviceId = DeviceId.From(id);
        var user = http.CurrentUser();
        var applied = await writer.ApplyAsync(new RegistryChangeBatch([new RegistryChange.PatchDevice(deviceId, patch, http.ExpectedVersion())]),
            new ChangeContext(user.UserId, "api", null, null), http.RequestAborted).ConfigureAwait(false);
        if (!applied.IsSuccess)
        {
            return HttpResults.Problem(applied.Error);
        }

        return HttpResults.From(registry.GetDevice(deviceId), d => HttpResults.Versioned(DeviceDto.Of(d, user.Language), d.Version));
    }

    private static async Task<IResult> ApplyBatchAsync(HttpContext http, [FromServices] IRegistryWriter writer)
    {
        using var body = await ReadJsonAsync(http).ConfigureAwait(false);
        if (body is null)
        {
            return HttpResults.Problem(ApiErrors.RequestInvalid());
        }

        var batch = RegistryJson.ParseBatch(body.RootElement);
        if (!batch.TryGetValue(out var changes, out var error))
        {
            return HttpResults.Problem(error);
        }

        var comment = body.RootElement.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var applied = await writer.ApplyAsync(changes, new ChangeContext(http.CurrentUser().UserId, "api", null, comment), http.RequestAborted).ConfigureAwait(false);
        return HttpResults.From(applied, () => Results.Json(new { applied = changes.Changes.Count }, ApiJson.Options));
    }

    private static IResult ListServers(HttpContext http, Guid? connection, Guid? location, string? text, [FromServices] IRegistryReader registry) =>
        HttpResults.From(http.Paging(), p =>
        {
            var page = registry.QueryServers(new ServerQuery(connection is { } cid ? ConnectionId.From(cid) : null,
                location is { } lid ? LocationId.From(lid) : null, null, text), p.Page);
            return ApiResults.Page(page.Items.Select(s => ServerDto.Of(s, http.CurrentUser().Language)).ToList(), page.NextCursor);
        });

    private static async Task<IResult> ListDiscoveredAsync(Guid id, string? status, HttpContext http, [FromServices] IInventoryReconciler reconciler)
    {
        DiscoveredStatus? filter = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<DiscoveredStatus>(status, ignoreCase: true, out var parsed) || int.TryParse(status, out _))
            {
                return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/query/status", CommonErrorCodes.ValueNotAllowed)]));
            }

            filter = parsed;
        }

        var items = await reconciler.GetDiscoveredAsync(ConnectionId.From(id), filter, http.RequestAborted).ConfigureAwait(false);
        return ApiResults.Page(items.Select(DiscoveredDto.Of).ToList(), null);
    }

    internal sealed record ImportBody(IReadOnlyList<string>? NativeIds, IReadOnlyDictionary<string, ImportOverridesBody>? Overrides, Guid? DefaultLocation);

    internal sealed record ImportOverridesBody(Kernel.Text.LocalizedName? Name, Guid? LocationId, IReadOnlyList<Guid>? Tags);

    internal sealed record IgnoreBody(IReadOnlyList<string>? NativeIds, bool? Ignored);

    private static async Task<IResult> ImportAsync(Guid id, ImportBody body, HttpContext http, [FromServices] IInventoryReconciler reconciler)
    {
        if (body.NativeIds is not { Count: > 0 } ids)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/nativeIds", CommonErrorCodes.ValueRequired)]));
        }

        var overrides = body.Overrides?.ToDictionary(o => o.Key, o => new ImportOverrides(o.Value.Name,
            o.Value.LocationId is { } l ? LocationId.From(l) : null, o.Value.Tags?.Select(TagId.From).ToList()));
        var request = new ImportRequest(ids, overrides, body.DefaultLocation is { } d ? LocationId.From(d) : null);
        var imported = await reconciler.ImportAsync(ConnectionId.From(id), request, new ChangeContext(http.CurrentUser().UserId, "api", null, null), http.RequestAborted)
            .ConfigureAwait(false);
        return HttpResults.From(imported);
    }

    private static async Task<IResult> IgnoreAsync(Guid id, IgnoreBody body, HttpContext http, [FromServices] IInventoryReconciler reconciler)
    {
        if (body.NativeIds is not { Count: > 0 } ids)
        {
            return HttpResults.Problem(CommonErrors.ValidationFailed([FieldError.Create("/nativeIds", CommonErrorCodes.ValueRequired)]));
        }

        return HttpResults.From(await reconciler.SetIgnoredAsync(ConnectionId.From(id), ids, body.Ignored ?? true, http.RequestAborted).ConfigureAwait(false));
    }

    private static Error? Merge(Result<DeviceQuery> a, Result<PagingParameters> b)
    {
        var fields = new List<FieldError>();
        if (!a.IsSuccess)
        {
            fields.AddRange(a.Error.FieldErrors);
        }

        if (!b.IsSuccess)
        {
            fields.AddRange(b.Error.FieldErrors);
        }

        return fields.Count > 0 ? CommonErrors.ValidationFailed(fields) : null;
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpContext http)
    {
        try
        {
            return await JsonDocument.ParseAsync(http.Request.Body, new JsonDocumentOptions { MaxDepth = ApiJson.MaxDepth }, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<T>? Guids<T>(IQueryCollection q, string name, List<FieldError> errors, Func<Guid, T> create)
    {
        if (!q.TryGetValue(name, out var values) || StringValues.IsNullOrEmpty(values))
        {
            return null;
        }

        var list = new List<T>();
        foreach (var part in values.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (Guid.TryParse(part, out var guid))
            {
                list.Add(create(guid));
            }
            else
            {
                errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat, ("expected", "uuid")));
                return null;
            }
        }

        return list;
    }

    private static List<T>? Strings<T>(IQueryCollection q, string name, List<FieldError> errors, Func<string, Result<T>> parse)
    {
        if (!q.TryGetValue(name, out var values) || StringValues.IsNullOrEmpty(values))
        {
            return null;
        }

        var list = new List<T>();
        foreach (var part in values.SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (parse(part).TryGetValue(out var value, out _))
            {
                list.Add(value);
            }
            else
            {
                errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat));
                return null;
            }
        }

        return list;
    }

    private static bool? Bool(IQueryCollection q, string name, List<FieldError> errors)
    {
        if (!q.TryGetValue(name, out var values) || StringValues.IsNullOrEmpty(values))
        {
            return null;
        }

        if (bool.TryParse(values.ToString(), out var b))
        {
            return b;
        }

        errors.Add(FieldError.Create("/query/" + name, CommonErrorCodes.ValueInvalidFormat, ("expected", "boolean")));
        return null;
    }
}
