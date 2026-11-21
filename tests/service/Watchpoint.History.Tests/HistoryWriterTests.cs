using System.Text.Json;
using Shouldly;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal.Writers;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Xunit;

namespace Watchpoint.History.Tests;

[Trait("layer", "U")]
[Trait("spec", "HIS")]
public sealed class HistoryWriterTests : IDisposable
{
    private readonly HistoryHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void HIS_U_001_Event_record_maps_to_row_with_personal_split_and_person_refs()
    {
        var payload = new Dictionary<string, WpValue>
        {
            ["name"] = WpValue.String("Ann Example"),
            ["holderRef"] = WpValue.String("holder-42"),
            ["credentialRef"] = WpValue.String("cred-7"),
            ["reader"] = WpValue.String("R1"),
        };
        var correlation = new RecordCorrelation(CommandId.From(Watchpoint.Testing.Ids.TestIds.Uuid(900)));
        var record = _h.Record(
            41, RecordKinds.Event, new EventRecordBody("credential.rejected", payload, Severity.Warning), ts: _h.Time.GetUtcNow(),
            origin: new RecordOrigin(RecordOriginType.Connection, Watchpoint.Testing.Ids.TestIds.Uuid(5)), correlation: correlation, source: _h.Time.GetUtcNow().AddSeconds(-2));

        var row = _h.EventMapper().Map(record, LogStream.Log).ShouldNotBeNull();

        row.Id.ShouldBe(record.Envelope.Id);
        row.Seq.ShouldBe(41);
        row.Time.ShouldBe(record.Envelope.Ts);
        row.Kind.ShouldBe("credential.rejected");
        row.Category.ShouldBe((short)EventCategory.Device);
        row.Severity.ShouldBe((short)Severity.Warning);
        row.SourceTime.ShouldBe(record.Envelope.Src);
        row.EntityId.ShouldBe(HistoryHarness.Door.Value);
        row.EntityKind.ShouldBe((short)0);
        row.ServerId.ShouldBe(HistoryHarness.Server.Value);
        row.LocationId.ShouldBe(HistoryHarness.LocationFloor);
        row.LocationIds.ShouldBe([HistoryHarness.LocationRoot, HistoryHarness.LocationFloor]);
        row.LocationNames.ShouldBe(["Site", "Floor 1"]);
        row.TagKeys.ShouldBe(["entrance", "vip"]);
        row.TypeId.ShouldBe("ac.door");
        row.SourceName.ShouldBe("Main door");
        row.OriginType.ShouldBe((short)RecordOriginType.Connection);
        row.OriginId.ShouldBe(Watchpoint.Testing.Ids.TestIds.Uuid(5));

        // Personal fields leave the payload and the title arguments; their references are searchable.
        using var open = JsonDocument.Parse(row.PayloadJson);
        open.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["reader"]);
        using var personal = JsonDocument.Parse(row.PersonalJson!);
        personal.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["credentialRef", "holderRef", "name"]);
        row.PersonRefs.ShouldNotBeNull().Order().ShouldBe(["cred-7", "holder-42"]);
        row.TitleKey.ShouldBe("capabilities.credential.events.rejected.title");
        using var title = JsonDocument.Parse(row.TitleArgsJson);
        title.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["device", "reader"]);
        row.PayloadJson.ShouldNotContain("Ann");
        row.TitleArgsJson.ShouldNotContain("Ann");
        row.CorrelationJson.ShouldNotBeNull().ShouldContain(correlation.Command!.Value.Value.ToString("D"));

        // A health event of the pipeline is a health event; an event without personal fields has none stored.
        var health = _h.Record(42, RecordKinds.Event, new EventRecordBody("health.wentOffline", new Dictionary<string, WpValue>(), Severity.Offline));
        var healthRow = _h.EventMapper().Map(health, LogStream.Log).ShouldNotBeNull();
        healthRow.Category.ShouldBe((short)EventCategory.Health);
        healthRow.PersonalJson.ShouldBeNull();
        healthRow.PersonRefs.ShouldBeNull();
    }

    [Fact]
    public void HIS_U_002_System_events_and_notes_from_WP_SYS_get_sequence_zero()
    {
        var body = new SysEventBody(
            "identity.accountLocked",
            new Dictionary<string, WpValue> { ["user"] = WpValue.String("jdoe"), ["reason"] = WpValue.String("tries") },
            Severity.Warning,
            ["user"]);
        var record = _h.Record(77, HistoryRecordKinds.SysEvent, body, global: true, origin: new RecordOrigin(RecordOriginType.System, null));

        var sys = _h.EventMapper().Map(record, LogStream.Sys).ShouldNotBeNull();
        sys.Seq.ShouldBe(0);
        sys.Kind.ShouldBe("identity.accountLocked");
        sys.Category.ShouldBe((short)EventCategory.System);
        sys.Severity.ShouldBe((short)Severity.Warning);
        sys.EntityId.ShouldBeNull();
        sys.LocationIds.ShouldBeNull();
        sys.TitleKey.ShouldBe("events.identity.accountLocked.title");
        sys.PersonalJson.ShouldNotBeNull().ShouldContain("jdoe");
        sys.PayloadJson.ShouldNotContain("jdoe");

        // The same kind in a device-bound partition keeps its log sequence and the device.
        var bound = _h.Record(78, HistoryRecordKinds.SysEvent, body with { Severity = null });
        var boundRow = _h.EventMapper().Map(bound, LogStream.Log).ShouldNotBeNull();
        boundRow.Seq.ShouldBe(78);
        boundRow.EntityId.ShouldBe(HistoryHarness.Door.Value);
        boundRow.Severity.ShouldBe((short)Severity.Normal);

        var author = Watchpoint.Testing.Ids.TestIds.Uuid(66);
        var note = _h.Record(79, HistoryRecordKinds.OperatorNote, new OperatorNoteBody("Door repaired"), global: true, origin: new RecordOrigin(RecordOriginType.User, author));
        var noteRow = _h.EventMapper().Map(note, LogStream.Sys).ShouldNotBeNull();
        noteRow.Seq.ShouldBe(0);
        noteRow.Category.ShouldBe((short)EventCategory.Operator);
        noteRow.OriginId.ShouldBe(author);
        noteRow.TitleKey.ShouldBe("events.operator.note");
        noteRow.PayloadJson.ShouldContain("Door repaired");

        // Other kinds are not events.
        _h.EventMapper().Map(_h.Property(80, "t.temp", WpValue.Int(1)), LogStream.Log).ShouldBeNull();
    }

    private static readonly Guid _alarm = Watchpoint.Testing.Ids.TestIds.Uuid(500);

    private AlarmTransitionBody Alarm(AlarmHistoryState state, int count, Guid? alarm = null, int priority = 3, DateTimeOffset? ack = null, DateTimeOffset? closed = null,
        DateTimeOffset? resolved = null, string? report = null, Guid? by = null, AlarmActor? actor = null, JsonElement? detail = null) => new(
        alarm ?? _alarm, Watchpoint.Testing.Ids.TestIds.Uuid(501), "door.forced", 2, state, priority, 3, count, _raised, _raised.AddMinutes(count),
        AcknowledgedAt: ack, AcknowledgedBy: ack is null ? null : by, ResolvedAt: resolved, ClosedAt: closed, ClosedBy: closed is null ? null : by, Report: report,
        Actor: actor, Detail: detail);

    private readonly DateTimeOffset _raised = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HIS_U_003_Alarm_transitions_fold_into_one_row_with_monotonic_merge()
    {
        var user1 = Watchpoint.Testing.Ids.TestIds.Uuid(61);
        var user2 = Watchpoint.Testing.Ids.TestIds.Uuid(62);
        var records = new[]
        {
            _h.Record(10, "alarm.raised", Alarm(AlarmHistoryState.Active, 1)),
            _h.Record(11, "alarm.occurred", Alarm(AlarmHistoryState.Active, 2)),
            _h.Record(12, "alarm.acknowledged", Alarm(AlarmHistoryState.Acknowledged, 2, ack: _raised.AddMinutes(3), by: user1)),

            // A later record repeats the acknowledgement with another time and user: the first one stays.
            _h.Record(13, "alarm.priorityChanged", Alarm(AlarmHistoryState.Acknowledged, 2, priority: 5, ack: _raised.AddMinutes(9), by: user2)),
            _h.Record(14, "alarm.resolved", Alarm(AlarmHistoryState.Resolved, 2, priority: 5, ack: _raised.AddMinutes(3), resolved: _raised.AddMinutes(4), by: user1)),
            _h.Record(15, "alarm.closed", Alarm(AlarmHistoryState.Closed, 2, priority: 5, ack: _raised.AddMinutes(3), resolved: _raised.AddMinutes(4), closed: _raised.AddMinutes(5), report: "false alarm", by: user2)),
            _h.Record(16, "alarm.raised", Alarm(AlarmHistoryState.Active, 1, alarm: Watchpoint.Testing.Ids.TestIds.Uuid(502))),
        };

        var (alarms, timeline) = _h.AlarmFolder().Fold(records);

        alarms.Count.ShouldBe(2);
        var row = alarms.Single(a => a.Id == _alarm);
        row.Time.ShouldBe(_raised);
        row.Seq.ShouldBe(15);
        row.State.ShouldBe((short)AlarmHistoryState.Closed);
        row.PriorityInitial.ShouldBe(3);
        row.PriorityFinal.ShouldBe(5);
        row.Count.ShouldBe(2);
        row.LastOccurrenceAt.ShouldBe(_raised.AddMinutes(2));
        row.AcknowledgedAt.ShouldBe(_raised.AddMinutes(3));
        row.AcknowledgedBy.ShouldBe(user1);
        row.ResolvedAt.ShouldBe(_raised.AddMinutes(4));
        row.ClosedAt.ShouldBe(_raised.AddMinutes(5));
        row.ClosedBy.ShouldBe(user2);
        row.Report.ShouldBe("false alarm");
        row.SourceId.ShouldBe(HistoryHarness.Door.Value);
        row.SourceName.ShouldBe("Main door");
        row.LocationIds.ShouldBe([HistoryHarness.LocationRoot, HistoryHarness.LocationFloor]);
        row.TypeId.ShouldBe("ac.door");
        timeline.Count.ShouldBe(7);
    }

    [Fact]
    public void HIS_U_004_Out_of_order_redelivery_does_not_regress_alarm_state()
    {
        var closed = _h.Record(15, "alarm.closed", Alarm(AlarmHistoryState.Closed, 3, priority: 5, closed: _raised.AddMinutes(5), report: "done", by: Watchpoint.Testing.Ids.TestIds.Uuid(61)));
        var acknowledged = _h.Record(12, "alarm.acknowledged", Alarm(AlarmHistoryState.Acknowledged, 2, ack: _raised.AddMinutes(3), by: Watchpoint.Testing.Ids.TestIds.Uuid(62)));
        var raised = _h.Record(10, "alarm.raised", Alarm(AlarmHistoryState.Active, 1));

        // The newest record arrives first, older ones after it.
        var (rows, _) = _h.AlarmFolder().Fold([closed, acknowledged, raised]);
        var row = rows.ShouldHaveSingleItem();
        row.State.ShouldBe((short)AlarmHistoryState.Closed);
        row.PriorityFinal.ShouldBe(5);
        row.Count.ShouldBe(3);
        row.Seq.ShouldBe(15);
        row.Report.ShouldBe("done");
        row.AcknowledgedAt.ShouldBe(_raised.AddMinutes(3));

        // A batch redelivered after the row was stored (the database merges it with the same rule): nothing changes.
        var (again, _) = _h.AlarmFolder().Fold([acknowledged, raised]);
        var merged = AlarmFolder.Merge(row, again.Single());
        merged.ShouldBe(row);
        AlarmFolder.Merge(row, row).ShouldBe(row);

        // An older record merged into a newer row fills what the row lacks but never moves state, priority or report back.
        var older = again.Single() with { Report = "stale", PriorityFinal = 1 };
        var after = AlarmFolder.Merge(row, older);
        after.State.ShouldBe(row.State);
        after.PriorityFinal.ShouldBe(5);
        after.Report.ShouldBe("done");
    }

    [Fact]
    public void HIS_U_005_Timeline_has_one_entry_per_transition_with_the_actor()
    {
        var user = Watchpoint.Testing.Ids.TestIds.Uuid(61);
        var session = Watchpoint.Testing.Ids.TestIds.Uuid(71);
        var comment = JsonDocument.Parse("""{"text":"checked on site"}""").RootElement.Clone();
        var raised = _h.Record(10, "alarm.raised", Alarm(AlarmHistoryState.Active, 1), ts: _raised);
        var ack = _h.Record(11, "alarm.acknowledged", Alarm(AlarmHistoryState.Acknowledged, 1, ack: _raised.AddMinutes(1), by: user, actor: new AlarmActor(user, session, 2)), ts: _raised.AddMinutes(1));
        var commented = _h.Record(12, "alarm.commented", Alarm(AlarmHistoryState.Acknowledged, 1, ack: _raised.AddMinutes(1), by: user, actor: new AlarmActor(user, session, 2), detail: comment), ts: _raised.AddMinutes(2));

        var (_, timeline) = _h.AlarmFolder().Fold([raised, ack, commented]);

        timeline.Select(t => t.Kind).ShouldBe(["alarm.raised", "alarm.acknowledged", "alarm.commented"]);
        timeline.Select(t => t.RecordId).ShouldBe([raised.Envelope.Id, ack.Envelope.Id, commented.Envelope.Id]);
        timeline.Select(t => t.Time).ShouldBe([_raised, _raised.AddMinutes(1), _raised.AddMinutes(2)]);
        timeline.ShouldAllBe(t => t.AlarmId == _alarm);
        timeline[0].ActorUser.ShouldBeNull();
        timeline[1].ActorUser.ShouldBe(user);
        timeline[1].ActorSession.ShouldBe(session);
        timeline[1].ClientKind.ShouldBe((short)2);
        using var body = JsonDocument.Parse(timeline[2].BodyJson);
        body.RootElement.GetProperty("state").GetString().ShouldBe("acknowledged");
        body.RootElement.GetProperty("detail").GetProperty("text").GetString().ShouldBe("checked on site");

        // The same records again produce the same timeline rows (the database ignores rows whose record id exists).
        var (_, again) = _h.AlarmFolder().Fold([raised, ack, commented]);
        again.ShouldBe(timeline);
    }

    [Fact]
    public void HIS_U_006_Command_lifecycle_folds_into_one_row()
    {
        var requested = _raised;
        var id = Watchpoint.Testing.Ids.TestIds.Uuid(800);
        var user = Watchpoint.Testing.Ids.TestIds.Uuid(61);
        var args = JsonDocument.Parse("""{"duration":"PT5S"}""").RootElement.Clone();
        var issuer = JsonDocument.Parse("""{"type":"user"}""").RootElement.Clone();
        var corr = new RecordCorrelation(CommandId.From(id));

        LogRecord Rec(long seq, string kind, CommandTransitionBody body, int seconds) =>
            _h.Record(seq, kind, body, ts: requested.AddSeconds(seconds), correlation: corr);

        var records = new[]
        {
            Rec(30, "command.requested", new CommandTransitionBody(id, requested, Path: "openable.open", Arguments: args, Issuer: issuer, IssuerUser: user, Reason: "visitor", Risk: 2, Priority: 1), 0),
            Rec(31, "command.queued", new CommandTransitionBody(id, requested, Priority: 4), 1),
            Rec(32, "command.dispatched", new CommandTransitionBody(id, requested, Attempt: 1), 2),
            Rec(33, "command.failed", new CommandTransitionBody(id, requested, Failure: JsonDocument.Parse("""{"code":"timeout"}""").RootElement.Clone()), 3),
            Rec(34, "command.dispatched", new CommandTransitionBody(id, requested, Attempt: 2), 4),
            Rec(35, "command.succeeded", new CommandTransitionBody(id, requested), 5),
            Rec(36, "command.verified", new CommandTransitionBody(id, requested), 7),
        };

        var row = _h.CommandFolder().Fold(records).ShouldHaveSingleItem();

        row.Id.ShouldBe(id);
        row.Time.ShouldBe(requested);
        row.DeviceId.ShouldBe(HistoryHarness.Door.Value);
        row.DeviceName.ShouldBe("Main door");
        row.Path.ShouldBe("openable.open");
        row.ArgumentsJson.ShouldContain("PT5S");
        row.IssuerUser.ShouldBe(user);
        row.Reason.ShouldBe("visitor");
        row.Risk.ShouldBe((short)2);
        row.Priority.ShouldBe((short)4);
        row.State.ShouldBe((short)CommandHistoryState.Verified);
        row.QueuedAt.ShouldBe(requested.AddSeconds(1));
        row.DispatchedAt.ShouldBe(requested.AddSeconds(2));
        row.CompletedAt.ShouldBe(requested.AddSeconds(3));
        row.VerifiedAt.ShouldBe(requested.AddSeconds(7));
        row.Attempt.ShouldBe(2);
        row.FailureJson.ShouldNotBeNull().ShouldContain("timeout");
        row.Seq.ShouldBe(36);
        row.CorrelationJson.ShouldNotBeNull().ShouldContain(id.ToString("D"));

        // The request arriving after the later records (a batch boundary or a redelivery) still ends in the same row.
        var late = _h.CommandFolder().Fold(records[1..]).Single();
        late.Path.ShouldBeEmpty();
        late.Risk.ShouldBe((short)-1);
        var merged = CommandFolder.Merge(late, _h.CommandFolder().Fold(records[..1]).Single());
        merged.Path.ShouldBe("openable.open");
        merged.Risk.ShouldBe((short)2);
        merged.State.ShouldBe((short)CommandHistoryState.Verified);
        merged.Priority.ShouldBe((short)4);
        merged.ShouldBe(row);

        // A record without a device or with an unknown kind is skipped, not folded.
        var skipped = new List<string>();
        _h.CommandFolder().Fold([_h.Record(40, "command.requested", new CommandTransitionBody(id, requested), server: HistoryHarness.Server)], (_, why) => skipped.Add(why)).ShouldBeEmpty();
        skipped.ShouldHaveSingleItem();
    }

    [Fact]
    public void HIS_U_007_Values_map_to_columns_for_every_kind()
    {
        ValueColumns.From(WpValue.Bool(true)).ShouldBe(new ValueColumns(null, null, true, null));
        ValueColumns.From(WpValue.Bool(false)).Bool.ShouldBe(false);
        ValueColumns.From(WpValue.Int(-42)).ShouldBe(new ValueColumns(-42m, null, null, null));
        ValueColumns.From(WpValue.Decimal(21.5m)).ShouldBe(new ValueColumns(21.5m, null, null, null));
        ValueColumns.From(WpValue.Enum("heldOpen")).ShouldBe(new ValueColumns(null, "heldOpen", null, null));
        ValueColumns.From(WpValue.String("héllo")).ShouldBe(new ValueColumns(null, "héllo", null, null));
        ValueColumns.From(WpValue.Timestamp(new DateTimeOffset(2026, 1, 5, 9, 30, 15, 250, TimeSpan.Zero))).Text.ShouldBe("2026-01-05T09:30:15.250Z");
        ValueColumns.From(WpValue.Duration(TimeSpan.FromMinutes(2.5))).ShouldBe(new ValueColumns(150m, null, null, null));
        ValueColumns.From(WpValue.Duration(TimeSpan.FromMilliseconds(1500))).Num.ShouldBe(1.5m);
        ValueColumns.From(WpValue.Null).ShouldBe(default);

        var geo = ValueColumns.From(WpValue.Geo(48.85, 2.35));
        geo.Json.ShouldNotBeNull().ShouldContain("48.85");
        geo.Num.ShouldBeNull();
        var list = ValueColumns.From(WpValue.List([WpValue.Int(1), WpValue.Int(2)]));
        list.Json.ShouldNotBeNull();
        using (var parsed = JsonDocument.Parse(list.Json))
        {
            parsed.RootElement.ToString().ShouldContain("1");
        }

        var obj = ValueColumns.From(WpValue.Object(JsonDocument.Parse("""{"a":1}""").RootElement));
        obj.Json.ShouldNotBeNull().ShouldContain("\"a\"");
        obj.Text.ShouldBeNull();

        // The JSON column reads back to the value written.
        JsonSerializer.Deserialize(list.Json, Internal.HistoryJson.Info<WpValue>()).ShouldBe(WpValue.List([WpValue.Int(1), WpValue.Int(2)]));
    }
}
