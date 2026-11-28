using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Tests.Support;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Xunit;
using static Watchpoint.Api.Tests.ProblemTests;

namespace Watchpoint.Api.Tests;

public sealed class AuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void MapGuarded(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/tests/operate", (HttpContext c) => Results.Ok(c.CurrentUser().Language)).RequirePermission("operate.devices.view").WithName("Tests_GetOperate");
        app.MapGet("/api/v1/tests/config", () => Results.Ok()).RequirePermission("config.registry.edit").WithName("Tests_GetConfig");
        app.MapGet("/api/v1/tests/desktop", () => Results.Ok()).RequirePermission("operate.devices.view").ConfigurationEndpoint().WithName("Tests_GetDesktop");
        app.MapGet("/api/v1/tests/me", () => Results.Ok()).RequireAuthentication().WithName("Tests_GetMe");
    }

    [Fact]
    public async Task API_U_006_Missing_invalid_or_expired_token_answers_401()
    {
        await using var host = await ApiHost.StartAsync(MapGuarded);

        await ProblemOf(await host.Client.GetAsync("/api/v1/tests/operate", Ct), HttpStatusCode.Unauthorized, "common.unauthenticated");
        await ProblemOf(await host.Client.GetAsync("/api/v1/tests/me", Ct), HttpStatusCode.Unauthorized, "common.unauthenticated");

        using var invalid = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tests/operate");
        invalid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        await ProblemOf(await host.Client.SendAsync(invalid, Ct), HttpStatusCode.Unauthorized, "identity.token.invalid");

        using var expired = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tests/operate");
        expired.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "expired");
        await ProblemOf(await host.Client.SendAsync(expired, Ct), HttpStatusCode.Unauthorized, "identity.token.expired");

        // The query-string token is accepted only on the hub path.
        (await host.Client.GetAsync("/api/v1/tests/operate?access_token=" + host.Tokens.Issue(false, ClientKind.Web), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var valid = host.ClientFor();
        (await valid.GetStringAsync("/api/v1/tests/operate", Ct)).ShouldBe("\"fr\"");
    }

    [Fact]
    public async Task API_U_007_Revoked_session_token_answers_401_revoked()
    {
        await using var host = await ApiHost.StartAsync(MapGuarded);
        using var client = host.ClientFor();
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        host.Sessions.Revoked.Add(host.Tokens.ClaimsOf(token).SessionId);

        await ProblemOf(await client.GetAsync("/api/v1/tests/operate", Ct), HttpStatusCode.Unauthorized, "identity.token.revoked");
    }

    [Fact]
    public async Task API_U_008_M1_fallback_config_needs_administrator_operate_needs_authentication()
    {
        await using var host = await ApiHost.StartAsync(MapGuarded);
        using var user = host.ClientFor(admin: false);
        using var admin = host.ClientFor(admin: true);

        (await user.GetAsync("/api/v1/tests/operate", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = await ProblemOf(await user.GetAsync("/api/v1/tests/config", Ct), HttpStatusCode.Forbidden, "common.forbidden");
        denied.GetProperty("args").GetProperty("permission").GetString().ShouldBe("config.registry.edit");
        (await admin.GetAsync("/api/v1/tests/config", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_U_009_Permission_filter_uses_the_registered_evaluator()
    {
        var evaluator = new OnlyEvaluator("operate.special");
        await using var host = await ApiHost.StartAsync(app =>
        {
            MapGuarded(app);
            app.MapGet("/api/v1/tests/special", () => Results.Ok()).RequirePermission("operate.special").WithName("Tests_GetSpecial");
        }, services: s => s.AddSingleton<IPermissionEvaluator>(evaluator));
        using var admin = host.ClientFor(admin: true);

        (await admin.GetAsync("/api/v1/tests/special", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync("/api/v1/tests/config", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        evaluator.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task API_U_010_Configuration_endpoint_refuses_web_and_mobile_tokens()
    {
        await using var host = await ApiHost.StartAsync(MapGuarded);

        foreach (var kind in new[] { ClientKind.Web, ClientKind.Mobile })
        {
            using var client = host.ClientFor(admin: true, kind: kind);
            var problem = await ProblemOf(await client.GetAsync("/api/v1/tests/desktop", Ct), HttpStatusCode.Forbidden, "api.client_kind.not_allowed");
            problem.GetProperty("args").GetProperty("required").GetString().ShouldBe("desktop");
        }

        using var desktop = host.ClientFor(admin: true, kind: ClientKind.Desktop);
        (await desktop.GetAsync("/api/v1/tests/desktop", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_U_025_Session_touched_once_per_authenticated_request()
    {
        await using var host = await ApiHost.StartAsync(MapGuarded);
        using var client = host.ClientFor();

        await client.GetAsync("/api/v1/tests/operate", Ct);
        await client.GetAsync("/api/v1/tests/me", Ct);
        await host.Client.GetAsync("/api/v1/system/info", Ct);

        host.Sessions.Touches.ShouldBe(2);
    }

    private sealed class OnlyEvaluator(string allowed) : IPermissionEvaluator
    {
        public int Calls { get; private set; }

        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(permission.Value == allowed);
        }

        public ValueTask<IVisibleScope> GetVisibleScopeAsync(ICurrentUser user, PermissionCode permission, CancellationToken ct) =>
            ValueTask.FromResult<IVisibleScope>(EverythingScope.Instance);
    }
}
