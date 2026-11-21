using System.Text.Json;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.History.Contracts;

/// <summary>
/// The record kinds history consumes (spec HIS §5). <c>event</c> and <c>property</c> belong to the pipeline
/// (<c>RecordKinds</c>) and <c>command.*</c> to CMD, which registers them: history reads their JSON bodies itself and does not
/// depend on that module. <c>sysevent</c>, <c>operator.note</c> and <c>alarm.*</c> are registered by history until their owning modules
/// (EVT, ALM) exist and then shared with them: the body types below are the contract those modules write.
/// </summary>
public static class HistoryRecordKinds
{
    /// <summary>A system event (<see cref="SysEventBody"/>), in <c>WP_LOG</c> when device-bound, else <c>WP_SYS</c>.</summary>
    public const string SysEvent = "sysevent";

    /// <summary>An operator note (<see cref="OperatorNoteBody"/>).</summary>
    public const string OperatorNote = "operator.note";

    /// <summary>The filter of the alarms consumer.</summary>
    public const string AlarmFilter = "alarm.*";

    /// <summary>The filter of the commands consumer.</summary>
    public const string CommandFilter = "command.*";

    /// <summary>The alarm record kinds (ALM §7.2), all carrying an <see cref="AlarmTransitionBody"/>.</summary>
    public static IReadOnlyList<string> AlarmKinds { get; } =
    [
        "alarm.raised", "alarm.occurred", "alarm.acknowledged", "alarm.resolved", "alarm.closed", "alarm.shelved", "alarm.unshelved",
        "alarm.commented", "alarm.transferred", "alarm.procedureAnswered", "alarm.priorityChanged", "alarm.escalated", "alarm.actionExecuted",
    ];

}

/// <summary>Body of a <see cref="HistoryRecordKinds.SysEvent"/> record (EVT §6).</summary>
/// <param name="Kind">The system event kind, e.g. <c>identity.accountLocked</c>.</param>
/// <param name="Payload">The event fields.</param>
/// <param name="Severity">The severity from the system event catalogue; <see cref="Severity.Normal"/> when absent.</param>
/// <param name="PersonalFields">The payload fields that hold personal data (catalogue <c>PersonalDataFields</c>).</param>
public sealed record SysEventBody(
    string Kind,
    IReadOnlyDictionary<string, WpValue> Payload,
    Severity? Severity = null,
    IReadOnlyList<string>? PersonalFields = null);

/// <summary>Body of a <see cref="HistoryRecordKinds.OperatorNote"/> record (EVT §7).</summary>
/// <param name="Text">The note.</param>
/// <param name="Location">The location it concerns.</param>
/// <param name="Attachments">The attached files.</param>
public sealed record OperatorNoteBody(string Text, Guid? Location = null, IReadOnlyList<Guid>? Attachments = null);

/// <summary>The state of an alarm in history (ALM §7.2); stored as the numbers shown.</summary>
public enum AlarmHistoryState
{
    /// <summary>Raised and not acknowledged.</summary>
    Active = 0,

    /// <summary>Acknowledged, condition still present.</summary>
    Acknowledged = 1,

    /// <summary>Condition gone, not acknowledged yet.</summary>
    ActiveResolved = 2,

    /// <summary>Acknowledged and condition gone.</summary>
    Resolved = 3,

    /// <summary>Closed.</summary>
    Closed = 4,

    /// <summary>Shelved.</summary>
    Shelved = 5,
}

/// <summary>Who did something to an alarm.</summary>
/// <param name="User">The user; <see langword="null"/> for the system.</param>
/// <param name="Session">The session.</param>
/// <param name="ClientKind">The client kind as a number.</param>
public sealed record AlarmActor(Guid? User, Guid? Session, short? ClientKind);

/// <summary>
/// Body of every <c>alarm.*</c> record: the alarm as it is after the transition, plus who caused it (ALM §7.2). History
/// folds these into one row per alarm and one timeline entry per record.
/// </summary>
/// <param name="Id">The alarm id.</param>
/// <param name="RuleId">The rule.</param>
/// <param name="RuleKey">The rule key.</param>
/// <param name="RuleVersion">The rule version.</param>
/// <param name="State">The state after the transition.</param>
/// <param name="Priority">The priority after the transition.</param>
/// <param name="InitialPriority">The priority when raised.</param>
/// <param name="Count">The number of occurrences.</param>
/// <param name="RaisedAt">When the alarm was raised: the key of the history row, the same in every record of the alarm.</param>
/// <param name="LastOccurrenceAt">The latest occurrence.</param>
/// <param name="SourceId">The device or server the alarm is about (the record's <c>dev</c> or <c>srv</c> when absent).</param>
/// <param name="SourceKind">0 for a device, 1 for a server.</param>
/// <param name="AcknowledgedAt">When acknowledged.</param>
/// <param name="AcknowledgedBy">Who acknowledged.</param>
/// <param name="ResolvedAt">When resolved.</param>
/// <param name="ClosedAt">When closed.</param>
/// <param name="ClosedBy">Who closed.</param>
/// <param name="Report">The closing report.</param>
/// <param name="Owner">The owner (user or user group), as JSON.</param>
/// <param name="EscalationLevel">The escalation level.</param>
/// <param name="ProcedureId">The procedure.</param>
/// <param name="ProcedureVersion">The procedure version.</param>
/// <param name="FalseAlarm">Whether the operator called it a false alarm (procedure answer).</param>
/// <param name="Actor">Who caused the transition.</param>
/// <param name="Detail">Kind-specific detail kept in the timeline (comment text, shelve duration, answer…).</param>
public sealed record AlarmTransitionBody(
    Guid Id,
    Guid RuleId,
    string RuleKey,
    long RuleVersion,
    AlarmHistoryState State,
    int Priority,
    int InitialPriority,
    int Count,
    DateTimeOffset RaisedAt,
    DateTimeOffset LastOccurrenceAt,
    Guid? SourceId = null,
    short? SourceKind = null,
    DateTimeOffset? AcknowledgedAt = null,
    Guid? AcknowledgedBy = null,
    DateTimeOffset? ResolvedAt = null,
    DateTimeOffset? ClosedAt = null,
    Guid? ClosedBy = null,
    string? Report = null,
    JsonElement? Owner = null,
    int EscalationLevel = 0,
    Guid? ProcedureId = null,
    long? ProcedureVersion = null,
    bool? FalseAlarm = null,
    AlarmActor? Actor = null,
    JsonElement? Detail = null);

/// <summary>The state of a command in history (CMD §4 <c>CommandState</c>); stored as the numbers shown.</summary>
public enum CommandHistoryState
{
    /// <summary>Requested.</summary>
    Requested = 0,

    /// <summary>Rejected by validation, or the approval was refused.</summary>
    Rejected = 1,

    /// <summary>Waiting for approval (also after the approval, until the command is queued).</summary>
    AwaitingApproval = 2,

    /// <summary>Queued for dispatch.</summary>
    Queued = 3,

    /// <summary>Sent to the integration.</summary>
    Dispatched = 4,

    /// <summary>Executed.</summary>
    Succeeded = 5,

    /// <summary>Failed.</summary>
    Failed = 6,

    /// <summary>No answer in time.</summary>
    TimedOut = 7,

    /// <summary>Expired before dispatch.</summary>
    Expired = 8,

    /// <summary>Cancelled.</summary>
    Cancelled = 9,

    /// <summary>The expected effect was observed.</summary>
    Verified = 10,

    /// <summary>The expected effect was not observed.</summary>
    Unverified = 11,
}
