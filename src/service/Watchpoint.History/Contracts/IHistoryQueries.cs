using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Contracts;

/// <summary>Read side of history (spec HIS §7). Failures are returned as errors (<see cref="HistoryErrors"/>), never thrown.</summary>
public interface IHistoryQueries
{
    /// <summary>Queries the journal of events, keyset-paged on (time, id).</summary>
    /// <param name="query">The query.</param>
    /// <param name="page">The page request.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page, or <c>history.query.range_too_large</c> (more than 31 days), <c>history.query.invalid</c>, <c>common.cursor.invalid</c>, <c>history.query.timeout</c>.</returns>
    Task<Result<Page<JournalEvent>>> QueryEventsAsync(EventQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Returns the timeline of an alarm, oldest first.</summary>
    /// <param name="id">The alarm.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The entries, or <c>history.query.timeout</c>.</returns>
    Task<Result<IReadOnlyList<AlarmTimelineEntry>>> GetAlarmTimelineAsync(AlarmId id, CancellationToken ct);

    /// <summary>Queries alarms, keyset-paged on (raised time, id).</summary>
    /// <param name="query">The query.</param>
    /// <param name="page">The page request.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page, or an error as for <see cref="QueryEventsAsync"/>.</returns>
    Task<Result<Page<AlarmHistoryRow>>> QueryAlarmsAsync(AlarmHistoryQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Queries commands, keyset-paged on (requested time, id).</summary>
    /// <param name="query">The query.</param>
    /// <param name="page">The page request.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The page, or an error as for <see cref="QueryEventsAsync"/>.</returns>
    Task<Result<Page<CommandHistoryRow>>> QueryCommandsAsync(CommandHistoryQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Returns the stored values of a property, raw or bucketed.</summary>
    /// <param name="query">The query.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The points, or <c>history.query.too_many_points</c> (more than 10,000 raw points: ask for a bucket).</returns>
    Task<Result<IReadOnlyList<SeriesPoint>>> QuerySeriesAsync(SeriesQuery query, CancellationToken ct);

    /// <summary>Counts events, alarms or commands per bucket and group (reports, dashboards).</summary>
    /// <param name="query">The query.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The counts, or <c>history.query.invalid</c> (unknown table, column or filter), <c>history.query.range_too_large</c> (more than 366 days).</returns>
    Task<Result<IReadOnlyList<BucketCount>>> CountAsync(CountQuery query, CancellationToken ct);
}

/// <summary>The category of a journal event (EVT §4).</summary>
public enum EventCategory
{
    /// <summary>An event of a device.</summary>
    Device = 0,

    /// <summary>A health event (offline, fault).</summary>
    Health = 1,

    /// <summary>A system event.</summary>
    System = 2,

    /// <summary>An operator note.</summary>
    Operator = 3,

    /// <summary>Alarm activity.</summary>
    Alarm = 4,

    /// <summary>Command activity.</summary>
    Command = 5,
}

/// <summary>
/// A journal query (EVT §7). History stores events, health events, system events and operator notes: the two
/// <c>Include…Activity</c> flags are kept for the journal (EVT, T2.14) and select nothing more here.
/// </summary>
/// <param name="From">Inclusive start (received time).</param>
/// <param name="To">Exclusive end; at most 31 days after <paramref name="From"/>.</param>
/// <param name="Categories">The categories, or <see langword="null"/> for all.</param>
/// <param name="Kinds">Exact kinds or prefixes written <c>doorPosition.*</c>.</param>
/// <param name="MinSeverity">The lowest severity.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Servers">The servers.</param>
/// <param name="Entities">The devices or servers.</param>
/// <param name="Tags">Tags; an event matches when its source has any.</param>
/// <param name="PersonRef">A holder or credential reference.</param>
/// <param name="Operator">The user who produced the event.</param>
/// <param name="Text">A text searched in payload and title arguments.</param>
/// <param name="IncludeAlarmActivity">Reserved for the journal.</param>
/// <param name="IncludeCommandActivity">Reserved for the journal.</param>
/// <param name="Scope">What the caller may see.</param>
/// <param name="Descending">Newest first.</param>
public sealed record EventQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<EventCategory>? Categories = null,
    IReadOnlyList<string>? Kinds = null,
    Severity? MinSeverity = null,
    IReadOnlyList<LocationId>? LocationsSubtree = null,
    IReadOnlyList<ServerId>? Servers = null,
    IReadOnlyList<Guid>? Entities = null,
    IReadOnlyList<TagId>? Tags = null,
    string? PersonRef = null,
    UserId? Operator = null,
    string? Text = null,
    bool IncludeAlarmActivity = false,
    bool IncludeCommandActivity = false,
    HistoryScope? Scope = null,
    bool Descending = true);

/// <summary>The device or server an event came from, as it was at the time.</summary>
/// <param name="EntityId">The entity.</param>
/// <param name="IsServer">Whether the entity is a server.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="TypeId">The type, as <c>id@version</c>.</param>
/// <param name="LocationId">The location.</param>
/// <param name="LocationNames">The names of the location and its ancestors.</param>
/// <param name="ServerId">The server.</param>
/// <param name="TagKeys">The tag keys.</param>
public sealed record JournalEventSource(
    Guid EntityId,
    bool IsServer,
    string? DisplayName,
    string? TypeId,
    Guid? LocationId,
    IReadOnlyList<string> LocationNames,
    Guid? ServerId,
    IReadOnlyList<string> TagKeys);

/// <summary>An event of the journal (EVT §4), read from history. Personal fields are returned unmasked: masking is the API's job.</summary>
/// <param name="Id">The record id.</param>
/// <param name="Sequence">The <c>WP_LOG</c> sequence, or 0 for <c>WP_SYS</c>.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Category">The category.</param>
/// <param name="Severity">The severity.</param>
/// <param name="SourceTime">The source time.</param>
/// <param name="ReceivedTime">The received time.</param>
/// <param name="Source">The source.</param>
/// <param name="Origin">Who produced the event.</param>
/// <param name="Payload">The non-personal fields.</param>
/// <param name="Personal">The personal fields, or <see langword="null"/> when none or purged.</param>
/// <param name="TitleKey">The translation key of the title.</param>
/// <param name="TitleArgs">The title arguments.</param>
/// <param name="Correlation">The correlation.</param>
public sealed record JournalEvent(
    Guid Id,
    long Sequence,
    string Kind,
    EventCategory Category,
    Severity Severity,
    DateTimeOffset? SourceTime,
    DateTimeOffset ReceivedTime,
    JournalEventSource? Source,
    RecordOrigin Origin,
    JsonElement Payload,
    JsonElement? Personal,
    string TitleKey,
    JsonElement TitleArgs,
    RecordCorrelation Correlation);

/// <summary>An entry of the timeline of an alarm.</summary>
/// <param name="Time">The time of the record.</param>
/// <param name="RecordId">The record id.</param>
/// <param name="Kind">The record kind, e.g. <c>alarm.acknowledged</c>.</param>
/// <param name="ActorUser">The user.</param>
/// <param name="ActorSession">The session.</param>
/// <param name="ClientKind">The client kind.</param>
/// <param name="Body">The detail.</param>
public sealed record AlarmTimelineEntry(DateTimeOffset Time, Guid RecordId, string Kind, Guid? ActorUser, Guid? ActorSession, short? ClientKind, JsonElement Body);

/// <summary>An alarm query.</summary>
/// <param name="From">Inclusive start of the raised time.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="RuleIds">The rules.</param>
/// <param name="RuleKeys">The rule keys.</param>
/// <param name="Sources">The sources.</param>
/// <param name="States">The states.</param>
/// <param name="MinPriority">The lowest final priority.</param>
/// <param name="FalseAlarm">Only false (or only genuine) alarms.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Scope">What the caller may see.</param>
/// <param name="Descending">Newest first.</param>
public sealed record AlarmHistoryQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<Guid>? RuleIds = null,
    IReadOnlyList<string>? RuleKeys = null,
    IReadOnlyList<Guid>? Sources = null,
    IReadOnlyList<AlarmHistoryState>? States = null,
    int? MinPriority = null,
    bool? FalseAlarm = null,
    IReadOnlyList<LocationId>? LocationsSubtree = null,
    HistoryScope? Scope = null,
    bool Descending = true);

/// <summary>An alarm in history.</summary>
/// <param name="Id">The alarm.</param>
/// <param name="RaisedAt">When it was raised.</param>
/// <param name="RuleId">The rule.</param>
/// <param name="RuleKey">The rule key.</param>
/// <param name="RuleVersion">The rule version.</param>
/// <param name="SourceId">The source.</param>
/// <param name="SourceName">The source name at the time.</param>
/// <param name="LocationId">The location.</param>
/// <param name="TypeId">The type of the source.</param>
/// <param name="State">The state.</param>
/// <param name="InitialPriority">The priority when raised.</param>
/// <param name="FinalPriority">The latest priority.</param>
/// <param name="Count">The number of occurrences.</param>
/// <param name="LastOccurrenceAt">The latest occurrence.</param>
/// <param name="AcknowledgedAt">When acknowledged.</param>
/// <param name="AcknowledgedBy">Who acknowledged.</param>
/// <param name="ResolvedAt">When resolved.</param>
/// <param name="ClosedAt">When closed.</param>
/// <param name="ClosedBy">Who closed.</param>
/// <param name="Report">The closing report.</param>
/// <param name="EscalationLevel">The escalation level.</param>
/// <param name="FalseAlarm">Whether it was a false alarm.</param>
public sealed record AlarmHistoryRow(
    Guid Id,
    DateTimeOffset RaisedAt,
    Guid RuleId,
    string RuleKey,
    long RuleVersion,
    Guid? SourceId,
    string? SourceName,
    Guid? LocationId,
    string? TypeId,
    AlarmHistoryState State,
    int InitialPriority,
    int FinalPriority,
    int Count,
    DateTimeOffset LastOccurrenceAt,
    DateTimeOffset? AcknowledgedAt,
    Guid? AcknowledgedBy,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    Guid? ClosedBy,
    string? Report,
    int EscalationLevel,
    bool? FalseAlarm);

/// <summary>A command query.</summary>
/// <param name="From">Inclusive start of the request time.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Devices">The devices.</param>
/// <param name="IssuerUser">The issuing user.</param>
/// <param name="States">The states.</param>
/// <param name="Path">The command path.</param>
/// <param name="MinRisk">The lowest risk.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Scope">What the caller may see.</param>
/// <param name="Descending">Newest first.</param>
public sealed record CommandHistoryQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<DeviceId>? Devices = null,
    UserId? IssuerUser = null,
    IReadOnlyList<CommandHistoryState>? States = null,
    string? Path = null,
    short? MinRisk = null,
    IReadOnlyList<LocationId>? LocationsSubtree = null,
    HistoryScope? Scope = null,
    bool Descending = true);

