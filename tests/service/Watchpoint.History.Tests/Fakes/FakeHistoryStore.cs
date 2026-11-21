using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>An in-memory history store: records every write, can fail or stall writes, and serves scripted query results.</summary>
internal sealed class FakeHistoryStore : IHistoryStore
{
    private readonly Lock _gate = new();
    private int _failures;

    public List<IReadOnlyList<HistoryEventRow>> EventWrites { get; } = [];

    public List<(IReadOnlyList<HistoryAlarmRow> Alarms, IReadOnlyList<HistoryAlarmTimelineRow> Timeline)> AlarmWrites { get; } = [];

    public List<IReadOnlyList<HistoryCommandRow>> CommandWrites { get; } = [];

    public List<IReadOnlyList<HistoryPropertyRow>> PropertyWrites { get; } = [];

    public Dictionary<Guid, DateTimeOffset> CommandTimes { get; } = [];

    public List<IReadOnlyList<Guid>> CommandTimeLookups { get; } = [];

    public List<IReadOnlyList<(Guid DeviceId, string Path)>> LastRowLookups { get; } = [];

    public List<HistoryPropertyRow> LastRows { get; } = [];

    public List<HistoryPolicyOverrideRow> Overrides { get; } = [];

    public int WriteAttempts { get; private set; }

    public int SchemaCalls { get; private set; }

    /// <summary>The next writes fail this many times with an exception.</summary>
    public void FailNext(int count)
    {
        lock (_gate)
        {
            _failures = count;
        }
    }

    /// <summary>Raised on every write attempt, successful or not.</summary>
    public Action? OnAttempt { get; set; }

    public Func<HistoryEventFilter, int, IReadOnlyList<HistoryEventRow>>? EventQuery { get; set; }

    public Func<HistorySeriesRequest, IReadOnlyList<HistorySeriesRow>>? SeriesQuery { get; set; }

    public List<HistoryEventFilter> EventFilters { get; } = [];

    public List<HistorySeriesRequest> SeriesRequests { get; } = [];

    public List<HistoryCountRequest> CountRequests { get; } = [];

    public Task EnsureSchemaAsync(DateTimeOffset now, CancellationToken ct)
    {
        SchemaCalls++;
        return Task.CompletedTask;
    }

    public Task<int> WriteEventsAsync(IReadOnlyList<HistoryEventRow> rows, CancellationToken ct) => Write(() => EventWrites.Add(rows), rows.Count);

    public Task<int> WriteAlarmsAsync(IReadOnlyList<HistoryAlarmRow> alarms, IReadOnlyList<HistoryAlarmTimelineRow> timeline, CancellationToken ct) =>
        Write(() => AlarmWrites.Add((alarms, timeline)), alarms.Count + timeline.Count);

    public Task<int> WriteCommandsAsync(IReadOnlyList<HistoryCommandRow> rows, CancellationToken ct) => Write(() => CommandWrites.Add(rows), rows.Count);

    public Task<int> WritePropertyChangesAsync(IReadOnlyList<HistoryPropertyRow> rows, CancellationToken ct) => Write(() => PropertyWrites.Add(rows), rows.Count);

    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetCommandTimesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        CommandTimeLookups.Add(ids);
        return Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(CommandTimes.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }

    public Task<IReadOnlyList<HistoryPropertyRow>> GetLastPropertyRowsAsync(IReadOnlyList<(Guid DeviceId, string Path)> keys, CancellationToken ct)
    {
        LastRowLookups.Add(keys);
        return Task.FromResult<IReadOnlyList<HistoryPropertyRow>>([.. LastRows.Where(r => keys.Contains((r.DeviceId, r.Path)))]);
    }

    public Task<IReadOnlyList<HistoryPolicyOverrideRow>> GetPolicyOverridesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryPolicyOverrideRow>>([.. Overrides]);

    public Task<IReadOnlyList<HistoryEventRow>> QueryEventsAsync(HistoryEventFilter filter, int limit, TimeSpan timeout, CancellationToken ct)
    {
        EventFilters.Add(filter);
        return Task.FromResult(EventQuery?.Invoke(filter, limit) ?? []);
    }

    public Task<IReadOnlyList<HistoryAlarmRow>> QueryAlarmsAsync(HistoryAlarmFilter filter, int limit, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryAlarmRow>>([]);

    public Task<IReadOnlyList<HistoryAlarmTimelineRow>> GetAlarmTimelineAsync(Guid alarmId, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryAlarmTimelineRow>>([]);

    public Task<IReadOnlyList<HistoryCommandRow>> QueryCommandsAsync(HistoryCommandFilter filter, int limit, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HistoryCommandRow>>([]);

    public Task<IReadOnlyList<HistorySeriesRow>> QuerySeriesAsync(HistorySeriesRequest request, TimeSpan timeout, CancellationToken ct)
    {
        SeriesRequests.Add(request);
        return Task.FromResult(SeriesQuery?.Invoke(request) ?? []);
    }

    public Task<IReadOnlyList<HistoryBucketRow>> CountAsync(HistoryCountRequest request, TimeSpan timeout, CancellationToken ct)
    {
        CountRequests.Add(request);
        return Task.FromResult<IReadOnlyList<HistoryBucketRow>>([]);
    }

    /// <summary>When set, writes wait for it (a slow database).</summary>
    public Task? Gate { get; set; }

    private async Task<int> Write(Action record, int rows)
    {
        lock (_gate)
        {
            WriteAttempts++;
            OnAttempt?.Invoke();
        }

        if (Gate is { } gate)
        {
            await gate.ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException("the database is down");
            }

            record();
            return rows;
        }
    }
}
