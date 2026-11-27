using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Context;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Integrations.Internal.Storage;
using Watchpoint.Integrations.Tests.Support;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Testing.Async;
using ConnectorStatus = Watchpoint.Integration.Abstractions.Context.ConnectionStatus;

namespace Watchpoint.Integrations.Tests;

/// <summary>The connector context (spec IRT §8, IRT-U-048…058).</summary>
[Trait("layer", "U")]
[Trait("spec", "IRT")]
public sealed class ConnectorContextTests
{
    private static readonly DeviceKey _d1 = new("root", "d1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IRT_U_048_Sink_maps_keys_and_counts_unknown_devices()
    {
        await using var h = new ConnectionHarness();
        var id = await h.CreateAsync();
        var server = h.Registry.AddServer(id, "root");
        var device = h.Registry.AddDevice(server, "d1");
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);
        var sink = h.Host.Created.Single().Context.Sink;
        var command = Guid.CreateVersion7();

        sink.ReportProperty(_d1, "doorPosition.position", WpValue.Enum("open"), sourceTime: h.Clock.GetUtcNow(), causedByCommand: command);
        sink.ReportEvent(_d1, "doorPosition.forced");
        sink.ReportServerHealth("root", Connectivity.Online);
        sink.ReportProperty(new DeviceKey("root", "unknown"), "doorPosition.position", WpValue.Enum("open"));
        sink.ReportServerProperty("other", "health.status", WpValue.Enum("ok"));
        var flushed = await sink.FlushAsync(Ct);

        flushed.ShouldBe(new SinkFlushResult(3, 0, 2));
        var observations = h.Ingest.All;
        observations.Count.ShouldBe(3);
        observations.ShouldAllBe(o => o.Connection == id && o.Id != Guid.Empty);
        observations[0].ShouldSatisfyAllConditions(
            o => o.Kind.ShouldBe(ObservationKind.Property),
            o => o.Device.ShouldBe(device.Id),
            o => o.Property.ShouldBe(PropertyPath.Parse("doorPosition.position").Value),
            o => o.CorrelatedCommand.ShouldBe(CommandId.From(command)));
        observations[1].Event!.Value.Value.ShouldBe("doorPosition.forced");
        observations[2].Server.ShouldBe(server.Id);
        h.Manager.GetStatus(id).Counters.UnknownDevices.ShouldBe(2);

        // Unknown devices are logged at most once a minute, with the count.
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        sink.ReportProperty(new DeviceKey("root", "unknown"), "doorPosition.position", WpValue.Enum("open"));
        ((long)h.Logs.WithEventId(1840).ShouldHaveSingleItem().Properties["Count"]!).ShouldBe(3);
    }

    [Fact]
    public async Task IRT_U_049_Sink_auto_flush_cadence()
    {
        await using var h = new ConnectionHarness();
        await using var sink = Sink(h, out _);

        for (var i = 0; i < 600; i++)
        {
            sink.ReportProperty(_d1, "doorPosition.position", WpValue.Int(i));
        }

        await Eventually.UntilAsync(() => h.Ingest.Calls == 1, because: "500 items flush at once", ct: Ct);
        h.Ingest.Batches[0].Count.ShouldBe(500);
        await Eventually.UntilAsync(() => sink.Buffered == 100, because: "the rest waits", ct: Ct);
        h.Ingest.Calls.ShouldBe(1, "no flush before 50 ms");

        h.Clock.Advance(TimeSpan.FromMilliseconds(50));

        await Eventually.UntilAsync(() => h.Ingest.Calls == 2, because: "the remaining 100 within 50 ms", ct: Ct);
        h.Ingest.Batches[1].Count.ShouldBe(100);
        h.Ingest.All.Select(o => o.Value).ShouldBe([.. Enumerable.Range(0, 600).Select(i => WpValue.Int(i))], "in order");
    }

