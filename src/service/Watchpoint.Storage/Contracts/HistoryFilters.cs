namespace Watchpoint.Storage.Contracts;

/// <summary>The scope restriction of a history query: at most one of the two lists is set; both <see langword="null"/> means unrestricted.</summary>
/// <param name="LocationIds">Rows whose <c>location_ids</c> overlap these locations.</param>
/// <param name="EntityIds">Rows about these devices (<c>entity_id</c>, <c>source_id</c> or <c>device_id</c>).</param>
public sealed record HistoryScopeClause(IReadOnlyList<Guid>? LocationIds, IReadOnlyList<Guid>? EntityIds)
{
    /// <summary>No restriction.</summary>
    public static HistoryScopeClause Unrestricted { get; } = new(null, null);
}

/// <summary>The keyset position after which a page continues.</summary>
/// <param name="Time">The time of the last item of the previous page.</param>
/// <param name="Id">The id of the last item.</param>
public sealed record HistoryKeyset(DateTimeOffset Time, Guid Id);

/// <summary>Filters of an events query. Lists are alternatives (any of); <see langword="null"/> means no filter.</summary>
/// <param name="From">Inclusive start.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Categories">The categories.</param>
/// <param name="KindsExact">Exact kinds.</param>
/// <param name="KindPrefixes">Kind prefixes (<c>doorPosition.</c> for <c>doorPosition.*</c>).</param>
/// <param name="MinSeverity">The lowest severity.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Servers">The servers.</param>
/// <param name="Entities">The devices or servers.</param>
/// <param name="TagKeys">Tag keys; a row matches when it has any.</param>
/// <param name="PersonRef">A holder or credential reference.</param>
/// <param name="OperatorUser">The user who produced the event.</param>
/// <param name="Text">A text searched in payload and title arguments (case-insensitive).</param>
/// <param name="Scope">The scope restriction.</param>
/// <param name="Descending">Newest first.</param>
/// <param name="After">The keyset position, or <see langword="null"/> for the first page.</param>
public sealed record HistoryEventFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<short>? Categories,
    IReadOnlyList<string>? KindsExact,
    IReadOnlyList<string>? KindPrefixes,
    short? MinSeverity,
    IReadOnlyList<Guid>? LocationsSubtree,
    IReadOnlyList<Guid>? Servers,
    IReadOnlyList<Guid>? Entities,
    IReadOnlyList<string>? TagKeys,
    string? PersonRef,
    Guid? OperatorUser,
    string? Text,
    HistoryScopeClause Scope,
    bool Descending,
    HistoryKeyset? After);

/// <summary>Filters of an alarms query.</summary>
/// <param name="From">Inclusive start of the raised time.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="RuleIds">The rules.</param>
/// <param name="RuleKeys">The rule keys.</param>
/// <param name="Sources">The sources.</param>
/// <param name="States">The states.</param>
/// <param name="MinPriority">The lowest final priority.</param>
/// <param name="FalseAlarm">Only false (or only genuine) alarms.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Scope">The scope restriction.</param>
/// <param name="Descending">Newest first.</param>
/// <param name="After">The keyset position.</param>
public sealed record HistoryAlarmFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<Guid>? RuleIds,
    IReadOnlyList<string>? RuleKeys,
    IReadOnlyList<Guid>? Sources,
    IReadOnlyList<short>? States,
    int? MinPriority,
    bool? FalseAlarm,
    IReadOnlyList<Guid>? LocationsSubtree,
    HistoryScopeClause Scope,
    bool Descending,
    HistoryKeyset? After);

/// <summary>Filters of a commands query.</summary>
/// <param name="From">Inclusive start of the request time.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Devices">The devices.</param>
/// <param name="IssuerUser">The issuing user.</param>
/// <param name="States">The states.</param>
/// <param name="Path">The command path.</param>
/// <param name="MinRisk">The lowest risk.</param>
/// <param name="LocationsSubtree">Locations, each standing for its subtree.</param>
/// <param name="Scope">The scope restriction.</param>
/// <param name="Descending">Newest first.</param>
/// <param name="After">The keyset position.</param>
public sealed record HistoryCommandFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<Guid>? Devices,
    Guid? IssuerUser,
    IReadOnlyList<short>? States,
    string? Path,
    short? MinRisk,
    IReadOnlyList<Guid>? LocationsSubtree,
    HistoryScopeClause Scope,
    bool Descending,
    HistoryKeyset? After);

/// <summary>A property series request.</summary>
/// <param name="DeviceId">The device.</param>
/// <param name="Path">The property path.</param>
/// <param name="From">Inclusive start.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Bucket">The bucket width, or <see langword="null"/> for raw points.</param>
/// <param name="Limit">The most rows to return.</param>
public sealed record HistorySeriesRequest(Guid DeviceId, string Path, DateTimeOffset From, DateTimeOffset To, TimeSpan? Bucket, int Limit);

