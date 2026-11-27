using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Tests.Support;
using Xunit;
using static Watchpoint.Api.Tests.ProblemTests;

namespace Watchpoint.Api.Tests;

public sealed class ConventionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task API_U_005_Endpoint_without_AllowAnonymous_or_permission_is_rejected_at_startup()
    {
        await using var host = await ApiHost.StartAsync(app =>
        {
            app.MapGet("/api/v1/tests/naked", () => Results.Ok()).WithName("Tests_GetNaked");
            app.MapGet("/api/v1/tests/both", () => Results.Ok()).AllowAnonymous().RequirePermission("operate.x").WithName("Tests_GetBoth");
        });

        var problems = ApiConventions.Check(host.Endpoints);

        problems.Count.ShouldBe(2);
        problems.ShouldContain(p => p.Contains("/api/v1/tests/naked", StringComparison.Ordinal) && p.Contains("has 0", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("/api/v1/tests/both", StringComparison.Ordinal) && p.Contains("has 2", StringComparison.Ordinal));
        Should.Throw<InvalidOperationException>(() => ApiConventions.Validate(new FakeRouteBuilder(host)));
    }

    [Fact]
    public async Task API_U_029_Operation_ids_unique_and_following_the_naming_rule()
    {
        await using var host = await ApiHost.StartAsync(app =>
        {
            app.MapGet("/api/v1/tests/a", () => Results.Ok()).AllowAnonymous().WithName("Tests_GetThing");
            app.MapGet("/api/v1/tests/b", () => Results.Ok()).AllowAnonymous().WithName("Tests_GetThing");
            app.MapGet("/api/v1/tests/c", () => Results.Ok()).AllowAnonymous().WithName("getThing");
            app.MapGet("/api/v1/tests/d", () => Results.Ok()).AllowAnonymous();
        });

        var problems = ApiConventions.Check(host.Endpoints);

        problems.ShouldContain(p => p.Contains("already used", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("'getThing'", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("/api/v1/tests/d", StringComparison.Ordinal));
        problems.Count.ShouldBe(3);

        // The framework's own endpoints follow the rules.
        await using var clean = await ApiHost.StartAsync(_ => { });
        ApiConventions.Check(clean.Endpoints).ShouldBeEmpty();
    }

    [Fact]
    public async Task API_U_030_Every_endpoint_has_one_guard_from_endpoint_metadata()
    {
        await using var host = await ApiHost.StartAsync(_ => { });

        var api = host.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal)).ToList();

        api.Count.ShouldBeGreaterThan(0);
        api.ShouldAllBe(e => (e.Metadata.GetMetadata<PermissionRequirement>() != null ? 1 : 0)
            + (e.Metadata.GetMetadata<AuthenticationRequired>() != null ? 1 : 0)
            + (e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>() != null ? 1 : 0) == 1);
    }

    [Fact]
    public async Task API_U_017_Paging_binding_limits_cursor_and_sort()
    {
        var sorts = new HashSet<string>(["name", "createdAt"], StringComparer.Ordinal);
        await using var host = await ApiHost.StartAsync(app => app.MapGet("/api/v1/tests/page", (HttpContext c) =>
            HttpResults.From(c.Paging(sorts, new Kernel.Paging.SortSpec([new Kernel.Paging.SortField("name", false)])), p =>
                Results.Ok(new { p.Page.Limit, p.Page.Cursor, Sort = string.Join(',', p.Sort!.Fields.Select(f => (f.Descending ? "-" : "") + f.Name)) })))
            .AllowAnonymous().WithPaging(defaultLimit: 50).WithName("Tests_ListPage"));

        using (var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/tests/page", Ct)))
        {
            doc.RootElement.GetProperty("limit").GetInt32().ShouldBe(50);
            doc.RootElement.GetProperty("sort").GetString().ShouldBe("name");
        }

        using (var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/v1/tests/page?limit=1000&cursor=abc&sort=-createdAt,name", Ct)))
        {
            doc.RootElement.GetProperty("limit").GetInt32().ShouldBe(1000);
            doc.RootElement.GetProperty("cursor").GetString().ShouldBe("abc");
            doc.RootElement.GetProperty("sort").GetString().ShouldBe("-createdAt,name");
        }

        foreach (var (query, path, code) in new[]
        {
            ("limit=0", "/query/limit", "common.value.out_of_range"),
            ("limit=1001", "/query/limit", "common.value.out_of_range"),
            ("limit=abc", "/query/limit", "common.value.out_of_range"),
            ("cursor=" + new string('c', 2049), "/query/cursor", "common.cursor.invalid"),
            ("sort=color", "/query/sort", "common.sort.invalid_field"),
        })
        {
            var problem = await ProblemOf(await host.Client.GetAsync("/api/v1/tests/page?" + query, Ct), HttpStatusCode.BadRequest, "common.validation.failed");
            var field = problem.GetProperty("errors")[0];
            field.GetProperty("path").GetString().ShouldBe(path);
            field.GetProperty("code").GetString().ShouldBe(code);
        }
    }

    private sealed class FakeRouteBuilder(ApiHost host) : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => host.Services;

        public ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> DataSources { get; } =
            [new Microsoft.AspNetCore.Routing.DefaultEndpointDataSource(host.Endpoints)];

        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }
}