/// <summary>A command in history.</summary>
/// <param name="Id">The command.</param>
/// <param name="RequestedAt">When it was requested.</param>
/// <param name="GroupId">The command group.</param>
/// <param name="DeviceId">The device.</param>
/// <param name="DeviceName">The device name at the time.</param>
/// <param name="Path">The command path.</param>
/// <param name="Arguments">The arguments.</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="IssuerUser">The issuing user.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Risk">The risk.</param>
/// <param name="Priority">The priority.</param>
/// <param name="State">The state.</param>
/// <param name="Failure">The failure.</param>
/// <param name="QueuedAt">When queued.</param>
/// <param name="DispatchedAt">When first dispatched.</param>
/// <param name="CompletedAt">When finished.</param>
/// <param name="VerifiedAt">When verified.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="Approval">The approval.</param>
public sealed record CommandHistoryRow(
    Guid Id,
    DateTimeOffset RequestedAt,
    Guid? GroupId,
    Guid DeviceId,
    string? DeviceName,
    string Path,
    JsonElement Arguments,
    JsonElement Issuer,
    Guid? IssuerUser,
    string? Reason,
    short Risk,
    short Priority,
    CommandHistoryState State,
    JsonElement? Failure,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? VerifiedAt,
    int Attempt,
    JsonElement? Approval);

