using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Connections;
using Watchpoint.Integrations.Tests.Support;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Async;
using ConnectorStatus = Watchpoint.Integration.Abstractions.Context.ConnectionStatus;

namespace Watchpoint.Integrations.Tests;

/// <summary>Connections: validation, state machine, watchdog, inventory, command channel (spec IRT §7, IRT-U-026…047, 059, 060).</summary>
[Trait("layer", "U")]
[Trait("spec", "IRT")]
public sealed class ConnectionRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IRT_U_026_Create_connection_validates_settings()
    {
        await using var h = new ConnectionHarness();

        var created = await h.Manager.CreateAsync(h.NewConnection(settings: """{ "host": "acme.local", "port": "https", "unknown": 1 }"""), ConnectionHarness.Ctx, Ct);

        created.IsSuccess.ShouldBeFalse();
        created.Error.Code.Value.ShouldBe("common.validation.failed");
        created.Error.FieldErrors!.Select(f => f.Path).ShouldBe(["/settings/port", "/settings/unknown"], ignoreOrder: true);
        h.Manager.List().ShouldBeEmpty();
        h.Vault.Count.ShouldBe(0, "nothing is stored for a refused connection");
    }

    [Fact]
    public async Task IRT_U_027_Create_stores_secrets_in_vault_with_owner()
    {
        await using var h = new ConnectionHarness();

        var id = await h.CreateAsync(startNow: false);

        var create = h.Vault.Calls.ShouldHaveSingleItem();
        create.Action.ShouldBe("create");
        create.Owner.ShouldBe(new Secrets.Contracts.SecretOwner("Integrations", id.Value));
        var stored = (await h.Repository.ListAsync(Ct)).ShouldHaveSingleItem();
        stored.Secrets.ShouldBe(new Dictionary<string, SecretId> { ["password"] = create.Id });
        ConnectionColumnsText(stored).ShouldNotContain("S3cret-value!");
    }

    [Fact]
    public async Task IRT_U_028_Required_secret_missing_is_a_validation_error()
    {
        await using var h = new ConnectionHarness();

        var created = await h.Manager.CreateAsync(h.NewConnection(secrets: new Dictionary<string, string> { ["token"] = "t-1234" }), ConnectionHarness.Ctx, Ct);

        created.Error.FieldErrors!.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            f => f.Path.ShouldBe("/secrets/password"),
            f => f.Code.Value.ShouldBe(IntegrationErrors.SecretRequired));
        h.Vault.Count.ShouldBe(0);
    }

    [Fact]
    public async Task IRT_U_029_Isolation_required_refused_in_process()
    {
        await using var h = new ConnectionHarness();
        h.Packages.Isolation = "isolatedRequired";

        var created = await h.Manager.CreateAsync(h.NewConnection(), ConnectionHarness.Ctx, Ct);

        created.Error.Code.Value.ShouldBe(IntegrationErrors.ConnectionIsolationRequired);
        created.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task IRT_U_030_Isolated_and_remote_refused_in_M1()
    {
        await using var h = new ConnectionHarness();

        foreach (var mode in new[] { HostMode.Isolated, HostMode.Remote })
        {
            var created = await h.Manager.CreateAsync(h.NewConnection() with { HostMode = mode }, ConnectionHarness.Ctx, Ct);
            created.Error.Code.Value.ShouldBe(IntegrationErrors.HostUnavailable);
            created.Error.Kind.ShouldBe(ErrorKind.Unprocessable);
        }

        h.Manager.List().ShouldBeEmpty();
    }

    [Fact]
    public async Task IRT_U_031_Enabled_connections_start_at_Connections_phase_staggered()
    {
        await using var h = new ConnectionHarness();
        var a = await h.CreateAsync("A");
        var b = await h.CreateAsync("B");
        var c = await h.CreateAsync("C");
        var disabled = await h.CreateAsync("D", startNow: false);
        var t0 = h.Clock.GetUtcNow();
        h.States(a).ShouldBeEmpty("nothing starts before the Connections phase");

        await h.Manager.ActivateAsync(Ct);
        (await h.WaitForAsync(a, ConnectionState.Starting)).Since.ShouldBe(t0);
        await h.AdvanceUntilAsync(() => h.States(b).Count > 0, TimeSpan.FromMilliseconds(100), "B starts");
        await h.AdvanceUntilAsync(() => h.States(c).Count > 0, TimeSpan.FromMilliseconds(100), "C starts");

        (await h.WaitForAsync(b, ConnectionState.Starting)).Since.ShouldBe(t0.AddMilliseconds(200));
        (await h.WaitForAsync(c, ConnectionState.Starting)).Since.ShouldBe(t0.AddMilliseconds(400));
        h.Manager.GetStatus(disabled).State.ShouldBe(ConnectionState.Disabled);
        h.Host.Created.Count.ShouldBe(3);
    }

    [Fact]
    public async Task IRT_U_032_Starting_to_Connected_on_health_report()
    {
        await using var h = new ConnectionHarness();
        h.Host.Configure = c => c.OnStart = (_, _) => ValueTask.CompletedTask;
        var id = await h.StartedAsync(ConnectionState.Starting);

        // The state is Starting before the host has created the connector.
        await Eventually.UntilAsync(() => h.Host.Created.Count == 1, because: "the connector is created", ct: Ct);
        var connector = h.Host.Created.Single();
        await Eventually.UntilAsync(() => connector.Context is not null, because: "the connector is initialised", ct: Ct);

        connector.Context.Health.Report(ConnectorStatus.Connecting);
        connector.Context.Health.Report(ConnectorStatus.Connected);

        var status = await h.WaitForAsync(id, ConnectionState.Connected);
        status.LastConnectedAt.ShouldBe(h.Clock.GetUtcNow());
        await h.RecordedAsync(id);
        h.States(id).ShouldBe([ConnectionState.Starting, ConnectionState.Connected], "Connecting while starting is not a transition");
    }

    [Fact]
    public async Task IRT_U_033_First_connect_triggers_describe_and_reconcile()
    {
        await using var h = new ConnectionHarness();

        var id = await h.StartedAsync();

        await Eventually.UntilAsync(() => h.Registry.ReconciledSnapshot().Count == 1, because: "the first connect reconciles", ct: Ct);
        var (connection, report, options) = h.Registry.ReconciledSnapshot().Single();
        connection.ShouldBe(id);
        options.ShouldBe(new ReconcileOptions(AutoImportNew: true, DefaultLocation: h.Registry.Root));
        report.IsComplete.ShouldBeTrue();
        report.Devices.ShouldHaveSingleItem().Type.ShouldBe(FakeTypes.Ref("ac.door", 1, 2, 0));
        h.Host.Created.Single().DescribeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task IRT_U_034_Initialize_failure_faults_without_restart()
    {
        await using var h = new ConnectionHarness();
        h.Host.Configure = c => c.OnInitialize = _ => ValueTask.FromResult(Result.Failure(CommonErrors.ValidationFailed([CommonErrors.RequiredAt("/host")])));

        var id = await h.StartedAsync(ConnectionState.Faulted);

        var status = h.Manager.GetStatus(id);
        status.Reason!.Key.ShouldBe("irt.connection.initializeRefused");
        h.Clock.Advance(TimeSpan.FromHours(1));
        await Eventually.UntilAsync(() => h.Host.Released == 1, because: "the refused instance is released", ct: Ct);
        h.Host.Created.Count.ShouldBe(1, "no automatic restart");
        h.Host.Created.Single().StopCalls.ShouldBe(1, "an initialised instance is stopped");
        h.Logs.WithEventId(1852).ShouldHaveSingleItem();

        // A manual start retries.
        (await h.Manager.StartAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        await h.WaitForAsync(id, ConnectionState.Starting, occurrence: 2);
    }

    [Fact]
    public async Task IRT_U_035_Exception_faults_and_restarts_with_back_off()
    {
        await using var h = new ConnectionHarness();
        h.Host.Configure = c => c.OnStart = (_, _) => throw new InvalidOperationException("connector crash");
        var id = await h.StartedAsync(ConnectionState.Faulted);

        // Delays 1, 2, 4… s, capped at Connections:MaxRestartBackoff (5 min); each restart is a new instance.
        TimeSpan[] expected = [.. Enumerable.Range(0, 10).Select(i => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, i), 300)))];
        for (var i = 0; i < expected.Length; i++)
        {
            var faultedAt = (await h.WaitForAsync(id, ConnectionState.Faulted, i + 1)).Since;
            h.Clock.AdvanceTo(faultedAt + expected[i] - TimeSpan.FromTicks(1));
            h.Host.Created.Count.ShouldBe(i + 1, $"restart {i + 1} not before {expected[i]}");
            h.Clock.Advance(TimeSpan.FromTicks(1));
            (await h.WaitForAsync(id, ConnectionState.Starting, i + 2)).Since.ShouldBe(faultedAt + expected[i]);
        }

        h.Manager.GetStatus(id).RestartCount.ShouldBe(expected.Length);
        h.Host.Created.Take(expected.Length).ShouldAllBe(c => c.Disposed, "every crashed instance is disposed");
        h.Logs.WithEventId(1850).Count().ShouldBeGreaterThanOrEqualTo(expected.Length);
    }

    [Fact]
    public async Task IRT_U_036_Reconnecting_faults_after_FaultAfter()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();
        var connector = h.Host.Created.Single();

        connector.Context.Health.Report(ConnectorStatus.Disconnected, ConnectorMessage.Of("lost"));
        var reconnecting = await h.WaitForAsync(id, ConnectionState.Reconnecting);
        reconnecting.Reason!.Key.ShouldBe("packages.vendor.acme.lost");
        h.Clock.Advance(h.Options.FaultAfter - TimeSpan.FromTicks(1));
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Reconnecting);
        h.Clock.Advance(TimeSpan.FromTicks(1));
        (await h.WaitForAsync(id, ConnectionState.Faulted)).Reason!.Key.ShouldBe("irt.connection.reconnectTimeout");

        // Display only: the same instance keeps trying and its report brings the connection back.
        connector.Context.Health.Report(ConnectorStatus.Connected);
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 2);
        h.Host.Created.Count.ShouldBe(1);

        // Disable while reconnecting: stopped once, Disabled, no restart.
        connector.Context.Health.Report(ConnectorStatus.Connecting);
        await h.WaitForAsync(id, ConnectionState.Reconnecting, occurrence: 2);
        (await h.Manager.StopAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Disabled);
        connector.StopCalls.ShouldBe(1);
        h.Clock.Advance(h.Options.FaultAfter * 2);
        await h.RecordedAsync(id);
        h.States(id).ShouldBe([ConnectionState.Starting, ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Faulted,
            ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Stopping, ConnectionState.Disabled]);
    }

    [Fact]
    public async Task IRT_U_037_Settings_update_restarts_connection()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();

        var updated = await h.Manager.UpdateAsync(id, new ConnectionUpdate(Settings: ConnectionHarness.Json("""{ "host": "acme.local", "port": 8443 }""")),
            h.Manager.Get(id).Value.Version, ConnectionHarness.Ctx, Ct);

        updated.IsSuccess.ShouldBeTrue();
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 2);
        var (first, second) = (h.Host.Created[0], h.Host.Created[1]);
        first.StopCalls.ShouldBe(1);
        first.Disposed.ShouldBeTrue();
        second.Context.Settings.GetProperty("port").GetInt32().ShouldBe(8443);
        await h.RecordedAsync(id);
        h.States(id).ShouldBe([ConnectionState.Starting, ConnectionState.Connected, ConnectionState.Stopping, ConnectionState.Configured,
            ConnectionState.Starting, ConnectionState.Connected]);

        // Reconfigured while connecting: the starting instance stops, one new instance gets the latest settings.
        var hold = new TaskCompletionSource();
        h.Host.Configure = c => c.OnStart = async (_, ct) => await hold.Task.WaitAsync(ct);
        (await h.Manager.RestartAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        await h.WaitForAsync(id, ConnectionState.Starting, occurrence: 3);
        h.Host.Configure = _ => { };
        var stale = h.Manager.Get(id).Value.Version;
        (await h.Manager.UpdateAsync(id, new ConnectionUpdate(Settings: ConnectionHarness.Json("""{ "host": "b.local" }""")), stale, ConnectionHarness.Ctx, Ct)).IsSuccess.ShouldBeTrue();
        (await h.Manager.UpdateAsync(id, new ConnectionUpdate(Settings: ConnectionHarness.Json("""{ "host": "c.local" }""")), stale, ConnectionHarness.Ctx, Ct))
            .Error.Code.Value.ShouldBe("common.version.conflict", "a stale version is refused");
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 3);
        h.Host.Created.Count.ShouldBe(4);
        h.Host.Created[3].Context.Settings.GetProperty("host").GetString().ShouldBe("b.local");
        h.Host.Created[2].Disposed.ShouldBeTrue();
        hold.TrySetResult();
    }

    [Fact]
    public async Task IRT_U_038_Stop_timeout_abandons_instance()
    {
        await using var h = new ConnectionHarness();
        var never = new TaskCompletionSource();
        h.Host.Configure = c => c.OnStop = async (_, _) => await never.Task;
        var id = await h.StartedAsync();

        var stop = h.Manager.StopAsync(id, Ct);
        await h.WaitForAsync(id, ConnectionState.Stopping);
        stop.IsCompleted.ShouldBeFalse("the stop waits for the connector, at most 15 s");
        await h.AdvanceUntilAsync(() => stop.IsCompleted, TimeSpan.FromSeconds(1), "the stop gives up");

        (await stop).IsSuccess.ShouldBeTrue();
        var abandoned = h.Logs.WithEventId(1851).ShouldHaveSingleItem();
        abandoned.Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Error);
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Disabled);
        h.Host.Created.Single().Disposed.ShouldBeFalse("an abandoned instance is not disposed");
        h.Host.Released.ShouldBe(1);
        (await h.WaitForAsync(id, ConnectionState.Disabled)).Since.ShouldBeGreaterThanOrEqualTo(abandoned is null ? default : h.Clock.GetUtcNow() - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task IRT_U_039_State_transitions_recorded()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();

        // Concurrent operations: serialised per connection, every transition recorded once and in order.
        var operations = Enumerable.Range(0, 20).Select(i => (i % 3) switch
        {
            0 => h.Manager.StopAsync(id, Ct),
            1 => h.Manager.StartAsync(id, Ct),
            _ => h.Manager.RestartAsync(id, Ct),
        }).ToList();
        await Task.WhenAll(operations);
        (await h.Manager.StartAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        await Eventually.UntilAsync(() => h.Manager.GetStatus(id).State == ConnectionState.Connected, because: "the connection settles connected", ct: Ct);
        await h.Manager.Runtime(id)!.ShutdownAsync();
        await h.Manager.Runtime(id)!.StartAsync();
        await h.Manager.Runtime(id)!.ActivateAsync();
        await Eventually.UntilAsync(() => h.Manager.GetStatus(id).State == ConnectionState.Connected, because: "reconnected", ct: Ct);
        await h.RecordedAsync(id);

        var changed = h.Transitions(id);
        var events = await h.Repository.ListEventsAsync(id, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, Ct);
        var records = h.EventLog.Appended.Where(r => r.Record.Kind == "connection.state").ToList();
        events.Select(e => e.State).ShouldBe(changed.Select(c => c.Status.State));
        records.Select(r => r.Record.Body.GetProperty("state").GetString()).ShouldBe(changed.Select(c => ConnectionColumnsState(c.Status.State)));
        records.ShouldAllBe(r => r.Stream == LogStream.Sys && r.Record.Origin.Id == id.Value);
        changed.Zip(changed.Skip(1)).ShouldAllBe(p => p.First.Status.State == p.Second.Previous, "each transition starts where the previous one ended");
        changed.Zip(changed.Skip(1)).ShouldAllBe(p => p.First.Status.State != p.Second.Status.State || !Equals(p.First.Status.Reason, p.Second.Status.Reason));

        // Never two live instances: every instance but the last was stopped once and disposed.
        h.Host.Created.SkipLast(1).ShouldAllBe(c => c.Disposed && c.StopCalls == 1);
        h.Host.Created[^1].Disposed.ShouldBeFalse();
        h.Logs.WithEventId(1820).Count().ShouldBe(changed.Count);
    }

    [Fact]
    public async Task IRT_U_040_Faulted_or_stopped_connection_marks_root_servers_unknown()
    {
        await using var h = new ConnectionHarness();
        var id = await h.CreateAsync();
        var root = h.Registry.AddServer(id, "root");
        var child = h.Registry.AddServer(id, "sub", root.Id);
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);

        (await h.Manager.StopAsync(id, Ct)).IsSuccess.ShouldBeTrue();

        await Eventually.UntilAsync(() => h.Ingest.All.Count(o => o.Kind == ObservationKind.Health) >= 2, because: "Stopping and Disabled are reported", ct: Ct);
        var health = h.Ingest.All.Where(o => o.Kind == ObservationKind.Health).ToList();
        health.ShouldAllBe(o => o.Server == root.Id && o.Connection == id && o.Health!.Connectivity == Connectivity.Unknown);
        health.Select(o => o.Health!.Reason).ShouldBe(["irt.connection.stopping", "irt.connection.disabled"]);
        health.ShouldNotContain(o => o.Server == child.Id, "only root servers");
    }

    [Fact]
    public async Task IRT_U_041_Watchdog_hung_command_restarts_connection()
    {
        await using var h = new ConnectionHarness();
        var hung = new TaskCompletionSource<ConnectorCommandResult>();
        h.Host.Configure = c => c.OnExecute = (_, _) => new ValueTask<ConnectorCommandResult>(hung.Task);
        var id = await h.CreateAsync();
        var server = h.Registry.AddServer(id, "root");
        h.Registry.AddDevice(server, "d1");
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);
        var deadline = h.Clock.GetUtcNow().AddSeconds(10);

        var execute = h.Manager.ExecuteAsync(id, Command("d1", deadline), Ct).AsTask();
        await h.AdvanceUntilAsync(() => execute.IsCompleted, TimeSpan.FromSeconds(1), "the caller gets its answer at the deadline");
        (await execute).Failure.ShouldBe(FailureCategory.Timeout);
        h.Logs.WithEventId(1860).ShouldBeEmpty("not hung before deadline + 30 s");

        await h.AdvanceUntilAsync(() => h.Logs.WithEventId(1860).Any(), TimeSpan.FromSeconds(5), "the watchdog finds the hung call");
        h.Clock.GetUtcNow().ShouldBeGreaterThan(deadline + TimeSpan.FromSeconds(30));
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 2);
        h.Host.Created.Count.ShouldBe(2);
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Connected);
        hung.TrySetResult(ConnectorCommandResult.Succeeded());
    }

    [Fact]
    public async Task IRT_U_042_Watchdog_silent_connector_warns_only()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();
        await Eventually.UntilAsync(() => h.Registry.ReconciledSnapshot().Count == 1, because: "connected", ct: Ct);

        await h.AdvanceUntilAsync(() => h.Logs.WithEventId(1862).Any(), TimeSpan.FromSeconds(10), "the silence is reported");

        var silence = (TimeSpan)h.Logs.WithEventId(1862).Single().Properties["Silence"]!;
        silence.ShouldBeGreaterThanOrEqualTo(h.Options.SilenceTimeout);
        silence.ShouldBeLessThan(h.Options.SilenceTimeout + h.Options.WatchdogInterval + TimeSpan.FromSeconds(1));
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        h.Logs.WithEventId(1862).Count().ShouldBe(1, "a warning once per silence");
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Connected);
        h.Host.Created.Count.ShouldBe(1, "a warning only: no restart");
    }

    [Fact]
    public async Task IRT_U_043_Flooding_signal_reports_root_server_fault()
    {
        await using var h = new ConnectionHarness();
        var id = await h.CreateAsync();
        var root = h.Registry.AddServer(id, "root");
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);
        var connector = h.Host.Created.Single();
        connector.Context.Sink.ReportServerHealth("root", Connectivity.Degraded);
        await connector.Context.Sink.FlushAsync(Ct);

        ((Pipeline.Contracts.IIngestFloodingListener)h.Manager).OnFloodingChanged(id, flooding: true, pending: 100_000);

        await Eventually.UntilAsync(() => h.Ingest.All.Any(o => o.Health?.Faults.Contains("flooding") == true), because: "the fault is reported", ct: Ct);
        var flooding = h.Ingest.All.Last(o => o.Health?.Faults.Contains("flooding") == true);
        flooding.Server.ShouldBe(root.Id);
        flooding.Health!.Connectivity.ShouldBe(Connectivity.Degraded, "the connector's last connectivity is kept");
        await Eventually.UntilAsync(() => h.Manager.GetStatus(id).Reason?.Key == "irt.connection.flooding", because: "the status says why", ct: Ct);
        connector.Context.Sink.ReportServerHealth("root", Connectivity.Online, ["batteryLow"]);
        await connector.Context.Sink.FlushAsync(Ct);
        h.Ingest.All[^1].Health!.Faults.ShouldBe(["batteryLow", "flooding"], "the connector's reports carry the fault while flooding");

        ((Pipeline.Contracts.IIngestFloodingListener)h.Manager).OnFloodingChanged(id, flooding: false, pending: 0);
        await Eventually.UntilAsync(() => h.Manager.GetStatus(id).Reason is null, because: "cleared", ct: Ct);
        await Eventually.UntilAsync(() => h.Ingest.All[^1].Health is { Faults.Count: 0 }, because: "the fault is cleared", ct: Ct);
        h.Logs.WithEventId(1863).Count().ShouldBe(2);
    }

    [Fact]
    public async Task IRT_U_044_Describe_interval_triggers_rediscovery()
    {
        await using var h = new ConnectionHarness();
        var id = await h.StartedAsync();
        var connector = h.Host.Created.Single();
        await Eventually.UntilAsync(() => connector.DescribeCalls == 1, because: "first connect", ct: Ct);

        h.Clock.Advance(Connection.DefaultDescribeInterval - h.Options.WatchdogInterval);
        connector.DescribeCalls.ShouldBe(1);
        await h.AdvanceUntilAsync(() => connector.DescribeCalls == 2, h.Options.WatchdogInterval, "rediscovery after the interval");

        await Eventually.UntilAsync(() => h.Registry.ReconciledSnapshot().Count == 2, because: "reconciled again", ct: Ct);
        h.Registry.ReconciledSnapshot().ShouldAllBe(r => r.Connection == id);

        // A reconnect within the interval does not describe again.
        connector.Context.Health.Report(ConnectorStatus.Disconnected);
        connector.Context.Health.Report(ConnectorStatus.Connected);
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 2);
        connector.DescribeCalls.ShouldBe(2);
    }

    [Fact]
    public async Task IRT_U_045_ReportInventoryChanged_triggers_reconcile_with_partial_flag()
    {
        await using var h = new ConnectionHarness();
        await h.StartedAsync();
        var connector = h.Host.Created.Single();
        await Eventually.UntilAsync(() => h.Registry.ReconciledSnapshot().Count == 1, because: "first connect", ct: Ct);

        connector.Context.Sink.ReportInventoryChanged(FakeConnector.Inventory(complete: false));

        await Eventually.UntilAsync(() => h.Registry.ReconciledSnapshot().Count == 2, because: "the change is reconciled", ct: Ct);
        h.Registry.ReconciledSnapshot()[1].Report.IsComplete.ShouldBeFalse();
        connector.DescribeCalls.ShouldBe(1, "the reported snapshot is used as is");
    }

    [Fact]
    public async Task IRT_U_046_Unknown_type_in_inventory_warns_and_skips_item()
    {
        await using var h = new ConnectionHarness();
        var coordinator = new InventoryCoordinator(h.Registry, h.Types, Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<InventoryCoordinator>(h.Loggers));
        var id = ConnectionId.New();
        var attributes = new Dictionary<string, WpValue> { ["model"] = WpValue.String("X1") };
        var snapshot = new InventorySnapshot(
            [new InventoryServerItem("root", null, "acme.server", "1.0.0", "Acme", attributes)],
            [
                new InventoryDeviceItem("d1", "root", null, "ac.door", "1.0", "Door 1", attributes, ["doorLock"], [new InventoryLinkItem("controls", "d2", "root")]),
                new InventoryDeviceItem("d2", "root", "d1", "x.unknown", "1.0", "Thing", attributes, [], []),
                new InventoryDeviceItem("d3", "root", null, "ac.door", "1", "Door 3", attributes, ["Not An Id"], []),
            ],
            IsComplete: true);

        var mapped = coordinator.Map(id, snapshot).Value;

        // Mapping per IRT §7.4: ids, vendor fields, links copied; type resolved with MOD; capabilities parsed.
        var server = mapped.Report.Servers.ShouldHaveSingleItem();
        server.ShouldBe(new InventoryServer("root", null, FakeTypes.Ref("acme.server", 1, 0, 0), "Acme", attributes));
        var device = mapped.Report.Devices.ShouldHaveSingleItem();
        device.NativeId.ShouldBe("d1");
        device.ServerNativeId.ShouldBe("root");
        device.Type.ShouldBe(FakeTypes.Ref("ac.door", 1, 2, 0), "\"1.0\" resolves to the latest 1.x");
        device.VendorName.ShouldBe("Door 1");
        device.VendorAttributes.ShouldBe(attributes);
        device.UnsupportedOptionalCapabilities.ShouldBe(new HashSet<CapabilityId> { CapabilityId.FromTrusted("doorLock") });
        device.Links.ShouldBe([new InventoryLink("controls", "d2", "root")]);

        // Unknown type and invalid capability id: skipped with a warning; the report becomes partial (no orphaning).
        mapped.Warnings.Select(w => (w.Code, w.NativeId)).ShouldBe([(IntegrationErrors.InventoryTypeUnknown, "d2"), (IntegrationErrors.InventoryTypeUnknown, "d3")]);
        mapped.Report.IsComplete.ShouldBeFalse();
        h.Logs.WithEventId(1832).Count().ShouldBe(2);

        var reconciled = await coordinator.ReconcileAsync(TestConnection(id, h.Registry.Root), snapshot, Ct);
        reconciled.Value.Warnings.Select(w => w.NativeId).ShouldBe(["d2", "d3"]);
        h.Registry.ReconciledSnapshot().Single().Report.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task IRT_U_047_Device_map_excludes_disabled_and_orphaned_devices_and_raises_Changed_on_import()
    {
        await using var h = new ConnectionHarness();
        var id = await h.CreateAsync();
        var server = h.Registry.AddServer(id, "root");
        h.Registry.AddDevice(server, "d1");
        h.Registry.AddDevice(server, "d2", enabled: false);
        h.Registry.AddDevice(server, "d3", orphaned: true);
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);
        var devices = h.Host.Created.Single().Context.Devices;
        devices.All.Select(d => d.Key.NativeId).ShouldBe(["d1"]);
        devices.TryGet(new DeviceKey("root", "d2"), out _).ShouldBeFalse();
        var changes = new List<DeviceMapChangedEventArgsSnapshot>();
        devices.Changed += (_, e) =>
        {
            lock (changes)
            {
                changes.Add(new([.. e.Added.Select(a => a.Key.NativeId)], [.. e.Removed.Select(r => r.NativeId)]));
            }
        };

        var imported = h.Registry.AddDevice(server, "d4");
        h.Registry.RaiseDevicesChanged(imported);

        await Eventually.UntilAsync(() => changes.Count == 1, because: "the import is pushed to the connector", ct: Ct);
        changes.Single().ShouldBe(new DeviceMapChangedEventArgsSnapshot(["d4"], []));
        devices.All.Select(d => d.Key.NativeId).Order().ShouldBe(["d1", "d4"]);
        devices.TryGet(new DeviceKey("root", "d4"), out var handle).ShouldBeTrue();
        handle!.TypeId.ShouldBe("ac.door");
    }

    [Fact]
    public async Task IRT_U_059_Command_channel_not_connected_returns_ConnectionDown()
    {
        await using var h = new ConnectionHarness();
        var id = await h.CreateAsync();

        var configured = await h.Manager.ExecuteAsync(id, Command("d1", h.Clock.GetUtcNow().AddSeconds(5)), Ct);
        var unknown = await h.Manager.ExecuteAsync(ConnectionId.New(), Command("d1", h.Clock.GetUtcNow().AddSeconds(5)), Ct);
        var batch = await h.Manager.ExecuteBatchAsync(id, [Command("d1", h.Clock.GetUtcNow().AddSeconds(5)), Command("d2", h.Clock.GetUtcNow().AddSeconds(5))], Ct);

        configured.Failure.ShouldBe(FailureCategory.ConnectionDown);
        unknown.Failure.ShouldBe(FailureCategory.ConnectionDown);
        batch.ShouldAllBe(r => r.Failure == FailureCategory.ConnectionDown);
        h.Manager.SupportsBatch(id).ShouldBeFalse();
        h.Host.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task IRT_U_060_Command_channel_deadline_times_out_and_exception_faults()
    {
        await using var h = new ConnectionHarness();
        var calls = 0;
        h.Host.Configure = c => c.OnExecute = async (command, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await new TaskCompletionSource().Task.WaitAsync(ct); // honours cancellation
            }

            throw new InvalidOperationException("connector bug");
        };
        var id = await h.CreateAsync();
        var server = h.Registry.AddServer(id, "root");
        h.Registry.AddDevice(server, "d1");
        await h.Manager.ActivateAsync(Ct);
        await h.WaitForAsync(id, ConnectionState.Connected);

        var timeout = h.Manager.ExecuteAsync(id, Command("d1", h.Clock.GetUtcNow().AddSeconds(5)), Ct).AsTask();
        await h.AdvanceUntilAsync(() => timeout.IsCompleted, TimeSpan.FromSeconds(1), "cancelled at the deadline");
        (await timeout).Failure.ShouldBe(FailureCategory.Timeout);
        h.Manager.GetStatus(id).State.ShouldBe(ConnectionState.Connected, "a timeout is not a fault");

        var failed = await h.Manager.ExecuteAsync(id, Command("d1", h.Clock.GetUtcNow().AddSeconds(5)), Ct);
        failed.Failure.ShouldBe(FailureCategory.InternalError);
        (await h.WaitForAsync(id, ConnectionState.Faulted)).Reason!.Key.ShouldBe("irt.connection.faulted");
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await h.WaitForAsync(id, ConnectionState.Connected, occurrence: 2);
        var counters = h.Manager.GetStatus(id).Counters;
        counters.CommandsFailed.ShouldBe(2);
        counters.CommandsSucceeded.ShouldBe(0);
    }

    internal static ConnectorCommand Command(string nativeId, DateTimeOffset deadline) => new(Guid.CreateVersion7(),
        new DeviceHandle(new DeviceKey("root", nativeId), "ac.door", "1.0.0", new Dictionary<string, WpValue>(), []), "doorLock.unlock",
        new Dictionary<string, WpValue>(), deadline, 1, CommandPriority.Normal);

    internal static Connection TestConnection(ConnectionId id, LocationId location) => new(id, LocalizedName.Of("t"), ConnectionHarness.Package,
        ConnectionHarness.Version, ConnectionHarness.ServerType, ConnectionHarness.Json("{}"), new Dictionary<string, SecretId>(), location, HostMode.InProcess,
        null, true, true, Connection.DefaultDescribeInterval, null, 1);

    private static string ConnectionColumnsText(Connection connection) =>
        connection.Settings.GetRawText() + Internal.Storage.ConnectionColumns.SecretsJson(connection.Secrets);

    private static string ConnectionColumnsState(ConnectionState state) => Internal.Storage.ConnectionColumns.StateText(state);

    private sealed record DeviceMapChangedEventArgsSnapshot(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
    {
        public bool Equals(DeviceMapChangedEventArgsSnapshot? other) =>
            other is not null && Added.SequenceEqual(other.Added) && Removed.SequenceEqual(other.Removed);

        public override int GetHashCode() => Added.Count;
    }

    [Fact]
    public async Task IRT_U_065_Manifest_silence_timeout_overrides_the_node_setting_per_connector_type()
    {
        await using var h = new ConnectionHarness();
        h.Host.Limits = new ConnectorLimitsDoc(SilenceTimeout: TimeSpan.FromMinutes(2));
        await h.StartedAsync();

        // The node setting is 30 minutes; the connector type says 2: the warning comes at about 2 minutes.
        await h.AdvanceUntilAsync(() => h.Logs.WithEventId(1862).Any(), TimeSpan.FromSeconds(10), "the silence is reported");
        var silence = (TimeSpan)h.Logs.WithEventId(1862).Single().Properties["Silence"]!;
        silence.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(2));
        silence.ShouldBeLessThan(TimeSpan.FromMinutes(2) + h.Options.WatchdogInterval + TimeSpan.FromSeconds(1));
        silence.ShouldBeLessThan(h.Options.SilenceTimeout);

        // A longer manifest limit holds the warning back past the node setting.
        await using var longer = new ConnectionHarness();
        longer.Host.Limits = new ConnectorLimitsDoc(SilenceTimeout: TimeSpan.FromHours(3));
        await longer.StartedAsync();
        longer.Clock.Advance(longer.Options.SilenceTimeout + TimeSpan.FromMinutes(5));
        await Task.Yield();
        longer.Logs.WithEventId(1862).ShouldBeEmpty("the type allows 3 hours of silence");
    }
}
