using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal;

/// <summary>
/// The history store of the configured provider, created on first use: the store map exists only after the storage start-up
/// phases, while the history service starts with the host. A failure (storage not ready) is not remembered: the caller retries.
/// </summary>
internal sealed class LazyHistoryStore(Func<IHistoryStore> create) : IHistoryStore
{
    private IHistoryStore? _inner;

    private IHistoryStore Inner => _inner ??= create();

    public Task EnsureSchemaAsync(DateTimeOffset now, CancellationToken ct) => Inner.EnsureSchemaAsync(now, ct);

    public Task<int> WriteEventsAsync(IReadOnlyList<HistoryEventRow> rows, CancellationToken ct) => Inner.WriteEventsAsync(rows, ct);

    public Task<int> WriteAlarmsAsync(IReadOnlyList<HistoryAlarmRow> alarms, IReadOnlyList<HistoryAlarmTimelineRow> timeline, CancellationToken ct) =>
        Inner.WriteAlarmsAsync(alarms, timeline, ct);

    public Task<int> WriteCommandsAsync(IReadOnlyList<HistoryCommandRow> rows, CancellationToken ct) => Inner.WriteCommandsAsync(rows, ct);

    public Task<int> WritePropertyChangesAsync(IReadOnlyList<HistoryPropertyRow> rows, CancellationToken ct) => Inner.WritePropertyChangesAsync(rows, ct);

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetCommandTimesAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => Inner.GetCommandTimesAsync(ids, ct);

    public Task<IReadOnlyList<HistoryPropertyRow>> GetLastPropertyRowsAsync(IReadOnlyList<(Guid DeviceId, string Path)> keys, CancellationToken ct) =>
        Inner.GetLastPropertyRowsAsync(keys, ct);

    public Task<IReadOnlyList<HistoryPolicyOverrideRow>> GetPolicyOverridesAsync(CancellationToken ct) => Inner.GetPolicyOverridesAsync(ct);

    public Task<IReadOnlyList<HistoryEventRow>> QueryEventsAsync(HistoryEventFilter filter, int limit, TimeSpan timeout, CancellationToken ct) =>
        Inner.QueryEventsAsync(filter, limit, timeout, ct);

    public Task<IReadOnlyList<HistoryAlarmRow>> QueryAlarmsAsync(HistoryAlarmFilter filter, int limit, TimeSpan timeout, CancellationToken ct) =>
        Inner.QueryAlarmsAsync(filter, limit, timeout, ct);

    public Task<IReadOnlyList<HistoryAlarmTimelineRow>> GetAlarmTimelineAsync(Guid alarmId, TimeSpan timeout, CancellationToken ct) =>
        Inner.GetAlarmTimelineAsync(alarmId, timeout, ct);

    public Task<IReadOnlyList<HistoryCommandRow>> QueryCommandsAsync(HistoryCommandFilter filter, int limit, TimeSpan timeout, CancellationToken ct) =>
        Inner.QueryCommandsAsync(filter, limit, timeout, ct);

    public Task<IReadOnlyList<HistorySeriesRow>> QuerySeriesAsync(HistorySeriesRequest request, TimeSpan timeout, CancellationToken ct) =>
        Inner.QuerySeriesAsync(request, timeout, ct);

    public Task<IReadOnlyList<HistoryBucketRow>> CountAsync(HistoryCountRequest request, TimeSpan timeout, CancellationToken ct) =>
        Inner.CountAsync(request, timeout, ct);
}
