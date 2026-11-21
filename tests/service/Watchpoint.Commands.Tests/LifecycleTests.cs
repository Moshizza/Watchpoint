using System.Text.Json;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Xunit;
using CommandPriority = Watchpoint.Commands.Contracts.CommandPriority;

namespace Watchpoint.Commands.Tests;

/// <summary>The lifecycle state machine, pending store, records and snapshot (spec CMD §6; T2.01).</summary>
[Trait("layer", "U")]
[Trait("spec", "CMD")]
public sealed class LifecycleTests : IDisposable
{
    private static readonly (CommandState From, string Kind, CommandState To)[] _specTransitions =
    [
        (CommandState.Requested, CommandRecordKinds.Rejected, CommandState.Rejected),
        (CommandState.Requested, CommandRecordKinds.AwaitingApproval, CommandState.AwaitingApproval),
        (CommandState.Requested, CommandRecordKinds.Queued, CommandState.Queued),
        (CommandState.AwaitingApproval, CommandRecordKinds.Approved, CommandState.Queued),
        (CommandState.AwaitingApproval, CommandRecordKinds.ApprovalRejected, CommandState.Rejected),
        (CommandState.AwaitingApproval, CommandRecordKinds.Rejected, CommandState.Rejected),
        (CommandState.AwaitingApproval, CommandRecordKinds.Cancelled, CommandState.Cancelled),
        (CommandState.Queued, CommandRecordKinds.Dispatched, CommandState.Dispatched),
        (CommandState.Queued, CommandRecordKinds.Expired, CommandState.Expired),
        (CommandState.Queued, CommandRecordKinds.Cancelled, CommandState.Cancelled),
        (CommandState.Queued, CommandRecordKinds.Failed, CommandState.Failed),
        (CommandState.Dispatched, CommandRecordKinds.Succeeded, CommandState.Succeeded),
        (CommandState.Dispatched, CommandRecordKinds.Failed, CommandState.Failed),
        (CommandState.Dispatched, CommandRecordKinds.TimedOut, CommandState.TimedOut),
        (CommandState.Dispatched, CommandRecordKinds.Queued, CommandState.Queued),
        (CommandState.Succeeded, CommandRecordKinds.Verified, CommandState.Verified),
        (CommandState.Succeeded, CommandRecordKinds.Unverified, CommandState.Unverified),
    ];

    private readonly CommandsHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task CMD_U_020_State_machine_every_allowed_transition_terminal_states_final()
    {
        CommandStateMachine.All.OrderBy(t => t.ToString()).ShouldBe(_specTransitions.OrderBy(t => t.ToString()));

        foreach (var (from, kind, to) in _specTransitions)
        {
            var id = await InStateAsync(from);
            await DeliverAsync(id, kind);
            _h.Command(id).State.ShouldBe(to, $"{from} --{kind}--> {to}");
        }

        // Every other (state, kind) pair is refused, and nothing moves a terminal command.
        var states = new[] { CommandState.Requested, CommandState.AwaitingApproval, CommandState.Queued, CommandState.Dispatched, CommandState.Succeeded,
            CommandState.Rejected, CommandState.Failed, CommandState.TimedOut, CommandState.Expired, CommandState.Cancelled, CommandState.Verified, CommandState.Unverified };
        foreach (var from in states)
        {
            foreach (var kind in CommandRecordKinds.All.Where(k => k != CommandRecordKinds.Requested && !_specTransitions.Contains((from, k, Target(from, k)))))
            {
                var id = await InStateAsync(from);
                await DeliverAsync(id, kind);
                _h.Command(id).State.ShouldBe(from, $"{from} must ignore {kind}");
            }
        }

        // Succeeded is terminal for a command without an expected effect.
        var plain = await InStateAsync(CommandState.Succeeded, CommandsHarness.Reset);
        CommandStateMachine.IsTerminal(_h.Command(plain)).ShouldBeTrue();
        await DeliverAsync(plain, CommandRecordKinds.Verified);
        _h.Command(plain).State.ShouldBe(CommandState.Succeeded);
    }