/// <summary>A point of a property series: a raw point, or the aggregate of a bucket.</summary>
/// <param name="Time">The time of the point, or the start of the bucket.</param>
/// <param name="VNum">Raw numeric value.</param>
/// <param name="VText">Raw text value.</param>
/// <param name="VBool">Raw boolean value.</param>
/// <param name="VJson">Raw JSON value.</param>
/// <param name="Min">Bucket minimum (numeric properties).</param>
/// <param name="Avg">Bucket average.</param>
/// <param name="Max">Bucket maximum.</param>
/// <param name="Count">Number of stored points (1 for raw points).</param>
public sealed record HistorySeriesRow(DateTimeOffset Time, decimal? VNum, string? VText, bool? VBool, string? VJson, decimal? Min, decimal? Avg, decimal? Max, int Count);

/// <summary>A filter of a count request: the column holds one of the values (as text).</summary>
/// <param name="Column">A whitelisted column (<see cref="HistoryCountColumns"/>).</param>
/// <param name="Values">The accepted values.</param>
public sealed record HistoryCountFilter(string Column, IReadOnlyList<string> Values);

/// <summary>A count request.</summary>
/// <param name="Table"><c>events</c>, <c>alarms</c> or <c>commands</c>.</param>
/// <param name="From">Inclusive start.</param>
/// <param name="To">Exclusive end.</param>
/// <param name="Bucket">The bucket width.</param>
/// <param name="GroupBy">Whitelisted columns.</param>
/// <param name="Filters">Whitelisted filters, all of which must match.</param>
/// <param name="Scope">The scope restriction.</param>
public sealed record HistoryCountRequest(
    string Table,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan Bucket,
    IReadOnlyList<string> GroupBy,
    IReadOnlyList<HistoryCountFilter> Filters,
    HistoryScopeClause Scope);

/// <summary>One count of a bucket and group.</summary>
/// <param name="Bucket">The start of the bucket.</param>
/// <param name="Group">The group-by values as text, by column.</param>
/// <param name="Count">The number of rows.</param>
public sealed record HistoryBucketRow(DateTimeOffset Bucket, IReadOnlyDictionary<string, string?> Group, long Count);

/// <summary>The columns that count requests may group and filter by (spec HIS §7 whitelist), with their types.</summary>
public static class HistoryCountColumns
{
    /// <summary>The kind of a column.</summary>
    public enum ColumnType
    {
        /// <summary>Text.</summary>
        Text,

        /// <summary>A 16-bit integer.</summary>
        ShortInteger,

        /// <summary>A 32-bit number.</summary>
        Number,

        /// <summary>A UUID.</summary>
        Uuid,
    }

    private static readonly Dictionary<string, Dictionary<string, ColumnType>> _tables = new(StringComparer.Ordinal)
    {
        ["events"] = new(StringComparer.Ordinal)
        {
            ["kind"] = ColumnType.Text,
            ["category"] = ColumnType.ShortInteger,
            ["severity"] = ColumnType.ShortInteger,
            ["entity_id"] = ColumnType.Uuid,
            ["server_id"] = ColumnType.Uuid,
            ["location_id"] = ColumnType.Uuid,
            ["type_id"] = ColumnType.Text,
            ["origin_type"] = ColumnType.ShortInteger,
        },
        ["alarms"] = new(StringComparer.Ordinal)
        {
            ["rule_key"] = ColumnType.Text,
            ["state"] = ColumnType.ShortInteger,
            ["source_id"] = ColumnType.Uuid,
            ["location_id"] = ColumnType.Uuid,
            ["type_id"] = ColumnType.Text,
            ["priority_final"] = ColumnType.Number,
            ["false_alarm"] = ColumnType.Text,
        },
        ["commands"] = new(StringComparer.Ordinal)
        {
            ["state"] = ColumnType.ShortInteger,
            ["risk"] = ColumnType.ShortInteger,
            ["priority"] = ColumnType.ShortInteger,
            ["device_id"] = ColumnType.Uuid,
            ["path"] = ColumnType.Text,
            ["issuer_user"] = ColumnType.Uuid,
        },
    };

    /// <summary>Gets the table names.</summary>
    public static IReadOnlyCollection<string> Tables => _tables.Keys;

    /// <summary>Returns the whitelisted columns of a table.</summary>
    /// <param name="table">The table.</param>
    /// <param name="columns">The columns and their types.</param>
    /// <returns><see langword="false"/> for an unknown table.</returns>
    public static bool TryGetColumns(string table, out IReadOnlyDictionary<string, ColumnType> columns)
    {
        if (_tables.TryGetValue(table, out var found))
        {
            columns = found;
            return true;
        }

        columns = new Dictionary<string, ColumnType>();
        return false;
    }
}
