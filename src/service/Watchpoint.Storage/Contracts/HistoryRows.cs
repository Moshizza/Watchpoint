namespace Watchpoint.Storage.Contracts;

/// <summary>A row of <c>wp_history.events</c> (spec HIS §4). JSON columns are carried as JSON text.</summary>
/// <param name="Time">The received time.</param>
/// <param name="Id">The record id.</param>
/// <param name="Seq">The <c>WP_LOG</c> sequence, or 0 for <c>WP_SYS</c>.</param>
/// <param name="Kind">The event kind.</param>
/// <param name="Category">The event category (<c>EventCategory</c> as a number).</param>
/// <param name="Severity">The severity (<c>Severity</c> as a number).</param>
/// <param name="SourceTime">The source time, if known.</param>
/// <param name="EntityId">The device or server the event is about.</param>
/// <param name="EntityKind">0 for a device, 1 for a server.</param>
/// <param name="ServerId">The server.</param>
/// <param name="LocationId">The location of the source.</param>
/// <param name="LocationIds">The location and its ancestors, root first.</param>
/// <param name="TypeId">The type of the source.</param>
/// <param name="TagKeys">The tag keys of the source.</param>
/// <param name="OriginType">The origin type (<c>RecordOriginType</c> as a number).</param>
/// <param name="OriginId">The origin id.</param>
/// <param name="PayloadJson">The payload without personal fields (JSON object).</param>
/// <param name="PersonalJson">The personal fields only (JSON object), or <see langword="null"/>.</param>
/// <param name="PersonRefs">The holder and credential references of the payload.</param>
/// <param name="TitleKey">The translation key of the title.</param>
/// <param name="TitleArgsJson">The title arguments (JSON object).</param>
/// <param name="SourceName">The display name of the source at the time.</param>
/// <param name="LocationNames">The names of the location and its ancestors.</param>
/// <param name="CorrelationJson">The correlation (JSON object), or <see langword="null"/>.</param>
public sealed record HistoryEventRow(
    DateTimeOffset Time,
    Guid Id,
    long Seq,
    string Kind,
    short Category,
    short Severity,
    DateTimeOffset? SourceTime,
    Guid? EntityId,
    short? EntityKind,
    Guid? ServerId,
    Guid? LocationId,
    IReadOnlyList<Guid>? LocationIds,
    string? TypeId,
    IReadOnlyList<string>? TagKeys,
    short OriginType,
    Guid? OriginId,
    string PayloadJson,
    string? PersonalJson,
    IReadOnlyList<string>? PersonRefs,
    string TitleKey,
    string TitleArgsJson,
    string? SourceName,
    IReadOnlyList<string>? LocationNames,
    string? CorrelationJson);

/// <summary>A row of <c>wp_history.alarms</c> (spec HIS §4), plus <see cref="Seq"/>, the sequence of the newest record merged into it.</summary>
/// <param name="Time">The time the alarm was raised.</param>
/// <param name="Id">The alarm id.</param>
/// <param name="Seq">The log sequence of the newest record merged into the row (state and final priority follow it).</param>
/// <param name="RuleId">The rule.</param>
/// <param name="RuleKey">The rule key.</param>
/// <param name="RuleVersion">The rule version.</param>
/// <param name="SourceId">The source.</param>
/// <param name="SourceKind">The source kind.</param>
/// <param name="SourceName">The source name at the time.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationIds">The location and its ancestors.</param>
/// <param name="TypeId">The type of the source.</param>
/// <param name="TagKeys">The tag keys of the source.</param>
/// <param name="State">The alarm state.</param>
/// <param name="PriorityInitial">The priority when raised.</param>
/// <param name="PriorityFinal">The latest priority.</param>
/// <param name="Count">The number of occurrences.</param>
/// <param name="LastOccurrenceAt">The latest occurrence.</param>
/// <param name="AcknowledgedAt">When acknowledged (set once).</param>
/// <param name="AcknowledgedBy">Who acknowledged.</param>
/// <param name="ResolvedAt">When resolved (set once).</param>
/// <param name="ClosedAt">When closed (set once).</param>
/// <param name="ClosedBy">Who closed.</param>
/// <param name="Report">The closing report.</param>
/// <param name="OwnerJson">The owner (JSON), or <see langword="null"/>.</param>
/// <param name="EscalationLevel">The escalation level.</param>
/// <param name="ProcedureId">The procedure.</param>
/// <param name="ProcedureVersion">The procedure version.</param>
/// <param name="FalseAlarm">Whether the operator called it a false alarm.</param>
public sealed record HistoryAlarmRow(
    DateTimeOffset Time,
    Guid Id,
    long Seq,
    Guid RuleId,
    string RuleKey,
    long RuleVersion,
    Guid? SourceId,
    short? SourceKind,
    string? SourceName,
    Guid? LocationId,
    IReadOnlyList<Guid>? LocationIds,
    string? TypeId,
    IReadOnlyList<string>? TagKeys,
    short State,
    int PriorityInitial,
    int PriorityFinal,
    int Count,
    DateTimeOffset LastOccurrenceAt,
    DateTimeOffset? AcknowledgedAt,
    Guid? AcknowledgedBy,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    Guid? ClosedBy,
    string? Report,
    string? OwnerJson,
    int EscalationLevel,
    Guid? ProcedureId,
    long? ProcedureVersion,
    bool? FalseAlarm);

