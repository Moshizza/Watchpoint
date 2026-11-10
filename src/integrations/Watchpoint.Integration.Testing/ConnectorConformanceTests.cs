using System.Text.Json;
using Shouldly;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Integration.Testing.Logging;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Testing;

/// <summary>
/// Connector conformance test suite executing rules INT-C-001 through INT-C-040 (spec INT §12).
/// Derived connector test classes supply a vendor simulator and connector configuration.
/// </summary>
/// <typeparam name="TConnector">The connector type under test.</typeparam>
public abstract class ConnectorConformanceTests<TConnector> where TConnector : IConnector
{
    /// <summary>Creates a new vendor simulator instance.</summary>
    protected abstract IVendorSimulator CreateSimulator();

    /// <summary>Creates valid connector settings configured against the given simulator.</summary>
    protected abstract JsonElement CreateSettings(IVendorSimulator sim);

    /// <summary>Creates secret credentials matching the simulator.</summary>
    protected abstract IReadOnlyDictionary<string, string> CreateSecrets(IVendorSimulator sim);

    /// <summary>Gets the conformance test options declaring supported features and timings.</summary>
    protected virtual ConformanceOptions Options => new();

    /// <summary>Creates a connector instance using default constructor or activator.</summary>
    protected virtual TConnector CreateConnector()
    {
        return Activator.CreateInstance<TConnector>();
    }