    [Fact]
    public async Task CMD_U_021_Late_transitions_are_ignored_and_counted()
    {
        var id = await InStateAsync(CommandState.Dispatched);
        await DeliverAsync(id, CommandRecordKinds.TimedOut);
        var timedOut = _h.Command(id);

        await DeliverAsync(id, CommandRecordKinds.Succeeded); // the connector answered after the deadline
        await DeliverAsync(id, CommandRecordKinds.Failed);

        _h.Command(id).ShouldBe(timedOut); // the first terminal outcome wins
        _h.Meters.Sum("watchpoint.commands.transitions.ignored", "kind", CommandRecordKinds.Succeeded).ShouldBe(1);
        _h.Meters.Sum("watchpoint.commands.transitions.ignored", "kind", CommandRecordKinds.Failed).ShouldBe(1);
        var ignored = _h.Logs.WithEventId(2420).ToList();
        ignored.Count.ShouldBe(2);
        ignored[0].Level.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Debug);
        ignored[0].Properties["State"].ShouldBe("TimedOut");

        // A record of a command the partition does not know is ignored and counted as well.
        var stranger = _h.Command(id) with { Id = CommandId.New() };
        await _h.Partitions.DeliverAsync(Partitioning.Of(_h.Door), _h.Codec.Envelope(CommandRecordKinds.Dispatched, stranger, new CommandDispatchedBody(stranger.Id, 1, null), _h.Time.GetUtcNow()));
        _h.Meters.Sum("watchpoint.commands.transitions.ignored", "kind", CommandRecordKinds.Dispatched).ShouldBe(1);
        _h.Logs.WithEventId(2420).Count().ShouldBe(3);
    }

    [Fact]
    public async Task CMD_U_022_Finished_commands_leave_the_pending_store_after_60_s_and_stay_readable()
    {
        var accepted = await _h.AcceptAsync(_h.Request());
        var partition = Partitioning.Of(_h.Door);
        (await _h.Service.CancelAsync(accepted.Id, _h.User(), Ct)).ShouldBeSuccess();

        _h.Time.Advance(TimeSpan.FromSeconds(59));
        await _h.TickAsync();
        _h.Processor.Find(partition, accepted.Id).ShouldNotBeNull();

        _h.Time.Advance(TimeSpan.FromSeconds(1));
        await _h.TickAsync();
        _h.Processor.Find(partition, accepted.Id).ShouldBeNull();
        _h.Processor.Store(partition).Count.ShouldBe(0);
        _h.Command(accepted.Id).State.ShouldBe(CommandState.Cancelled); // from the 24 h read cache

        // An active command is never evicted, whatever its age.
        var active = await _h.AcceptAsync(_h.Request());
        _h.Time.Advance(TimeSpan.FromHours(24));
        await _h.TickAsync();
        _h.Service.Get(accepted.Id).ShouldFailWith("commands.command.not_found");
        _h.Command(active.Id).State.ShouldBe(CommandState.Queued);
        _h.Processor.Find(partition, active.Id).ShouldNotBeNull();
    }

    [Fact]
    public async Task CMD_U_023_Command_records_serialise_with_the_envelope_correlation()
    {
        foreach (var kind in CommandRecordKinds.All)
        {
            CommandsHarness.Records.TryGetSchemaVersion(kind, out var schema).ShouldBeTrue(kind);
            schema.ShouldBe(1);
        }

        var accepted = await _h.AcceptAsync(_h.Request(CommandsHarness.UnlockFor, """{"duration":30,"level":12.25}""", correlation: new RecordCorrelation(Alarm: TestIds.Of<AlarmId>(3))));
        var command = _h.Command(accepted.Id);
        var requested = _h.RecordsOf(accepted.Id)[0];

        // The envelope: device partition, user origin, the command's correlation; valid for WP_LOG.
        requested.Envelope.Validate(LogStream.Log).IsSuccess.ShouldBeTrue();
        requested.Envelope.Origin.ShouldBe(new RecordOrigin(RecordOriginType.User, _h.Operator.Value));
        requested.Envelope.Corr.ShouldBe(new RecordCorrelation(accepted.Id, TestIds.Of<AlarmId>(3)));

        // The body is the full command, camelCase, round-tripping exactly.
        var json = requested.Envelope.Body;
        json.GetProperty("path").GetString().ShouldBe("openable.unlockFor");
        json.GetProperty("issuer").GetProperty("type").GetString().ShouldBe("user");
        json.GetProperty("effectiveRisk").GetString().ShouldBe("normal");
        json.GetProperty("timeout").GetString().ShouldBe("PT10S");
        var body = _h.Codec.Body<Command>(requested);
        Serialize(body with { State = command.State, QueuedAt = command.QueuedAt }).ShouldBe(Serialize(command));
        body.Arguments.ShouldBe(command.Arguments);

        var open = _h.Codec.Body<Command>(_h.RecordsOf((await _h.AcceptAsync(_h.Request())).Id)[0]);
        open.Verification.ShouldBe(CommandsHarness.DoorType.Commands[CommandsHarness.Open].ExpectedEffect);

        // Every other body round-trips through the kind registry.
        var failure = new CommandFailure(FailureCategory.DeviceOffline, "packages.sim.offline", new Dictionary<string, string?> { ["door"] = "7" }, "E42");
        RoundTrip(CommandRecordKinds.Rejected, new CommandRejectedBody(command.Id, new CommandRecordError("x.y.z", new Dictionary<string, string?> { ["a"] = "1" }, [new("/p", "c.d")]), command.Path, command.Issuer, "r", CommandId.New()));
        RoundTrip(CommandRecordKinds.AwaitingApproval, new CommandAwaitingApprovalBody(command.Id, _h.Time.GetUtcNow()));
        RoundTrip(CommandRecordKinds.Approved, new CommandApprovalBody(command.Id, _h.Supervisor, null));
        RoundTrip(CommandRecordKinds.Queued, new CommandQueuedBody(command.Id, CommandPriority.Background, 2));
        RoundTrip(CommandRecordKinds.Dispatched, new CommandDispatchedBody(command.Id, 1, TestIds.Of<NodeId>(1)));
        RoundTrip(CommandRecordKinds.Succeeded, new CommandSucceededBody(command.Id, 120));
        RoundTrip(CommandRecordKinds.Failed, new CommandFailedBody(command.Id, failure));
        RoundTrip(CommandRecordKinds.TimedOut, new CommandIdBody(command.Id));
        RoundTrip(CommandRecordKinds.Cancelled, new CommandByBody(command.Id, _h.Operator));
        RoundTrip(CommandRecordKinds.Verified, new CommandVerificationBody(command.Id, WpValue.Enum("open"), 800));

        void RoundTrip<T>(string kind, T body)
        {
            var envelope = _h.Codec.Envelope(kind, command, body, _h.Time.GetUtcNow());
            envelope.Validate(LogStream.Log).IsSuccess.ShouldBeTrue(kind);
            envelope.Corr.Command.ShouldBe(command.Id);
            var read = new LogRecord(1, envelope).BodyAs<T>(CommandsHarness.Records);
            Serialize(read).ShouldBe(Serialize(body), kind);
        }
    }

    [Fact]
    public async Task CMD_U_025_Cancel_queued_or_awaiting_cancels_dispatched_is_too_late_concurrent_cancels_serialised()
    {
        var queued = await _h.AcceptAsync(_h.Request());
        (await _h.Service.CancelAsync(queued.Id, _h.User(), Ct)).ShouldBeSuccess();
        _h.Command(queued.Id).State.ShouldBe(CommandState.Cancelled);
        _h.Codec.Body<CommandByBody>(_h.RecordsOf(queued.Id)[^1]).ShouldBe(new CommandByBody(queued.Id, _h.Operator));
        (await _h.Service.CancelAsync(queued.Id, _h.User(), Ct)).ShouldFailWith("commands.cancel.too_late").Args["state"].ShouldBe("Cancelled");

        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var awaiting = await _h.AcceptAsync(_h.Request());
        (await _h.Service.CancelAsync(awaiting.Id, _h.User(), Ct)).ShouldBeSuccess();
        _h.Command(awaiting.Id).State.ShouldBe(CommandState.Cancelled);
        _h.Authorizer.Allowed[_h.Operator] = Contracts.CommandConstraints.None;

        var dispatched = await InStateAsync(CommandState.Dispatched);
        (await _h.Service.CancelAsync(dispatched, _h.User(), Ct)).ShouldFailWith("commands.cancel.too_late").Args["state"].ShouldBe("Dispatched");
        (await _h.Service.CancelAsync(CommandId.New(), _h.User(), Ct)).ShouldFailWith("commands.command.not_found");

        // Concurrent updates of one command are serialised by its partition: one cancel wins, the other is too late,
        // and approving while a cancel is in flight finds the command cancelled.
        var racing = await _h.AcceptAsync(_h.Request());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Partitions.BetweenWorkAndAppend = () => release.Task;
        var first = _h.Service.CancelAsync(racing.Id, _h.User(), Ct);
        var second = _h.Service.CancelAsync(racing.Id, _h.User(_h.Supervisor), Ct);
        await Task.Yield();
        second.IsCompleted.ShouldBeFalse();
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => !r.IsSuccess).ShouldFailWith("commands.cancel.too_late");
        _h.RecordsOf(racing.Id).Count(r => r.Envelope.Kind == CommandRecordKinds.Cancelled).ShouldBe(1);
        _h.Partitions.MaxConcurrent.ShouldBe(1);
    }

    [Fact]
    public async Task CMD_U_030_Snapshot_participant_serialises_the_pending_store_and_restores_it()
    {
        var partition = Partitioning.Of(_h.Door);
        var snapshot = new PendingCommandSnapshot(_h.Processor, _h.Time);
        var queued = await _h.AcceptAsync(_h.Request(key: "k-1", correlation: new RecordCorrelation(Alarm: TestIds.Of<AlarmId>(1))));
        var dispatched = await InStateAsync(CommandState.Dispatched);

        var (upserts, deletes) = snapshot.Collect(partition);
        upserts.Select(i => i.Key).ShouldBe([queued.Id.Value, dispatched.Value], ignoreOrder: true);
        var last = _h.Log[^1].Sequence;
        upserts.ShouldAllBe(i => i.Kind == PendingCommandSnapshot.Kind && i.Partition == partition && i.Sequence == last);
        deletes.ShouldBeEmpty();
        snapshot.Collect(partition).Upserts.ShouldBeEmpty(); // nothing changed since

        // A new node restores the store: same commands, same idempotency keys, and the lifecycle continues.
        using var restored = new CommandsHarness();
        new PendingCommandSnapshot(restored.Processor, restored.Time).Restore(partition, upserts);
        foreach (var id in new[] { queued.Id, dispatched })
        {
            Serialize(restored.Command(id)).ShouldBe(Serialize(_h.Command(id)));
        }

        restored.Processor.Store(partition).Sequence.ShouldBe(_h.Log[^1].Sequence);
        (await restored.AcceptAsync(restored.Request(key: "k-1"))).Id.ShouldBe(queued.Id);
        (await restored.Service.CancelAsync(queued.Id, restored.User(), Ct)).ShouldBeSuccess();
        await restored.DeliverAsync(dispatched, CommandRecordKinds.Succeeded, new CommandSucceededBody(dispatched, 5));
        restored.Command(dispatched).State.ShouldBe(CommandState.Succeeded);

        // A command that left the store is a delete.
        (await _h.Service.CancelAsync(queued.Id, _h.User(), Ct)).ShouldBeSuccess();
        _h.Time.Advance(TimeSpan.FromSeconds(60));
        await _h.TickAsync();
        snapshot.Collect(partition).Deletes.ShouldBe([new Storage.Contracts.StateItemKey(PendingCommandSnapshot.Kind, queued.Id.Value)]);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, CommandRecordCodec.Json);

    private static CommandState Target(CommandState from, string kind) =>
        _specTransitions.FirstOrDefault(t => t.From == from && t.Kind == kind).To;

    /// <summary>A new command driven into <paramref name="state"/> by records, as the later tasks will write them.</summary>
    private async Task<CommandId> InStateAsync(CommandState state, Model.Contracts.CommandPath? path = null)
    {
        var now = _h.Time.GetUtcNow();
        var command = new Command
        {
            Id = CommandId.New(),
            Target = _h.Door,
            Path = path ?? CommandsHarness.Open,
            Arguments = new Dictionary<string, WpValue>(),
            Issuer = _h.User(),
            EffectiveRisk = Model.Contracts.Risk.Normal,
            Priority = CommandPriority.High,
            Timeout = TimeSpan.FromSeconds(10),
            ExpiresAt = now + TimeSpan.FromMinutes(5),
            Verification = (path ?? CommandsHarness.Open) == CommandsHarness.Open ? CommandsHarness.DoorType.Commands[CommandsHarness.Open].ExpectedEffect : null,
            Approval = new CommandApproval(null, null, now + TimeSpan.FromMinutes(5)),
            State = CommandState.Requested,
            RequestedAt = now,
        };
        await _h.Partitions.DeliverAsync(Partitioning.Of(_h.Door), _h.Codec.Envelope(CommandRecordKinds.Requested, command, command, now));
        string[] path2 = state switch
        {
            CommandState.Requested => [],
            CommandState.AwaitingApproval => [CommandRecordKinds.AwaitingApproval],
            CommandState.Queued => [CommandRecordKinds.Queued],
            CommandState.Dispatched => [CommandRecordKinds.Queued, CommandRecordKinds.Dispatched],
            CommandState.Succeeded => [CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.Succeeded],
            CommandState.Rejected => [CommandRecordKinds.Rejected],
            CommandState.Failed => [CommandRecordKinds.Queued, CommandRecordKinds.Failed],
            CommandState.TimedOut => [CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.TimedOut],
            CommandState.Expired => [CommandRecordKinds.Queued, CommandRecordKinds.Expired],
            CommandState.Cancelled => [CommandRecordKinds.Queued, CommandRecordKinds.Cancelled],
            CommandState.Verified => [CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.Succeeded, CommandRecordKinds.Verified],
            CommandState.Unverified => [CommandRecordKinds.Queued, CommandRecordKinds.Dispatched, CommandRecordKinds.Succeeded, CommandRecordKinds.Unverified],
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        foreach (var kind in path2)
        {
            await DeliverAsync(command.Id, kind);
        }

        _h.Command(command.Id).State.ShouldBe(state);
        return command.Id;
    }

    private Task DeliverAsync(CommandId id, string kind) => kind switch
    {
        CommandRecordKinds.Rejected => _h.DeliverAsync(id, kind, new CommandRejectedBody(id, new CommandRecordError("commands.approval.expired", new Dictionary<string, string?>(), null))),
        CommandRecordKinds.AwaitingApproval => _h.DeliverAsync(id, kind, new CommandAwaitingApprovalBody(id, _h.Time.GetUtcNow() + TimeSpan.FromMinutes(5))),
        CommandRecordKinds.Approved or CommandRecordKinds.ApprovalRejected => _h.DeliverAsync(id, kind, new CommandApprovalBody(id, _h.Supervisor, "r")),
        CommandRecordKinds.Queued => _h.DeliverAsync(id, kind, new CommandQueuedBody(id, CommandPriority.High)),
        CommandRecordKinds.Dispatched => _h.DeliverAsync(id, kind, new CommandDispatchedBody(id, 1, null)),
        CommandRecordKinds.Succeeded => _h.DeliverAsync(id, kind, new CommandSucceededBody(id, 10)),
        CommandRecordKinds.Failed => _h.DeliverAsync(id, kind, new CommandFailedBody(id, new CommandFailure(FailureCategory.RefusedBySystem, "errors.x", new Dictionary<string, string?>(), null))),
        CommandRecordKinds.TimedOut => _h.DeliverAsync(id, kind, new CommandIdBody(id)),
        CommandRecordKinds.Expired or CommandRecordKinds.Cancelled => _h.DeliverAsync(id, kind, new CommandByBody(id, null)),
        CommandRecordKinds.Verified or CommandRecordKinds.Unverified => _h.DeliverAsync(id, kind, new CommandVerificationBody(id, null, 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
