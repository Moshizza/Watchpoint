using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Api.Internal.Idempotency;
using Watchpoint.Api.Tests.Support;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Testing.Async;
using Xunit;
using static Watchpoint.Api.Tests.ProblemTests;

namespace Watchpoint.Api.Tests;

public sealed class VersioningAndIdempotencyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Thing
    {
        public long Version { get; set; } = 3;
    }

    private static void MapThing(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, Thing thing)
    {
        var g = app.MapGroup("/api/v1/things").RequirePermission("operate.things.view").Versioned();
        g.MapGet("/1", () => HttpResults.Versioned(new { name = "one" }, thing.Version)).WithName("Things_GetThing");
        g.MapPatch("/1", (HttpContext c) =>
        {
            if (c.ExpectedVersion() != thing.Version)
            {
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                return HttpResults.Problem(CommonErrors.VersionConflict(c.ExpectedVersion()!.Value.ToString(invariant), thing.Version.ToString(invariant)));
            }

            thing.Version++;
            return HttpResults.Versioned(new { name = "one" }, thing.Version);
        }).WithName("Things_PatchThing");
    }

    [Fact]
    public async Task API_U_011_Versioned_patch_without_If_Match_answers_428()
    {
        await using var host = await ApiHost.StartAsync(app => MapThing(app, new Thing()));
        using var client = host.ClientFor();

        using var content = new StringContent("{}", Encoding.UTF8, "application/merge-patch+json");
        await ProblemOf(await client.PatchAsync("/api/v1/things/1", content, Ct), (HttpStatusCode)428, "common.version.required");
    }

    [Fact]
    public async Task API_U_012_ETag_written_on_GET_and_stale_If_Match_answers_412()
    {
        await using var host = await ApiHost.StartAsync(app => MapThing(app, new Thing()));
        using var client = host.ClientFor();

        var get = await client.GetAsync("/api/v1/things/1", Ct);
        get.Headers.ETag!.Tag.ShouldBe("\"3\"");

        using var ok = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/things/1") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        ok.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        var patched = await client.SendAsync(ok, Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK);
        patched.Headers.ETag!.Tag.ShouldBe("\"4\"");

        using var stale = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/things/1") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"3\"");
        await ProblemOf(await client.SendAsync(stale, Ct), HttpStatusCode.PreconditionFailed, "common.version.conflict");

        using var garbage = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/things/1") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        garbage.Headers.TryAddWithoutValidation("If-Match", "\"abc\"");
        await ProblemOf(await client.SendAsync(garbage, Ct), HttpStatusCode.BadRequest, "common.validation.failed");
    }

    private sealed class Counter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource? Gate { get; set; }

        public async Task<IResult> InvokeAsync()
        {
            var n = Interlocked.Increment(ref _calls);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return Results.Created("/api/v1/orders/" + n, new { n });
        }
    }

    private static HttpRequestMessage Post(string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    public async Task API_U_013_Idempotency_replay_returns_the_stored_response_with_header()
    {
        var counter = new Counter();
        await using var host = await ApiHost.StartAsync(app =>
            app.MapPost("/api/v1/orders", counter.InvokeAsync).RequireAuthentication().Idempotent().WithName("Orders_CreateOrder"));
        using var client = host.ClientFor();

        using var first = Post("key-1", "{\"a\":1}");
        var r1 = await client.SendAsync(first, Ct);
        using var second = Post("key-1", "{\"a\":1}");
        var r2 = await client.SendAsync(second, Ct);

        r1.StatusCode.ShouldBe(HttpStatusCode.Created);
        r2.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await r2.Content.ReadAsStringAsync(Ct)).ShouldBe(await r1.Content.ReadAsStringAsync(Ct));
        r2.Headers.Location.ShouldBe(r1.Headers.Location);
        r2.Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);
        r1.Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        counter.Calls.ShouldBe(1);

        // Another user's key is independent; an invalid key is refused.
        using var other = host.ClientFor();
        using var third = Post("key-1", "{\"a\":1}");
        (await other.SendAsync(third, Ct)).Headers.Contains("Idempotent-Replayed").ShouldBeFalse();
        using var bad = Post("bad key!", "{}");
        await ProblemOf(await client.SendAsync(bad, Ct), HttpStatusCode.BadRequest, "api.idempotency.key_invalid");
    }

    [Fact]
    public async Task API_U_014_Idempotency_key_reuse_with_another_body_answers_422()
    {
        var counter = new Counter();
        await using var host = await ApiHost.StartAsync(app =>
            app.MapPost("/api/v1/orders", counter.InvokeAsync).RequireAuthentication().Idempotent().WithName("Orders_CreateOrder"));
        using var client = host.ClientFor();

        using var first = Post("key-2", "{\"a\":1}");
        await client.SendAsync(first, Ct);
        using var second = Post("key-2", "{\"a\":2}");

        await ProblemOf(await client.SendAsync(second, Ct), HttpStatusCode.UnprocessableEntity, "api.idempotency.mismatch");
        counter.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task API_U_015_Concurrent_duplicate_answers_409_in_progress()
    {
        var counter = new Counter { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await ApiHost.StartAsync(app =>
            app.MapPost("/api/v1/orders", counter.InvokeAsync).RequireAuthentication().Idempotent().WithName("Orders_CreateOrder"));
        using var client = host.ClientFor();

        using var first = Post("key-3", "{}");
        var running = client.SendAsync(first, Ct);
        await Eventually.UntilAsync(() => counter.Calls > 0, because: "the first request runs", ct: Ct);

        using var duplicate = Post("key-3", "{}");
        await ProblemOf(await client.SendAsync(duplicate, Ct), HttpStatusCode.Conflict, "api.idempotency.in_progress");
        counter.Gate.SetResult();
        (await running).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task API_U_016_Idempotency_records_purged_after_24_hours()
    {
        var time = Kernel.Testing.TestTime.CreateProvider();
        var store = new InMemoryIdempotencyStore(time);
        using var purge = new IdempotencyPurgeService(store, time);
        var request = new IdempotencyRequest(UserId.New(), "k", "POST", "/x", "hash");
        (await store.BeginAsync(request, Ct)).Kind.ShouldBe(IdempotencyBeginKind.Started);
        await store.CompleteAsync(request, new StoredResponse(201, [], new Dictionary<string, string>(), time.GetUtcNow()), Ct);

        time.Advance(TimeSpan.FromHours(23));
        (await purge.PurgeOnceAsync(Ct)).ShouldBe(0);
        (await store.BeginAsync(request, Ct)).Kind.ShouldBe(IdempotencyBeginKind.Replay);

        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(1));
        (await purge.PurgeOnceAsync(Ct)).ShouldBe(1);
        (await store.BeginAsync(request, Ct)).Kind.ShouldBe(IdempotencyBeginKind.Started);
    }
}
