using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using NATS.Net;
using Shouldly;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Host;

[Trait("layer", "I")]
[Trait("spec", "HST")]
[Collection(SerialHostProcesses.Name)]
public sealed class HostIntegrationTests(PostgresContainerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HST_I_001_Host_starts_with_compose_infrastructure()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(HostIntegrationTests), Ct);
        await using var host = await HostProcess.StartWithNatsAsync(
            "i001", new Dictionary<string, JsonNode> { ["Database"] = HostProcess.DatabaseSection(db.ConnectionString) }, Ct);

        using var http = new HttpClient();
        var ready = await PollAsync(TimeSpan.FromSeconds(30), host, async () =>
        {
            using var resp = await http.GetAsync($"http://127.0.0.1:{host.MainPort}/health/ready", Ct);
            return resp.StatusCode == HttpStatusCode.OK && (await resp.Content.ReadAsStringAsync(Ct)).Contains("\"ready\"", StringComparison.Ordinal);
        });

        ready.ShouldBeTrue(host.StoppedDescription());
    }

    [Fact]
    public async Task HST_I_002_Host_reports_not_ready_without_database()
    {
        // Same settings format as a real database, but on a port nothing listens on.
        await using var db = await postgres.CreateDatabaseAsync(nameof(HostIntegrationTests), Ct);
        var database = HostProcess.DatabaseSection(db.ConnectionString, startupWait: "PT3S");
        var closedPort = HostProcess.GetFreePort();
        database["Databases"]!["default"]!["Host"] = "127.0.0.1";
        database["Databases"]!["default"]!["Port"] = closedPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var host = await HostProcess.StartWithNatsAsync("i002", new Dictionary<string, JsonNode> { ["Database"] = database }, Ct);

        using var http = new HttpClient();
        var reportedDatabase = await PollAsync(TimeSpan.FromSeconds(10), host, async () =>
        {
            using var resp = await http.GetAsync($"http://127.0.0.1:{host.MainPort}/health/ready", Ct);
            if (resp.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                return false;
            }

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(Ct))!;
            return body["status"]!.GetValue<string>() == "notReady"
                && body["reasons"]!.AsArray().Any(r => r!.GetValue<string>() == "database");
        });

        await host.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);
        reportedDatabase.ShouldBeTrue(host.StoppedDescription());
        host.ExitCode.ShouldBe(75);
    }

    [Fact]
    public async Task HST_I_003_Management_endpoints_only_on_management_port()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(HostIntegrationTests), Ct);
        await using var host = await HostProcess.StartWithNatsAsync(
            "i003", new Dictionary<string, JsonNode> { ["Database"] = HostProcess.DatabaseSection(db.ConnectionString) }, Ct);

        using var http = new HttpClient();
        var managementResponded = await PollAsync(TimeSpan.FromSeconds(30), host, async () =>
        {
            using var resp = await http.GetAsync($"http://127.0.0.1:{host.ManagementPort}/metrics", Ct);
            return resp.StatusCode == HttpStatusCode.OK;
        });

        managementResponded.ShouldBeTrue(host.StoppedDescription());
        using var main = await http.GetAsync($"http://127.0.0.1:{host.MainPort}/metrics", Ct);
        main.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    [Trait("spec", "PIP")]
    public async Task PIP_I_016_Host_runs_the_pipeline()
    {
        // With the other host tests: hosts started side by side would compete for the default management port.
        await using var db = await postgres.CreateDatabaseAsync(nameof(HostIntegrationTests), Ct);
        await using var host = await HostProcess.StartWithNatsAsync(
            "pipi016", new Dictionary<string, JsonNode> { ["Database"] = HostProcess.DatabaseSection(db.ConnectionString) }, Ct);

        // The node becomes ready: streams provisioned (Infrastructure), recovery done, workers running.
        using var http = new HttpClient();
        await Eventually.UntilAsync(
            () =>
            {
                host.HasExited.ShouldBeFalse(host.StoppedDescription());
                try
                {
                    using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{host.MainPort}/health/ready"), Ct);
                    return response.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(60),
            "the host with the pipeline is ready",
            TimeSpan.FromMilliseconds(200),
            Ct);

        // Its three streams exist on its NATS server.
        await using var client = new NatsClient(host.NatsUrl);
        var js = client.CreateJetStreamContext();
        foreach (var (stream, subject) in new[] { ("WP_LOG", "wp.log.>"), ("WP_CFG", "wp.cfg.>"), ("WP_SYS", "wp.sys.>") })
        {
            var config = (await js.GetStreamAsync(stream, cancellationToken: Ct)).Info.Config;
            config.Subjects.ShouldBe([subject]);
        }
    }

    [Fact]
    public async Task HST_I_004_systemd_notify()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Skip("systemd notify is only supported on Linux");
        }

        await using var db = await postgres.CreateDatabaseAsync(nameof(HostIntegrationTests), Ct);
        var socketPath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        try
        {
            await using var host = await HostProcess.StartWithNatsAsync(
                "i004",
                new Dictionary<string, JsonNode> { ["Database"] = HostProcess.DatabaseSection(db.ConnectionString) },
                Ct,
                new Dictionary<string, string> { ["NOTIFY_SOCKET"] = socketPath, ["WATCHDOG_USEC"] = "2000000" });

            var buffer = new byte[1024];
            var readyReceived = false;
            var watchdogPings = 0;
            var sw = Stopwatch.StartNew();

            // WATCHDOG_USEC = 2 s → a WATCHDOG=1 ping every second; wait for READY=1 and two pings.
            while (sw.Elapsed < TimeSpan.FromSeconds(30) && !host.HasExited && !(readyReceived && watchdogPings >= 2))
            {
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                receive.CancelAfter(TimeSpan.FromMilliseconds(500));
                try
                {
                    var received = await socket.ReceiveAsync(buffer, SocketFlags.None, receive.Token);
                    var message = System.Text.Encoding.UTF8.GetString(buffer, 0, received);
                    readyReceived |= message.Contains("READY=1", StringComparison.Ordinal);
                    if (message.Contains("WATCHDOG=1", StringComparison.Ordinal))
                    {
                        watchdogPings++;
                    }
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                }
            }

            readyReceived.ShouldBeTrue();
            watchdogPings.ShouldBeGreaterThanOrEqualTo(2);

            // READY=1 is sent only after Edge: readiness must already be 200.
            using var http = new HttpClient();
            using var ready = await http.GetAsync($"http://127.0.0.1:{host.ManagementPort}/health/ready", Ct);
            ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    private static async Task<bool> PollAsync(TimeSpan timeout, HostProcess host, Func<Task<bool>> probe)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !host.HasExited)
        {
            try
            {
                if (await probe())
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
        }

        return false;
    }
}
