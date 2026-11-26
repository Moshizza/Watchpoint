using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Testing.Async;
using Xunit;
using CommandPriority = Watchpoint.Commands.Contracts.CommandPriority;

namespace Watchpoint.Commands.Tests;

/// <summary>Dispatch to the connections: queues, per-device serialisation, limits, results and recovery (spec CMD §7–§8; T2.02).</summary>
[Trait("layer", "U")]
[Trait("spec", "CMD")]
public sealed class DispatchTests : IAsyncDisposable
{
    private readonly CommandsHarness _h = new(dispatch: true);

    public ValueTask DisposeAsync() => _h.DisposeAsync();

    [Fact]
    public async Task CMD_U_031_Dispatcher_takes_the_highest_priority_first()
    {
        Use(o => o.MaxInFlightPerConnection = 1);
        var blocker = await QueueAsync(Door(10));
        await UntilCallsAsync(1);
        var background = await QueueAsync(Door(11), CommandPriority.Background);
        var normal = await QueueAsync(Door(12), CommandPriority.Normal);
        var high = await QueueAsync(Door(13));
        var critical = await QueueAsync(Door(14), CommandPriority.Critical, _h.Supervisor);

        await CompleteInTurnAsync(blocker, critical, high, normal, background);

        CalledIds().ShouldBe([blocker, critical, high, normal, background]);
    }

    [Fact]
    public async Task CMD_U_032_Second_command_on_the_same_device_waits_other_devices_proceed()
    {
        var first = await QueueAsync(_h.Door);
        var second = await QueueAsync(_h.Door);
        var other = await QueueAsync(Door(2));

        await UntilCallsAsync(2);
        CalledIds().ShouldBe([first, other], ignoreOrder: true);
        _h.Command(second).State.ShouldBe(CommandState.Queued);

        _h.Channel.Complete(first.Value, ConnectorCommandResult.Succeeded());
        await UntilCallsAsync(3);
        CalledIds()[2].ShouldBe(second);
    }

    [Fact]
    public async Task CMD_U_033_At_most_MaxInFlightPerConnection_commands_in_flight()
    {
        Use(o => o.MaxInFlightPerConnection = 2);
        var ids = new List<CommandId>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add(await QueueAsync(Door(20 + i)));
        }

        await UntilCallsAsync(2);
        _h.Channel.Waiting.ShouldBe(2);
        _h.Dispatch!.Of(_h.Connection)!.InFlight.ShouldBe(2);

