using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Commands.Internal.Dispatch;
using Watchpoint.Host.Modules;
using Watchpoint.Hosting;
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
    public async Task CMD_I_001_Open_door_on_the_simulator_succeeds_then_verifies_with_its_records_in_the_log()
    {
        await using var node = await StartAsync("i001");
        await Diagnosed(node, async () =>
        {
            var (_, doors) = await SimulatorAsync(node, new { profile = "office", doors = 2, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm" });
            var door = doors[0];

            var id = await RequestAsync(node, door);

            // The door really opened in the simulator: its position reached the log and verified the expected effect (CMD §9).
            var done = await UntilAsync(node, id, c => c.State == CommandState.Verified);
            done.Attempt.ShouldBe(1);
            var records = (await node.ReadLogAsync(Ct)).Where(r => r.Envelope.Corr.Command == id).ToList();
            records.Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Dispatched,
                CommandRecordKinds.Succeeded, CommandRecordKinds.Verified]);
            records[^1].BodyAs<CommandVerificationBody>(node.Services.GetRequiredService<ILogRecordTypeRegistry>()).Observed.ShouldBe(WpValue.Enum("open"));
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
    public async Task CMD_I_004_Node_crash_during_dispatch_requeues_the_idempotent_command_as_attempt_2()
    {
        await using var node = await StartAsync("i004", new StateModule());
        await Diagnosed(node, async () =>
        {
            var (_, doors) = await SimulatorAsync(node,
                new { profile = "office", doors = 1, areas = 1, zonesPerArea = 1, seed = 3, activity = "calm", commandLatency = "PT8S" });
            var id = await RequestAsync(node, doors[0], timeout: TimeSpan.FromSeconds(60));
            await UntilAsync(node, id, c => c.State == CommandState.Dispatched);

            // The node dies while the connector call runs: the dispatch is gone, no outcome is ever recorded. The pending
            // store (command Dispatched) is in the final snapshot.
            await node.Services.GetRequiredService<CommandDispatch>().DisposeAsync();
            await node.RestartAsync(Ct);

            // openable.open is idempotent and this was attempt 1: requeued as attempt 2 and dispatched again (CMD §8).
            var done = await UntilAsync(node, id, c => c.State == CommandState.Verified);
            done.Attempt.ShouldBe(2);
            var records = (await node.ReadLogAsync(Ct)).Where(r => r.Envelope.Corr.Command == id).ToList();
            records.Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Dispatched,
                CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.Succeeded, CommandRecordKinds.Verified]);
            var codec = node.Services.GetRequiredService<ILogRecordTypeRegistry>();
            records[3].BodyAs<CommandQueuedBody>(codec).Attempt.ShouldBe(2);
            records[4].BodyAs<CommandDispatchedBody>(codec).Attempt.ShouldBe(2);
        });
    }

    [Fact]
    public async Task CMD_I_005_Thousand_door_group_batched_all_verified_with_progress_records()
    {
        var batches = new BatchCounter();
        await using var node = await StartAsync("i005", batches);
        await Diagnosed(node, async () =>
        {
            // The campus profile has 400 doors; a custom site gives the 1,000 doors of the test.
            var (connection, doors) = await SimulatorAsync(node, new
            {
                profile = "custom",
                doors = 1_000,
                readersPerDoor = 0,
                areas = 1,
                zonesPerArea = 1,
                inputs = 0,
                outputs = 0,
                analogs = 0,
                seed = 5,
                activity = "calm",
                commandLatency = "PT0S",
            }, expectedDoors: 1_000);
            doors.Count.ShouldBe(1_000);

            // Queued while the connection is stopped (an automation's children wait), dispatched in batches once it is up.
            (await node.Connections.StopAsync(connection, Ct)).IsSuccess.ShouldBeTrue();
            var service = node.Services.GetRequiredService<ICommandService>();
            var issuer = new CommandIssuer.Automation(AutomationId.From(Guid.CreateVersion7()), AutomationRunId.From(Guid.CreateVersion7()),
                UserId.From(Guid.CreateVersion7()));
            var request = new CommandRequest(new CommandTargetSpec.Devices(doors), _open, JsonDocument.Parse("{}").RootElement.Clone(), issuer,
                "campus drill", null, null, null, null, TimeSpan.FromSeconds(60), null);
            var accepted = await service.RequestAsync(request, Ct);
            accepted.IsSuccess.ShouldBeTrue(accepted.IsSuccess ? null : accepted.Error.Code.Value);
            var group = accepted.Value;
            group.IsGroup.ShouldBeTrue();
            group.TargetCount.ShouldBe(1_000);
            (await node.Connections.StartAsync(connection, Ct)).IsSuccess.ShouldBeTrue();

            var done = await Eventually.UntilAsync(() => service.GetGroup(group.Id).Value is { CompletedAt: not null } g ? g : null,
                TimeSpan.FromSeconds(120), "group completed", ct: Ct);
            done.Counters.ShouldBe(new CommandGroupCounters(1_000, 1_000, 0, 0, 1_000, 0, 0, 0));
            batches.Sizes.Sum().ShouldBe(1_000, "every child went in a batch");
            batches.Sizes.ShouldAllBe(n => n <= 100, "chunks of Commands:BatchSize");

            // WP_SYS: requested, then progress records (throttled), then completed.
            var sys = await ReadSysAsync(node, group.Id);
            sys[0].Envelope.Kind.ShouldBe(CommandGroupRecordKinds.Requested);
            sys[^1].Envelope.Kind.ShouldBe(CommandGroupRecordKinds.Completed);
            var progress = sys.Where(r => r.Envelope.Kind == CommandGroupRecordKinds.Progress).ToList();
            progress.ShouldNotBeEmpty();
            for (var i = 1; i < progress.Count; i++)
            {
                (progress[i].Envelope.Ts - progress[i - 1].Envelope.Ts).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(499));
            }
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

            await UntilAsync(node, critical, c => c.State is CommandState.Succeeded or CommandState.Verified);
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

    private Task<ConnectionNode> StartAsync(string name, params IWatchpointModule[] before) => StartAsync(name, null, before);

    private Task<ConnectionNode> StartAsync(string name, BatchCounter? batches, params IWatchpointModule[] before) =>
        ConnectionNode.StartAsync(postgres, "cmd-" + name, new NodeOptions(
            [.. before, new CommandsModule()],
            new Dictionary<string, string?>(),
            services =>
            {
                services.TryAddSingleton<ICommandAuthorizer, AllowAll>();
                services.TryAddSingleton<IStepUpValidator, NoStepUp>();
            },
            batches is null ? null : services =>
            {
                // Counts the batches the dispatch sends; the runtime's channel does the work.
                services.AddSingleton<IConnectionCommandChannel>(sp => batches.Wrap(sp.GetRequiredService<IConnectionManager>() as IConnectionCommandChannel
                    ?? throw new InvalidOperationException("The connection manager is the command channel.")));
            }), Ct);

    private static async Task<List<LogRecord>> ReadSysAsync(ConnectionNode node, CommandId group)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var records = new List<LogRecord>();
        var reader = node.Services.GetRequiredService<IEventLogReader>();
        await foreach (var record in reader.ReadGlobalAsync(LogStream.Sys, 1, timeout.Token))
        {
            if (record.Envelope.Corr.Command == group)
            {
                records.Add(record);
            }
        }

        return records;
    }

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

    private static async Task<(ConnectionId Connection, List<DeviceId> Doors)> SimulatorAsync(ConnectionNode node, object settings, int expectedDoors = 1)
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
        var doors = await Eventually.UntilAsync(() => Doors(node) is { } found && found.Count >= expectedDoors ? found : null, _wait, "doors imported", ct: Ct);
        return (id, doors);
    }

    /// <summary>Every imported door (all registry pages).</summary>
    private static List<DeviceId> Doors(ConnectionNode node)
    {
        var doors = new List<DeviceId>();
        string? cursor = null;
        do
        {
            var page = node.Registry.QueryDevices(new DeviceQuery(), new PageRequest(PageRequest.MaxLimit, cursor));
            doors.AddRange(page.Items.Where(d => d.Type.Id.Value == "ac.door").Select(d => d.Id));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return doors;
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

    /// <summary>Wraps the command channel and records the size of every batch.</summary>
    private sealed class BatchCounter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _sizes = new();

        public IReadOnlyList<int> Sizes => [.. _sizes];

        public IConnectionCommandChannel Wrap(IConnectionCommandChannel inner) => new Counting(inner, _sizes);

        private sealed class Counting(IConnectionCommandChannel inner, System.Collections.Concurrent.ConcurrentQueue<int> sizes) : IConnectionCommandChannel
        {
            public ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectionId connection, ConnectorCommand command, CancellationToken ct) =>
                inner.ExecuteAsync(connection, command, ct);

            public ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(ConnectionId connection, IReadOnlyList<ConnectorCommand> commands, CancellationToken ct)
            {
                sizes.Enqueue(commands.Count);
                return inner.ExecuteBatchAsync(connection, commands, ct);
            }

            public bool SupportsBatch(ConnectionId connection) => inner.SupportsBatch(connection);
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
