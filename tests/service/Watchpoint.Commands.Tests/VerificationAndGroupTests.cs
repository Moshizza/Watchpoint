using System.Text.Json;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.Commands.Tests;

/// <summary>Verification of the expected effect and group commands (spec CMD §9–§10; T2.03).</summary>
[Trait("layer", "U")]
[Trait("spec", "CMD")]
public sealed class VerificationAndGroupTests : IAsyncDisposable
{
    private static readonly WpValue _open = WpValue.Enum("open");
    private static readonly WpValue _closed = WpValue.Enum("closed");
    private readonly CommandsHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task CMD_U_051_Verification_registered_on_success_with_an_expected_effect()
    {
        var id = await SucceedAsync(_h.Door);

        var command = _h.Command(id);
        command.State.ShouldBe(CommandState.Succeeded, "not terminal: the effect is expected");
        _h.Processor.Verifier.Count(Partitioning.Of(_h.Door)).ShouldBe(1);
        var watch = _h.Processor.Verifier.All(Partitioning.Of(_h.Door)).Single();
        watch.Id.ShouldBe(id);
        watch.Effect.Property.ShouldBe(CommandsHarness.Position);
        watch.Deadline.ShouldBe(command.CompletedAt!.Value + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CMD_U_052_Matching_property_record_verifies()
    {
        var id = await SucceedAsync(_h.Door);
        _h.Time.Advance(TimeSpan.FromMilliseconds(1200));

        await _h.PropertyAsync(_h.Door, CommandsHarness.Position, _closed);
        _h.Command(id).State.ShouldBe(CommandState.Succeeded, "a value that does not match changes nothing");
        await _h.PropertyAsync(TestIds.Device(99), CommandsHarness.Position, _open);
        _h.Command(id).State.ShouldBe(CommandState.Succeeded, "another device changes nothing");

        await _h.PropertyAsync(_h.Door, CommandsHarness.Position, _open);

        var verified = _h.Command(id);
        verified.State.ShouldBe(CommandState.Verified);
        var body = _h.RecordsOf(id).Single(r => r.Envelope.Kind == CommandRecordKinds.Verified).BodyAs<CommandVerificationBody>(CommandsHarness.Records);
        body.Observed.ShouldBe(_open);
        body.AfterMs.ShouldBe(1200);
        _h.Processor.Verifier.Count(Partitioning.Of(_h.Door)).ShouldBe(0);
    }

    [Fact]
    public async Task CMD_U_053_Already_in_the_expected_state_at_success_verifies_at_once()
    {
        _h.State.Set(_h.Door, CommandsHarness.Position, _open);

        var id = await SucceedAsync(_h.Door);

        _h.Command(id).State.ShouldBe(CommandState.Verified);
        _h.RecordsOf(id).Single(r => r.Envelope.Kind == CommandRecordKinds.Verified)
            .BodyAs<CommandVerificationBody>(CommandsHarness.Records).AfterMs.ShouldBe(0);
    }

    [Fact]
    public async Task CMD_U_054_Deadline_on_tick_gives_Unverified()
    {
        _h.State.Set(_h.Door, CommandsHarness.Position, _closed);
        var id = await SucceedAsync(_h.Door);

        _h.Time.Advance(TimeSpan.FromSeconds(4));
        await _h.TickAsync();
        _h.Command(id).State.ShouldBe(CommandState.Succeeded);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await _h.TickAsync();

        _h.Command(id).State.ShouldBe(CommandState.Unverified);
        var body = _h.RecordsOf(id).Single(r => r.Envelope.Kind == CommandRecordKinds.Unverified).BodyAs<CommandVerificationBody>(CommandsHarness.Records);
        body.Observed.ShouldBe(_closed);
        body.AfterMs.ShouldBe(5000);
        _h.Logs.WithEventId(2412).ShouldNotBeEmpty();

        // A late matching value no longer changes it.
        await _h.PropertyAsync(_h.Door, CommandsHarness.Position, _open);
        _h.Command(id).State.ShouldBe(CommandState.Unverified);
    }

    [Fact]
    public async Task CMD_U_055_In_expectation()
    {
        var door = DoorWithEffect(new ExpectedEffect(CommandsHarness.Position, In: [_open, WpValue.Enum("ajar")], Within: TimeSpan.FromSeconds(5)));
        var id = await SucceedAsync(door);

        await _h.PropertyAsync(door, CommandsHarness.Position, _closed);
        _h.Command(id).State.ShouldBe(CommandState.Succeeded);
        await _h.PropertyAsync(door, CommandsHarness.Position, WpValue.Enum("ajar"));

        _h.Command(id).State.ShouldBe(CommandState.Verified);
    }

    [Fact]
    public async Task CMD_U_056_No_expected_effect_Succeeded_is_terminal()
    {
        var id = await SucceedAsync(_h.Door, CommandsHarness.Reset);

        var command = _h.Command(id);
        command.State.ShouldBe(CommandState.Succeeded);
        CommandStateMachine.IsTerminal(command).ShouldBeTrue();
        _h.Processor.Verifier.Count(Partitioning.Of(_h.Door)).ShouldBe(0);
        _h.Time.Advance(TimeSpan.FromSeconds(61));
        await _h.TickAsync();
        _h.Processor.Store(Partitioning.Of(_h.Door)).Find(id).ShouldBeNull("finished: leaves the pending store after 60 s");
    }

    [Fact]
    public async Task CMD_U_057_Group_from_ids_and_from_selector_within_the_visible_scope()
    {
        var doors = Doors(4);

        var fromIds = await _h.AcceptAsync(Group([doors[0], doors[2], doors[0]]));

        fromIds.IsGroup.ShouldBeTrue();
        fromIds.TargetCount.ShouldBe(2, "duplicates count once");
        Children(fromIds.Id).Select(c => c.Target).ShouldBe([doors[0], doors[2]]);
        var requested = SysBody<CommandGroupRequestedBody>(CommandGroupRecordKinds.Requested, fromIds.Id);
        requested.Targets.ShouldBe([doors[0], doors[2]]);
        requested.Count.ShouldBe(2);
        requested.Path.ShouldBe(CommandsHarness.Open);
        _h.Sys.Records.First(r => r.Corr.Command == fromIds.Id).Kind.ShouldBe(CommandGroupRecordKinds.Requested, "recorded before any child");
        _h.Log.First(r => r.Envelope.Kind == CommandRecordKinds.Requested && _h.Command(r.Envelope.Corr.Command!.Value).GroupId == fromIds.Id)
            .ShouldNotBeNull();

        // The selector is resolved once, within the devices the issuer may see.
        _h.Authorizer.Visible = new HashSet<DeviceId> { doors[1], doors[3] };
        var fromSelector = await _h.AcceptAsync(_h.Request() with { Target = new CommandTargetSpec.Selector(new DeviceQuery()) });

        fromSelector.TargetCount.ShouldBe(2);
        Children(fromSelector.Id).Select(c => c.Target).ToHashSet().ShouldBe(new HashSet<DeviceId> { doors[1], doors[3] });
        _h.Service.GetGroup(fromSelector.Id).ShouldBeSuccess().Children.ShouldAllBe(c => c.State == CommandState.Queued);
    }

    [Fact]
    public async Task CMD_U_058_Group_too_large_is_rejected()
    {
        var ids = Enumerable.Range(1, 5_001).Select(n => TestIds.Device(10_000 + n)).ToList();

        var result = await _h.Service.RequestAsync(Group(ids), Ct);

        result.ShouldFailWith("commands.group.too_large");
        result.Error.Args["max"].ShouldBe(5_000);
        _h.Sys.Records.ShouldBeEmpty();
        _h.Log.ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_059_Group_risk_is_one_level_higher_than_the_highest_child()
    {
        var doors = Doors(2);

        // normal → sensitive: no reason needed.
        (await _h.Service.RequestAsync(Group(doors), Ct)).ShouldBeSuccess();

        // sensitive → critical: reason (and step-up) required for the group.
        (await _h.Service.RequestAsync(Group(doors, CommandsHarness.Reset), Ct)).ShouldFailWith("commands.reason.required");

        // critical stays critical (not above): the same safeguards.
        _h.StepUp.Valid.Add(("proof-c", _h.Session));
        (await _h.Service.RequestAsync(Group(doors, CommandsHarness.Lockdown, user: _h.Supervisor), Ct)).ShouldFailWith("commands.reason.required");
        var critical = await _h.AcceptAsync(Group(doors, CommandsHarness.Lockdown, "drill", "proof-c", _h.Supervisor));
        Children(critical.Id).ShouldAllBe(c => _h.Command(c.CommandId!.Value).EffectiveRisk == Risk.Critical);
    }

    [Fact]
    public async Task CMD_U_060_Child_validation_failures_do_not_block_the_others()
    {
        var doors = Doors(3);
        _h.AddDoor(doors[1], enabled: false);

        var group = await _h.AcceptAsync(Group(doors));

        var view = _h.Service.GetGroup(group.Id).ShouldBeSuccess();
        view.Children.Select(c => c.State).ShouldBe([CommandState.Queued, CommandState.Rejected, CommandState.Queued]);
        view.Counters.Rejected.ShouldBe(1);
        view.Counters.Pending.ShouldBe(2);
        var refused = _h.Log.Single(r => r.Envelope.Kind == CommandRecordKinds.Rejected).BodyAs<CommandRejectedBody>(CommandsHarness.Records);
        refused.GroupId.ShouldBe(group.Id);
        refused.Error.Code.ShouldBe("commands.target.disabled");
    }

    [Fact]
    public async Task CMD_U_061_Batch_dispatch_chunks_per_connection_and_maps_results_per_child()
    {
        await using var h = new CommandsHarness(o => o.BatchSize = 3, dispatch: true);
        var doors = Enumerable.Range(1, 7).Select(n => n == 1 ? h.Door : h.AddDoor(TestIds.Device(100 + n))).ToList();
        h.Channel.Batches = true;
        var refused = doors[4];
        h.Channel.BatchHandler = c => c.Target.Key.NativeId == "door-" + refused.Value
            ? ConnectorCommandResult.Failed(FailureCategory.RefusedBySystem, ConnectorMessage.Of("doorBlocked"), "E7")
            : ConnectorCommandResult.Succeeded();
        h.Channel.Handler = h.Channel.BatchHandler; // a lone child (7 = 3 + 3 + 1) is dispatched on its own

        // Queued while the connection starts (they wait), dispatched together once it is up.
        h.Connections.Set(h.Connection, ConnectionState.Starting);
        h.StepUp.Valid.Add(("proof-b", h.Session));
        var group = await h.AcceptAsync(h.Request(CommandsHarness.Reset, reason: "r", stepUp: "proof-b") with { Target = new CommandTargetSpec.Devices(doors) });
        h.Connections.Set(h.Connection, ConnectionState.Connected);

        await Eventually.UntilAsync(() => h.Service.GetGroup(group.Id).Value.Counters.Pending == 0, ct: Ct);
        h.Channel.BatchCalls.Select(b => b.Count).ShouldBe([3, 3], "chunks of Commands:BatchSize");
        h.Channel.Calls.Count.ShouldBe(1, "the remaining child alone: no batch of one");
        h.Channel.BatchCalls.SelectMany(b => b).Concat(h.Channel.Calls).Select(c => c.CommandId).Distinct().Count().ShouldBe(7);
        var view = h.Service.GetGroup(group.Id).Value;
        view.Counters.Succeeded.ShouldBe(6);
        view.Counters.Failed.ShouldBe(1);
        var failed = h.Command(view.Children.Single(c => c.Target == refused).CommandId!.Value);
        failed.Failure!.MessageKey.ShouldBe("packages.sim.doorBlocked");
        failed.Failure.VendorCode.ShouldBe("E7");
        h.Log.Count(r => r.Envelope.Kind == CommandRecordKinds.Dispatched).ShouldBe(7, "each child is dispatched (durable) before the batch call");
    }

    [Fact]
    public async Task CMD_U_062_Non_batchable_group_dispatched_individually_under_the_limits()
    {
        await using var h = new CommandsHarness(o => o.MaxInFlightPerConnection = 2, dispatch: true);
        var doors = Enumerable.Range(1, 5).Select(n => n == 1 ? h.Door : h.AddDoor(TestIds.Device(200 + n))).ToList();
        h.Channel.Handler = _ => ConnectorCommandResult.Succeeded();
        h.Channel.Batches = false; // the connector does not execute batches

        h.StepUp.Valid.Add(("proof-n", h.Session));
        var group = await h.AcceptAsync(h.Request(CommandsHarness.Reset, reason: "r", stepUp: "proof-n") with { Target = new CommandTargetSpec.Devices(doors) });

        await Eventually.UntilAsync(() => h.Service.GetGroup(group.Id).Value.Counters.Pending == 0, ct: Ct);
        h.Channel.Calls.Count.ShouldBe(5);
        h.Channel.BatchCalls.ShouldBeEmpty();
        h.Channel.MaxInFlight.ShouldBeLessThanOrEqualTo(2);
        h.Service.GetGroup(group.Id).Value.Counters.Succeeded.ShouldBe(5);
    }

    [Fact]
    public async Task CMD_U_063_Group_counters_and_progress_throttled_to_500_ms()
    {
        var doors = Doors(3);
        var group = await _h.AcceptAsync(Group(doors, CommandsHarness.Reset, "r", "proof-g"));
        var children = Children(group.Id).Select(c => c.CommandId!.Value).ToList();

        await SettleAsync(children[0], CommandRecordKinds.Succeeded);
        await Eventually.UntilAsync(() => Progress(group.Id).Count == 1, ct: Ct);
        Progress(group.Id)[0].Counters.ShouldBe(new CommandGroupCounters(3, 1, 0, 0, 0, 0, 0, 2));

        _h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await SettleAsync(children[1], CommandRecordKinds.TimedOut);
        Progress(group.Id).Count.ShouldBe(1, "within 500 ms of the last progress record");
        _h.Service.GetGroup(group.Id).Value.Counters.TimedOut.ShouldBe(1, "the counters themselves are current");

        _h.Time.Advance(TimeSpan.FromMilliseconds(400));
        await Eventually.UntilAsync(() => Progress(group.Id).Count == 2, ct: Ct);
        Progress(group.Id)[1].Counters.ShouldBe(new CommandGroupCounters(3, 1, 0, 1, 0, 0, 0, 1));
        _h.Sys.Records.ShouldAllBe(r => r.P == 0 && r.Dev == null);
    }

    [Fact]
    public async Task CMD_U_064_Group_completed_record_when_every_child_is_terminal()
    {
        _h.State.Set(TestIds.Device(301), CommandsHarness.Position, _open); // verified at once
        var doors = new List<DeviceId> { _h.AddDoor(TestIds.Device(301)), _h.AddDoor(TestIds.Device(302)), _h.AddDoor(TestIds.Device(303)) };
        var group = await _h.AcceptAsync(Group(doors));
        var children = Children(group.Id).Select(c => c.CommandId!.Value).ToList();

        await SettleAsync(children[0], CommandRecordKinds.Succeeded); // → Verified
        await SettleAsync(children[1], CommandRecordKinds.Failed);
        await SettleAsync(children[2], CommandRecordKinds.Succeeded); // waits for its effect
        Completed(group.Id).ShouldBeEmpty("a succeeded child still waiting for its effect is not terminal");

        _h.Time.Advance(TimeSpan.FromSeconds(5));
        await _h.TickAsync(doors[2]); // → Unverified

        var completed = await Eventually.UntilAsync(() => Completed(group.Id) is { Count: 1 } c ? c : null, ct: Ct);
        completed[0].Counters.ShouldBe(new CommandGroupCounters(3, 2, 1, 0, 1, 1, 0, 0));
        _h.Service.GetGroup(group.Id).Value.CompletedAt.ShouldNotBeNull();
        _h.Logs.WithEventId(2430).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CMD_U_065_Retry_failed_creates_a_new_group_of_the_failed_children()
    {
        var doors = Doors(4);
        var group = await _h.AcceptAsync(Group(doors, CommandsHarness.Reset, "r", "proof-1"));
        var children = Children(group.Id).Select(c => c.CommandId!.Value).ToList();
        await SettleAsync(children[0], CommandRecordKinds.Failed);
        await SettleAsync(children[1], CommandRecordKinds.TimedOut);
        await SettleAsync(children[2], CommandRecordKinds.Succeeded);
        // children[3] still queued: not retried.

        // The new group is validated again: a fresh step-up proof is needed.
        (await _h.Service.RetryFailedAsync(group.Id, _h.User(), null, "proof-1", Ct)).ShouldFailWith("identity.stepup.invalid");
        _h.StepUp.Valid.Add(("proof-2", _h.Session));
        var retry = (await _h.Service.RetryFailedAsync(group.Id, _h.User(), null, "proof-2", Ct)).ShouldBeSuccess();

        retry.IsGroup.ShouldBeTrue();
        retry.Id.ShouldNotBe(group.Id);
        retry.TargetCount.ShouldBe(2);
        Children(retry.Id).Select(c => c.Target).ShouldBe([doors[0], doors[1]]);
        var requested = SysBody<CommandGroupRequestedBody>(CommandGroupRecordKinds.Requested, retry.Id);
        requested.RetryOf.ShouldBe(group.Id);
        _h.Service.GetGroup(retry.Id).Value.RetryOf.ShouldBe(group.Id);
        Children(retry.Id).ShouldAllBe(c => _h.Command(c.CommandId!.Value).Reason == "r");

        // Nothing to retry, unknown group.
        var clean = await _h.AcceptAsync(Group(Doors(2), CommandsHarness.Open));
        (await _h.Service.RetryFailedAsync(clean.Id, _h.User(), null, null, Ct)).ShouldFailWith("commands.group.nothing_to_retry");
        (await _h.Service.RetryFailedAsync(CommandId.New(), _h.User(), null, null, Ct)).ShouldFailWith("commands.command.not_found");
    }

    private List<DeviceId> Doors(int count) =>
        [.. Enumerable.Range(1, count).Select(n => _h.Registry.Devices.ContainsKey(TestIds.Device(n)) ? TestIds.Device(n) : _h.AddDoor(TestIds.Device(n)))];

    private CommandRequest Group(IReadOnlyList<DeviceId> doors, CommandPath? path = null, string? reason = null, string? stepUp = null, UserId? user = null)
    {
        if (stepUp is not null)
        {
            _h.StepUp.Valid.Add((stepUp, _h.Session));
        }

        return _h.Request(path ?? CommandsHarness.Open, reason: reason, stepUp: stepUp, issuer: _h.User(user)) with { Target = new CommandTargetSpec.Devices(doors) };
    }

    private IReadOnlyList<CommandGroupChild> Children(CommandId group) => _h.Service.GetGroup(group).ShouldBeSuccess().Children;

    /// <summary>A single command dispatched then succeeded (the records the dispatch would append).</summary>
    private async Task<CommandId> SucceedAsync(DeviceId door, CommandPath? path = null)
    {
        var id = (await _h.AcceptAsync(_h.Request(path ?? CommandsHarness.Open, target: door))).Id;
        await SettleAsync(id, CommandRecordKinds.Succeeded);
        return id;
    }

    /// <summary>Dispatches a queued command and records its outcome.</summary>
    private async Task SettleAsync(CommandId id, string outcome)
    {
        await _h.DeliverAsync(id, CommandRecordKinds.Dispatched, new CommandDispatchedBody(id, 1, null));
        switch (outcome)
        {
            case CommandRecordKinds.Succeeded:
                await _h.DeliverAsync(id, outcome, new CommandSucceededBody(id, 10));
                break;
            case CommandRecordKinds.Failed:
                await _h.DeliverAsync(id, outcome, new CommandFailedBody(id, new CommandFailure(FailureCategory.DeviceOffline, "packages.sim.offline",
                    new Dictionary<string, string?>(), null)));
                break;
            default:
                await _h.DeliverAsync(id, outcome, new CommandIdBody(id));
                break;
        }
    }

    /// <summary>A door whose <c>openable.open</c> has another expected effect (its own type).</summary>
    private DeviceId DoorWithEffect(ExpectedEffect effect)
    {
        var type = CommandsHarness.DoorType;
        var variant = type with
        {
            Ref = new TypeRef(TypeId.FromTrusted("ac.door.in"), new SemVersion(1, 0, 0)),
            Commands = new Dictionary<CommandPath, CommandDefinition>(type.Commands) { [CommandsHarness.Open] = type.Commands[CommandsHarness.Open] with { ExpectedEffect = effect } },
        };
        _h.Types.Types[variant.Ref] = variant;
        var door = _h.AddDoor(TestIds.Device(50));
        _h.Registry.Devices[door] = _h.Registry.Devices[door] with { Type = variant.Ref };
        return door;
    }

    private T SysBody<T>(string kind, CommandId group) =>
        JsonSerializer.Deserialize<T>(_h.Sys.Records.Single(r => r.Kind == kind && r.Corr.Command == group).Body, CommandRecordCodec.Json)!;

    private List<CommandGroupProgressBody> Progress(CommandId group) => Bodies(CommandGroupRecordKinds.Progress, group);

    private List<CommandGroupProgressBody> Completed(CommandId group) => Bodies(CommandGroupRecordKinds.Completed, group);

    private List<CommandGroupProgressBody> Bodies(string kind, CommandId group) =>
        [.. _h.Sys.Records.Where(r => r.Kind == kind && r.Corr.Command == group).Select(r => JsonSerializer.Deserialize<CommandGroupProgressBody>(r.Body, CommandRecordCodec.Json)!)];
}
