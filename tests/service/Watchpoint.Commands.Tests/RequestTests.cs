using System.Globalization;
using Shouldly;
using Watchpoint.Commands.Contracts;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Xunit;

namespace Watchpoint.Commands.Tests;

/// <summary>Request validation (spec CMD §4–§5; T2.01).</summary>
[Trait("layer", "U")]
[Trait("spec", "CMD")]
public sealed class RequestTests : IDisposable
{
    private readonly CommandsHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task CMD_U_001_Valid_request_writes_Requested_and_Queued_records_in_the_device_partition()
    {
        var accepted = await _h.AcceptAsync(_h.Request());

        accepted.IsGroup.ShouldBeFalse();
        accepted.State.ShouldBe(CommandState.Queued);
        accepted.TargetCount.ShouldBe(1);
        var records = _h.RecordsOf(accepted.Id);
        records.Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued]);
        records.ShouldAllBe(r => r.Envelope.Dev == _h.Door && r.Envelope.P == Partitioning.Of(_h.Door));
        records[1].Sequence.ShouldBe(records[0].Sequence + 1); // one batch
        var requested = _h.Codec.Body<Command>(records[0]);
        requested.State.ShouldBe(CommandState.Requested);
        requested.Target.ShouldBe(_h.Door);
        _h.Codec.Body<CommandQueuedBody>(records[1]).ShouldBe(new CommandQueuedBody(accepted.Id, CommandPriority.High));

        var command = _h.Command(accepted.Id);
        command.State.ShouldBe(CommandState.Queued);
        command.QueuedAt.ShouldBe(_h.Time.GetUtcNow());
        command.Verification!.Property.ShouldBe(CommandsHarness.Position);
        _h.Meters.Sum("watchpoint.commands.requests", "outcome", "accepted").ShouldBe(1);
        _h.Logs.WithEventId(2401).Count().ShouldBe(2);
    }

    [Fact]
    public async Task CMD_U_002_Idempotent_duplicate_returns_the_existing_command_without_new_records()
    {
        var first = await _h.AcceptAsync(_h.Request(key: "click-42"));
        var count = _h.Log.Count;

        var again = await _h.AcceptAsync(_h.Request(key: "click-42", reason: "a retry with another body"));

        again.ShouldBe(first);
        _h.Log.Count.ShouldBe(count);
        _h.Meters.Sum("watchpoint.commands.requests", "outcome", "duplicate").ShouldBe(1);

        // The key belongs to the identity: another user's same key is a new command.
        var other = await _h.AcceptAsync(_h.Request(key: "click-42", issuer: _h.User(_h.Supervisor)));
        other.Id.ShouldNotBe(first.Id);

        // After the idempotency window the key is free again.
        _h.Time.Advance(_h.Options.IdempotencyWindow);
        (await _h.AcceptAsync(_h.Request(key: "click-42"))).Id.ShouldNotBe(first.Id);

        // A key longer than 64 characters is refused.
        (await _h.Service.RequestAsync(_h.Request(key: new string('k', 65)), Ct)).ShouldFailWith("common.validation.failed")
            .ShouldHaveFieldError("/clientCommandId", ErrorCode.FromTrusted("common.value.too_long"));
    }

    [Fact]
    public async Task CMD_U_003_Unknown_disabled_and_orphaned_devices_are_rejected_with_their_codes()
    {
        var unknown = TestIds.Device(90);
        var disabled = _h.AddDoor(TestIds.Device(91), enabled: false);
        var orphaned = _h.AddDoor(TestIds.Device(92), orphaned: true);

        (await _h.Service.RequestAsync(_h.Request(target: unknown), Ct)).ShouldFailWith("commands.target.not_found");
        (await _h.Service.RequestAsync(_h.Request(target: disabled), Ct)).ShouldFailWith("commands.target.disabled");
        (await _h.Service.RequestAsync(_h.Request(target: orphaned), Ct)).ShouldFailWith("commands.target.orphaned");

        // Each rejection is recorded for audit in the target's partition, with what was requested and by whom.
        var rejected = _h.Log.Where(r => r.Envelope.Kind == CommandRecordKinds.Rejected).ToList();
        rejected.Select(r => r.Envelope.Dev).ShouldBe([unknown, disabled, orphaned]);
        rejected.Select(r => _h.Codec.Body<CommandRejectedBody>(r).Error.Code)
            .ShouldBe(["commands.target.not_found", "commands.target.disabled", "commands.target.orphaned"]);
        var body = _h.Codec.Body<CommandRejectedBody>(rejected[0]);
        body.Path.ShouldBe(CommandsHarness.Open);
        body.Issuer.ShouldBe(_h.User());
        _h.Service.Get(body.Id).ShouldFailWith("commands.command.not_found");
        _h.Log.ShouldNotContain(r => r.Envelope.Kind == CommandRecordKinds.Requested);
        _h.Meters.Sum("watchpoint.commands.requests", "outcome", "rejected").ShouldBe(3);
    }

    [Fact]
    public async Task CMD_U_004_Command_not_in_type_or_on_an_unsupported_optional_capability_is_rejected()
    {
        (await _h.Service.RequestAsync(_h.Request(path: CommandPath.FromTrusted("openable.fly")), Ct)).ShouldFailWith("model.command.not_found");

        var limited = _h.AddDoor(TestIds.Device(93), unsupported: "tamper");
        (await _h.Service.RequestAsync(_h.Request(path: CommandsHarness.TamperTest, target: limited), Ct)).ShouldFailWith("commands.not_supported");
        (await _h.Service.RequestAsync(_h.Request(path: CommandsHarness.TamperTest), Ct)).ShouldBeSuccess();
    }

    [Fact]
    public async Task CMD_U_005_Argument_errors_are_field_errors_defaults_applied_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); // decimal comma: must not matter
        try
        {
            var accepted = await _h.AcceptAsync(_h.Request(CommandsHarness.UnlockFor, """{"level":21.5}"""));
            var arguments = _h.Command(accepted.Id).Arguments;
            arguments["level"].ShouldBe(WpValue.Decimal(21.5m));
            arguments["duration"].ShouldBe(WpValue.Int(5));        // default
            arguments["mode"].ShouldBe(WpValue.Enum("normal"));    // default

            var range = (await _h.Service.RequestAsync(_h.Request(CommandsHarness.UnlockFor, """{"duration":3601,"level":-0.5}"""), Ct))
                .ShouldFailWith("model.document.invalid");
            range.ShouldHaveFieldError("/arguments/duration", ErrorCode.FromTrusted("model.value.out_of_range"));
            range.ShouldHaveFieldError("/arguments/level", ErrorCode.FromTrusted("model.value.out_of_range"));

            var kinds = (await _h.Service.RequestAsync(_h.Request(CommandsHarness.UnlockFor, """{"duration":"5","level":"21,5","mode":"fast"}"""), Ct))
                .ShouldFailWith("model.document.invalid");
            kinds.ShouldHaveFieldError("/arguments/duration", ErrorCode.FromTrusted("model.value.type_mismatch"));
            kinds.ShouldHaveFieldError("/arguments/level", ErrorCode.FromTrusted("model.value.type_mismatch"));
            kinds.ShouldHaveFieldError("/arguments/mode", ErrorCode.FromTrusted("model.value.enum_unknown"));

            (await _h.Service.RequestAsync(_h.Request(CommandsHarness.UnlockFor, """{"duration":1.5}"""), Ct))
                .ShouldFailWith("model.document.invalid").ShouldHaveFieldError("/arguments/duration", ErrorCode.FromTrusted("model.value.type_mismatch"));

            // The rejection record carries the field problems.
            var last = _h.Codec.Body<CommandRejectedBody>(_h.Log[^1]);
            last.Error.Fields!.ShouldContain(new CommandRecordFieldError("/arguments/duration", "model.value.type_mismatch"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task CMD_U_006_Effective_risk_from_riskByArgument()
    {
        _h.StepUp.Valid.Add(("proof-1", _h.Session));
        var normal = await _h.AcceptAsync(_h.Request(CommandsHarness.UnlockFor, """{"mode":"normal"}""", _h.User(_h.Supervisor)));
        var emergency = await _h.AcceptAsync(_h.Request(CommandsHarness.UnlockFor, """{"mode":"emergency"}""", _h.User(_h.Supervisor), reason: "fire drill", stepUp: "proof-1"));

        _h.Command(normal.Id).EffectiveRisk.ShouldBe(Risk.Normal);
        _h.Command(emergency.Id).EffectiveRisk.ShouldBe(Risk.Critical);
        _h.Command(emergency.Id).Priority.ShouldBe(CommandPriority.Critical);

        // The raised risk drives the checks: no reason → refused.
        (await _h.Service.RequestAsync(_h.Request(CommandsHarness.UnlockFor, """{"mode":"emergency"}""", _h.User(_h.Supervisor)), Ct))
            .ShouldFailWith("commands.reason.required");
    }

    [Fact]
    public async Task CMD_U_007_Authorizer_denial_rejects_with_common_forbidden_and_writes_the_record()
    {
        var stranger = TestIds.User(77); // not allowed: the hook denies by default

        var denied = (await _h.Service.RequestAsync(_h.Request(issuer: _h.User(stranger)), Ct)).ShouldFailWith("common.forbidden");

        denied.Args["permission"]!.ToString().ShouldBe("commands.openable.open");
        _h.Authorizer.Calls.ShouldBe(1);
        var record = _h.Log.ShouldHaveSingleItem();
        record.Envelope.Kind.ShouldBe(CommandRecordKinds.Rejected);
        record.Envelope.Origin.ShouldBe(new RecordOrigin(RecordOriginType.User, stranger.Value));
        _h.Codec.Body<CommandRejectedBody>(record).Error.Code.ShouldBe("common.forbidden");
    }

    [Fact]
    public async Task CMD_U_008_Outside_schedule_and_client_kind_constraints()
    {
        _h.Authorizer.Denial = CommandErrors.OutsideSchedule();
        (await _h.Service.RequestAsync(_h.Request(), Ct)).ShouldFailWith("commands.outside_schedule");
        _h.Authorizer.Denial = null;

        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(AllowedClientKinds: new HashSet<ClientKind> { ClientKind.Desktop });
        (await _h.Service.RequestAsync(_h.Request(issuer: _h.User(client: ClientKind.Web)), Ct))
            .ShouldFailWith("commands.client_kind_not_allowed").Args["clientKind"].ShouldBe("Web");
        (await _h.Service.RequestAsync(_h.Request(issuer: _h.User(client: ClientKind.Desktop)), Ct)).ShouldBeSuccess();
        _h.Log.Count(r => r.Envelope.Kind == CommandRecordKinds.Rejected).ShouldBe(2);
    }

    [Fact]
    public async Task CMD_U_009_Reason_required_for_Critical()
    {
        _h.Options.StepUpForCritical = false;
        var supervisor = _h.User(_h.Supervisor);

        (await _h.Service.RequestAsync(_h.Request(CommandsHarness.Lockdown, issuer: supervisor), Ct))
            .ShouldFailWith("commands.reason.required").ShouldHaveFieldError("/reason", ErrorCode.FromTrusted("commands.reason.required"));
        (await _h.Service.RequestAsync(_h.Request(CommandsHarness.Lockdown, issuer: supervisor, reason: "   "), Ct)).ShouldFailWith("commands.reason.required");
        var accepted = await _h.AcceptAsync(_h.Request(CommandsHarness.Lockdown, issuer: supervisor, reason: "intruder in hall B"));
        _h.Command(accepted.Id).Reason.ShouldBe("intruder in hall B");

        // A constraint can require it for any risk.
        (await _h.Service.RequestAsync(_h.Request(), Ct)).ShouldBeSuccess();
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ReasonRequired: true);
        (await _h.Service.RequestAsync(_h.Request(), Ct)).ShouldFailWith("commands.reason.required");
    }

    [Fact]
    public async Task CMD_U_010_Step_up_required_and_validated_bound_to_the_session_and_single_use()
    {
        var supervisor = _h.User(_h.Supervisor);
        _h.StepUp.Valid.Add(("proof-1", _h.Session));
        _h.StepUp.Valid.Add(("proof-other-session", TestIds.Of<SessionId>(9)));
        CommandRequest Lockdown(string? proof) => _h.Request(CommandsHarness.Lockdown, issuer: supervisor, reason: "drill", stepUp: proof);

        (await _h.Service.RequestAsync(Lockdown(null), Ct)).ShouldFailWith("identity.stepup.required");
        (await _h.Service.RequestAsync(Lockdown("forged"), Ct)).ShouldFailWith("identity.stepup.required");
        (await _h.Service.RequestAsync(Lockdown("proof-other-session"), Ct)).ShouldFailWith("identity.stepup.required");
        (await _h.Service.RequestAsync(Lockdown("proof-1"), Ct)).ShouldBeSuccess();
        (await _h.Service.RequestAsync(Lockdown("proof-1"), Ct)).ShouldFailWith("identity.stepup.invalid"); // used once already

        // A constraint requires it for a normal command; the setting can lift it for critical ones.
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(StepUpRequired: true);
        (await _h.Service.RequestAsync(_h.Request(), Ct)).ShouldFailWith("identity.stepup.required");
        _h.Options.StepUpForCritical = false;
        (await _h.Service.RequestAsync(Lockdown(null), Ct)).ShouldBeSuccess();
    }

    [Fact]
    public async Task CMD_U_012_Approval_required_puts_the_command_in_AwaitingApproval()
    {
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var now = _h.Time.GetUtcNow();

        var accepted = await _h.AcceptAsync(_h.Request());

        accepted.State.ShouldBe(CommandState.AwaitingApproval);
        var records = _h.RecordsOf(accepted.Id);
        records.Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.AwaitingApproval]);
        _h.Codec.Body<CommandAwaitingApprovalBody>(records[1]).RequestExpiresAt.ShouldBe(now + _h.Options.ApprovalWindow);
        var command = _h.Command(accepted.Id);
        command.State.ShouldBe(CommandState.AwaitingApproval);
        command.Approval.ShouldBe(new CommandApproval(null, null, now + _h.Options.ApprovalWindow));
        command.QueuedAt.ShouldBeNull();
    }

    [Fact]
    public async Task CMD_U_013_Self_approval_is_forbidden()
    {
        _h.Authorizer.Allowed[_h.Supervisor] = new CommandConstraints(ApprovalRequired: true);
        var accepted = await _h.AcceptAsync(_h.Request(issuer: _h.User(_h.Supervisor)));

        (await _h.Service.ApproveAsync(accepted.Id, _h.Supervisor, _h.Session, Ct)).ShouldFailWith("commands.approval.self_forbidden");
        (await _h.Service.RejectAsync(accepted.Id, _h.Supervisor, "no", Ct)).ShouldFailWith("commands.approval.self_forbidden");
        _h.Command(accepted.Id).State.ShouldBe(CommandState.AwaitingApproval);
        _h.RecordsOf(accepted.Id).Count.ShouldBe(2);
    }

    [Fact]
    public async Task CMD_U_014_Approve_queues_and_reject_rejects_with_the_reason()
    {
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var first = await _h.AcceptAsync(_h.Request());
        var second = await _h.AcceptAsync(_h.Request());
        var third = await _h.AcceptAsync(_h.Request());

        (await _h.Service.ApproveAsync(first.Id, _h.Supervisor, _h.Session, Ct)).ShouldBeSuccess();
        var approved = _h.Command(first.Id);
        approved.State.ShouldBe(CommandState.Queued);
        approved.Approval!.ApprovedBy.ShouldBe(_h.Supervisor);
        approved.QueuedAt.ShouldBe(_h.Time.GetUtcNow());
        _h.RecordsOf(first.Id)[^1].Envelope.Kind.ShouldBe(CommandRecordKinds.Approved);
        (await _h.Service.ApproveAsync(first.Id, _h.Supervisor, _h.Session, Ct)).ShouldFailWith("commands.approval.not_pending");

        (await _h.Service.RejectAsync(second.Id, _h.Supervisor, "", Ct)).ShouldFailWith("commands.reason.required");
        (await _h.Service.RejectAsync(second.Id, _h.Supervisor, "door under repair", Ct)).ShouldBeSuccess();
        var rejected = _h.Command(second.Id);
        rejected.State.ShouldBe(CommandState.Rejected);
        rejected.Failure!.MessageKey.ShouldBe("errors.commands.approval.rejected");
        rejected.Failure.Args["reason"].ShouldBe("door under repair");
        _h.Codec.Body<CommandApprovalBody>(_h.RecordsOf(second.Id)[^1]).ShouldBe(new CommandApprovalBody(second.Id, _h.Supervisor, "door under repair"));

        // The approver needs the right (commands.approve) and the command must exist.
        var colleague = TestIds.User(5);
        (await _h.Service.ApproveAsync(third.Id, colleague, _h.Session, Ct)).ShouldFailWith("common.forbidden");
        (await _h.Service.ApproveAsync(CommandId.New(), _h.Supervisor, _h.Session, Ct)).ShouldFailWith("commands.command.not_found");
    }

    [Fact]
    public async Task CMD_U_015_Approval_expiry_rejects_with_commands_approval_expired()
    {
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var waiting = await _h.AcceptAsync(_h.Request());
        var late = await _h.AcceptAsync(_h.Request());

        _h.Time.Advance(_h.Options.ApprovalWindow - TimeSpan.FromMilliseconds(1));
        await _h.TickAsync();
        _h.Command(waiting.Id).State.ShouldBe(CommandState.AwaitingApproval);

        // At the end of the window an approval is refused even before the tick rejects the command.
        _h.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await _h.Service.ApproveAsync(late.Id, _h.Supervisor, _h.Session, Ct)).ShouldFailWith("commands.approval.expired");

        await _h.TickAsync();
        await _h.TickAsync(); // a second tick does not reject twice
        foreach (var id in new[] { waiting.Id, late.Id })
        {
            var command = _h.Command(id);
            command.State.ShouldBe(CommandState.Rejected);
            command.Failure!.MessageKey.ShouldBe("errors.commands.approval.expired");
            var rejections = _h.RecordsOf(id).Where(r => r.Envelope.Kind == CommandRecordKinds.Rejected).ToList();
            _h.Codec.Body<CommandRejectedBody>(rejections.ShouldHaveSingleItem()).Error.Code.ShouldBe("commands.approval.expired");
        }
    }

    [Fact]
    public async Task CMD_U_016_Priority_defaults_per_issuer_kind_and_risk()
    {
        foreach (var identity in new[] { TestIds.User(10), TestIds.User(11), TestIds.User(12), TestIds.User(13), TestIds.User(14) })
        {
            _h.Authorizer.Allowed[identity] = CommandConstraints.None;
        }

        async Task<CommandPriority> PriorityOf(CommandIssuer issuer, CommandPath? path = null, string? reason = null, CommandPriority? asked = null) =>
            _h.Command((await _h.AcceptAsync(_h.Request(path, issuer: issuer, reason: reason, priority: asked))).Id).Priority;

        (await PriorityOf(_h.User())).ShouldBe(CommandPriority.High);
        (await PriorityOf(new CommandIssuer.Federation(TestIds.Of<FederationLinkId>(1), "remote", TestIds.User(14)))).ShouldBe(CommandPriority.High);
        (await PriorityOf(new CommandIssuer.Automation(TestIds.Of<AutomationId>(1), TestIds.Of<AutomationRunId>(1), TestIds.User(10)))).ShouldBe(CommandPriority.Normal);
        (await PriorityOf(new CommandIssuer.Script(TestIds.Of<ScriptId>(1), TestIds.User(11)))).ShouldBe(CommandPriority.Normal);
        (await PriorityOf(new CommandIssuer.AlarmAction(TestIds.Of<AlarmId>(1), TestIds.Of<AlarmRuleId>(1), TestIds.User(12)))).ShouldBe(CommandPriority.Normal);
        (await PriorityOf(new CommandIssuer.Automation(TestIds.Of<AutomationId>(2), TestIds.Of<AutomationRunId>(2), TestIds.User(13), LifeSafety: true))).ShouldBe(CommandPriority.Critical);
        _h.Options.StepUpForCritical = false;
        (await PriorityOf(_h.User(_h.Supervisor), CommandsHarness.Lockdown, "drill")).ShouldBe(CommandPriority.Critical);

        // Lowering is always allowed.
        (await PriorityOf(_h.User(), asked: CommandPriority.Background)).ShouldBe(CommandPriority.Background);
        (await PriorityOf(_h.User(_h.Supervisor), CommandsHarness.Lockdown, "drill", CommandPriority.Normal)).ShouldBe(CommandPriority.Normal);
    }

    [Fact]
    public async Task CMD_U_017_Raising_to_Critical_requires_the_permission_or_a_life_safety_automation()
    {
        _h.Authorizer.Allowed[TestIds.User(10)] = CommandConstraints.None;
        async Task<CommandPriority> Asked(CommandIssuer issuer, CommandPriority asked) =>
            _h.Command((await _h.AcceptAsync(_h.Request(issuer: issuer, priority: asked))).Id).Priority;

        (await Asked(_h.User(), CommandPriority.Critical)).ShouldBe(CommandPriority.High);                  // no permission: ignored
        (await Asked(_h.User(_h.Supervisor), CommandPriority.Critical)).ShouldBe(CommandPriority.Critical); // commands.priority.critical
        (await Asked(new CommandIssuer.Automation(TestIds.Of<AutomationId>(1), TestIds.Of<AutomationRunId>(1), TestIds.User(10), LifeSafety: true), CommandPriority.Critical))
            .ShouldBe(CommandPriority.Critical);
        (await Asked(new CommandIssuer.Automation(TestIds.Of<AutomationId>(2), TestIds.Of<AutomationRunId>(2), TestIds.User(10)), CommandPriority.Critical))
            .ShouldBe(CommandPriority.Normal);
        (await Asked(new CommandIssuer.Automation(TestIds.Of<AutomationId>(3), TestIds.Of<AutomationRunId>(3), TestIds.User(10)), CommandPriority.High))
            .ShouldBe(CommandPriority.Normal);                                                                 // any other raise is ignored too
    }

    [Fact]
    public async Task CMD_U_018_NotBefore_more_than_24_h_ahead_is_rejected()
    {
        var now = _h.Time.GetUtcNow();

        (await _h.Service.RequestAsync(_h.Request(notBefore: now + TimeSpan.FromHours(24) + TimeSpan.FromMilliseconds(1)), Ct))
            .ShouldFailWith("commands.not_before.too_far").Args["maxHours"].ShouldBe(24);
        var accepted = await _h.AcceptAsync(_h.Request(notBefore: now + TimeSpan.FromHours(24)));
        _h.Command(accepted.Id).NotBefore.ShouldBe(now + TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task CMD_U_019_Expiry_defaults_to_5_min_plus_the_approval_window()
    {
        var now = _h.Time.GetUtcNow();
        var plain = await _h.AcceptAsync(_h.Request());
        var later = await _h.AcceptAsync(_h.Request(notBefore: now + TimeSpan.FromHours(1), timeout: TimeSpan.FromSeconds(3)));
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var approval = await _h.AcceptAsync(_h.Request());

        _h.Command(plain.Id).ExpiresAt.ShouldBe(now + TimeSpan.FromMinutes(5));
        _h.Command(plain.Id).Timeout.ShouldBe(TimeSpan.FromSeconds(10)); // the definition's
        _h.Command(later.Id).ExpiresAt.ShouldBe(now + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(5));
        _h.Command(later.Id).Timeout.ShouldBe(TimeSpan.FromSeconds(3));
        _h.Command(approval.Id).ExpiresAt.ShouldBe(now + TimeSpan.FromMinutes(10));

        using var configured = new CommandsHarness(o => o.DefaultExpiry = TimeSpan.FromMinutes(2));
        var short2 = await configured.AcceptAsync(configured.Request());
        configured.Command(short2.Id).ExpiresAt.ShouldBe(configured.Time.GetUtcNow() + TimeSpan.FromMinutes(2));
        (await configured.Service.RequestAsync(configured.Request(timeout: TimeSpan.Zero), Ct)).ShouldFailWith("common.validation.failed");
    }

    [Fact]
    public async Task CMD_U_024_Group_request_rejected_outright_when_the_group_validation_fails()
    {
        var second = _h.AddDoor(TestIds.Device(2));
        var supervisor = _h.User(_h.Supervisor);
        CommandRequest Group(CommandPath path, string? reason = null, string? stepUp = null) =>
            _h.Request(path, issuer: supervisor, reason: reason, stepUp: stepUp) with { Target = new CommandTargetSpec.Devices([_h.Door, second]) };

        // Sensitive children make a critical group: reason and step-up are decided once, before any child exists.
        (await _h.Service.RequestAsync(Group(CommandsHarness.Reset), Ct)).ShouldFailWith("commands.reason.required");
        (await _h.Service.RequestAsync(Group(CommandsHarness.Reset, "monthly reset"), Ct)).ShouldFailWith("identity.stepup.required");
        _h.Log.ShouldBeEmpty();

        _h.StepUp.Valid.Add(("proof-1", _h.Session));
        var group = await _h.AcceptAsync(Group(CommandsHarness.Reset, "monthly reset", "proof-1"));
        group.IsGroup.ShouldBeTrue();
        group.TargetCount.ShouldBe(2);
        group.State.ShouldBe(CommandState.Queued);
        var children = _h.Log.Where(r => r.Envelope.Kind == CommandRecordKinds.Requested).Select(r => _h.Codec.Body<Command>(r)).ToList();
        children.Select(c => c.Target).ShouldBe([_h.Door, second]);
        children.ShouldAllBe(c => c.GroupId == group.Id && c.EffectiveRisk == Risk.Sensitive);

        // Normal children make a sensitive group: no reason needed; a child that fails its own checks is rejected alone.
        var unknown = TestIds.Device(99);
        var normal = await _h.AcceptAsync(_h.Request(issuer: supervisor) with { Target = new CommandTargetSpec.Devices([_h.Door, unknown]) });
        normal.State.ShouldBe(CommandState.Queued);
        var refused = _h.Codec.Body<CommandRejectedBody>(_h.Log.Single(r => r.Envelope.Kind == CommandRecordKinds.Rejected));
        refused.GroupId.ShouldBe(normal.Id);
        refused.Error.Code.ShouldBe("commands.target.not_found");

        (await _h.Service.RequestAsync(_h.Request() with { Target = new CommandTargetSpec.Devices([.. Enumerable.Range(1, 5_001).Select(i => TestIds.Device(i))]) }, Ct))
            .ShouldFailWith("commands.group.too_large");
    }

    [Fact]
    public void CMD_U_026_Available_commands_reflect_the_authorizer_constraints()
    {
        var limited = _h.AddDoor(TestIds.Device(3), unsupported: "tamper");
        var all = _h.Service.GetAvailable(limited, _h.User());
        all.Select(c => c.Path.Value).ShouldBe(["lockdown.engage", "maintenance.reset", "openable.open", "openable.unlockFor"]);
        var lockdown = all[0];
        (lockdown.EffectiveRisk, lockdown.RequiresReason, lockdown.RequiresStepUp, lockdown.RequiresApproval).ShouldBe((Risk.Critical, true, true, false));
        var open = all[2];
        (open.EffectiveRisk, open.RequiresReason, open.RequiresStepUp, open.RequiresApproval, open.Batchable).ShouldBe((Risk.Normal, false, false, false, true));

        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ReasonRequired: true, StepUpRequired: true, ApprovalRequired: true);
        _h.Service.GetAvailable(limited, _h.User()).ShouldAllBe(c => c.RequiresReason && c.RequiresStepUp && c.RequiresApproval);

        // Step-up is for interactive users; denied users, other clients and unusable devices get nothing.
        _h.Authorizer.Allowed[TestIds.User(10)] = CommandConstraints.None;
        _h.Service.GetAvailable(limited, new CommandIssuer.Script(TestIds.Of<ScriptId>(1), TestIds.User(10))).ShouldAllBe(c => !c.RequiresStepUp);
        _h.Service.GetAvailable(limited, _h.User(TestIds.User(77))).ShouldBeEmpty();
        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(AllowedClientKinds: new HashSet<ClientKind> { ClientKind.Desktop });
        _h.Service.GetAvailable(limited, _h.User(client: ClientKind.Mobile)).ShouldBeEmpty();
        _h.Service.GetAvailable(TestIds.Device(98), _h.User()).ShouldBeEmpty();
        _h.Service.GetAvailable(_h.AddDoor(TestIds.Device(4), enabled: false), _h.User()).ShouldBeEmpty();
    }

    [Fact]
    public async Task CMD_U_027_Arguments_never_contain_personal_data()
    {
        // MOD command definitions declare no personal-data parameters (only event payloads do), and only declared
        // parameters reach the command and its record: anything else a client sends is dropped.
        foreach (var definition in CommandsHarness.DoorType.Commands.Values)
        {
            var declared = definition.Parameters.RawElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            var accepted = await _h.AcceptAsync(_h.Request(definition.Path, """{"cardholderName":"Jane Doe","badge":"0042"}""", _h.User(_h.Supervisor),
                reason: "check", stepUp: definition.Risk == Risk.Critical ? Proof() : null));
            _h.Command(accepted.Id).Arguments.Keys.ShouldAllBe(k => declared.Contains(k));
        }

        _h.Log.ShouldAllBe(r => !r.Envelope.Body.GetRawText().Contains("Jane Doe", StringComparison.Ordinal) && !r.Envelope.Body.GetRawText().Contains("cardholderName", StringComparison.Ordinal));

        string Proof()
        {
            var token = "proof-" + Guid.NewGuid().ToString("N");
            _h.StepUp.Valid.Add((token, _h.Session));
            return token;
        }
    }

    [Fact]
    public async Task CMD_U_028_Reason_length_limit()
    {
        (await _h.Service.RequestAsync(_h.Request(reason: new string('r', 500)), Ct)).ShouldBeSuccess();
        (await _h.Service.RequestAsync(_h.Request(reason: new string('r', 501)), Ct))
            .ShouldFailWith("common.validation.failed").ShouldHaveFieldError("/reason", ErrorCode.FromTrusted("common.value.too_long"));

        _h.Authorizer.Allowed[_h.Operator] = new CommandConstraints(ApprovalRequired: true);
        var waiting = await _h.AcceptAsync(_h.Request());
        (await _h.Service.RejectAsync(waiting.Id, _h.Supervisor, new string('r', 501), Ct)).ShouldFailWith("common.validation.failed");
        (await _h.Service.RejectAsync(waiting.Id, _h.Supervisor, new string('r', 500), Ct)).ShouldBeSuccess();
    }

    [Fact]
    public async Task CMD_U_029_Correlation_of_alarm_and_automation_run_is_propagated_to_every_record()
    {
        var alarm = TestIds.Of<AlarmId>(7);
        var run = TestIds.Of<AutomationRunId>(8);
        var accepted = await _h.AcceptAsync(_h.Request(correlation: new RecordCorrelation(Alarm: alarm, AutomationRun: run)));
        (await _h.Service.CancelAsync(accepted.Id, _h.User(), Ct)).ShouldBeSuccess();

        var records = _h.RecordsOf(accepted.Id);
        records.Select(r => r.Envelope.Kind).ShouldBe([CommandRecordKinds.Requested, CommandRecordKinds.Queued, CommandRecordKinds.Cancelled]);
        records.ShouldAllBe(r => r.Envelope.Corr == new RecordCorrelation(accepted.Id, alarm, run));
        _h.Command(accepted.Id).Correlation.ShouldBe(new RecordCorrelation(accepted.Id, alarm, run));

        // A refused request carries the correlation too.
        await _h.Service.RequestAsync(_h.Request(target: TestIds.Device(90), correlation: new RecordCorrelation(Alarm: alarm)), Ct);
        _h.Log[^1].Envelope.Corr.Alarm.ShouldBe(alarm);
    }
}