        _h.Channel.Complete(CalledIds()[0].Value, ConnectorCommandResult.Succeeded());
        await UntilCallsAsync(3);
        _h.Channel.ReleaseAll();
        await UntilCallsAsync(4);
        _h.Channel.ReleaseAll();
        _h.Channel.MaxInFlight.ShouldBe(2);
    }

    [Fact]
    public async Task CMD_U_034_Fail_fast_when_disconnected_by_default()
    {
        _h.Connections.Set(_h.Connection, ConnectionState.Reconnecting);

        var id = await QueueAsync(_h.Door);

        var failed = await UntilStateAsync(id, CommandState.Failed);
        failed.Failure!.Category.ShouldBe(FailureCategory.ConnectionDown);
        failed.Failure.MessageKey.ShouldBe("errors.commands.connection.down");
        failed.Failure.Args["state"].ShouldBe("Reconnecting");
        _h.Channel.Calls.ShouldBeEmpty();
        _h.RecordsOf(id).Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Failed]);
    }

    [Fact]
    public async Task CMD_U_035_Kept_queued_when_fail_fast_is_disabled_then_expires()
    {
        Use(o => o.FailFastWhenDisconnected = false);
        _h.Connections.Set(_h.Connection, ConnectionState.Faulted);
        var id = await QueueAsync(_h.Door);
        var expiresAt = _h.Command(id).ExpiresAt;

        _h.Time.Advance(expiresAt - _h.Time.GetUtcNow() - TimeSpan.FromSeconds(1));
        Nudge();
        await Task.Yield();
        _h.Command(id).State.ShouldBe(CommandState.Queued);

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        Nudge();
        await UntilStateAsync(id, CommandState.Expired);
        _h.Channel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_036_NotBefore_is_respected()
    {
        var notBefore = _h.Time.GetUtcNow().AddSeconds(30);
        var id = (await _h.AcceptAsync(_h.Request(notBefore: notBefore))).Id;
        var now = await QueueAsync(Door(2));

        await UntilCallsAsync(1);
        CalledIds().ShouldBe([now]);

        _h.Time.Advance(TimeSpan.FromSeconds(30));
        Nudge();
        await UntilCallsAsync(2);
        CalledIds()[1].ShouldBe(id);
    }

    [Fact]
    public async Task CMD_U_037_Dispatched_record_is_durable_before_the_connector_call()
    {
        var seen = new List<bool>();
        _h.Channel.Handler = call =>
        {
            lock (seen)
            {
                seen.Add(_h.Log.Any(r => r.Envelope.Kind == CommandRecordKinds.Dispatched && r.Envelope.Corr.Command?.Value == call.CommandId));
            }

            return ConnectorCommandResult.Succeeded();
        };

        var id = await QueueAsync(_h.Door);

        await UntilStateAsync(id, CommandState.Succeeded);
        seen.ShouldBe([true]);
        var dispatched = _h.RecordsOf(id).Single(r => r.Envelope.Kind == CommandRecordKinds.Dispatched).BodyAs<CommandDispatchedBody>(CommandsHarness.Records);
        dispatched.Attempt.ShouldBe(1);
        var call = _h.Channel.Calls.Single();
        call.Command.ShouldBe("openable.open");
        call.Target.Key.ShouldBe(new DeviceKey("srv-1", "door-" + _h.Door.Value));
        call.Deadline.ShouldBe(_h.Command(id).DispatchedAt!.Value + TimeSpan.FromSeconds(10));
        call.Priority.ShouldBe(Integration.Abstractions.Model.CommandPriority.High);
    }

    [Fact]
    public async Task CMD_U_038_Result_mapping_succeeded_failed_with_category_and_message_not_supported()
    {
        var succeeded = await QueueAsync(_h.Door);
        var failed = await QueueAsync(Door(2));
        var unsupported = await QueueAsync(Door(3));
        await UntilCallsAsync(3);

        _h.Channel.Complete(succeeded.Value, ConnectorCommandResult.Succeeded());
        _h.Channel.Complete(failed.Value, ConnectorCommandResult.Failed(FailureCategory.RefusedBySystem, ConnectorMessage.Of("doorBlocked", ("door", 7)), "E42"));
        _h.Channel.Complete(unsupported.Value, ConnectorCommandResult.NotSupported());

        await UntilStateAsync(succeeded, CommandState.Succeeded);
        var refusal = (await UntilStateAsync(failed, CommandState.Failed)).Failure!;
        refusal.Category.ShouldBe(FailureCategory.RefusedBySystem);
        refusal.MessageKey.ShouldBe("packages.sim.doorBlocked");
        refusal.Args["door"].ShouldBe("7");
        refusal.VendorCode.ShouldBe("E42");
        var notSupported = (await UntilStateAsync(unsupported, CommandState.Failed)).Failure!;
        notSupported.Category.ShouldBe(FailureCategory.NotSupported);
        notSupported.MessageKey.ShouldBe("errors.commands.not_supported");
        _h.RecordsOf(succeeded).Single(r => r.Envelope.Kind == CommandRecordKinds.Succeeded)
            .BodyAs<CommandSucceededBody>(CommandsHarness.Records).DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CMD_U_039_Deadline_gives_TimedOut_and_a_late_connector_success_is_ignored()
    {
        _h.Channel.IgnoreCancellation = true;
        var id = await QueueAsync(_h.Door);
        await UntilCallsAsync(1);

        _h.Time.Advance(TimeSpan.FromSeconds(10));
        await UntilStateAsync(id, CommandState.TimedOut);
        _h.Channel.Complete(id.Value, ConnectorCommandResult.Succeeded());

        var other = await QueueAsync(_h.Door);
        await UntilCallsAsync(2);
        _h.Channel.ReleaseAll();
        await UntilStateAsync(other, CommandState.Succeeded);
        _h.Command(id).State.ShouldBe(CommandState.TimedOut);
        _h.RecordsOf(id).Select(r => r.Envelope.Kind).ShouldNotContain(CommandRecordKinds.Succeeded);
    }

    [Fact]
    public async Task CMD_U_040_Connector_exception_gives_Failed_InternalError()
    {
        _h.Channel.Handler = _ => throw new InvalidOperationException("connector bug");

        var id = await QueueAsync(_h.Door);

        var failure = (await UntilStateAsync(id, CommandState.Failed)).Failure!;
        failure.Category.ShouldBe(FailureCategory.InternalError);
        failure.MessageKey.ShouldBe("errors.commands.connector.failed");
        _h.Logs.WithEventId(2413).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task CMD_U_041_Recovery_requeues_Queued_commands_in_priority_order()
    {
        Use(o => o.MaxInFlightPerConnection = 1);
        _h.Processor.Sink = null; // the node stopped before dispatching them
        var background = await QueueAsync(_h.Door, CommandPriority.Background);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        var normalFirst = await QueueAsync(_h.Door, CommandPriority.Normal);
        _h.Time.Advance(TimeSpan.FromSeconds(1));
        var normalSecond = await QueueAsync(_h.Door, CommandPriority.Normal);
        var critical = await QueueAsync(_h.Door, CommandPriority.Critical, _h.Supervisor);
        _h.Processor.Sink = _h.Dispatch;

        _h.Dispatch!.Recovered(Partitioning.Of(_h.Door));

        await CompleteInTurnAsync(critical, normalFirst, normalSecond, background);
        CalledIds().ShouldBe([critical, normalFirst, normalSecond, background]);
    }

    [Fact]
    public async Task CMD_U_042_Recovery_requeues_a_dispatched_idempotent_first_attempt_as_attempt_2()
    {
        var id = await DispatchedBeforeCrashAsync(attempt: 1);

        _h.Dispatch!.Recovered(Partitioning.Of(_h.Door));

        await UntilCallsAsync(1);
        _h.Channel.Calls[0].Attempt.ShouldBe(2);
        var requeued = _h.RecordsOf(id).Where(r => r.Envelope.Kind == CommandRecordKinds.Queued).Last().BodyAs<CommandQueuedBody>(CommandsHarness.Records);
        requeued.Attempt.ShouldBe(2);
        _h.Channel.ReleaseAll();
        (await UntilStateAsync(id, CommandState.Succeeded)).Attempt.ShouldBe(2);
    }

    [Fact]
    public async Task CMD_U_043_Recovery_fails_a_dispatched_non_idempotent_or_second_attempt_with_outcome_unknown()
    {
        var second = await DispatchedBeforeCrashAsync(attempt: 2);
        var door = Door(2);
        var type = CommandsHarness.DoorType;
        var open = type.Commands[CommandsHarness.Open] with { Idempotent = false };
        var nonIdempotent = type with
        {
            Ref = new TypeRef(TypeId.FromTrusted("ac.door.once"), new SemVersion(1, 0, 0)),
            Commands = new Dictionary<CommandPath, CommandDefinition>(type.Commands) { [CommandsHarness.Open] = open },
        };
        _h.Types.Types[nonIdempotent.Ref] = nonIdempotent;
        _h.Registry.Devices[door] = _h.Registry.Devices[door] with { Type = nonIdempotent.Ref };
        var once = await DispatchedBeforeCrashAsync(attempt: 1, door);

        _h.Dispatch!.Recovered(Partitioning.Of(_h.Door));
        _h.Dispatch.Recovered(Partitioning.Of(door));

        foreach (var id in new[] { second, once })
        {
            var failure = (await UntilStateAsync(id, CommandState.Failed)).Failure!;
            failure.Category.ShouldBe(FailureCategory.Conflict);
            failure.MessageKey.ShouldBe("errors.commands.outcome_unknown");
        }

        _h.Channel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_044_Recovery_keeps_AwaitingApproval_and_its_expiry_still_applies()
    {
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        _h.Processor.Sink = null;
        var id = (await _h.AcceptAsync(_h.Request())).Id;
        _h.Processor.Sink = _h.Dispatch;

        _h.Dispatch!.Recovered(Partitioning.Of(_h.Door));
        await _h.TickAsync();

        _h.Command(id).State.ShouldBe(CommandState.AwaitingApproval);
        _h.Time.Advance(_h.Options.ApprovalWindow);
        await _h.TickAsync();
        (await UntilStateAsync(id, CommandState.Rejected)).Failure!.MessageKey.ShouldBe("errors.commands.approval.expired");
        _h.Channel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_045_Queue_lanes_are_FIFO_within_a_priority()
    {
        Use(o => o.MaxInFlightPerConnection = 1);
        var blocker = await QueueAsync(Door(30));
        await UntilCallsAsync(1);
        var first = await QueueAsync(Door(31), CommandPriority.Normal);
        var second = await QueueAsync(Door(32), CommandPriority.Normal);
        var third = await QueueAsync(Door(33), CommandPriority.Normal);

        await CompleteInTurnAsync(blocker, first, second, third);

        CalledIds().ShouldBe([blocker, first, second, third]);
    }

    [Fact]
    public async Task CMD_U_046_Commands_of_a_locked_device_are_skipped_without_blocking_the_lane()
    {
        var running = await QueueAsync(_h.Door);
        await UntilCallsAsync(1);
        var waiting = await QueueAsync(_h.Door, CommandPriority.Critical, _h.Supervisor);
        var lower = await QueueAsync(Door(2), CommandPriority.Background);

        await UntilCallsAsync(2);
        CalledIds()[1].ShouldBe(lower);
        _h.Command(waiting).State.ShouldBe(CommandState.Queued);

        _h.Channel.Complete(running.Value, ConnectorCommandResult.Succeeded());
        await UntilCallsAsync(3);
        CalledIds()[2].ShouldBe(waiting);
    }

    [Fact]
    public async Task CMD_U_047_Dispatcher_survives_a_connection_restart_queued_commands_wait()
    {
        // Fail fast stays on: a connection that is starting is not down (CMD §7 step 3), its commands wait.
        var before = await QueueAsync(_h.Door);
        await UntilCallsAsync(1);
        _h.Channel.Complete(before.Value, ConnectorCommandResult.Succeeded());
        await UntilStateAsync(before, CommandState.Succeeded);

        _h.Connections.Set(_h.Connection, ConnectionState.Stopping);
        _h.Connections.Set(_h.Connection, ConnectionState.Starting);
        var during = await QueueAsync(_h.Door);
        await Task.Yield();
        _h.Command(during).State.ShouldBe(CommandState.Queued);
        _h.Channel.Calls.Count.ShouldBe(1);

        _h.Connections.Set(_h.Connection, ConnectionState.Connected);
        await UntilCallsAsync(2);
        CalledIds()[1].ShouldBe(during);
    }

    [Fact]
    public async Task CMD_U_048_Metrics_recorded_per_outcome_and_queue_gauge()
    {
        _h.Channel.Handler = call => call.Target.Key.NativeId.EndsWith(Door(2).Value.ToString(), StringComparison.Ordinal)
            ? ConnectorCommandResult.Failed(FailureCategory.DeviceOffline, ConnectorMessage.Of("offline"))
            : ConnectorCommandResult.Succeeded();
        var ok = await QueueAsync(Door(3), path: CommandsHarness.Reset);
        var ko = await QueueAsync(Door(2), path: CommandsHarness.Reset);
        await UntilStateAsync(ok, CommandState.Succeeded);
        await UntilStateAsync(ko, CommandState.Failed);

        _h.Meters.Values("watchpoint.commands.latency", ("path", "maintenance.reset"), ("state", "Succeeded")).Count.ShouldBe(1);
        _h.Meters.Values("watchpoint.commands.latency", ("path", "maintenance.reset"), ("state", "Failed")).Count.ShouldBe(1);

        Use(o => o.FailFastWhenDisconnected = false);
        _h.Connections.Set(_h.Connection, ConnectionState.Reconnecting);
        await QueueAsync(_h.Door);
        await QueueAsync(Door(4), CommandPriority.Background);
        await Eventually.UntilAsync(() => _h.Dispatch!.Of(_h.Connection)!.Queued(CommandPriority.Background) == 1, ct: TestContext.Current.CancellationToken);
        _h.Meters.Observe();
        var connection = _h.Connection.Value;
        _h.Meters.Values("watchpoint.commands.queue", ("connection", connection.ToString()), ("priority", "High")).ShouldBe([1]);
        _h.Meters.Values("watchpoint.commands.queue", ("connection", connection.ToString()), ("priority", "Background")).ShouldBe([1]);
        _h.Meters.Values("watchpoint.commands.queue", ("connection", connection.ToString()), ("priority", "Critical")).ShouldBe([0]);
    }

    [Fact]
    public async Task CMD_U_049_Command_for_a_device_deleted_while_queued_fails_with_target_missing()
    {
        Use(o => o.FailFastWhenDisconnected = false);
        _h.Connections.Set(_h.Connection, ConnectionState.Reconnecting);
        var id = await QueueAsync(_h.Door);
        await Eventually.UntilAsync(() => _h.Dispatch!.Of(_h.Connection)?.Queued(CommandPriority.High) == 1, ct: TestContext.Current.CancellationToken);

        _h.Registry.Devices.Remove(_h.Door);
        _h.Connections.Set(_h.Connection, ConnectionState.Connected);

        var failure = (await UntilStateAsync(id, CommandState.Failed)).Failure!;
        failure.Category.ShouldBe(FailureCategory.InvalidState);
        failure.MessageKey.ShouldBe("errors.commands.target.not_found");
        _h.Channel.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_050_Background_progresses_when_no_higher_priority_work_is_waiting()
    {
        Use(o => o.MaxInFlightPerConnection = 1);
        var blocker = await QueueAsync(Door(40));
        await UntilCallsAsync(1);
        var background = await QueueAsync(Door(41), CommandPriority.Background);
        var high = await QueueAsync(Door(42));

        await CompleteInTurnAsync(blocker, high, background);
        var later = await QueueAsync(Door(43));
        await CompleteInTurnAsync(later);

        CalledIds().ShouldBe([blocker, high, background, later]);
        _h.Command(background).State.ShouldBe(CommandState.Succeeded);
    }

    /// <summary>Changes settings; the dispatchers read them live.</summary>
    private void Use(Action<CommandsOptions> configure) => configure(_h.Options);

    private DeviceId Door(int n) =>
        _h.Registry.Devices.ContainsKey(TestIds.Device(n)) ? TestIds.Device(n) : _h.AddDoor(TestIds.Device(n));

    private async Task<CommandId> QueueAsync(DeviceId door, CommandPriority? priority = null, UserId? user = null, CommandPath? path = null)
    {
        var accepted = await _h.AcceptAsync(_h.Request(path: path, target: door, priority: priority, issuer: _h.User(user)));
        return accepted.Id;
    }

    /// <summary>A command recorded as dispatched (attempt <paramref name="attempt"/>) by a node that then stopped.</summary>
    private async Task<CommandId> DispatchedBeforeCrashAsync(int attempt, DeviceId? door = null)
    {
        _h.Processor.Sink = null;
        var id = await QueueAsync(door ?? _h.Door);
        if (attempt > 1)
        {
            await _h.DeliverAsync(id, CommandRecordKinds.Dispatched, new CommandDispatchedBody(id, 1, null));
            await _h.DeliverAsync(id, CommandRecordKinds.Queued, new CommandQueuedBody(id, CommandPriority.High, attempt));
        }

        await _h.DeliverAsync(id, CommandRecordKinds.Dispatched, new CommandDispatchedBody(id, attempt, null));
        _h.Processor.Sink = _h.Dispatch;
        _h.Command(id).State.ShouldBe(CommandState.Dispatched);
        return id;
    }

    /// <summary>Waits for each command's call in turn and completes it with success.</summary>
    private async Task CompleteInTurnAsync(params CommandId[] ids)
    {
        var start = _h.Channel.Calls.Count(c => !ids.Any(i => i.Value == c.CommandId));
        for (var i = 0; i < ids.Length; i++)
        {
            await UntilCallsAsync(start + i + 1);
            var called = _h.Channel.Calls[start + i].CommandId;
            _h.Channel.Complete(called, ConnectorCommandResult.Succeeded());
        }
    }

    private List<CommandId> CalledIds() => [.. _h.Channel.Calls.Select(c => CommandId.From(c.CommandId))];

    private Task UntilCallsAsync(int count) =>
        Eventually.UntilAsync(() => _h.Channel.Calls.Count >= count, because: $"{count} connector calls expected, saw {_h.Channel.Calls.Count}", ct: TestContext.Current.CancellationToken);

    private async Task<Command> UntilStateAsync(CommandId id, CommandState state)
    {
        await Eventually.UntilAsync(() => _h.Command(id).State == state, because: $"{id} should reach {state}, is {_h.Command(id).State}", ct: TestContext.Current.CancellationToken);
        return _h.Command(id);
    }

    private void Nudge() => _h.Dispatch!.Of(_h.Connection)?.Signal();
}