    [Fact]
    public async Task IRT_U_050_FlushAsync_waits_for_prior_items_only()
    {
        await using var h = new ConnectionHarness();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        h.Ingest.Hold = call => call == 1 ? first.Task : second.Task;
        await using var sink = Sink(h, out _);

        sink.ReportProperty(_d1, "doorPosition.position", WpValue.Int(1));
        var flushA = sink.FlushAsync(Ct).AsTask();
        await Eventually.UntilAsync(() => h.Ingest.Calls == 1, because: "A is sent", ct: Ct);
        sink.ReportProperty(_d1, "doorPosition.position", WpValue.Int(2));
        flushA.IsCompleted.ShouldBeFalse("A is not durable yet");

        first.SetResult();
        (await flushA.WaitAsync(TestTimeouts.Default, Ct)).Accepted.ShouldBe(1);

        // B goes with the next automatic flush (50 ms).
        await h.AdvanceUntilAsync(() => h.Ingest.Calls == 2, TimeSpan.FromMilliseconds(10), "B is sent");
        var flushB = sink.FlushAsync(Ct).AsTask();
        flushB.IsCompleted.ShouldBeFalse("B is still in the ingest");
        second.SetResult();
        (await flushB.WaitAsync(TestTimeouts.Default, Ct)).Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task IRT_U_051_Sink_never_blocks_Report_when_full()
    {
        await using var h = new ConnectionHarness();
        h.Ingest.Gate = new TaskCompletionSource();
        await using var sink = Sink(h, out _);
        var paths = Enumerable.Range(0, 1000).Select(i => "doorPosition.p" + i).ToArray();

        // 120,000 reports with the ingest stalled: buffer 50,000 + overflow 50,000, then coalescing.
        for (var i = 0; i < 110_000; i++)
        {
            sink.ReportProperty(_d1, paths[i % paths.Length], WpValue.Int(i));
        }

        for (var i = 0; i < 10_000; i++)
        {
            sink.ReportEvent(_d1, "doorPosition.forced", new Dictionary<string, WpValue> { ["n"] = WpValue.Int(i) });
        }

        sink.Buffered.ShouldBe(100_000 + paths.Length + 10_000, "properties beyond the overflow coalesce per path, events are kept");
        var flush = sink.FlushAsync(Ct).AsTask();
        flush.IsCompleted.ShouldBeFalse("FlushAsync waits while the ingest is stalled");

        h.Ingest.Gate.SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var all = h.Ingest.All;
        all.Count.ShouldBe(111_000);
        all.Count(o => o.Kind == ObservationKind.Event).ShouldBe(10_000);
        all.Where(o => o.Kind == ObservationKind.Event).Select(o => o.Payload!["n"]).ShouldBe([.. Enumerable.Range(0, 10_000).Select(i => WpValue.Int(i))]);
        all.Last(o => o.Property?.Value == "doorPosition.p0").Value.ShouldBe(WpValue.Int(109_000), "the latest value per path");
    }

    [Fact]
    public async Task IRT_U_052_Store_limits()
    {
        var repository = new InMemoryConnectionRepository();
        var id = ConnectionId.New();
        await using var h = new ConnectionHarness();
        using var store = new ConnectionStore(id, repository, h.Logger);

        var tooBig = await Should.ThrowAsync<ConnectionStoreLimitException>(store.SetAsync("big", new byte[(64 * 1024) + 1], Ct).AsTask());
        tooBig.Error.Code.Value.ShouldBe(IntegrationErrors.StoreLimit);

        // Concurrent writers: serialised, every value durable, the total limit holds.
        await Task.WhenAll(Enumerable.Range(0, 15).Select(i => store.SetAsync("k" + i, new byte[64 * 1024], Ct).AsTask()));
        var total = await Should.ThrowAsync<ConnectionStoreLimitException>(store.SetAsync("k15", new byte[64 * 1024], Ct).AsTask());
        total.Error.Args["limit"].ShouldBe("total");
        await store.SetAsync("k0", new byte[1000], Ct);
        await store.DeleteAsync("k1", Ct);
        await store.SetAsync("k15", new byte[1000], Ct);

        var persisted = await repository.LoadAsync(id, Ct);
        persisted.Keys.Order().ShouldBe(Enumerable.Range(0, 16).Where(i => i != 1).Select(i => "k" + i).Order());
        using var reopened = new ConnectionStore(id, repository, h.Logger);
        foreach (var (key, value) in persisted)
        {
            (await reopened.GetAsync(key, Ct))!.Length.ShouldBe(value.Length);
            (await store.GetAsync(key, Ct))!.Length.ShouldBe(value.Length);
        }

        (await store.GetAsync("big", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task IRT_U_053_Scheduler_paused_when_disconnected()
    {
        await using var h = new ConnectionHarness();
        h.Host.Configure = c => c.OnStart = (connector, _) =>
        {
            connector.Context.Scheduler.SchedulePolling("poll", TimeSpan.FromSeconds(1), _ => ValueTask.CompletedTask);
            connector.Context.Scheduler.SchedulePolling("always", TimeSpan.FromSeconds(1), _ => ValueTask.CompletedTask, new PollingOptions(RunWhenDisconnected: true));
            return ValueTask.CompletedTask;
        };
        var id = await h.StartedAsync(ConnectionState.Starting);
        await Eventually.UntilAsync(() => h.Manager.Runtime(id)!.Current?.Services?.Scheduler.Count == 2, because: "the loops are scheduled", ct: Ct);
        var scheduler = h.Manager.Runtime(id)!.Current!.Services!.Scheduler;
        var connector = h.Host.Created.Single();
        scheduler.IsPaused("poll").ShouldBeTrue("not connected yet");
        scheduler.IsPaused("always").ShouldBeFalse();

        connector.Context.Health.Report(ConnectorStatus.Connected);
        await h.WaitForAsync(id, ConnectionState.Connected);
        scheduler.IsPaused("poll").ShouldBeFalse();

        connector.Context.Health.Report(ConnectorStatus.Disconnected);
        await h.WaitForAsync(id, ConnectionState.Reconnecting);
        scheduler.IsPaused("poll").ShouldBeTrue();
        scheduler.IsPaused("always").ShouldBeFalse();

        (await h.Manager.StopAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        scheduler.Count.ShouldBe(0, "registrations are disposed at stop");
    }

    [Fact]
    public async Task IRT_U_054_Gate_priority_and_concurrency()
    {
        await using var h = new ConnectionHarness();
        var defaults = await h.StartedAsync(name: "Default");
        h.Host.Created.Single().Context.Gate.MaxConcurrency.ShouldBe(ConnectorContextFactory.DefaultMaxConcurrency);
        var created = await h.Manager.CreateAsync(h.NewConnection("One", """{ "host": "a", "maxConcurrency": 1 }"""), ConnectionHarness.Ctx, Ct);
        await h.WaitForAsync(created.Value, ConnectionState.Connected);
        var gate = h.Host.Created.Single(c => c.Context.Connection.Id == created.Value.Value).Context.Gate;
        gate.MaxConcurrency.ShouldBe(1);

        var busy = new TaskCompletionSource();
        var order = new List<CommandPriority>();
        var running = gate.RunAsync(CommandPriority.Normal, async _ => await busy.Task.ContinueWith(_ => 0, TaskScheduler.Default), Ct).AsTask();
        var queued = new[] { CommandPriority.Background, CommandPriority.Normal, CommandPriority.High, CommandPriority.Critical }
            .Select(p => gate.RunAsync(p, _ =>
            {
                lock (order)
                {
                    order.Add(p);
                }

                return ValueTask.FromResult(0);
            }, Ct).AsTask()).ToList();
        order.ShouldBeEmpty("one at a time");

        busy.SetResult();
        await Task.WhenAll([running, .. queued]).WaitAsync(TestTimeouts.Default, Ct);
        order.ShouldBe([CommandPriority.Critical, CommandPriority.High, CommandPriority.Normal, CommandPriority.Background]);
        h.Manager.GetStatus(defaults).State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task IRT_U_055_Secret_accessor_uses_vault_with_owner_and_purpose()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();
        var context = h.Host.Created.Single().Context;

        using (var secret = await context.Secrets.GetAsync("password", Ct))
        {
            secret.ShouldNotBeNull().UseAsString(s => s).ShouldBe("S3cret-value!");
            secret.ToString().ShouldBe("***");

            // A connector that logs the value: masked by the runtime's loggers.
            var logger = context.Logging.CreateLogger("Vendor.Acme");
            secret.UseAsString(s =>
            {
#pragma warning disable CA1848 // A connector's own logging call, as connectors write it.
                logger.LogWarning("login with password={Password} -> {Raw}", s, s);
#pragma warning restore CA1848
                return 0;
            });
        }

        (await context.Secrets.GetAsync("token", Ct)).ShouldBeNull("not set");
        var read = h.Vault.Calls.Where(c => c.Action == "read").ShouldHaveSingleItem();
        read.Owner.ShouldBe(new Secrets.Contracts.SecretOwner("Integrations", id.Value));
        read.Purpose.ShouldBe("connector");
        var logged = h.Logs.Entries.Single(e => e.Category == "Watchpoint.Connector.vendor.acme");
        logged.RenderedMessage.ShouldBe("login with password=*** -> ***");
        h.Logs.Entries.ShouldAllBe(e => !e.RenderedMessage.Contains("S3cret-value!") && !e.Properties.Values.Any(v => Equals(v, "S3cret-value!")));
    }

    [Fact]
    public async Task IRT_U_056_SecureHttp_pin_CA_system_trust_and_plain_HTTP_rules()
    {
        var https = new Uri("https://vendor.example/");
        var http = new Uri("http://vendor.example/");
        var strict = new ConnectorHttp(allowInsecureHttp: false);
        var relaxed = new ConnectorHttp(allowInsecureHttp: true);

        using (var system = strict.CreateClient(new HttpClientOptions(https, TimeSpan.FromSeconds(5))))
        {
            system.BaseAddress.ShouldBe(https);
        }

        using (var pinned = strict.CreateClient(new HttpClientOptions(https, TimeSpan.FromSeconds(5), PinnedPublicKeySha256: Convert.ToHexString(SHA256.HashData([1, 2, 3])))))
        {
            pinned.Timeout.ShouldBe(TimeSpan.FromSeconds(5));
        }

        using var key = RSA.Create(2048);
        using var ca = new CertificateRequest("CN=Vendor CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        using (var custom = strict.CreateClient(new HttpClientOptions(https, TimeSpan.FromSeconds(5), CustomCaPem: ca.ExportCertificatePem())))
        {
            custom.BaseAddress.ShouldBe(https);
        }

        Should.Throw<ArgumentException>(() => strict.CreateClient(new HttpClientOptions(https, TimeSpan.FromSeconds(5), PinnedPublicKeySha256: "not-a-pin")));

        // Plain HTTP: only when the connector asks AND the connection settings allow it.
        Should.Throw<InvalidOperationException>(() => strict.CreateClient(new HttpClientOptions(http, TimeSpan.FromSeconds(5), AllowHttp: true)));
        Should.Throw<InvalidOperationException>(() => relaxed.CreateClient(new HttpClientOptions(http, TimeSpan.FromSeconds(5))));
        using (var plain = relaxed.CreateClient(new HttpClientOptions(http, TimeSpan.FromSeconds(5), AllowHttp: true)))
        {
            plain.BaseAddress.ShouldBe(http);
        }

        await using var h = new ConnectionHarness();
        var created = await h.Manager.CreateAsync(h.NewConnection(settings: """{ "host": "a", "allowInsecureHttp": true }"""), ConnectionHarness.Ctx, Ct);
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(created.Value, ConnectionState.Connected);
        h.Host.Created.Single().Context.Http.ShouldBeOfType<ConnectorHttp>().AllowInsecureHttp.ShouldBeTrue();
        h.Logs.WithEventId(1826).ShouldHaveSingleItem("a warning on start");
    }

    [Fact]
    public async Task IRT_U_057_Inbound_HTTP_auth_enforced_before_handler()
    {
        await using var h = new ConnectionHarness();
        var created = await h.Manager.CreateAsync(h.NewConnection(secrets: new Dictionary<string, string> { ["password"] = "p-1234", ["token"] = "hook-secret" }),
            ConnectionHarness.Ctx, Ct);
        var id = created.Value;
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);
        var inbound = h.Host.Created.Single().Context.Inbound;
        var calls = 0;
        ValueTask<InboundResponse> Handler(InboundRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            request.Body.Length.ShouldBe(5);
            return ValueTask.FromResult(new InboundResponse(202, "done"u8.ToArray(), "text/plain"));
        }

        var shared = await inbound.RegisterHttpAsync("/hook/", new InboundAuth.SharedSecretHeader("X-Token", "token"), Handler, Ct);
        shared.Address.ShouldBe($"/integrations/{id.Value:D}/hook");
        await inbound.RegisterHttpAsync("signed", new InboundAuth.HmacSignature("X-Signature", "token", "HMACSHA256"), Handler, Ct);
        Should.Throw<ArgumentException>(() => inbound.RegisterHttpAsync("x", new InboundAuth.SharedSecretHeader("X", "undeclared"), Handler, Ct).AsTask());

        (await SendAsync(h, id, "hook")).ShouldBe(401);
        (await SendAsync(h, id, "hook", ("X-Token", "wrong"))).ShouldBe(401);
        calls.ShouldBe(0, "the handler never runs for a refused request");
        (await SendAsync(h, id, "hook", ("X-Token", "hook-secret"))).ShouldBe(202);
        var signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData("hook-secret"u8, "hello"u8));
        (await SendAsync(h, id, "signed", ("X-Signature", signature))).ShouldBe(202);
        (await SendAsync(h, id, "signed", ("X-Signature", "sha256=" + new string('0', 64)))).ShouldBe(401);
        (await SendAsync(h, id, "missing", ("X-Token", "hook-secret"))).ShouldBe(404);
        (await SendAsync(h, id, "hook", new byte[InboundRouter.MaxBodyBytes + 1], ("X-Token", "hook-secret"))).ShouldBe(413);
        calls.ShouldBe(2);
        h.Logs.WithEventId(1846).Count().ShouldBe(3);

        // Released when the connector stops.
        (await h.Manager.StopAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        h.Router.Count.ShouldBe(0);
        (await SendAsync(h, id, "hook", ("X-Token", "hook-secret"))).ShouldBe(404);
    }

    [Fact]
    public async Task IRT_U_058_Inbound_TCP_requires_manifest_permission_and_setting()
    {
        await using var h = new ConnectionHarness();
        var secrets = new SecretAccessor(ConnectionId.New(), new Dictionary<string, SecretId>(), h.Vault, new SecretRedaction([]), h.Logger);
        var received = new TaskCompletionSource<string>();
        async ValueTask Handler(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[5];
            await stream.ReadExactlyAsync(buffer, ct);
            received.TrySetResult(Encoding.ASCII.GetString(buffer));
        }

        Endpoints(permitted: false, enabled: true).ShouldSatisfyAllConditions(
            e => Should.Throw<UnauthorizedAccessException>(() => e.RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct).AsTask()));
        Endpoints(permitted: true, enabled: false).ShouldSatisfyAllConditions(
            e => Should.Throw<UnauthorizedAccessException>(() => e.RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct).AsTask()));

        await using var allowed = Endpoints(permitted: true, enabled: true);
        var registration = await allowed.RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct);
        var port = int.Parse(registration.Address[(registration.Address.LastIndexOf(':') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port, Ct);
            await client.GetStream().WriteAsync("hello"u8.ToArray(), Ct);
            (await received.Task.WaitAsync(TestTimeouts.Default, Ct)).ShouldBe("hello");
        }

        await registration.DisposeAsync();
        using var refused = new TcpClient();
        await Should.ThrowAsync<SocketException>(refused.ConnectAsync(IPAddress.Loopback, port, Ct).AsTask());

        InboundEndpoints Endpoints(bool permitted, bool enabled) =>
            new(ConnectionId.New(), h.Router, secrets, new HashSet<string>(), permitted, enabled, h.Logger);
    }

    private static ObservationSink Sink(ConnectionHarness h, out ConnectionCounterSource counters)
    {
        var id = ConnectionId.New();
        var server = h.Registry.AddServer(id, "root");
        h.Registry.AddDevice(server, "d1");
        var map = new DeviceMap(h.Logger, id.Value);
        map.Update(DeviceMapSnapshot.Load(h.Registry, id));
        map.MarkReady(); // a fully set-up map, not the pre-connect race window IRT_U_067…069 exercise.
        counters = new ConnectionCounterSource();
        return new ObservationSink(id, h.Ingest, map, counters, _ => { }, h.Options.SinkBuffer, h.Clock, h.Logger);
    }

    /// <summary>
    /// A sink whose map starts empty, like a freshly launched instance before its first connect: the eager, pre-connect
    /// preload (<see cref="ConnectorContextFactory.Create"/>) finds nothing to load and does not mark the map ready.
    /// </summary>
    private static (ObservationSink Sink, DeviceMap Map, ConnectionCounterSource Counters) SinkWithEmptyMap(ConnectionHarness h, out ConnectionId id)
    {
        id = ConnectionId.New();
        var map = new DeviceMap(h.Logger, id.Value);
        map.Update(DeviceMapSnapshot.Load(h.Registry, id));
        var counters = new ConnectionCounterSource();
        var sink = new ObservationSink(id, h.Ingest, map, counters, _ => { }, h.Options.SinkBuffer, h.Clock, h.Logger);
        return (sink, map, counters);
    }

    private static Task<int> SendAsync(ConnectionHarness h, ConnectionId id, string path, params (string Name, string Value)[] headers) =>
        SendAsync(h, id, path, "hello"u8.ToArray(), headers);

    private static async Task<int> SendAsync(ConnectionHarness h, ConnectionId id, string path, byte[] body, params (string Name, string Value)[] headers)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Body = new MemoryStream(body);
        http.Request.ContentLength = body.Length;
        http.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        http.Response.Body = new MemoryStream();
        foreach (var (name, value) in headers)
        {
            http.Request.Headers[name] = value;
        }

        await h.Router.HandleAsync(http, id.Value, path);
        return http.Response.StatusCode;
    }

    [Fact]
    public async Task IRT_U_064_Inbound_TCP_is_granted_by_the_manifest_permissions_and_by_nothing_else()
    {
        await using var h = new ConnectionHarness();
        var secrets = new SecretAccessor(ConnectionId.New(), new Dictionary<string, SecretId>(), h.Vault, new SecretRedaction([]), h.Logger);
        ValueTask Handler(Stream stream, CancellationToken ct) => ValueTask.CompletedTask;
        string Manifest(string permissions) => """
            {"$schema":"https://schemas.watchpoint.dev/manifest/1.json","id":"acme.integration.demo","version":"1.0.0","vendor":"Acme","category":"generic","contract":"^1.0",
             "minWatchpointVersion":"1.0.0","platforms":["linux-x64"],"nativeCode":false,"isolation":"inProcessAllowed","licence":{"class":"included"},__PERMISSIONS__
             "connectors":[{"class":"A::A.C","serverType":"demo.server","supportedVendorVersions":"*","features":["discovery"],"settingsSchema":{"type":"object","properties":{}},"secretsSchema":{"fields":[]}}],
             "types":[],"capabilities":[],"ruleTemplates":[],"files":[{"path":"lib/A.dll","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}]}
            """.Replace("__PERMISSIONS__", permissions, StringComparison.Ordinal);
        var reader = new DocumentReader(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../schemas")));
        ManifestDocument Read(string permissions) => reader.ReadManifest(Encoding.UTF8.GetBytes(Manifest(permissions)), "manifest.json").Value;
        InboundEndpoints Endpoints(ManifestDocument manifest, bool enabled) =>
            new(ConnectionId.New(), h.Router, secrets, new HashSet<string>(), InProcessHost.PermissionsOf(manifest).Contains(InboundEndpoints.InboundTcpPermission), enabled, h.Logger);

        // No permissions in the manifest: nothing granted, even when the connection enables TCP. UDP alone does not grant TCP.
        InProcessHost.PermissionsOf(Read(string.Empty)).ShouldBeEmpty();
        InProcessHost.PermissionsOf(Read("\"permissions\":[\"inboundUdp\"],")).ShouldBe(["inboundUdp"]);
        Should.Throw<UnauthorizedAccessException>(() => Endpoints(Read(string.Empty), enabled: true).RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct).AsTask());
        Should.Throw<UnauthorizedAccessException>(() => Endpoints(Read("\"permissions\":[\"inboundUdp\"],"), enabled: true).RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct).AsTask());

        // Declared in the manifest and enabled in the connection settings: allowed; declared but not enabled: refused.
        var tcp = Read("\"permissions\":[\"inboundTcp\",\"inboundUdp\"],");
        InProcessHost.PermissionsOf(tcp).ShouldBe(["inboundTcp", "inboundUdp"], ignoreOrder: true);
        Should.Throw<UnauthorizedAccessException>(() => Endpoints(tcp, enabled: false).RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct).AsTask());
        await using var allowed = Endpoints(tcp, enabled: true);
        var registration = await allowed.RegisterTcpAsync(0, IPAddress.Loopback, Handler, Ct);
        registration.Address.ShouldContain(IPAddress.Loopback.ToString());
    }

