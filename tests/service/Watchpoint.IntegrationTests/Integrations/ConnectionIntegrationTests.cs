using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Integrations;

/// <summary>Connections end to end (spec IRT §15, IRT-I-005…012): real PostgreSQL, NATS, the simulator and a test connector.</summary>
[Trait("layer", "I")]
[Trait("spec", "IRT")]
public sealed class ConnectionIntegrationTests(PostgresContainerFixture postgres)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IRT_I_005_Connection_create_start_connected_devices_imported()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i005", Ct);
        using var http = node.Http();

        var created = await http.PostAsJsonAsync("api/v1/connections", new
        {
            name = "Simulator",
            packageId = SimulatorPackage.Id,
            serverType = "sim.server",
            settings = new { profile = "office", doors = 4, areas = 1, zonesPerArea = 2, seed = 7, activity = "busy" },
            locationId = node.Root.Value,
            autoImport = true,
            startNow = true,
        }, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = ConnectionId.From(Guid.Parse(JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!["id"]!.GetValue<string>()));
        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "connected", ct: Ct);
        var devices = await Eventually.UntilAsync(() =>
            node.Registry.QueryDevices(new DeviceQuery(), new PageRequest(PageRequest.MaxLimit)).Items is { Count: > 0 } items ? items : null, _wait, "devices imported", ct: Ct);
        devices.ShouldAllBe(d => d.LocationId == node.Root && !d.Orphaned);
        node.Registry.GetServersOfConnection(id).ShouldNotBeEmpty();

        // States flow: property records of the imported devices reach the log.
        var ids = devices.Select(d => d.Id).ToHashSet();
        await PollAsync(async () => (await node.ReadLogAsync(Ct)).Any(r => r.Envelope.Kind == RecordKinds.Property && r.Envelope.Dev is { } d && ids.Contains(d)),
            _wait, "properties are ingested", Ct);
        var api = JsonNode.Parse(await http.GetStringAsync($"api/v1/connections/{id.Value:D}", Ct))!;
        api["status"]!["state"]!.GetValue<string>().ShouldBe("connected");
        api["status"]!["counters"]!["observationsAccepted"]!.GetValue<long>().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IRT_I_006_Secrets_stored_encrypted_and_readable_by_connector()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i006", Ct);
        const string password = "Relay-Pa55word-2026";

        var id = (await node.Connections.CreateAsync(Relay(node, password, startNow: false), Context, Ct)).Value;

        var secrets = await node.ScalarAsync<string>("SELECT secrets::text FROM wp_integrations.connections WHERE id = @id", Ct, ("id", id.Value));
        var secretId = Guid.Parse(JsonNode.Parse(secrets!)!["password"]!.GetValue<string>());
        var ciphertext = await node.ScalarAsync<byte[]>("SELECT ciphertext FROM wp_secrets.secrets WHERE id = @id", Ct, ("id", secretId));
        ciphertext.ShouldNotBeNull().Length.ShouldBeGreaterThan(0);
        Contains(ciphertext, System.Text.Encoding.UTF8.GetBytes(password)).ShouldBeFalse("stored encrypted");
        (await node.ScalarAsync<long>("SELECT count(*) FROM wp_integrations.connections WHERE settings::text LIKE @p OR secrets::text LIKE @p", Ct, ("p", "%" + password + "%")))
            .ShouldBe(0);

        // The connector reads the stored secret (the test uses the connection's secrets, none given).
        var test = new TestConnectionRequest(PackageId.FromTrusted(RelayPackage.Id), null, RelayPackage.ServerType,
            JsonSerializer.SerializeToElement(new { expectedPassword = password }), new Dictionary<string, string>(), id);
        (await node.Connections.TestAsync(test, Ct)).Success.ShouldBeTrue();
        (await node.Connections.TestAsync(test with { Settings = JsonSerializer.SerializeToElement(new { expectedPassword = "other" }) }, Ct)).Success.ShouldBeFalse();
        node.Logs.Entries.ShouldAllBe(e => !e.RenderedMessage.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IRT_I_007_Restart_node_restarts_connections_and_keeps_cursors()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i007", Ct);
        var id = (await node.Connections.CreateAsync(Relay(node, "Relay-Pa55word-2026"), Context, Ct)).Value;
        await PollAsync(async () => Cursor(await node.StoreValueAsync(id, "cursor", Ct)) == 5, _wait, "first run reported 1…5", Ct);

        await node.RestartAsync(Ct);

        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "restarted at the Connections phase", ct: Ct);
        await PollAsync(async () => Cursor(await node.StoreValueAsync(id, "cursor", Ct)) == 10, _wait, "second run continued from the cursor", Ct);
        var door = node.Registry.QueryDevices(new DeviceQuery(), new PageRequest(10)).Items.Single(d => d.NativeId == "door-1");
        var events = (await node.ReadLogAsync(Ct)).Where(r => r.Envelope.Kind == RecordKinds.Event && r.Envelope.Dev == door.Id)
            .Select(r => (long)(r.Envelope.Src!.Value - DateTimeOffset.UnixEpoch).TotalSeconds).Order().ToList();
        events.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], "no duplicate, no gap");
    }

    [Fact]
    public async Task IRT_I_008_Connection_store_survives_restart()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i008", Ct);
        var id = (await node.Connections.CreateAsync(Relay(node, "Relay-Pa55word-2026"), Context, Ct)).Value;
        await PollAsync(async () => Starts(await node.StoreValueAsync(id, "starts", Ct)) == 1, _wait, "the first start counted", Ct);

        await node.RestartAsync(Ct);

        await PollAsync(async () => Starts(await node.StoreValueAsync(id, "starts", Ct)) == 2, _wait, "the count survived and was incremented", Ct);
        node.Connections.Get(id).Value.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task IRT_I_009_Command_through_channel_reaches_simulator()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i009", Ct);
        var id = (await node.Connections.CreateAsync(Simulator(node), Context, Ct)).Value;
        var door = await Eventually.UntilAsync(() =>
            node.Registry.QueryDevices(new DeviceQuery(TypeId: TypeId.FromTrusted("ac.door")), new PageRequest(10)).Items is { Count: > 0 } doors ? doors[0] : null, _wait, "a door imported", ct: Ct);
        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "connected", ct: Ct);
        var server = node.Registry.GetServer(door.ServerId).Value;
        var channel = node.Services.GetRequiredService<IConnectionCommandChannel>();
        var command = new ConnectorCommand(Guid.CreateVersion7(), new DeviceHandle(new DeviceKey(server.NativeId, door.NativeId), "ac.door", "1.0.0",
            new Dictionary<string, WpValue>(), []), "openable.open", new Dictionary<string, WpValue> { ["duration"] = WpValue.Int(5) },
            DateTimeOffset.UtcNow.AddSeconds(30), 1, CommandPriority.Normal);

        var result = await PollAsync(async () =>
            await channel.ExecuteAsync(id, command, Ct) is { Outcome: CommandOutcome.Succeeded } succeeded ? succeeded : null, _wait, "the door opens", Ct);

        result.Outcome.ShouldBe(CommandOutcome.Succeeded);
        node.Connections.GetStatus(id).Counters.CommandsSucceeded.ShouldBeGreaterThanOrEqualTo(1);
        await PollAsync(async () => (await node.ReadLogAsync(Ct)).Any(r => r.Envelope.Kind == RecordKinds.Event && r.Envelope.Dev == door.Id
            && r.Envelope.Body.GetProperty("path").GetString() == "openable.opened"), _wait, "the simulator's event is logged", Ct);
    }

    [Fact]
    public async Task IRT_I_010_Stop_connection_makes_devices_stale()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i010", Ct);
        var id = (await node.Connections.CreateAsync(Simulator(node), Context, Ct)).Value;
        await Eventually.UntilAsync(() => node.Registry.GetServersOfConnection(id).Any(s => s.ParentId is null), _wait, "imported", ct: Ct);
        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "connected", ct: Ct);
        var root = node.Registry.GetServersOfConnection(id).Single(s => s.ParentId is null);
        using var http = node.Http();

        var stopped = await http.PostAsync($"api/v1/connections/{id.Value:D}/stop", null, Ct);

        stopped.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await stopped.Content.ReadAsStringAsync(Ct))!["status"]!["state"]!.GetValue<string>().ShouldBe("disabled");
        await PollAsync(async () => (await node.ReadLogAsync(Ct)).Any(r => r.Envelope.Kind == RecordKinds.Health && r.Envelope.Srv == root.Id
            && r.Envelope.Body.GetProperty("connectivity").GetString() == "unknown"), _wait, "the root server is unknown, its devices stale", Ct);
    }

    [Fact]
    public async Task IRT_I_011_Inbound_webhook_end_to_end()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i011", Ct);
        const string password = "Relay-Pa55word-2026";
        var id = (await node.Connections.CreateAsync(Relay(node, password), Context, Ct)).Value;
        await PollAsync(async () => Cursor(await node.StoreValueAsync(id, "cursor", Ct)) == 5, _wait, "connected and imported", Ct);
        using var http = node.Http();

        using var refused = new HttpRequestMessage(HttpMethod.Post, $"integrations/{id.Value:D}/events") { Content = new StringContent("{}") };
        refused.Headers.Add("X-Webhook-Token", "wrong");
        (await http.SendAsync(refused, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var accepted = new HttpRequestMessage(HttpMethod.Post, $"integrations/{id.Value:D}/events") { Content = new StringContent("{}") };
        accepted.Headers.Add("X-Webhook-Token", password);
        (await http.SendAsync(accepted, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var door = node.Registry.QueryDevices(new DeviceQuery(), new PageRequest(10)).Items.Single(d => d.NativeId == "door-1");
        (await node.ReadLogAsync(Ct)).Count(r => r.Envelope.Kind == RecordKinds.Event && r.Envelope.Dev == door.Id && r.Envelope.Src == RelayPackage.WebhookEventTime)
            .ShouldBe(1, "flushed before the webhook answered");
    }

    [Fact]
    public async Task IRT_I_012_Connection_events_timeline_API()
    {
        await using var node = await ConnectionNode.StartAsync(postgres, "i012", Ct);
        var settings = JsonSerializer.SerializeToElement(new { failStarts = 1 });
        var id = (await node.Connections.CreateAsync(Relay(node, "Relay-Pa55word-2026") with { Settings = settings }, Context, Ct)).Value;
        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "connected after one restart", ct: Ct);
        using var http = node.Http();

        var timeline = await PollAsync(async () =>
        {
            var items = JsonNode.Parse(await http.GetStringAsync($"api/v1/connections/{id.Value:D}/events", Ct))!["items"]!.AsArray();
            return items.Count >= 4 ? items : null;
        }, _wait, "the timeline is recorded", Ct);

        timeline.Select(e => e!["state"]!.GetValue<string>()).ShouldBe(["starting", "faulted", "starting", "connected"]);
        var faulted = timeline[1]!["reason"]!;
        faulted["key"]!.GetValue<string>().ShouldBe("irt.connection.faulted");
        faulted["args"]!["exception"]!.GetValue<string>().ShouldBe("InvalidOperationException");
        timeline.Select(e => DateTimeOffset.Parse(e!["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)).ShouldBeInOrder();
        node.Connections.GetStatus(id).RestartCount.ShouldBe(1);
    }

    private static ChangeContext Context { get; } = new(null, "test");

    private static NewConnection Relay(ConnectionNode node, string password, bool startNow = true) => new(
        Watchpoint.Kernel.Text.LocalizedName.Of("Relay"), PackageId.FromTrusted(RelayPackage.Id), null, RelayPackage.ServerType,
        JsonSerializer.SerializeToElement(new { }), new Dictionary<string, string> { ["password"] = password }, node.Root, AutoImport: true, StartNow: startNow);

    private static NewConnection Simulator(ConnectionNode node) => new(
        Watchpoint.Kernel.Text.LocalizedName.Of("Simulator"), PackageId.FromTrusted(SimulatorPackage.Id), null, "sim.server",
        JsonSerializer.SerializeToElement(new { profile = "office", doors = 2, areas = 1, zonesPerArea = 1, seed = 3, commandLatency = "PT0S" }),
        new Dictionary<string, string>(), node.Root, AutoImport: true, StartNow: true);

    /// <summary>Polls an asynchronous condition (bounded, real time).</summary>
    private static async Task PollAsync(Func<Task<bool>> condition, TimeSpan timeout, string because, CancellationToken ct) =>
        await PollAsync(async () => await condition() ? string.Empty : null, timeout, because, ct);

    private static async Task<T> PollAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string because, CancellationToken ct)
        where T : class
    {
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            if (TimeProvider.System.GetElapsedTime(started) >= timeout)
            {
                throw new Watchpoint.Testing.TestAssertionException($"Timed out after {timeout.TotalSeconds:0} s: {because}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, ct);
        }
    }

    private static long Cursor(byte[]? value) => value is { Length: 8 } ? BitConverter.ToInt64(value) : 0;

    private static int Starts(byte[]? value) => value is { Length: 4 } ? BitConverter.ToInt32(value) : 0;

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
