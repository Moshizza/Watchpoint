using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Configuration;
using Watchpoint.Rights.Internal.Evaluation;

namespace Watchpoint.Rights;

/// <summary>REST endpoints of the rights module (spec RGT §12, part 1).</summary>
public static class RightsEndpoints
{
    private static readonly PermissionCode View = PermissionCode.FromTrusted("config.rights.view");

    /// <summary>Maps <c>/permissions</c>, <c>/users/{id}/grants</c>, <c>/users/{id}/effective</c>, <c>/me/permissions</c> and <c>/user-groups</c>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapRightsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api/v1").WithTags("Rights");

        api.MapGet("/permissions", ([FromServices] IPermissionCatalogue c) => ApiResults.Ok(new { items = c.All.Select(PermissionDto.Of) }))
            .RequireAuthentication().WithName("Rights_ListPermissions").WithSummary("Permission catalogue with label keys");

        api.MapGet("/me/permissions", (HttpContext http, [FromServices] IPermissionCatalogue c, [FromServices] IRightsEvaluator e, [FromServices] TimeProvider time) =>
        {
            var user = http.CurrentUser();
            var now = time.GetUtcNow();
            return ApiResults.Ok(new { permissions = MyPermissions.Of(c, e, user.UserId, user.IsAdministrator, now) });
        }).RequireAuthentication().WithName("Rights_MyPermissions").WithSummary("Permissions held somewhere, for UI gating");

        api.MapGet("/users/{id:guid}/grants", async (Guid id, HttpContext http, [FromServices] IGrantStore store, [FromServices] IPermissionEvaluator p) =>
            await AllowedAsync(http, id, p).ConfigureAwait(false)
                ? ApiResults.Ok(new GrantsDocument([.. store.GetGrants(UserId.From(id)).Select(GrantDto.Of)]))
                : HttpResults.Problem(CommonErrors.Forbidden(View)))
            .RequireAuthentication().WithName("Rights_GetGrants").WithSummary("Grants of a user (config.rights.view or self)");

        api.MapPut("/users/{id:guid}/grants", async (Guid id, GrantsDocument body, [FromServices] IGrantStore store, [FromServices] IPermissionCatalogue c,
            [FromServices] IUserGroupService groups, [FromServices] TimeProvider time, HttpContext http) =>
        {
            if (groups.Get(UserGroupId.From(id)).IsSuccess)
            {
                return HttpResults.Problem(Error.Create(ErrorKind.Unprocessable, "rights.grant.group_target"));
            }

            var user = UserId.From(id);
            var now = time.GetUtcNow();
            var grants = body.Grants.Select(g => g.ToGrant(user) with { CreatedAt = g.CreatedAt ?? now, CreatedBy = g.CreatedBy is { } cb ? UserId.From(cb) : http.CurrentUser().UserId }).ToList();
            var errors = new GrantValidator(new Internal.Catalogue.PermissionCatalogueAccess(c)).Validate(grants);
            if (errors.Count > 0)
            {
                return HttpResults.Problem(errors[0]);
            }

            await store.ReplaceAsync(user, grants, http.RequestAborted).ConfigureAwait(false);
            return ApiResults.Ok(new GrantsDocument([.. grants.Select(GrantDto.Of)]));
        }).RequirePermission("config.rights.edit").ConfigurationEndpoint().WithName("Rights_PutGrants").WithSummary("Full replacement of a user's grants");

        api.MapGet("/users/{id:guid}/effective", async (Guid id, string permission, Guid? device, HttpContext http, [FromServices] IRightsEvaluator e,
            [FromServices] IPermissionEvaluator p, [FromServices] TimeProvider time) =>
        {
            if (!await AllowedAsync(http, id, p).ConfigureAwait(false))
            {
                return HttpResults.Problem(CommonErrors.Forbidden(View));
            }

            if (!PermissionCode.Parse(permission).TryGetValue(out var code, out var error))
            {
                return HttpResults.Problem(error);
            }

            var now = time.GetUtcNow();
            AuthorizationTarget target = device is { } d ? new AuthorizationTarget.Device(DeviceId.From(d)) : new AuthorizationTarget.System();
            var decision = e.Authorize(UserId.From(id), code, target, now);
            var why = e.Explain(UserId.From(id), code, target, now).Select(x => new { grant = GrantDto.Of(x.Grant), x.Matched, x.Reason });
            return ApiResults.Ok(new { decision.Allowed, constraints = decision.EffectiveConstraints, decision.DenialCode, explanation = why });
        }).RequireAuthentication().WithName("Rights_Effective").WithSummary("Decision and explanation for a user");