/// <summary>A row of <c>wp_history.alarm_timeline</c> (spec HIS §4).</summary>
/// <param name="Time">The record time.</param>
/// <param name="AlarmId">The alarm.</param>
/// <param name="RecordId">The record id (idempotency key).</param>
/// <param name="Kind">The record kind.</param>
/// <param name="ActorUser">The acting user.</param>
/// <param name="ActorSession">The acting session.</param>
/// <param name="ClientKind">The client kind.</param>
/// <param name="BodyJson">The record detail (JSON object).</param>
public sealed record HistoryAlarmTimelineRow(
    DateTimeOffset Time,
    Guid AlarmId,
    Guid RecordId,
    string Kind,
    Guid? ActorUser,
    Guid? ActorSession,
    short? ClientKind,
    string BodyJson);

/// <summary>A row of <c>wp_history.commands</c> (spec HIS §4), plus <see cref="Seq"/>. A row built from a record that is not the request has an empty <see cref="Path"/>.</summary>
/// <param name="Time">The time the command was requested.</param>
/// <param name="Id">The command id.</param>
/// <param name="Seq">The log sequence of the newest record merged into the row.</param>
/// <param name="GroupId">The command group.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="DeviceName">The device name at the time.</param>
/// <param name="LocationIds">The location and its ancestors.</param>
/// <param name="Path">The command path; empty when the request record is not merged yet.</param>
/// <param name="ArgumentsJson">The arguments (JSON object).</param>
/// <param name="IssuerJson">The issuer (JSON object).</param>
/// <param name="IssuerUser">The issuing user.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Risk">The risk.</param>
/// <param name="Priority">The priority.</param>
/// <param name="State">The state.</param>
/// <param name="FailureJson">The failure (JSON), or <see langword="null"/>.</param>
/// <param name="QueuedAt">When queued (set once).</param>
/// <param name="DispatchedAt">When first dispatched (set once).</param>
/// <param name="CompletedAt">When finished (set once).</param>
/// <param name="VerifiedAt">When verified (set once).</param>
/// <param name="Attempt">The attempt number.</param>
/// <param name="ApprovalJson">The approval (JSON), or <see langword="null"/>.</param>
/// <param name="CorrelationJson">The correlation (JSON), or <see langword="null"/>.</param>
public sealed record HistoryCommandRow(
    DateTimeOffset Time,
    Guid Id,
    long Seq,
    Guid? GroupId,
    Guid DeviceId,
    string? DeviceName,
    IReadOnlyList<Guid>? LocationIds,
    string Path,
    string ArgumentsJson,
    string IssuerJson,
    Guid? IssuerUser,
    string? Reason,
    short Risk,
    short Priority,
    short State,
    string? FailureJson,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? VerifiedAt,
    int Attempt,
    string? ApprovalJson,
    string? CorrelationJson);

/// <summary>
/// A row of <c>wp_history.property_changes</c> (spec HIS §4). Exactly one of the value columns is set, none for a null value.
/// </summary>
/// <param name="Time">The time of the change.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="Path">The property path.</param>
/// <param name="VNum">Numeric value (integers, decimals, durations in seconds).</param>
/// <param name="VText">Text value (enums, strings, ISO timestamps).</param>
/// <param name="VBool">Boolean value.</param>
/// <param name="VJson">JSON value (geo points, lists, objects).</param>
/// <param name="Quality">The quality as a number.</param>
/// <param name="SourceTime">The source time.</param>
/// <param name="RecordId">The record id (idempotency key).</param>
public sealed record HistoryPropertyRow(
    DateTimeOffset Time,
    Guid DeviceId,
    string Path,
    decimal? VNum,
    string? VText,
    bool? VBool,
    string? VJson,
    short Quality,
    DateTimeOffset? SourceTime,
    Guid RecordId);

/// <summary>A property history policy override stored in <c>wp_history.policy_overrides</c> (spec HIS §6).</summary>
/// <param name="Scope"><c>device</c> or <c>type</c>.</param>
/// <param name="Key">The device id, or the type id.</param>
/// <param name="Path">The property path.</param>
/// <param name="PolicyJson">The policy document (<c>mode</c>, <c>type</c>, <c>threshold</c>, <c>interval</c>).</param>
public sealed record HistoryPolicyOverrideRow(string Scope, string Key, string Path, string PolicyJson);
