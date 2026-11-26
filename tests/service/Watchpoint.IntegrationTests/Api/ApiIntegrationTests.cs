using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Shouldly;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>The REST API through a real host (spec API §11, REG §14, MOD §11, STA §11).</summary>
[Trait("layer", "I")]
[Trait("spec", "API")]
public sealed class ApiIntegrationTests(ApiNodeFixture node) : IClassFixture<ApiNodeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonNode> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = JsonNode.Parse(text)!;
        problem["code"]!.GetValue<string>().ShouldBe(code);
        problem["traceId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        return problem;
    }

    private static async Task<JsonNode> FirstDeviceAsync(HttpClient client) =>
        JsonNode.Parse(await client.GetStringAsync("api/v1/devices?limit=1", Ct))!["items"]![0]!;

    [Fact]
    public async Task API_I_001_Sign_in_then_GET_devices_with_the_token()
    {
        using var anonymous = node.Client();
        await ProblemAsync(await anonymous.GetAsync("api/v1/devices", Ct), HttpStatusCode.Unauthorized, "common.unauthenticated");

        var token = await node.SignInAsync("web", Ct);
        using var client = node.Client(token);
        var page = JsonNode.Parse(await client.GetStringAsync("api/v1/devices", Ct))!;
        page["items"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var device = page["items"]![0]!;
        device["displayName"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        device["type"]!["id"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        var me = JsonNode.Parse(await client.GetStringAsync("api/v1/auth/me", Ct))!;
        me["user"]!["login"]!.GetValue<string>().ShouldBe("admin");
        me["session"]!["clientKind"]!.GetValue<string>().ShouldBe("web");
        me.ToJsonString().ShouldNotContain("hash", Case.Insensitive);
        me.ToJsonString().ShouldNotContain("secret", Case.Insensitive);

        (await client.PostAsync("api/v1/auth/sign-out", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("api/v1/devices", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task API_I_002_Device_PATCH_with_ETag_through_the_real_host()
    {
        using var admin = node.Client(node.AdminToken);
        var id = (await FirstDeviceAsync(admin))["id"]!.GetValue<string>();
        var get = await admin.GetAsync($"api/v1/devices/{id}", Ct);
        var etag = get.Headers.ETag!.Tag;

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{id}")
        {
            Content = new StringContent("{\"maintenance\":true,\"attributes\":{\"noSuchAttribute\":null}}", Encoding.UTF8, "application/merge-patch+json"),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", etag);
        var patched = await admin.SendAsync(patch, Ct);
        patched.StatusCode.ShouldBe(HttpStatusCode.OK, await patched.Content.ReadAsStringAsync(Ct));
        patched.Headers.ETag!.Tag.ShouldNotBe(etag);
        JsonNode.Parse(await patched.Content.ReadAsStringAsync(Ct))!["maintenance"]!.GetValue<bool>().ShouldBeTrue();

        using var undo = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{id}") { Content = new StringContent("{\"maintenance\":false}", Encoding.UTF8, "application/json") };
        undo.Headers.TryAddWithoutValidation("If-Match", patched.Headers.ETag!.Tag);
        (await admin.SendAsync(undo, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task REG_I_010_PATCH_device_with_If_Match()
    {
        using var admin = node.Client(node.AdminToken);
        var id = (await FirstDeviceAsync(admin))["id"]!.GetValue<string>();
        var etag = (await admin.GetAsync($"api/v1/devices/{id}", Ct)).Headers.ETag!.Tag;

        HttpRequestMessage Patch(string? ifMatch)
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{id}") { Content = new StringContent("{\"name\":\"Door via API\"}", Encoding.UTF8, "application/json") };
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return request;
        }

        using (var missing = Patch(null))
        {
            await ProblemAsync(await admin.SendAsync(missing, Ct), (HttpStatusCode)428, "common.version.required");
        }

        string newTag;
        using (var ok = Patch(etag))
        {
            var response = await admin.SendAsync(ok, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            newTag = response.Headers.ETag!.Tag;
            JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["displayName"]!.GetValue<string>().ShouldBe("Door via API");
        }

        newTag.ShouldNotBe(etag);
        using var stale = Patch(etag);
        await ProblemAsync(await admin.SendAsync(stale, Ct), HttpStatusCode.PreconditionFailed, "common.version.conflict");

        using var clear = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{id}") { Content = new StringContent("{\"name\":null}", Encoding.UTF8, "application/json") };
        clear.Headers.TryAddWithoutValidation("If-Match", newTag);
        (await admin.SendAsync(clear, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_I_003_Problem_details_for_a_registry_validation_error_with_field_paths()
    {
        using var admin = node.Client(node.AdminToken);
        using var batch = new StringContent("{\"changes\":[{\"op\":\"deleteTag\",\"id\":\"" + Guid.NewGuid() + "\"},{\"op\":\"nope\"}]}", Encoding.UTF8, "application/json");

        var problem = await ProblemAsync(await admin.PostAsync("api/v1/registry/batch", batch, Ct), HttpStatusCode.BadRequest, "common.validation.failed");

        problem["errors"]![0]!["path"]!.GetValue<string>().ShouldBe("/changes/1/op");
        problem["errors"]![0]!["code"]!.GetValue<string>().ShouldBe("common.value.not_allowed");

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{(await FirstDeviceAsync(admin))["id"]}")
        {
            Content = new StringContent("{\"enabled\":\"yes\",\"color\":1}", Encoding.UTF8, "application/json"),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        var fields = (await ProblemAsync(await admin.SendAsync(patch, Ct), HttpStatusCode.BadRequest, "common.validation.failed"))["errors"]!.AsArray();
        fields.Select(f => f!["path"]!.GetValue<string>()).ShouldBe(["/enabled", "/color"]);
    }

    [Fact]
    public async Task API_I_004_Idempotency_table_persisted_in_PostgreSQL()
    {
        using var admin = node.Client(node.AdminToken);
        var key = "api-i-004-" + Guid.NewGuid().ToString("N")[..8];
        HttpRequestMessage Batch()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/registry/batch") { Content = new StringContent("{\"changes\":[]}", Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", key);
            return request;
        }

        using var first = Batch();
        (await admin.SendAsync(first, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var second = Batch();
        (await admin.SendAsync(second, Ct)).Headers.GetValues("Idempotent-Replayed").ShouldBe(["true"]);

        await using var connection = new NpgsqlConnection(node.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT status, route FROM wp_api.idempotency WHERE key = @key", connection);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe(200);
        reader.GetString(1).ShouldContain("/api/v1/registry/batch");
    }

    [Fact]
    public async Task API_I_005_Discovery_job_through_IRT_returns_202_and_completes()
    {
        using var admin = node.Client(node.AdminToken);
        var connection = JsonNode.Parse(await admin.GetStringAsync("api/v1/connections", Ct))!["items"]![0]!["id"]!.GetValue<string>();

        var accepted = await admin.PostAsync($"api/v1/connections/{connection}/discover", null, Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync(Ct));
        var location = accepted.Headers.Location!.ToString();
        location.ShouldStartWith("/api/v1/jobs/");
        JsonNode? job = null;
        await ApiNodeFixture.PollAsync(async () =>
        {
            job = JsonNode.Parse(await admin.GetStringAsync(location.TrimStart('/'), Ct));
            return job!["status"]!.GetValue<string>() != "running";
        }, TestTimeouts.Container, "the discovery job finishes", Ct);
        job!["status"]!.GetValue<string>().ShouldBe("succeeded", job.ToJsonString());
        job["result"].ShouldNotBeNull();
    }

    [Fact]
    public async Task API_I_006_Config_endpoint_refused_for_a_web_client_token()
    {
        var web = await node.SignInAsync("web", Ct);
        using var client = node.Client(web);
        var id = (await FirstDeviceAsync(client))["id"]!.GetValue<string>();
        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{id}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        patch.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        var problem = await ProblemAsync(await client.SendAsync(patch, Ct), HttpStatusCode.Forbidden, "api.client_kind.not_allowed");

        problem["args"]!["required"]!.GetValue<string>().ShouldBe("desktop");
    }

    [Fact]
    public async Task API_I_007_Package_upload_limit_of_500_MB_honoured()
    {
        using var admin = node.Client(node.AdminToken);
        admin.Timeout = TimeSpan.FromMinutes(5);
        using var content = new MultipartFormDataContent();
        var file = new StreamContent(new ZeroStream((501L * 1024 * 1024) + (64 * 1024)));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "huge.wpkg");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/packages") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        HttpResponseMessage response;
        try
        {
            response = await admin.SendAsync(request, Ct);
        }
        catch (HttpRequestException)
        {
            // The server may close the connection after answering 413 before the client finished sending.
            return;
        }

        await ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "api.request.too_large");
    }

    [Fact]
    public async Task API_I_008_Rate_limit_with_real_middleware()
    {
        // A dedicated session: 50 requests/s with a burst of 100 (API §4.2).
        var token = await node.SignInAsync("desktop", Ct);
        using var client = node.Client(token);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 160).Select(async _ =>
        {
            using var response = await client.GetAsync("api/v1/site", Ct);
            return (response.StatusCode, response.Headers.RetryAfter);
        }));

        statuses.Count(s => s.StatusCode == HttpStatusCode.TooManyRequests).ShouldBeGreaterThan(0);
        statuses.Where(s => s.StatusCode == HttpStatusCode.TooManyRequests).ShouldAllBe(s => s.RetryAfter != null);
        statuses.Count(s => s.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(100);
    }

    [Fact]
    public async Task API_I_009_Token_in_the_hub_query_string_not_present_in_log_files()
    {
        var token = await node.SignInAsync("web", Ct);
        using var anonymous = node.Client();
        await anonymous.GetAsync("hub/v1/negotiate?v=1&access_token=" + Uri.EscapeDataString(token), Ct);
        using (var body = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            // A POST is logged at Information (API §4 row 4): its line must carry the route template only.
            await anonymous.PostAsync("api/v1/auth/refresh?access_token=" + Uri.EscapeDataString(token), body, Ct);
        }

        var logs = Path.Combine(node.DataDirectory, "logs");
        await ApiNodeFixture.PollAsync(() => Task.FromResult(ReadLogs(logs).Contains("/api/v1/auth/refresh", StringComparison.Ordinal)),
            TestTimeouts.Default, "the request is logged", Ct);
        ReadLogs(logs).ShouldNotContain(token);
    }

    private static string ReadLogs(string folder)
    {
        var text = new StringBuilder();
        foreach (var file in Directory.Exists(folder) ? Directory.GetFiles(folder, "*.log") : [])
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }

    [Fact]
    public async Task API_I_010_System_info_and_ca()
    {
        using var anonymous = node.Client();

        var info = JsonNode.Parse(await anonymous.GetStringAsync("api/v1/system/info", Ct))!;
        info["product"]!.GetValue<string>().ShouldBe("Watchpoint");
        info["environment"]!.GetValue<string>().ShouldBe("development");
        info["siteName"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        info["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldContain("registry");

        var ca = JsonNode.Parse(await anonymous.GetStringAsync("api/v1/system/ca", Ct))!;
        ca["pem"]!.GetValue<string>().ShouldStartWith("-----BEGIN CERTIFICATE-----");
        ca["fingerprintSha256"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task API_I_012_Management_port_isolation()
    {
        using var main = node.Client();
        using var management = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{node.Host.ManagementPort}/") };

        (await main.GetAsync("metrics", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await main.GetAsync("health/details", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var metrics = await management.GetStringAsync("metrics", Ct);
        metrics.ShouldContain("watchpoint_api_requests");
        (await management.GetAsync("health/details", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task API_I_013_Concurrent_clients_listing_devices_within_latency_target()
    {
        var clients = new List<HttpClient>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                clients.Add(node.Client(await node.SignInAsync("web", Ct)));
            }

            // Warm-up, then 50 concurrent requests (10 per session, within each session's burst).
            await clients[0].GetStringAsync("api/v1/devices", Ct);
            var durations = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
            {
                var started = Stopwatch.GetTimestamp();
                using var response = await clients[i % clients.Count].GetAsync("api/v1/devices", Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                return Stopwatch.GetElapsedTime(started);
            }));

            var p95 = durations.Order().ElementAt((int)Math.Ceiling(durations.Length * 0.95) - 1);
            p95.ShouldBeLessThan(TimingLimits.For(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            clients.ForEach(c => c.Dispose());
        }
    }

    [Fact]
    public async Task REG_I_008_API_device_list_with_filters_and_paging()
    {
        using var admin = node.Client(node.AdminToken);
        var all = JsonNode.Parse(await admin.GetStringAsync("api/v1/devices?limit=1000", Ct))!["items"]!.AsArray();
        all.Count.ShouldBeGreaterThan(1);

        // Paging: limit 1 walks every device once, in the same (name) order.
        var walked = new List<string>();
        string? cursor = null;
        do
        {
            var page = JsonNode.Parse(await admin.GetStringAsync("api/v1/devices?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Ct))!;
            walked.AddRange(page["items"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
            cursor = page["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null && walked.Count <= all.Count);
        walked.ShouldBe(all.Select(d => d!["id"]!.GetValue<string>()));

        // Filters map to the query.
        var first = all[0]!;
        var type = first["type"]!["id"]!.GetValue<string>();
        var byType = JsonNode.Parse(await admin.GetStringAsync($"api/v1/devices?type={type}", Ct))!["items"]!.AsArray();
        byType.ShouldAllBe(d => d!["type"]!["id"]!.GetValue<string>() == type);
        var server = first["serverId"]!.GetValue<string>();
        JsonNode.Parse(await admin.GetStringAsync($"api/v1/devices?server={server}&roots=true", Ct))!["items"]!.AsArray()
            .ShouldAllBe(d => d!["serverId"]!.GetValue<string>() == server && d["parentId"] == null);
        JsonNode.Parse(await admin.GetStringAsync($"api/v1/devices?server={Guid.NewGuid()}", Ct))!["items"]!.AsArray().ShouldBeEmpty();
        var sorted = JsonNode.Parse(await admin.GetStringAsync("api/v1/devices?sort=-name&limit=1000", Ct))!["items"]!.AsArray()
            .Select(d => d!["id"]!.GetValue<string>()).ToList();
        sorted.ShouldBe(all.Select(d => d!["id"]!.GetValue<string>()).Reverse());

        var problem = await ProblemAsync(await admin.GetAsync("api/v1/devices?enabled=maybe&sort=color", Ct), HttpStatusCode.BadRequest, "common.validation.failed");
        problem["errors"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()).ShouldBe(["/query/enabled", "/query/sort"]);
    }

    [Fact]
    public async Task MOD_I_003_Symbol_endpoint_headers()
    {
        using var admin = node.Client(node.AdminToken);

        var response = await admin.GetAsync("api/v1/symbols/ac.door/normal.svg", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldBe("default-src 'none'; style-src 'unsafe-inline'");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("<svg");
        using var anonymous = node.Client();
        (await anonymous.GetAsync("api/v1/symbols/ac.door/normal.svg", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MOD_I_004_i18n_endpoint_caching()
    {
        using var anonymous = node.Client();
        var first = await anonymous.GetAsync("api/v1/i18n/types/fr", Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.CacheControl!.NoCache.ShouldBeTrue();
        var etag = first.Headers.ETag!.Tag;

        using var again = new HttpRequestMessage(HttpMethod.Get, "api/v1/i18n/types/fr");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await anonymous.SendAsync(again, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

        using var changed = new HttpRequestMessage(HttpMethod.Get, "api/v1/i18n/types/fr");
        changed.Headers.TryAddWithoutValidation("If-None-Match", "\"other\"");
        (await anonymous.SendAsync(changed, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task STA_I_004_API_state_endpoints()
    {
        using var admin = node.Client(node.AdminToken);
        var device = await FirstDeviceAsync(admin);
        var id = device["id"]!.GetValue<string>();

        var state = JsonNode.Parse(await admin.GetStringAsync($"api/v1/devices/{id}/state", Ct))!;
        state["id"]!.GetValue<string>().ShouldBe(id);
        state["overall"].ShouldNotBeNull();
        state["health"]!["connectivity"].ShouldNotBeNull();

        var batch = JsonNode.Parse(await (await admin.PostAsJsonAsync("api/v1/states/batch", new { ids = new[] { Guid.Parse(id), Guid.NewGuid() } }, Ct))
            .Content.ReadAsStringAsync(Ct))!;
        batch["items"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ShouldBe([id]);

        var query = JsonNode.Parse(await admin.GetStringAsync($"api/v1/states?server={device["serverId"]}&limit=1000", Ct))!;
        query["items"]!.AsArray().Count.ShouldBeGreaterThan(0);
        await ProblemAsync(await admin.GetAsync("api/v1/states?state=purple", Ct), HttpStatusCode.BadRequest, "common.validation.failed");

        var site = JsonNode.Parse(await admin.GetStringAsync("api/v1/site", Ct))!["id"]!.GetValue<string>();
        var aggregates = JsonNode.Parse(await (await admin.PostAsJsonAsync("api/v1/aggregates", new { scopes = new[] { new { kind = "site", id = Guid.Parse(site) } } }, Ct))
            .Content.ReadAsStringAsync(Ct))!;
        aggregates["items"]![0]!["total"]!.GetValue<int>().ShouldBeGreaterThan(0);
        aggregates["items"]![0]!["scope"]!["kind"]!.GetValue<string>().ShouldBe("site");
    }

    [Fact]
    public async Task STA_I_006_Server_offline_end_to_end()
    {
        using var admin = node.Client(node.AdminToken);
        var connection = JsonNode.Parse(await admin.GetStringAsync("api/v1/connections", Ct))!["items"]![0]!["id"]!.GetValue<string>();
        // The simulator must have delivered data first: a server that was never online cannot become unavailable.
        string? id = null;
        await ApiNodeFixture.PollAsync(async () =>
        {
            var states = JsonNode.Parse(await admin.GetStringAsync("api/v1/states?limit=1000", Ct))!["items"]!.AsArray();
            id = states.FirstOrDefault(s => s!["properties"]!.AsObject().Any(p => p.Value!["quality"]!.GetValue<string>() == "good"))?["id"]?.GetValue<string>();
            return id is not null;
        }, TestTimeouts.Container * 2, "a simulator device has live data", Ct);
        try
        {
            (await admin.PostAsync($"api/v1/connections/{connection}/stop", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

            JsonNode? last = null;
            try
            {
                await ApiNodeFixture.PollAsync(async () =>
                {
                    last = JsonNode.Parse(await admin.GetStringAsync($"api/v1/devices/{id}/state", Ct))!;
                    return last["serverUnavailable"]!.GetValue<bool>()
                        && last["properties"]!.AsObject().All(p => p.Value!["quality"]!.GetValue<string>() == "stale");
                }, TestTimeouts.Container, "the devices are served with quality stale", Ct);
            }
            catch (Watchpoint.Testing.TestAssertionException ex)
            {
                var status = await admin.GetStringAsync($"api/v1/connections/{connection}", Ct);
                throw new Watchpoint.Testing.TestAssertionException(ex.Message + " Last state: " + last?.ToJsonString() + " Connection: " + status, ex);
            }
        }
        finally
        {
            await admin.PostAsync($"api/v1/connections/{connection}/start", null, Ct);
        }
    }

    /// <summary>A read-only stream of <c>length</c> zero bytes (no memory held).</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
