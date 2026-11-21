namespace Watchpoint.Storage.Contracts;

/// <summary>
/// The history store (spec STO §3, HIS §4): provider-specific bulk writes and queries on the history tables. Writes are
/// idempotent: a row written twice is stored once, and alarm and command rows merge monotonically.
/// </summary>
public interface IHistoryStore
{
    /// <summary>Creates the history tables, indexes and (hyper)tables or partitions if they are missing; safe to call repeatedly and from several nodes.</summary>
    /// <param name="now">The current time: partitions are created from the month before it, up to three months ahead.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task EnsureSchemaAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Stores event rows; rows whose <c>(id, time)</c> exist are ignored.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of rows inserted.</returns>
    Task<int> WriteEventsAsync(IReadOnlyList<HistoryEventRow> rows, CancellationToken ct);

    /// <summary>
    /// Stores alarm rows (one per alarm) and timeline rows in one transaction. An existing alarm row is merged: state, final
    /// priority and escalation follow the newest <see cref="HistoryAlarmRow.Seq"/>, timestamps are set once, the count only grows.
    /// </summary>
    /// <param name="alarms">The folded rows, at most one per alarm id.</param>
    /// <param name="timeline">The timeline rows; rows whose <c>(record_id, time)</c> exist are ignored.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of alarm and timeline rows written.</returns>
    Task<int> WriteAlarmsAsync(IReadOnlyList<HistoryAlarmRow> alarms, IReadOnlyList<HistoryAlarmTimelineRow> timeline, CancellationToken ct);

    /// <summary>Stores command rows (one per command), merged like alarm rows.</summary>
    /// <param name="rows">The folded rows, at most one per command id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of rows written.</returns>
    Task<int> WriteCommandsAsync(IReadOnlyList<HistoryCommandRow> rows, CancellationToken ct);

    /// <summary>Stores property changes; rows whose <c>(record_id, time)</c> exist are ignored.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of rows inserted.</returns>
    Task<int> WritePropertyChangesAsync(IReadOnlyList<HistoryPropertyRow> rows, CancellationToken ct);

    /// <summary>Returns the latest stored row of each (device, path) that has one.</summary>
    /// <param name="keys">The keys.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<HistoryPropertyRow>> GetLastPropertyRowsAsync(IReadOnlyList<(Guid DeviceId, string Path)> keys, CancellationToken ct);

    /// <summary>Returns all policy overrides.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The overrides.</returns>
    Task<IReadOnlyList<HistoryPolicyOverrideRow>> GetPolicyOverridesAsync(CancellationToken ct);

    /// <summary>Queries events in keyset order (time, then id; direction per filter).</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistoryEventRow>> QueryEventsAsync(HistoryEventFilter filter, int limit, TimeSpan timeout, CancellationToken ct);

    /// <summary>Queries alarms in keyset order.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistoryAlarmRow>> QueryAlarmsAsync(HistoryAlarmFilter filter, int limit, TimeSpan timeout, CancellationToken ct);

    /// <summary>Returns the timeline of an alarm, oldest first.</summary>
    /// <param name="alarmId">The alarm.</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistoryAlarmTimelineRow>> GetAlarmTimelineAsync(Guid alarmId, TimeSpan timeout, CancellationToken ct);

    /// <summary>Queries commands in keyset order.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="limit">The most rows to return.</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistoryCommandRow>> QueryCommandsAsync(HistoryCommandFilter filter, int limit, TimeSpan timeout, CancellationToken ct);

    /// <summary>Queries a property series, oldest first.</summary>
    /// <param name="request">The request.</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>At most <see cref="HistorySeriesRequest.Limit"/> rows.</returns>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistorySeriesRow>> QuerySeriesAsync(HistorySeriesRequest request, TimeSpan timeout, CancellationToken ct);

    /// <summary>Counts rows per bucket and group.</summary>
    /// <param name="request">The request; columns must be whitelisted (<see cref="HistoryCountColumns"/>).</param>
    /// <param name="timeout">The statement timeout.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts, ordered by bucket.</returns>
    /// <exception cref="ArgumentException">A column is not whitelisted.</exception>
    /// <exception cref="HistoryQueryTimeoutException">The statement timeout elapsed.</exception>
    Task<IReadOnlyList<HistoryBucketRow>> CountAsync(HistoryCountRequest request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>A history query ran into its statement timeout.</summary>
public sealed class HistoryQueryTimeoutException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="HistoryQueryTimeoutException"/> class.</summary>
    public HistoryQueryTimeoutException()
        : base("The history query exceeded its statement timeout.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HistoryQueryTimeoutException"/> class.</summary>
    /// <param name="inner">The database exception.</param>
    public HistoryQueryTimeoutException(Exception inner)
        : base("The history query exceeded its statement timeout.", inner)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HistoryQueryTimeoutException"/> class.</summary>
    /// <param name="message">The message.</param>
    public HistoryQueryTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="HistoryQueryTimeoutException"/> class.</summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The database exception.</param>
    public HistoryQueryTimeoutException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