/// <summary>A series query.</summary>
/// <param name="Device">The device.</param>
/// <param name="Path">The property.</param>
/// <param name="From">Inclusive start.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Bucket"><see langword="null"/> for raw points (at most 10,000), else min, average and max per bucket.</param>
public sealed record SeriesQuery(DeviceId Device, PropertyPath Path, DateTimeOffset From, DateTimeOffset To, TimeSpan? Bucket);

/// <summary>
/// A point of a series. A raw point has <paramref name="Value"/> (numbers come back as decimals and enums as strings:
/// history keeps the column, not the property type); a bucket has <paramref name="Min"/>, <paramref name="Avg"/> and <paramref name="Max"/> for numeric properties.
/// </summary>
/// <param name="Time">The time of the point, or the start of the bucket.</param>
/// <param name="Value">The raw value.</param>
/// <param name="Min">The bucket minimum.</param>
/// <param name="Avg">The bucket average.</param>
/// <param name="Max">The bucket maximum.</param>
/// <param name="Count">The number of stored values.</param>
public sealed record SeriesPoint(DateTimeOffset Time, WpValue? Value, decimal? Min, decimal? Avg, decimal? Max, int Count);

/// <summary>A count query (reports, dashboards).</summary>
/// <param name="Table"><c>events</c>, <c>alarms</c> or <c>commands</c>.</param>
/// <param name="From">Inclusive start; at most 366 days before <paramref name="To"/>.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Bucket">The bucket width (at least one second).</param>
/// <param name="GroupBy">Whitelisted columns (<c>kind</c>, <c>severity</c>, <c>location_id</c>, <c>rule_key</c>, <c>state</c>…).</param>
/// <param name="Filters">A JSON object whose members are whitelisted columns and whose values are a value or an array of values.</param>
/// <param name="Scope">What the caller may see.</param>
public sealed record CountQuery(
    string Table,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Bucket,
    IReadOnlyList<string> GroupBy,
    JsonElement Filters,
    HistoryScope? Scope = null);

/// <summary>The number of rows of a bucket and group.</summary>
/// <param name="Bucket">The start of the bucket.</param>
/// <param name="Group">The group-by values as text, by column.</param>
/// <param name="Count">The number of rows.</param>
public sealed record BucketCount(DateTimeOffset Bucket, IReadOnlyDictionary<string, string?> Group, long Count);
