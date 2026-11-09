using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Time;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Scheduling;

/// <summary>
/// Interface for persisting trigger fire state (spec SCH §5, §6 <c>wp_scheduling.trigger_state</c>).
/// </summary>
public interface ITriggerStateStore
{
    /// <summary>Gets the last fired time for a trigger.</summary>
    ValueTask<DateTimeOffset?> GetLastFiredAsync(string owner, string key, CancellationToken ct = default);

    /// <summary>Sets the last fired time for a trigger (called only after callback completion).</summary>
    ValueTask SetLastFiredAsync(string owner, string key, DateTimeOffset firedAt, CancellationToken ct = default);
}

/// <summary>In-memory fallback trigger state store.</summary>
public sealed class InMemoryTriggerStateStore : ITriggerStateStore
{
    private readonly ConcurrentDictionary<(string, string), DateTimeOffset> _state = new();

    /// <inheritdoc/>
    public ValueTask<DateTimeOffset?> GetLastFiredAsync(string owner, string key, CancellationToken ct = default)
    {
        if (_state.TryGetValue((owner, key), out var val))
        {
            return ValueTask.FromResult<DateTimeOffset?>(val);
        }

        return ValueTask.FromResult<DateTimeOffset?>(null);
    }

    /// <inheritdoc/>
    public ValueTask SetLastFiredAsync(string owner, string key, DateTimeOffset firedAt, CancellationToken ct = default)
    {
        _state[(owner, key)] = firedAt;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Provider of site and location geo coordinates (spec SCH §5).</summary>
public interface ILocationGeoProvider
{
    /// <summary>Gets the geo coordinates for a location or site (null location = site).</summary>
    GeoPoint? GetGeoPoint(LocationId? location);
}

/// <summary>Default geo provider.</summary>
public sealed class DefaultLocationGeoProvider(GeoPoint? siteGeo = null) : ILocationGeoProvider
{
    private readonly ConcurrentDictionary<LocationId, GeoPoint> _locations = new();

    /// <summary>Gets or sets the site geo point.</summary>
    public GeoPoint? SiteGeo { get; set; } = siteGeo;

    /// <summary>Sets a location geo point.</summary>
    public void SetLocation(LocationId id, GeoPoint point) => _locations[id] = point;

    /// <inheritdoc/>
    public GeoPoint? GetGeoPoint(LocationId? location)
    {
        if (location.HasValue)
        {
            return _locations.TryGetValue(location.Value, out var pt) ? pt : SiteGeo;
        }

        return SiteGeo;
    }
}

/// <summary>
/// Dispatcher for executing trigger callbacks (defaults to synchronous / background queue).
/// </summary>
public interface ITriggerDispatcher
{
    /// <summary>Dispatches a trigger callback.</summary>
    ValueTask DispatchAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct);
}

/// <summary>Default trigger dispatcher.</summary>
public sealed class DefaultTriggerDispatcher : ITriggerDispatcher
{
    /// <inheritdoc/>
    public async ValueTask DispatchAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
    {
        await work(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Implementation of ITimeTriggerService (spec SCH §5).
/// Uses min-heap driven by TimeProvider, persists trigger_state, handles catch-up, clock jumps.
/// </summary>
public sealed class TimeTriggerService : ITimeTriggerService, IDisposable
{
    private readonly TimeProvider _clock;
    private readonly ICalendarService _calendarService;
    private readonly ITriggerStateStore _stateStore;
    private readonly ILocationGeoProvider _geoProvider;
    private readonly ITriggerDispatcher _defaultDispatcher;
    private readonly ILogger<TimeTriggerService> _logger;

    private readonly Lock _gate = new();
    private readonly PriorityQueue<TriggerEntry, DateTimeOffset> _heap = new();
    private readonly Dictionary<(string, string), TriggerEntry> _registrations = new();
    private readonly HashSet<string> _polarLoggedDates = [];

    private ITimer? _timer;
    private DateTimeOffset _lastObservedTime;
    private bool _disposed;

    /// <summary>Creates a TimeTriggerService.</summary>
    public TimeTriggerService(
        TimeProvider clock,
        ICalendarService calendarService,
        ITriggerStateStore stateStore,
        ILocationGeoProvider geoProvider,
        ILogger<TimeTriggerService> logger,
        ITriggerDispatcher? defaultDispatcher = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _geoProvider = geoProvider ?? throw new ArgumentNullException(nameof(geoProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultDispatcher = defaultDispatcher ?? new DefaultTriggerDispatcher();

        _lastObservedTime = _clock.GetUtcNow();
        _calendarService.Changed += OnCalendarChanged;
    }

    /// <inheritdoc/>
    public IDisposable Register(
        string owner,
        string key,
        TriggerSpec spec,
        MissedPolicy missed,
        Func<TriggerFired, CancellationToken, ValueTask> callback)
    {
        return RegisterCore(owner, key, spec, missed, _defaultDispatcher, callback);
    }

    /// <summary>Registers with a custom dispatcher (e.g. partition dispatcher).</summary>
    public IDisposable RegisterWithDispatcher(
        string owner,
        string key,
        TriggerSpec spec,
        MissedPolicy missed,
        ITriggerDispatcher dispatcher,
        Func<TriggerFired, CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return RegisterCore(owner, key, spec, missed, dispatcher, callback);
    }

    private UnregisterDisposable RegisterCore(
        string owner,
        string key,
        TriggerSpec spec,
        MissedPolicy missed,
        ITriggerDispatcher dispatcher,
        Func<TriggerFired, CancellationToken, ValueTask> callback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(callback);

        if (spec is TriggerSpec.Cron cron)
        {
            ValidateCron(cron);
        }
        else if (spec is TriggerSpec.Every every)
        {
            if (every.Interval < TimeSpan.FromSeconds(1))
            {
                throw new ArgumentException("Every trigger interval must be >= PT1S", nameof(spec));
            }
        }

        var entry = new TriggerEntry(owner, key, spec, missed, callback, dispatcher);

        lock (_gate)
        {
            _registrations[(owner, key)] = entry;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                SchedulingLog.TriggerRegistered(_logger, owner, key, spec.GetType().Name);
            }

            _ = CheckCatchUpAsync(entry);

            ComputeNext(entry, _clock.GetUtcNow());
            RescheduleTimerLocked();
        }

        return new UnregisterDisposable(this, owner, key);
    }

    /// <inheritdoc/>
    public DateTimeOffset? NextFire(string owner, string key)
    {
        lock (_gate)
        {
            return _registrations.TryGetValue((owner, key), out var entry) ? entry.NextFireTime : null;
        }
    }

    /// <summary>
    /// Processes all triggers due at or before the given instant (testable directly without timer).
    /// </summary>
    public DateTimeOffset? ProcessDue(DateTimeOffset now)
    {
        List<(TriggerEntry Entry, DateTimeOffset FireTime)> due = [];

        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            var delta = (_lastObservedTime - now).TotalMilliseconds;
            if (delta > 60_000)
            {
                SchedulingLog.ClockJumpDetected(_logger, delta);
                _lastObservedTime = now;
                RebuildHeapLocked();
            }

            _lastObservedTime = now;

            while (_heap.Count > 0 && _heap.TryPeek(out var nextEntry, out var fireTime) && fireTime <= now)
            {
                _heap.Dequeue();

                if (nextEntry.IsCancelled || nextEntry.IsDone)
                {
                    continue;
                }

                if (nextEntry.Spec is TriggerSpec.At)
                {
                    nextEntry.IsDone = true;
                }

                due.Add((nextEntry, fireTime));

                if (!nextEntry.IsDone && !nextEntry.IsCancelled)
                {
                    ComputeNext(nextEntry, now);
                }
            }

            RescheduleTimerLocked();
        }

        foreach (var (entry, fireTime) in due)
        {
            ExecuteCallback(entry, fireTime, now, catchUp: false);
        }

        lock (_gate)
        {
            return _heap.TryPeek(out _, out var next) ? next : null;
        }
    }

    private async Task CheckCatchUpAsync(TriggerEntry entry)
    {
        var now = _clock.GetUtcNow();
        var lastFired = await _stateStore.GetLastFiredAsync(entry.Owner, entry.Key).ConfigureAwait(false);

        if (entry.Missed == MissedPolicy.FireOnce && lastFired.HasValue)
        {
            var prevOccur = GetNextOccurrence(entry.Spec, lastFired.Value);
            if (prevOccur.HasValue && prevOccur.Value <= now)
            {
                SchedulingLog.CatchUpFired(_logger, entry.Owner, entry.Key, prevOccur.Value);
                ExecuteCallback(entry, prevOccur.Value, now, catchUp: true);
            }
        }
        else if (entry.Spec is TriggerSpec.At at)
        {
            if (lastFired.HasValue && lastFired.Value >= at.Utc)
            {
                lock (_gate)
                {
                    entry.IsDone = true;
                }
            }
        }
    }

    private void OnCalendarChanged(object? sender, CalendarChangedEventArgs e)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            bool changed = false;
            foreach (var entry in _registrations.Values)
            {
                if (entry.Spec is TriggerSpec.CalendarEdge edge &&
                    string.Equals(edge.CalendarKey, e.CalendarKey, StringComparison.Ordinal))
                {
                    ComputeNext(entry, now);
                    changed = true;
                }
            }

            if (changed)
            {
                RebuildHeapLocked();
                RescheduleTimerLocked();
            }
        }
    }

    private void OnTimerElapsed(object? state)
    {
        ProcessDue(_clock.GetUtcNow());
    }

    private void ExecuteCallback(TriggerEntry entry, DateTimeOffset scheduledAt, DateTimeOffset firedAt, bool catchUp)
    {
        var payload = new TriggerFired(entry.Owner, entry.Key, scheduledAt, firedAt, catchUp);
        var lateness = Math.Max(0, (firedAt - scheduledAt).TotalMilliseconds);
        SchedulingMetrics.TriggerLateness.Record(lateness);

        _ = entry.Dispatcher.DispatchAsync(async ct =>
        {
            await entry.Callback(payload, ct).ConfigureAwait(false);
            await _stateStore.SetLastFiredAsync(entry.Owner, entry.Key, firedAt, ct).ConfigureAwait(false);
            SchedulingMetrics.TriggersFired.Add(1, new KeyValuePair<string, object?>("owner", entry.Owner));
        }, CancellationToken.None).AsTask();
    }

    private void ComputeNext(TriggerEntry entry, DateTimeOffset after)
    {
        if (entry.IsCancelled || entry.IsDone)
        {
            entry.NextFireTime = null;
            return;
        }

        entry.NextFireTime = GetNextOccurrence(entry.Spec, after);
        if (entry.NextFireTime.HasValue)
        {
            _heap.Enqueue(entry, entry.NextFireTime.Value);
        }
    }

    private DateTimeOffset? GetNextOccurrence(TriggerSpec spec, DateTimeOffset after)
    {
        switch (spec)
        {
            case TriggerSpec.At at:
                return at.Utc >= after ? at.Utc : null;

            case TriggerSpec.Cron cron:
                var tz = GetTimeZone(cron.TimeZone);
                var cronExpression = CronExpression.Parse(cron.Expression, CronFormat.Standard);
                return cronExpression.GetNextOccurrence(after, tz);

            case TriggerSpec.CalendarEdge edge:
                return GetNextCalendarEdge(edge, after);

            case TriggerSpec.Every every:
                if (!every.Anchor.HasValue)
                {
                    return after.Add(every.Interval);
                }

                var anchor = every.Anchor.Value;
                if (after < anchor)
                {
                    return anchor;
                }

                var elapsed = after - anchor;
                var count = (long)Math.Floor(elapsed.Ticks / (double)every.Interval.Ticks) + 1;
                return anchor.AddTicks(count * every.Interval.Ticks);

            case TriggerSpec.Sun sun:
                return GetNextSunEvent(sun, after);

            default:
                return null;
        }
    }

    private DateTimeOffset? GetNextCalendarEdge(TriggerSpec.CalendarEdge edge, DateTimeOffset after)
    {
        var next = _calendarService.NextTransition(edge.CalendarKey, after);
        if (!next.HasValue)
        {
            return null;
        }

        if (edge.Edge == EdgeKind.Both)
        {
            return next;
        }

        var isStarting = _calendarService.IsActive(edge.CalendarKey, next.Value.AddMilliseconds(1));
        if (edge.Edge == EdgeKind.Start && isStarting)
        {
            return next;
        }

        if (edge.Edge == EdgeKind.End && !isStarting)
        {
            return next;
        }

        return GetNextCalendarEdge(edge, next.Value);
    }

    private DateTimeOffset? GetNextSunEvent(TriggerSpec.Sun sun, DateTimeOffset after)
    {
        var geo = _geoProvider.GetGeoPoint(sun.Location);
        if (!geo.HasValue)
        {
            return null;
        }

        var startDate = DateOnly.FromDateTime(after.UtcDateTime);
        for (int i = 0; i <= 365; i++)
        {
            var date = startDate.AddDays(i);
            var (sunrise, sunset) = SolarCalculator.Calculate(geo.Value.Latitude, geo.Value.Longitude, date);

            if (!sunrise.HasValue && !sunset.HasValue)
            {
                var logKey = $"{geo.Value.Latitude}:{geo.Value.Longitude}:{date}";
                if (_polarLoggedDates.Add(logKey))
                {
                    SchedulingLog.PolarDayNight(_logger, geo.Value.Latitude, geo.Value.Longitude, date);
                }

                continue;
            }

            var eventTime = sun.Event == SunEvent.Sunrise ? sunrise : sunset;
            if (eventTime.HasValue)
            {
                var target = eventTime.Value.Add(sun.Offset);
                if (target > after)
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static void ValidateCron(TriggerSpec.Cron cron)
    {
        var parts = cron.Expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            throw new ArgumentException(
                $"Cron expression '{cron.Expression}' is invalid: must have exactly 5 fields, got {parts.Length}",
                nameof(cron));
        }

        try
        {
            CronExpression.Parse(cron.Expression, CronFormat.Standard);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Cron expression '{cron.Expression}' is invalid: {ex.Message}", nameof(cron), ex);
        }

        if (!string.IsNullOrEmpty(cron.TimeZone))
        {
            var tzRes = SiteTimeZone.FromIana(cron.TimeZone);
            if (!tzRes.IsSuccess)
            {
                throw new ArgumentException($"Invalid cron time zone '{cron.TimeZone}'", nameof(cron));
            }
        }
    }

    private static TimeZoneInfo GetTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrEmpty(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    private void RebuildHeapLocked()
    {
        _heap.Clear();
        var now = _clock.GetUtcNow();
        foreach (var entry in _registrations.Values)
        {
            ComputeNext(entry, now);
        }
    }

    private void RescheduleTimerLocked()
    {
        if (_disposed)
        {
            return;
        }

        while (_heap.Count > 0 && _heap.TryPeek(out var nextEntry, out _) && (nextEntry.IsCancelled || nextEntry.IsDone))
        {
            _heap.Dequeue();
        }

        if (_heap.Count == 0 || !_heap.TryPeek(out _, out var nextFire))
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        var now = _clock.GetUtcNow();
        var dueTime = nextFire <= now ? TimeSpan.Zero : nextFire - now;

        _timer ??= _clock.CreateTimer(OnTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
    }

    private void Unregister(string owner, string key)
    {
        lock (_gate)
        {
            if (_registrations.Remove((owner, key), out var entry))
            {
                entry.IsCancelled = true;
                RescheduleTimerLocked();
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _calendarService.Changed -= OnCalendarChanged;
            _timer?.Dispose();
            _timer = null;
            _heap.Clear();
            _registrations.Clear();
        }
    }

    private sealed class TriggerEntry(
        string owner,
        string key,
        TriggerSpec spec,
        MissedPolicy missed,
        Func<TriggerFired, CancellationToken, ValueTask> callback,
        ITriggerDispatcher dispatcher)
    {
        public string Owner { get; } = owner;
        public string Key { get; } = key;
        public TriggerSpec Spec { get; } = spec;
        public MissedPolicy Missed { get; } = missed;
        public Func<TriggerFired, CancellationToken, ValueTask> Callback { get; } = callback;
        public ITriggerDispatcher Dispatcher { get; } = dispatcher;

        public DateTimeOffset? NextFireTime { get; set; }
        public bool IsCancelled { get; set; }
        public bool IsDone { get; set; }
    }

    private sealed class UnregisterDisposable(TimeTriggerService service, string owner, string key) : IDisposable
    {
        private TimeTriggerService? _service = service;

        public void Dispose()
        {
            Interlocked.Exchange(ref _service, null)?.Unregister(owner, key);
        }
    }
}
