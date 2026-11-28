using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Internal.Jobs;
using Watchpoint.Api.Tests.Support;
using Watchpoint.Kernel.Results;
using Watchpoint.Testing.Async;
using Xunit;
using static Watchpoint.Api.Tests.ProblemTests;

namespace Watchpoint.Api.Tests;

public sealed class PlatformTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task API_U_018_Rate_limits_anonymous_and_authenticated_with_Retry_After()
    {
        await using var host = await ApiHost.StartAsync(
            app => app.MapGet("/api/v1/tests/ping", () => Results.Ok()).AllowAnonymous().WithName("Tests_GetPing"),
            new Dictionary<string, string?>
            {
                ["Api:RateLimits:AnonymousPerSecond"] = "1",
                ["Api:RateLimits:AnonymousBurst"] = "2",
                ["Api:RateLimits:AuthenticatedPerSecond"] = "1",
                ["Api:RateLimits:AuthenticatedBurst"] = "3",
            });

        var anonymous = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            anonymous.Add((await host.Client.GetAsync("/api/v1/tests/ping", Ct)).StatusCode);
        }

        anonymous.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);
        var limited = await host.Client.GetAsync("/api/v1/tests/ping", Ct);
        await ProblemOf(limited, HttpStatusCode.TooManyRequests, "common.rate_limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();

        // A session has its own bucket (burst 3), independent of the anonymous one.
        using var client = host.ClientFor();
        var authenticated = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            authenticated.Add((await client.GetAsync("/api/v1/tests/ping", Ct)).StatusCode);
        }

        authenticated.ShouldBe([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests]);

        // Health endpoints are not limited.
        (await host.Client.GetAsync("/health/live", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task API_U_019_Heavy_policy_runs_2_queues_4_and_refuses_the_7th()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var arrived = 0;
        await using var host = await ApiHost.StartAsync(app => app.MapGet("/api/v1/tests/heavy", async () =>
        {
            Interlocked.Increment(ref running);
            await gate.Task;
            return Results.Ok();
        }).RequireAuthentication().Heavy().WithName("Tests_GetHeavy"), before: app => app.Use(async (c, next) =>
        {
            Interlocked.Increment(ref arrived);
            await next(c);
        }));
        using var client = host.ClientFor();

        var requests = Enumerable.Range(0, 6).Select(_ => client.GetAsync("/api/v1/tests/heavy", Ct)).ToList();
        await Eventually.UntilAsync(() => Volatile.Read(ref running) == 2 && Volatile.Read(ref arrived) == 6, because: "2 running, 4 queued", ct: Ct);

        // The 6th arrival reaches the limiter's queue synchronously after the counter; give it a moment before the 7th.
#pragma warning disable WP0005 // No signal exists for "enqueued in the rate limiter"; a short real-time grace avoids a racy 7th request.
        await Task.Delay(50, Ct);
#pragma warning restore WP0005
        await ProblemOf(await client.GetAsync("/api/v1/tests/heavy", Ct), HttpStatusCode.TooManyRequests, "common.rate_limited");
        Volatile.Read(ref running).ShouldBe(2);
        gate.SetResult();
        (await Task.WhenAll(requests)).ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_U_022_Jobs_202_with_Location_status_transitions_and_cancel()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ApiHost.StartAsync(app =>
        {
            app.MapPost("/api/v1/tests/jobs", (HttpContext c, IJobRegistry jobs) => ApiResults.Accepted(jobs.Start("test.work", c.CurrentUser().UserId, async (progress, ct) =>
            {
                progress.Report(40, "jobs.test.half");
                await finish.Task.WaitAsync(ct);
                return (JsonElement?)JsonDocument.Parse("{\"done\":true}").RootElement.Clone();
            }))).RequireAuthentication().WithName("Tests_StartJob");
        });
        using var client = host.ClientFor();
        var registry = (JobRegistry)host.Services.GetRequiredService<IJobRegistry>();

        var accepted = await client.PostAsync("/api/v1/tests/jobs", null, Ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var location = accepted.Headers.Location!.ToString();
        using (var body = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(Ct)))
        {
            body.RootElement.GetProperty("status").GetString().ShouldBe("running");
            location.ShouldBe("/api/v1/jobs/" + body.RootElement.GetProperty("jobId").GetGuid());
        }

        finish.SetResult();
        await registry.WhenFinishedAsync(Guid.Parse(location[(location.LastIndexOf('/') + 1)..]));
        using (var done = JsonDocument.Parse(await client.GetStringAsync(location, Ct)))
        {
            done.RootElement.GetProperty("status").GetString().ShouldBe("succeeded");
            done.RootElement.GetProperty("progress").GetInt32().ShouldBe(100);
            done.RootElement.GetProperty("result").GetProperty("done").GetBoolean().ShouldBeTrue();
        }

        // Cancel a running job.
        var never = registry.Start("test.never", host.Tokens.ClaimsOf(client.DefaultRequestHeaders.Authorization!.Parameter!).UserId,
            async (_, ct) => { await new TaskCompletionSource().Task.WaitAsync(ct); return Result.Success<JsonElement?>(null); });
        using (var cancelled = JsonDocument.Parse(await (await client.DeleteAsync("/api/v1/jobs/" + never.Id, Ct)).Content.ReadAsStringAsync(Ct)))
        {
            cancelled.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
        }

        await registry.WhenFinishedAsync(never.Id);
        registry.Find(never.Id)!.Status.ShouldBe(JobStatus.Cancelled);

        // Failed work records its error.
        var failed = registry.Start("test.fail", default, (_, _) => Task.FromResult<Result<JsonElement?>>(Error.Unavailable("test.down")));
        await registry.WhenFinishedAsync(failed.Id);
        registry.Find(failed.Id)!.Error!.Code.Value.ShouldBe("test.down");

        // Retained 24 h after the end.
        host.Time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        registry.Find(failed.Id).ShouldBeNull();
    }

    [Fact]
    public async Task API_U_023_Jobs_visible_only_to_owner_or_administrator()
    {
        await using var host = await ApiHost.StartAsync(_ => { });
        using var owner = host.ClientFor();
        using var stranger = host.ClientFor();
        using var admin = host.ClientFor(admin: true);
        var ownerId = host.Tokens.ClaimsOf(owner.DefaultRequestHeaders.Authorization!.Parameter!).UserId;
        var job = host.Services.GetRequiredService<IJobRegistry>().Start("test.mine", ownerId, (_, _) => Task.FromResult(Result.Success<JsonElement?>(null)));

        (await owner.GetAsync("/api/v1/jobs/" + job.Id, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.GetAsync("/api/v1/jobs/" + job.Id, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ProblemOf(await stranger.GetAsync("/api/v1/jobs/" + job.Id, Ct), HttpStatusCode.NotFound, "api.job.not_found");
        await ProblemOf(await stranger.DeleteAsync("/api/v1/jobs/" + job.Id, Ct), HttpStatusCode.NotFound, "api.job.not_found");
        await ProblemOf(await host.Client.GetAsync("/api/v1/jobs/" + job.Id, Ct), HttpStatusCode.Unauthorized, "common.unauthenticated");
    }

    [Fact]
    public async Task API_U_024_System_info_is_anonymous_and_contains_no_secrets()
    {
        await using var host = await ApiHost.StartAsync(_ => { });

        var response = await host.Client.GetAsync("/api/v1/system/info", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(
            ["product", "version", "apiVersion", "liveProtocol", "siteName", "nodeId", "nodeName", "environment", "features", "supportedCultures", "time"]);
        doc.RootElement.GetProperty("product").GetString().ShouldBe("Watchpoint");
        doc.RootElement.GetProperty("apiVersion").GetString().ShouldBe("1.0");
        doc.RootElement.GetProperty("liveProtocol").GetInt32().ShouldBe(1);
        doc.RootElement.GetProperty("supportedCultures").EnumerateArray().Select(e => e.GetString()).ShouldBe(["en", "fr", "ar"]);
        doc.RootElement.GetProperty("time").GetString().ShouldBe("2026-01-05T08:00:00.000Z");
    }

    [Fact]
    public async Task API_U_026_Forwarded_headers_ignored_unless_trusted_proxies_configured()
    {
        static void Map(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app) =>
            app.MapGet("/api/v1/tests/ip", (HttpContext c) => Results.Text(c.Connection.RemoteIpAddress?.ToString() ?? "-")).AllowAnonymous().WithName("Tests_GetIp");

        async Task<string> IpVia(ApiHost host)
        {
            var context = await host.Server.SendAsync(c =>
            {
                c.Request.Method = "GET";
                c.Request.Path = "/api/v1/tests/ip";
                c.Request.Headers["X-Forwarded-For"] = "203.0.113.7";
                c.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
            }, Ct);
            using var reader = new StreamReader(context.Response.Body);
            return await reader.ReadToEndAsync(Ct);
        }

        await using (var untrusted = await ApiHost.StartAsync(Map))
        {
            (await IpVia(untrusted)).ShouldBe("10.0.0.5");
        }

        await using (var trusted = await ApiHost.StartAsync(Map, new Dictionary<string, string?> { ["Http:TrustedProxies:0"] = "10.0.0.0/8" }))
        {
            (await IpVia(trusted)).ShouldBe("203.0.113.7");
        }
    }

    [Fact]
    public async Task API_U_027_HSTS_only_in_Production()
    {
        static async Task<HttpContext> HttpsGet(ApiHost host) => await host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Scheme = "https";
            c.Request.Host = new HostString("node.example.com");
            c.Request.Path = "/api/v1/system/info";
        }, Ct);

        await using (var production = await ApiHost.StartAsync(_ => { }, production: true))
        {
            (await HttpsGet(production)).Response.Headers.StrictTransportSecurity.ToString().ShouldStartWith("max-age=");
        }

        await using (var development = await ApiHost.StartAsync(_ => { }))
        {
            (await HttpsGet(development)).Response.Headers.ContainsKey("Strict-Transport-Security").ShouldBeFalse();
        }
    }

    [Fact]
    public async Task API_U_028_Compression_for_large_JSON_responses()
    {
        await using var host = await ApiHost.StartAsync(app =>
            app.MapGet("/api/v1/tests/large", () => ApiResults.Ok(Enumerable.Range(0, 500).Select(i => new { id = i, name = "device " + i }).ToList()))
                .AllowAnonymous().WithName("Tests_GetLarge"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/tests/large");
        request.Headers.AcceptEncoding.ParseAdd("br");
        var response = await host.Client.SendAsync(request, Ct);

        response.Content.Headers.ContentEncoding.ShouldBe(["br"]);
    }
}