    [Fact]
    public async Task IRT_U_066_Gate_size_is_the_connection_setting_then_the_manifest_limit_then_four()
    {
        await using var h = new ConnectionHarness();
        h.Host.Limits = new ConnectorLimitsDoc(MaxConcurrency: 7);
        var fromManifest = await h.StartedAsync(name: "Manifest");
        h.Host.Created.Single().Context.Gate.MaxConcurrency.ShouldBe(7);

        var overridden = await h.Manager.CreateAsync(h.NewConnection("Setting", """{ "host": "a", "maxConcurrency": 2 }"""), ConnectionHarness.Ctx, Ct);
        await h.WaitForAsync(overridden.Value, ConnectionState.Connected);
        h.Host.Created.Single(c => c.Context.Connection.Id == overridden.Value.Value).Context.Gate.MaxConcurrency.ShouldBe(2);

        var json = (string text) => JsonDocument.Parse(text).RootElement;
        ConnectorContextFactory.ResolveMaxConcurrency(json("{}"), null).ShouldBe(ConnectorContextFactory.DefaultMaxConcurrency);
        ConnectorContextFactory.ResolveMaxConcurrency(json("{}"), 9).ShouldBe(9);
        ConnectorContextFactory.ResolveMaxConcurrency(json("""{"maxConcurrency":3}"""), 9).ShouldBe(3);
        ConnectorContextFactory.ResolveMaxConcurrency(json("""{"maxConcurrency":0}"""), 9).ShouldBe(9);
        ConnectorContextFactory.ResolveMaxConcurrency(json("""{"maxConcurrency":"many"}"""), null).ShouldBe(ConnectorContextFactory.DefaultMaxConcurrency);
        h.Manager.GetStatus(fromManifest).State.ShouldBe(ConnectionState.Connected);
    }

