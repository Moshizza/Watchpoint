using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Host.Modules;
using Watchpoint.Identity.Contracts;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Testing;
using Watchpoint.IntegrationTests.Integrations;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;
using CommandPriority = Watchpoint.Commands.Contracts.CommandPriority;

namespace Watchpoint.IntegrationTests.Commands;

/// <summary>
/// Command dispatch end to end (spec CMD §15, CMD-I-001…008, T2.02): a node with the runtime and the commands modules on
/// PostgreSQL and NATS, the simulator as the connection. Rights: every command is allowed (RGT is M3).
/// </summary>
[Trait("layer", "I")]
[Trait("spec", "CMD")]
public sealed class CommandIntegrationTests(PostgresContainerFixture postgres)
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(60);
    private static readonly CommandPath _open = CommandPath.FromTrusted("openable.open");
    private static readonly string[] _languages = ["en", "fr", "ar"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CMD_I_001_Open_door_on_the_simulator_succeeds_with_its_records_in_the_log()
    {
        await using var node = await StartAsync("i001");
        await Diagnosed(node, async () =>
        {
            var (_, doors) = await SimulatorAsync(node, new { profile = "office", doors = 2, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm" });
            var door = doors[0];

            var id = await RequestAsync(node, door);

            var done = await UntilAsync(node, id, c => c.State == CommandState.Succeeded);
            done.Attempt.ShouldBe(1);
            var kinds = (await node.ReadLogAsync(Ct)).Where(r => r.Envelope.Corr.Command == id).Select(r => r.Envelope.Kind).ToList();
            kinds.ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.Succeeded]);

            // The door really opened in the simulator: its position reached the log (verification itself is T2.03).
            await PollAsync(async () => (await node.ReadLogAsync(Ct)).Any(r => r.Envelope.Kind == RecordKinds.Property && r.Envelope.Dev == door
                && r.Envelope.Body.ToString().Contains("\"open\"", StringComparison.Ordinal)), "door position open");
        });
    }

    [Fact]
    public async Task CMD_I_002_Simulator_refusal_fails_with_a_package_message_key_resolvable_in_en_fr_ar()
    {
        await using var node = await StartAsync("i002");
        await Diagnosed(node, async () =>
        {
            var failures = new[] { new { command = "openable.open", mode = "refuse", probability = 1.0 } };
            var (_, doors) = await SimulatorAsync(node, new { profile = "office", doors = 1, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm", failures });

            var id = await RequestAsync(node, doors[0]);

            var failure = (await UntilAsync(node, id, c => c.State == CommandState.Failed)).Failure!;
            failure.Category.ShouldBe(FailureCategory.RefusedBySystem);
            failure.MessageKey.ShouldBe("packages." + SimulatorPackage.Id + ".errors.refused");
            foreach (var language in _languages)
            {
                var catalog = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "i18n", language + ".json"), Ct))!;
                var text = failure.MessageKey.Split('.').Aggregate((JsonNode?)catalog, (n, part) => n?[part]);
                text.ShouldNotBeNull($"{failure.MessageKey} in {language}").GetValue<string>().ShouldNotBeNullOrWhiteSpace();
            }
        });
    }

    [Fact]
    public async Task CMD_I_003_Simulator_timeout_mode_gives_TimedOut_at_the_deadline()
    {
        await using var node = await StartAsync("i003");
        await Diagnosed(node, async () =>
        {
            var failures = new[] { new { command = "openable.open", mode = "timeout", probability = 1.0 } };
            var (_, doors) = await SimulatorAsync(node, new { profile = "office", doors = 1, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm", failures });

            var id = await RequestAsync(node, doors[0], timeout: TimeSpan.FromSeconds(2));

            var timedOut = await UntilAsync(node, id, c => c.State == CommandState.TimedOut);
            (timedOut.CompletedAt!.Value - timedOut.DispatchedAt!.Value).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2) - TimeSpan.FromMilliseconds(5));
            (await node.ReadLogAsync(Ct)).Where(r => r.Envelope.Corr.Command == id).Select(r => r.Envelope.Kind)
                .ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.TimedOut]);
        });
    }

    [Fact]
    public async Task CMD_I_006_Disconnected_connection_fails_fast()
    {
        await using var node = await StartAsync("i006");
        await Diagnosed(node, async () =>
        {
            var (connection, doors) = await SimulatorAsync(node, new { profile = "office", doors = 1, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm" });
            (await node.Connections.StopAsync(connection, Ct)).IsSuccess.ShouldBeTrue();
            await Eventually.UntilAsync(() => node.Connections.GetStatus(connection).State == ConnectionState.Disabled, _wait, "stopped", ct: Ct);

            var id = await RequestAsync(node, doors[0]);

            var failure = (await UntilAsync(node, id, c => c.State == CommandState.Failed)).Failure!;
            failure.Category.ShouldBe(FailureCategory.ConnectionDown);
            failure.MessageKey.ShouldBe("errors.commands.connection.down");
            (await node.ReadLogAsync(Ct)).ShouldNotContain(r => r.Envelope.Corr.Command == id && r.Envelope.Kind == CommandRecordKinds.Dispatched);
        });
    }

    [Fact]
    public async Task CMD_I_008_Critical_command_dispatched_before_500_queued_Background_commands()
    {
        await using var node = await StartAsync("i008");
        await Diagnosed(node, async () =>
        {
            var (_, doors) = await SimulatorAsync(node,
                new { profile = "office", doors = 20, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm", commandLatency = "PT0.2S" });
            var reserved = doors[0];
            var others = doors.Skip(1).ToList();
            var background = new List<CommandId>();
            for (var i = 0; i < 500; i++)
            {
                background.Add(await RequestAsync(node, others[i % others.Count], CommandPriority.Background));
            }

            var critical = await RequestAsync(node, reserved, CommandPriority.Critical);

            await UntilAsync(node, critical, c => c.State == CommandState.Succeeded);
            var log = await node.ReadLogAsync(Ct);
            var criticalRequested = log.First(r => r.Envelope.Corr.Command == critical).Sequence;
            var criticalDispatched = log.Single(r => r.Envelope.Corr.Command == critical && r.Envelope.Kind == CommandRecordKinds.Dispatched);
            var backgroundSet = background.ToHashSet();
            var overtaking = log.Count(r => r.Envelope.Kind == CommandRecordKinds.Dispatched && r.Envelope.Corr.Command is { } c && backgroundSet.Contains(c)
                && r.Envelope.Ts >= log.First(x => x.Sequence == criticalRequested).Envelope.Ts && r.Envelope.Ts < criticalDispatched.Envelope.Ts);
            overtaking.ShouldBeLessThanOrEqualTo(8, "at most the in-flight slots that were already taken may start before the critical command");
            var stillQueued = background.Count(id => node.Services.GetRequiredService<ICommandService>().Get(id).Value.State == CommandState.Queued);
            stillQueued.ShouldBeGreaterThan(100, "the critical command did not wait for the background backlog");
        });
    }

    private Task<ConnectionNode> StartAsync(string name) =>
        ConnectionNode.StartAsync(postgres, "cmd-" + name, new NodeOptions(
            [new CommandsModule()],
            new Dictionary<string, string?>(),
            services =>
            {
                services.TryAddSingleton<ICommandAuthorizer, AllowAll>();
                services.TryAddSingleton<IStepUpValidator, NoStepUp>();
            }), Ct);

    private static async Task Diagnosed(ConnectionNode node, Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (Exception ex) when (ex is ShouldAssertException or TimeoutException or Watchpoint.Testing.TestAssertionException)
        {
            throw new InvalidOperationException(ex.Message + Environment.NewLine + "Node problems:" + Environment.NewLine + node.Problems(), ex);
        }
    }

    private static async Task<(ConnectionId Connection, List<DeviceId> Doors)> SimulatorAsync(ConnectionNode node, object settings)
    {
        using var http = node.Http();
        var created = await http.PostAsJsonAsync("api/v1/connections", new
        {
            name = "Simulator",
            packageId = SimulatorPackage.Id,
            serverType = "sim.server",
            settings,
            locationId = node.Root.Value,
            autoImport = true,
            startNow = true,
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var id = ConnectionId.From(Guid.Parse(JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!["id"]!.GetValue<string>()));
        await Eventually.UntilAsync(() => node.Connections.GetStatus(id).State == ConnectionState.Connected, _wait, "connected", ct: Ct);
        var doors = await Eventually.UntilAsync(() =>
            node.Registry.QueryDevices(new DeviceQuery(), new PageRequest(PageRequest.MaxLimit)).Items.Where(d => d.Type.Id.Value == "ac.door")
                .Select(d => d.Id).ToList() is { Count: > 0 } found ? found : null, _wait, "doors imported", ct: Ct);
        return (id, doors);
    }

    private static async Task<CommandId> RequestAsync(ConnectionNode node, DeviceId door, CommandPriority? priority = null, TimeSpan? timeout = null)
    {
        var service = node.Services.GetRequiredService<ICommandService>();
        var issuer = new CommandIssuer.User(UserId.From(Guid.CreateVersion7()), SessionId.From(Guid.CreateVersion7()), ClientKind.Desktop, "ws-1");
        var request = new CommandRequest(new CommandTargetSpec.Device(door), _open, JsonDocument.Parse("{}").RootElement.Clone(), issuer,
            Reason: "integration test", StepUpToken: null, ClientCommandId: null, priority, NotBefore: null, timeout, Correlation: null);
        var accepted = await service.RequestAsync(request, Ct);
        accepted.IsSuccess.ShouldBeTrue(accepted.IsSuccess ? null : accepted.Error.Code.Value);
        return accepted.Value.Id;
    }

    private static async Task<Command> UntilAsync(ConnectionNode node, CommandId id, Func<Command, bool> done)
    {
        var service = node.Services.GetRequiredService<ICommandService>();
        return await Eventually.UntilAsync(() => service.Get(id) is { IsSuccess: true } found && done(found.Value) ? found.Value : null,
            _wait, $"command {id} state {service.Get(id).Value.State}", ct: Ct);
    }

    private static async Task PollAsync(Func<Task<bool>> condition, string because)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!await condition())
        {
            if (TimeProvider.System.GetElapsedTime(started) > _wait)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
        }
    }

    /// <summary>Every command allowed under no constraint, raising to critical included (RGT arrives in M3).</summary>
    private sealed class AllowAll : ICommandAuthorizer
    {
        public CommandAuthorization Authorize(CommandIssuer issuer, Device device, CommandDefinition command, Risk effectiveRisk,
            IReadOnlyDictionary<string, WpValue> arguments, DateTimeOffset now) => CommandAuthorization.Allow(new CommandConstraints(MayRaiseToCritical: true));

        public bool CanApprove(UserId approver, Command command, DateTimeOffset now) => true;
    }

    private sealed class NoStepUp : IStepUpValidator
    {
        public Result Validate(string stepUpToken, SessionId session) => Result.Success();
    }
}