        var groupRoutes = api.MapGroup("/user-groups");
        groupRoutes.MapGet("/", ([FromServices] IUserGroupService g) => ApiResults.Page(g.List().Select(GroupDto.Of).ToList(), null)).RequirePermission("config.users.view").WithName("UserGroups_List");
        groupRoutes.MapGet("/{id:guid}", (Guid id, [FromServices] IUserGroupService g) => HttpResults.From(g.Get(UserGroupId.From(id)), x => HttpResults.Versioned(GroupDto.Of(x), x.Version)))
            .RequirePermission("config.users.view").WithName("UserGroups_Get");
        groupRoutes.MapPost("/", async (GroupBody body, HttpContext http, [FromServices] IUserGroupService g) =>
            HttpResults.From(await g.CreateAsync(new LocalizedName(body.Name ?? string.Empty, new Dictionary<string, string>()), body.Description, [.. (body.Members ?? []).Select(UserId.From)],
                [.. (body.Managers ?? []).Select(UserId.From)], http.CurrentUser().UserId, http.RequestAborted).ConfigureAwait(false),
                x => HttpResults.CreatedVersioned("/api/v1/user-groups/" + x.Id.Value, GroupDto.Of(x), x.Version)))
            .RequireAuthentication().WithName("UserGroups_Create");
        groupRoutes.MapPatch("/{id:guid}", async (Guid id, GroupBody body, HttpContext http, [FromServices] IUserGroupService g) =>
            HttpResults.From(await g.UpdateAsync(UserGroupId.From(id),
                new UserGroupUpdate(body.Name is null ? null : new LocalizedName(body.Name, new Dictionary<string, string>()), body.Description,
                    body.Members?.Select(UserId.From).ToList(), body.Managers?.Select(UserId.From).ToList()),
                http.ExpectedVersion() ?? -1, http.CurrentUser().UserId, http.RequestAborted).ConfigureAwait(false),
                x => HttpResults.Versioned(GroupDto.Of(x), x.Version)))
            .RequireAuthentication().Versioned().WithName("UserGroups_Patch");
        groupRoutes.MapDelete("/{id:guid}", async (Guid id, HttpContext http, [FromServices] IUserGroupService g) =>
            HttpResults.From(await g.DeleteAsync(UserGroupId.From(id), http.CurrentUser().UserId, http.RequestAborted).ConfigureAwait(false)))
            .RequireAuthentication().WithName("UserGroups_Delete");
        groupRoutes.MapPost("/{id:guid}/bulk", async (Guid id, BulkBody body, HttpContext http, [FromServices] IUserGroupService g) =>
            HttpResults.From(await g.BulkAsync(UserGroupId.From(id), body.Action, body.ValidUntil, http.CurrentUser().UserId, http.RequestAborted).ConfigureAwait(false),
                failed => ApiResults.Ok(new { failed = failed.Select(u => u.Value) })))
            .RequireAuthentication().WithName("UserGroups_Bulk");
        return endpoints;
    }

    private static async Task<bool> AllowedAsync(HttpContext http, Guid id, IPermissionEvaluator evaluator)
    {
        var user = http.CurrentUser();
        return user.UserId.Value == id || await evaluator.HasPermissionAsync(user, View, http.RequestAborted).ConfigureAwait(false);
    }

    private sealed record PermissionDto(string Code, string Family, string Module, string LabelKey, string DescriptionKey, string ScopeKind, IReadOnlyList<string> Constraints, bool Sensitive)
    {
        public static PermissionDto Of(PermissionDefinition d) => new(d.Code.Value, d.Family, d.Module, d.LabelKey, d.DescriptionKey, d.ScopeKind.ToString(),
            [.. d.AllowedConstraints.Select(c => c.ToString())], d.Sensitive);
    }

    private sealed record GroupDto(Guid Id, string Name, string? Description, IReadOnlyList<Guid> Members, IReadOnlyList<Guid> Managers, long Version)
    {
        public static GroupDto Of(UserGroup g) => new(g.Id.Value, g.Name.Default, g.Description, [.. g.Members.Select(m => m.Value)], [.. g.Managers.Select(m => m.Value)], g.Version);
    }

    private sealed record GroupBody(string? Name, string? Description, List<Guid>? Members, List<Guid>? Managers);

    private sealed record BulkBody(GroupBulkAction Action, DateTimeOffset? ValidUntil);
}