    [Fact]
    public async Task IRT_U_067_Report_before_the_first_map_rebuild_is_deferred_not_unknown()
    {
        await using var h = new ConnectionHarness();
        var (sink, map, counters) = SinkWithEmptyMap(h, out var id);
        await using var disposableSink = sink;
        map.Ready.ShouldBeFalse("the eager pre-connect preload does not mark it ready");

        // Simulates ConnectorBase (INT §9): reports observations right after Connected, racing the reconcile + first
        // DeviceMap rebuild that same report triggers (IRT §7.2, §7.4).
        sink.ReportProperty(_d1, "doorPosition.position", WpValue.Enum("open"));
        sink.ReportEvent(_d1, "doorPosition.forced");
        sink.ReportServerHealth("root", Connectivity.Online);
        (await sink.FlushAsync(Ct)).ShouldBe(new SinkFlushResult(0, 0, 0), "deferred, not counted unknown yet");
        counters.Unknown.ShouldBe(0);
        sink.Buffered.ShouldBe(3);

        // The reconcile imports the server/device; ConnectionRuntime.RebuildMap rebuilds and marks the map ready.
        var server = h.Registry.AddServer(id, "root");
        h.Registry.AddDevice(server, "d1");
        map.Update(DeviceMapSnapshot.Load(h.Registry, id));
        map.MarkReady();
        map.Ready.ShouldBeTrue();

        (await sink.FlushAsync(Ct)).ShouldBe(new SinkFlushResult(3, 0, 0), "every deferred report resolves once the map is rebuilt");
        counters.Unknown.ShouldBe(0);
        sink.Buffered.ShouldBe(0);
    }

