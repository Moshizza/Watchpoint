using System.Text.Json;
using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class CommandExecutorTests
{
    [Fact]
    public async Task SIM_U_016_Open_command_success_with_latency()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new
        {
            mode = "embedded",
            doors = 1,
            profile = "custom",
            commandLatency = "PT1S",
        });

        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var cmd = new ConnectorCommand(
            Guid.NewGuid(),
            handle,
            "openable.open",
            new Dictionary<string, WpValue> { ["duration"] = WpValue.Int(5) },
            time.GetUtcNow().AddSeconds(10),
            1,
            CommandPriority.Normal);

        var execTask = connector.ExecuteAsync(cmd, CancellationToken.None);
        execTask.IsCompleted.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await execTask;

        result.Outcome.ShouldBe(CommandOutcome.Succeeded);
        context.Sink.Events.Any(e => e.EventPath == "openable.opened").ShouldBeTrue();
    }

    [Fact]
    public async Task SIM_U_017_Open_refused_when_lockedDown()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 1, profile = "custom" });
        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        connector.EmbeddedSite!.GetDeviceState("door-0001")!.Properties["doorMode.mode"] = "lockedDown";

        var cmd = new ConnectorCommand(
            Guid.NewGuid(),
            handle,
            "openable.open",
            new Dictionary<string, WpValue>(),
            time.GetUtcNow().AddSeconds(10),
            1,
            CommandPriority.Normal);

        var result = await connector.ExecuteAsync(cmd, CancellationToken.None);

        result.Outcome.ShouldBe(CommandOutcome.Failed);
        result.Failure.ShouldBe(FailureCategory.InvalidState);
        result.Message!.Key.ShouldBe("errors.lockedDown");
    }

    [Fact]
    public async Task SIM_U_018_Open_on_offline_door()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 1, profile = "custom" });
        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        connector.EmbeddedSite!.GetDeviceState("door-0001")!.IsOnline = false;

        var cmd = new ConnectorCommand(
            Guid.NewGuid(),
            handle,
            "openable.open",
            new Dictionary<string, WpValue>(),
            time.GetUtcNow().AddSeconds(10),
            1,
            CommandPriority.Normal);

        var result = await connector.ExecuteAsync(cmd, CancellationToken.None);

        result.Outcome.ShouldBe(CommandOutcome.Failed);
        result.Failure.ShouldBe(FailureCategory.DeviceOffline);
    }

    [Fact]
    public async Task SIM_U_019_Arm_refused_with_open_zones()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", profile = "office" });
        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "area-001"), "intrusion.area", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var zoneState = connector.EmbeddedSite!.GetDeviceState("zone-001-01")!;
        zoneState.Properties["zone.state"] = "active";

        var cmd = new ConnectorCommand(
            Guid.NewGuid(),
            handle,
            "armable.arm",
            new Dictionary<string, WpValue> { ["mode"] = WpValue.String("full") },
            time.GetUtcNow().AddSeconds(10),
            1,
            CommandPriority.Normal);

        var result = await connector.ExecuteAsync(cmd, CancellationToken.None);

        result.Outcome.ShouldBe(CommandOutcome.Failed);
        result.Failure.ShouldBe(FailureCategory.InvalidState);
        result.Message!.Key.ShouldBe("errors.zonesOpen");
        context.Sink.Events.Any(e => e.EventPath == "armable.armFailed").ShouldBeTrue();
    }

    [Fact]
    public async Task SIM_U_020_Arm_succeeds_when_zones_bypassed()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", profile = "office" });
        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "area-001"), "intrusion.area", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var zoneState = connector.EmbeddedSite!.GetDeviceState("zone-001-01")!;
        zoneState.Properties["zone.state"] = "active";
        zoneState.IsBypassed = true;

        var cmd = new ConnectorCommand(
            Guid.NewGuid(),
            handle,
            "armable.arm",
            new Dictionary<string, WpValue> { ["mode"] = WpValue.String("full") },
            time.GetUtcNow().AddSeconds(10),
            1,
            CommandPriority.Normal);

        var result = await connector.ExecuteAsync(cmd, CancellationToken.None);

        result.Outcome.ShouldBe(CommandOutcome.Succeeded);
    }

    [Fact]
    public async Task SIM_U_021_Failure_injection_refuse_timeout_error()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new
        {
            mode = "embedded",
            doors = 1,
            failures = new[]
            {
                new { command = "openable.open", mode = "refuse", probability = 1.0 },
            },
        });
        var context = new TestConnectorContext(json, time);
        var handle = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(handle);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var cmd = new ConnectorCommand(Guid.NewGuid(), handle, "openable.open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);
        var resRefuse = await connector.ExecuteAsync(cmd, CancellationToken.None);
        resRefuse.Outcome.ShouldBe(CommandOutcome.Failed);
        resRefuse.Failure.ShouldBe(FailureCategory.RefusedBySystem);

        connector.EmbeddedSite!.FailureInjector.SetRules([new FailureRule("openable.open", FailureMode.Timeout, 1.0)]);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(async () => await connector.ExecuteAsync(cmd, cts.Token));

        connector.EmbeddedSite!.FailureInjector.SetRules([new FailureRule("openable.open", FailureMode.Error, 1.0)]);
        await Should.ThrowAsync<InvalidOperationException>(async () => await connector.ExecuteAsync(cmd, CancellationToken.None));
    }

    [Fact]
    public async Task SIM_U_022_Batch_commands_return_one_result_per_command()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 2 });
        var context = new TestConnectorContext(json, time);
        var h1 = new DeviceHandle(new DeviceKey("sim-root", "door-0001"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var h2 = new DeviceHandle(new DeviceKey("sim-root", "door-0002"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        context.Devices.Add(h1);
        context.Devices.Add(h2);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var cmd1 = new ConnectorCommand(Guid.NewGuid(), h1, "lockable.lock", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);
        var cmd2 = new ConnectorCommand(Guid.NewGuid(), h2, "lockable.unlock", new Dictionary<string, WpValue>(), time.GetUtcNow().AddSeconds(5), 1, CommandPriority.Normal);

        var batchResults = await connector.ExecuteBatchAsync([cmd1, cmd2], CancellationToken.None);

        batchResults.Count.ShouldBe(2);
        batchResults[0].Outcome.ShouldBe(CommandOutcome.Succeeded);
        batchResults[1].Outcome.ShouldBe(CommandOutcome.Succeeded);
    }
}