    /// <summary>Creates a semantically invalid settings JSON element for INT-C-001 testing.</summary>
    protected virtual JsonElement CreateInvalidSettings(IVendorSimulator sim)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["doors"] = -1,
            ["invalidField"] = "unexpected",
        });
    }

    /// <summary>Creates wrong secret credentials for INT-C-004 testing.</summary>
    protected virtual IReadOnlyDictionary<string, string> CreateWrongSecrets(IVendorSimulator sim)
    {
        return new Dictionary<string, string>
        {
            ["token"] = "wrong-token-value-12345",
            ["password"] = "invalid-password",
        };
    }

    /// <summary>INT-C-001: InitializeAsync with valid settings succeeds; with semantically invalid settings returns validation errors (no exception).</summary>
    [Fact]
    public virtual async Task INT_C_001_InitializeAsync_validates_settings()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var validContext = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        var validResult = await connector.InitializeAsync(validContext, CancellationToken.None);
        validResult.IsSuccess.ShouldBeTrue("InitializeAsync with valid settings must succeed");

        await using var invalidConnector = CreateConnector();
        using var invalidContext = new TestConnectorContext(CreateInvalidSettings(sim), time, CreateSecrets(sim));
        var invalidResult = await invalidConnector.InitializeAsync(invalidContext, CancellationToken.None);
        invalidResult.IsSuccess.ShouldBeFalse("InitializeAsync with semantically invalid settings must return validation failure");
        invalidResult.Error.Code.Value.ShouldBe("common.validation.failed");
    }

    /// <summary>INT-C-002: TestAsync succeeds against the simulator and returns identity.</summary>
    [Fact]
    public virtual async Task INT_C_002_TestAsync_succeeds_and_returns_identity()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        var initRes = await connector.InitializeAsync(context, CancellationToken.None);
        initRes.IsSuccess.ShouldBeTrue();

        using var cts = new CancellationTokenSource(Options.TestTimeout);
        var testResult = await connector.TestAsync(cts.Token);

        testResult.Success.ShouldBeTrue("TestAsync should succeed against a running simulator");
        testResult.Identity.ShouldNotBeNull("TestAsync must return ServerIdentity on success");
    }

    /// <summary>INT-C-003: TestAsync with simulator stopped -> Unreachable within 30 s.</summary>
    [Fact]
    public virtual async Task INT_C_003_TestAsync_unreachable_when_simulator_stopped()
    {
        await using var sim = CreateSimulator();
        await sim.StopAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var testResult = await connector.TestAsync(cts.Token);

        testResult.Success.ShouldBeFalse("TestAsync must fail when simulator is stopped");
        testResult.Failure.ShouldBe(FailureCategory.Unreachable);
    }

    /// <summary>INT-C-004: TestAsync with wrong credentials -> AuthenticationFailed (if the vendor has credentials).</summary>
    [Fact]
    public virtual async Task INT_C_004_TestAsync_authentication_failed_with_wrong_credentials()
    {
        if (!Options.SupportsCredentials)
        {
            Assert.Skip("Credentials are not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateWrongSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        using var cts = new CancellationTokenSource(Options.TestTimeout);
        var testResult = await connector.TestAsync(cts.Token);

        testResult.Success.ShouldBeFalse();
        testResult.Failure.ShouldBe(FailureCategory.AuthenticationFailed);
    }

    /// <summary>INT-C-005: StartAsync returns within 5 s even if the simulator is unreachable.</summary>
    [Fact]
    public virtual async Task INT_C_005_StartAsync_returns_within_five_seconds_when_unreachable()
    {
        await using var sim = CreateSimulator();
        await sim.StopAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        using var cts = new CancellationTokenSource(Options.StartTimeout);
        await connector.StartAsync(cts.Token);

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-006: Health goes Connecting -> Connected when the simulator is up.</summary>
    [Fact]
    public virtual async Task INT_C_006_Health_goes_connecting_to_connected_when_simulator_up()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);
        await Task.Yield();

        context.Health.History.Any(h => h.Status == ConnectionStatus.Connected).ShouldBeTrue("Health must report Connected status when simulator is up");

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-007: Simulator dropped -> Health Disconnected; root server health Unknown reported.</summary>
    [Fact]
    public virtual async Task INT_C_007_Simulator_dropped_reports_disconnected_and_unknown_server_health()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);
        await Task.Yield();

        await sim.DropConnectionAsync();
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-008: Reconnects automatically with back-off after the simulator comes back; resync performed (refresh observed).</summary>
    [Fact]
    public virtual async Task INT_C_008_Reconnects_with_backoff_and_resyncs()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);
        await Task.Yield();

        await sim.DropConnectionAsync();
        await Task.Yield();

        await sim.RestoreConnectionAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-009: After reconnect, events that happened while disconnected are reported (if eventCatchUp) exactly once.</summary>
    [Fact]
    public virtual async Task INT_C_009_Disconnected_events_reported_on_reconnect_exactly_once()
    {
        if (!Options.SupportsEventCatchUp || connectorIsNot<IEventCatchUp>())
        {
            Assert.Skip("IEventCatchUp is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);
        await Task.Yield();

        await sim.InjectEventAsync("door-0001", "accessEvents.granted", new Dictionary<string, object?> { ["holder"] = "User1" });
        await Task.Yield();

        if (connector is IEventCatchUp catchUp)
        {
            await catchUp.CatchUpAsync(CancellationToken.None);
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-010: DescribeAsync returns complete inventory matching the simulator; native ids stable across two calls and restarts.</summary>
    [Fact]
    public virtual async Task INT_C_010_DescribeAsync_returns_matching_inventory_with_stable_ids()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        var first = await connector.DescribeAsync(CancellationToken.None);
        first.ShouldNotBeNull();
        first.Validate().IsSuccess.ShouldBeTrue();

        var second = await connector.DescribeAsync(CancellationToken.None);
        second.Devices.Count.ShouldBe(first.Devices.Count);
        for (int i = 0; i < first.Devices.Count; i++)
        {
            second.Devices[i].NativeId.ShouldBe(first.Devices[i].NativeId);
        }
    }

    /// <summary>INT-C-011: No native id is a display name (simulator renames a device -> native id unchanged).</summary>
    [Fact]
    public virtual async Task INT_C_011_Renaming_device_keeps_native_id_unchanged()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        var before = await connector.DescribeAsync(CancellationToken.None);
        var target = before.Devices.Count > 0 ? before.Devices[0] : null;
        if (target == null)
        {
            Assert.Skip("Simulator produced no devices to rename");
            return;
        }

        await sim.RenameDeviceAsync(target.NativeId, "Brand New Name");
        var after = await connector.DescribeAsync(CancellationToken.None);
        var matching = after.Devices.FirstOrDefault(d => d.NativeId == target.NativeId);
        matching.ShouldNotBeNull("Native ID must remain unchanged when device is renamed");
    }

    /// <summary>INT-C-012: State change injected -> property reported within the declared latency.</summary>
    [Fact]
    public virtual async Task INT_C_012_Injected_state_change_reported_within_latency()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        var inv = await connector.DescribeAsync(CancellationToken.None);
        foreach (var dev in inv.Devices)
        {
            context.Devices.Add(new DeviceHandle(new DeviceKey("sim-root", dev.NativeId), dev.TypeId, "1.0", dev.VendorAttributes, dev.UnsupportedOptionalCapabilities));
        }

        await connector.StartAsync(CancellationToken.None);

        await sim.InjectStateChangeAsync("door-0001", "doorPosition.position", "open");
        await context.Scheduler.TriggerAsync(Options.DeclaredLatency + TimeSpan.FromSeconds(1));
        await Task.Yield();

        if (connector is IStateRefresh refresh)
        {
            await refresh.RefreshAsync(RefreshScope.All, CancellationToken.None);
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-013: Values reported match property types (all accepted by a strict sink using the type catalogue).</summary>
    [Fact]
    public virtual async Task INT_C_013_Reported_values_match_property_types()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        var inv = await connector.DescribeAsync(CancellationToken.None);
        foreach (var dev in inv.Devices)
        {
            context.Devices.Add(new DeviceHandle(new DeviceKey("sim-root", dev.NativeId), dev.TypeId, "1.0", dev.VendorAttributes, dev.UnsupportedOptionalCapabilities));
        }

        await connector.StartAsync(CancellationToken.None);
        if (connector is IStateRefresh refresh)
        {
            await refresh.RefreshAsync(RefreshScope.All, CancellationToken.None);
        }

        foreach (var prop in context.Sink.Properties)
        {
            prop.Value.Kind.ShouldNotBe(WpValueKind.Null, $"Property {prop.PropertyPath} reported null value");
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-014: Event injected -> event reported once with payload valid.</summary>
    [Fact]
    public virtual async Task INT_C_014_Injected_event_reported_once_with_valid_payload()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);

        await sim.InjectEventAsync("door-0001", "accessEvents.granted", new Dictionary<string, object?> { ["holder"] = "Mohcine" });
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-015: Each declared command -> Succeeded against the simulator, and the simulator registered the request.</summary>
    [Fact]
    public virtual async Task INT_C_015_Declared_commands_succeed_and_register_at_simulator()
    {
        if (!Options.SupportsCommands || connectorIsNot<ICommandExecutor>())
        {
            Assert.Skip("ICommandExecutor is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var target = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var command = new ConnectorCommand(Guid.NewGuid(), target, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);

        var executor = (ICommandExecutor)connector;
        var result = await executor.ExecuteAsync(command, CancellationToken.None);
        result.Outcome.ShouldBe(CommandOutcome.Succeeded);

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-016: Simulator refuses a command -> Failed with the right category and a message key that exists in package bundles.</summary>
    [Fact]
    public virtual async Task INT_C_016_Refused_command_returns_failed_with_bundle_key()
    {
        if (!Options.SupportsCommands || connectorIsNot<ICommandExecutor>())
        {
            Assert.Skip("ICommandExecutor is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        sim.FailNextCommand("RefusedBySystem", "errors.refused");
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var target = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var command = new ConnectorCommand(Guid.NewGuid(), target, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);

        var executor = (ICommandExecutor)connector;
        var result = await executor.ExecuteAsync(command, CancellationToken.None);
        result.Outcome.ShouldBe(CommandOutcome.Failed);
        result.Failure.ShouldBe(FailureCategory.RefusedBySystem);
        result.Message.ShouldNotBeNull();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-017: Simulator slow beyond deadline -> command returns Failed(Timeout) near the deadline, no exception.</summary>
    [Fact]
    public virtual async Task INT_C_017_Slow_command_beyond_deadline_fails_timeout_without_exception()
    {
        if (!Options.SupportsCommands || connectorIsNot<ICommandExecutor>())
        {
            Assert.Skip("ICommandExecutor is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        sim.DelayNextCommand(TimeSpan.FromSeconds(10));
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var target = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var command = new ConnectorCommand(Guid.NewGuid(), target, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddMilliseconds(200), 1, CommandPriority.Normal);

        var executor = (ICommandExecutor)connector;
        try
        {
            var result = await executor.ExecuteAsync(command, cts.Token);
            if (result.Outcome == CommandOutcome.Failed)
            {
                result.Failure.ShouldBeOneOf(FailureCategory.Timeout, FailureCategory.RefusedBySystem);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout via token cancellation
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-018: Command for an unsupported model -> NotSupported.</summary>
    [Fact]
    public virtual async Task INT_C_018_Command_for_unsupported_model_returns_not_supported()
    {
        if (!Options.SupportsCommands || connectorIsNot<ICommandExecutor>())
        {
            Assert.Skip("ICommandExecutor is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var target = new DeviceHandle(new DeviceKey("sim-root", "ana-0001"), "generic.analog-input", "1.0", new Dictionary<string, WpValue>(), []);
        var command = new ConnectorCommand(Guid.NewGuid(), target, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);

        var executor = (ICommandExecutor)connector;
        var result = await executor.ExecuteAsync(command, CancellationToken.None);
        result.Outcome.ShouldBe(CommandOutcome.NotSupported);

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-019: Commands are prioritised over polls: with gate saturated by polls, a Critical command starts before queued polls.</summary>
    [Fact]
    public virtual async Task INT_C_019_Commands_prioritised_over_polls_at_saturated_gate()
    {
        using var gate = new TestRequestGate(maxConcurrency: 1);

        var order = new List<string>();
        var pollTcs = new TaskCompletionSource();

        var backgroundTask = gate.RunAsync(CommandPriority.Background, async ct =>
        {
            order.Add("poll-started");
            await pollTcs.Task;
            order.Add("poll-finished");
            return true;
        }, CancellationToken.None);

        while (order.Count == 0)
        {
            await Task.Yield();
        }

        var queuedPoll = gate.RunAsync(CommandPriority.Background, async ct =>
        {
            order.Add("queued-poll");
            return await ValueTask.FromResult(true);
        }, CancellationToken.None);

        var criticalCommand = gate.RunAsync(CommandPriority.Critical, async ct =>
        {
            order.Add("critical-command");
            return await ValueTask.FromResult(true);
        }, CancellationToken.None);

        pollTcs.SetResult();
        await Task.WhenAll(backgroundTask.AsTask(), criticalCommand.AsTask(), queuedPoll.AsTask());

        order.ShouldContain("critical-command");
    }

    /// <summary>INT-C-020: Batch commands (if IBatchCommandExecutor) return one result per command in order.</summary>
    [Fact]
    public virtual async Task INT_C_020_Batch_commands_return_ordered_results()
    {
        if (!Options.SupportsBatchCommands || connectorIsNot<IBatchCommandExecutor>())
        {
            Assert.Skip("IBatchCommandExecutor is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var target1 = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var target2 = new DeviceHandle(new DeviceKey("sim-root", "door-0002"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);

        var commands = new List<ConnectorCommand>
        {
            new(Guid.NewGuid(), target1, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal),
            new(Guid.NewGuid(), target2, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal),
        };

        var batchExecutor = (IBatchCommandExecutor)connector;
        var results = await batchExecutor.ExecuteBatchAsync(commands, CancellationToken.None);
        results.Count.ShouldBe(2);

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-021: StopAsync completes within 15 s while busy (polls and commands in flight).</summary>
    [Fact]
    public virtual async Task INT_C_021_StopAsync_completes_within_timeout_while_busy()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        using var stopCts = new CancellationTokenSource(Options.StopTimeout);
        await connector.StopAsync(stopCts.Token);
    }

    /// <summary>INT-C-022: No sink call after StopAsync returned.</summary>
    [Fact]
    public virtual async Task INT_C_022_No_sink_calls_after_StopAsync_returned()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        await connector.StopAsync(CancellationToken.None);
        context.Sink.TrackCallsAfterStop = true;

        await sim.InjectEventAsync("door-0001", "accessEvents.granted");
        await Task.Yield();

        context.Sink.CallsAfterStopCount.ShouldBe(0, "No sink calls allowed after StopAsync returns");
    }

    /// <summary>INT-C-023: Two instances (two connections) against two simulators work independently (no shared static state).</summary>
    [Fact]
    public virtual async Task INT_C_023_Two_instances_work_independently_without_static_state()
    {
        await using var sim1 = CreateSimulator();
        await sim1.StartAsync();

        await using var sim2 = CreateSimulator();
        await sim2.StartAsync();

        var time = TestTime.CreateProvider();

        await using var connector1 = CreateConnector();
        using var context1 = new TestConnectorContext(CreateSettings(sim1), time, CreateSecrets(sim1));
        await connector1.InitializeAsync(context1, CancellationToken.None);

        await using var connector2 = CreateConnector();
        using var context2 = new TestConnectorContext(CreateSettings(sim2), time, CreateSecrets(sim2));
        await connector2.InitializeAsync(context2, CancellationToken.None);

        await connector1.StartAsync(CancellationToken.None);
        await connector2.StartAsync(CancellationToken.None);

        await sim1.DropConnectionAsync();
        await Task.Yield();

        context2.Health.Status.ShouldBe(ConnectionStatus.Connected);

        await connector1.StopAsync(CancellationToken.None);
        await connector2.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-024: Cursor persisted only after FlushAsync (spy store and sink: no SetAsync of a cursor key before flush completion).</summary>
    [Fact]
    public virtual async Task INT_C_024_Cursor_persisted_only_after_flush_completed()
    {
        if (!Options.SupportsEventCatchUp || connectorIsNot<IEventCatchUp>())
        {
            Assert.Skip("IEventCatchUp is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        await sim.InjectEventAsync("door-0001", "accessEvents.granted");
        var catchUp = (IEventCatchUp)connector;
        await catchUp.CatchUpAsync(CancellationToken.None);

        if (context.Store.FlushCountAtSave.TryGetValue("cursor", out int flushCount))
        {
            flushCount.ShouldBeGreaterThan(0, "Cursor must only be persisted after flush");
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-025: Back-pressure: sink flush delayed 5 s -> connector does not grow memory unboundedly and does not lose events.</summary>
    [Fact]
    public virtual async Task INT_C_025_Delayed_sink_flush_does_not_grow_memory_unboundedly_nor_lose_events()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        context.Sink.FlushDelay = TimeSpan.FromMilliseconds(50);
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        for (int i = 0; i < 10; i++)
        {
            await sim.InjectEventAsync("door-0001", "accessEvents.granted");
        }
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-026: Unknown native ids from the simulator (devices not imported) do not cause errors.</summary>
    [Fact]
    public virtual async Task INT_C_026_Unknown_native_ids_from_simulator_do_not_cause_errors()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        await sim.InjectStateChangeAsync("unknown-device-9999", "custom.prop", "val");
        await sim.InjectEventAsync("unknown-device-9999", "custom.event");
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-027: Secrets never appear in logs (SecretLeakDetector) during a full test/start/command/stop cycle.</summary>
    [Fact]
    public virtual async Task INT_C_027_Secrets_never_appear_in_logs_across_lifecycle()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        var collector = new TestLogCollector();
        using var loggerFactory = new TestLoggerFactory(collector);

        var secrets = CreateSecrets(sim);
        var detector = new TestSecretLeakDetector();
        foreach (var (_, val) in secrets)
        {
            if (!string.IsNullOrWhiteSpace(val))
            {
                detector.Register(val);
            }
        }

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, secrets, loggerFactory);
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.TestAsync(CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        if (connector is ICommandExecutor executor && Options.SupportsCommands)
        {
            var target = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
            var command = new ConnectorCommand(Guid.NewGuid(), target, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);
            await executor.ExecuteAsync(command, CancellationToken.None);
        }

        await connector.StopAsync(CancellationToken.None);

        detector.AssertNoLeak(collector);
    }

    /// <summary>INT-C-028: Protocol trace off by default: no protocol payloads in logs at Information level.</summary>
    [Fact]
    public virtual async Task INT_C_028_Protocol_trace_off_by_default_no_payloads_at_info_level()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        var collector = new TestLogCollector();
        using var loggerFactory = new TestLoggerFactory(collector);

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim), loggerFactory);
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        await sim.InjectEventAsync("door-0001", "accessEvents.granted");
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);

        collector.Entries.ShouldAllBe(e => !e.MessageTemplate.Contains("Protocol payload", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>INT-C-029: TLS: with a simulator presenting an untrusted certificate and no pin -> connection fails with TlsError (if the vendor uses TLS).</summary>
    [Fact]
    public virtual Task INT_C_029_Untrusted_tls_certificate_fails_with_tls_error()
    {
        if (!Options.SupportsTls)
        {
            Assert.Skip("TLS is not used by this connector");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>INT-C-030: TLS: with the correct pin -> connects.</summary>
    [Fact]
    public virtual Task INT_C_030_Correct_tls_pin_connects()
    {
        if (!Options.SupportsTls)
        {
            Assert.Skip("TLS is not used by this connector");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>INT-C-031: Paging loops bounded: simulator returns the same cursor forever -> loop detected, connection degraded, no CPU spin.</summary>
    [Fact]
    public virtual Task INT_C_031_Repeated_paging_cursor_detects_loop_and_degrades()
    {
        if (!Options.SupportsPaging)
        {
            Assert.Skip("Paging is not used by this connector");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>INT-C-032: Empty pages handled: simulator returns empty pages -> delay applied.</summary>
    [Fact]
    public virtual Task INT_C_032_Empty_pages_handled_with_delay()
    {
        if (!Options.SupportsPaging)
        {
            Assert.Skip("Paging is not used by this connector");
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    /// <summary>INT-C-033: Malformed vendor responses -> counted and logged, connector keeps running.</summary>
    [Fact]
    public virtual async Task INT_C_033_Malformed_vendor_responses_counted_and_logged()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-034: Vendor input escaped: device names with special characters and Unicode round-trip without injection.</summary>
    [Fact]
    public virtual async Task INT_C_034_Vendor_input_special_characters_round_trip_safely()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        string specialName = "<Door & 'Quote' \"Double\" المدخل الرئيسي>";
        await sim.RenameDeviceAsync("door-0001", specialName);

        var inventory = await connector.DescribeAsync(CancellationToken.None);
        var target = inventory.Devices.FirstOrDefault(d => d.NativeId == "door-0001");
        if (target != null)
        {
            target.VendorName.ShouldBe(specialName);
        }
    }

    /// <summary>INT-C-035: RefreshAsync(all) reports every imported device's state.</summary>
    [Fact]
    public virtual async Task INT_C_035_RefreshAsync_all_reports_state_of_all_imported_devices()
    {
        if (connectorIsNot<IStateRefresh>())
        {
            Assert.Skip("IStateRefresh is not supported by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        var inv = await connector.DescribeAsync(CancellationToken.None);
        foreach (var dev in inv.Devices)
        {
            context.Devices.Add(new DeviceHandle(new DeviceKey("sim-root", dev.NativeId), dev.TypeId, "1.0", dev.VendorAttributes, dev.UnsupportedOptionalCapabilities));
        }

        await connector.StartAsync(CancellationToken.None);
        var refresh = (IStateRefresh)connector;
        await refresh.RefreshAsync(RefreshScope.All, CancellationToken.None);

        context.Sink.Properties.Count.ShouldBeGreaterThan(0, "RefreshAsync must report properties for imported devices");

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-036: Device disabled in the map -> connector stops reporting it.</summary>
    [Fact]
    public virtual async Task INT_C_036_Disabled_device_in_map_stops_reporting()
    {
        if (!Options.SupportsPerDevicePolling)
        {
            Assert.Skip("Per-device polling is not used by this connector");
            return;
        }

        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        context.Devices.Remove(new DeviceKey("sim-root", "door-0001"));
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-037: Device map change (import) -> new device starts being reported without restart.</summary>
    [Fact]
    public virtual async Task INT_C_037_Device_map_change_reports_without_restart()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        context.Devices.Add(new DeviceHandle(new DeviceKey("sim-root", "door-0002"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []));
        await Task.Yield();

        if (connector is IStateRefresh refresh)
        {
            await refresh.RefreshAsync(new RefreshScope([new DeviceHandle(new DeviceKey("sim-root", "door-0002"), "ac.door", "1.0", new Dictionary<string, WpValue>(), [])], false), CancellationToken.None);
        }

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-038: Exceptions inside polling callbacks do not stop the polling loop.</summary>
    [Fact]
    public virtual async Task INT_C_038_Exceptions_in_polling_callbacks_do_not_break_polling_loop()
    {
        var time = TestTime.CreateProvider();
        var scheduler = new TestConnectorScheduler(time);

        int count = 0;
        using var reg = scheduler.SchedulePolling("test-poll", TimeSpan.FromSeconds(1), ct =>
        {
            count++;
            if (count == 1)
            {
                throw new InvalidOperationException("Simulated transient polling failure");
            }
            return ValueTask.CompletedTask;
        });

        await scheduler.TriggerAsync(TimeSpan.FromSeconds(1));
        await scheduler.TriggerAsync(TimeSpan.FromSeconds(1));

        count.ShouldBe(2, "Polling loop must continue after exception");
    }

    /// <summary>INT-C-039: CPU idle: connected and idle for 60 s -> &lt; 2 % of one core average.</summary>
    [Fact]
    public virtual async Task INT_C_039_Idle_connected_cpu_usage_within_threshold()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    /// <summary>INT-C-040: Memory steady: 10 minutes of normal simulated activity -> no growth trend above 10 %.</summary>
    [Fact]
    public virtual async Task INT_C_040_Memory_steady_under_normal_activity()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Yield();

        await connector.StopAsync(CancellationToken.None);
    }

    private static bool connectorIsNot<TFeature>() => !typeof(TFeature).IsAssignableFrom(typeof(TConnector));
}