    [Fact]
    public async Task IRT_U_068_Report_for_a_device_never_imported_is_unknown_once_the_map_is_ready()
    {
        await using var h = new ConnectionHarness();
        var (sink, map, counters) = SinkWithEmptyMap(h, out var id);
        await using var disposableSink = sink;

        // The first rebuild happened (reconcile completed), but this device was never imported: genuinely unknown.
        h.Registry.AddServer(id, "root");
        map.Update(DeviceMapSnapshot.Load(h.Registry, id));
        map.MarkReady();

        sink.ReportProperty(_d1, "doorPosition.position", WpValue.Enum("open"));
        sink.ReportServerHealth("other-root", Connectivity.Online);

        counters.Unknown.ShouldBe(2, "resolved against a ready map: dropped as unknown, not deferred");
        sink.Buffered.ShouldBe(0, "nothing is held back once the map is ready");
    }

    [Fact]
    public async Task IRT_U_069_Deferred_buffer_is_bounded_oldest_dropped_first()
    {
        await using var h = new ConnectionHarness(o => o.SinkBuffer = 2);
        var (sink, map, counters) = SinkWithEmptyMap(h, out var id);
        await using var disposableSink = sink;

        for (var i = 0; i < 6; i++)
        {
            sink.ReportProperty(new DeviceKey("root", "d" + i), "doorPosition.position", WpValue.Int(i));
        }

        sink.Buffered.ShouldBe(4, "bounded by the sink's own capacity (SinkBuffer * 2), like the main buffer's overflow");
        counters.Rejected.ShouldBe(2, "the 2 oldest deferred reports, dropped to make room");

        // The map rebuilds with every device imported: only the 4 kept deferred reports resolve.
        var server = h.Registry.AddServer(id, "root");
        for (var i = 0; i < 6; i++)
        {
            h.Registry.AddDevice(server, "d" + i);
        }

        map.Update(DeviceMapSnapshot.Load(h.Registry, id));
        map.MarkReady();

        // FlushAsync reports deltas since the last call: the 2 overflow drops (not reported yet) and the 4 accepted.
        (await sink.FlushAsync(Ct)).ShouldBe(new SinkFlushResult(4, 2, 0));
    }
}
