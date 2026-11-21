using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The live state engine (spec STA §6): the 64 partition stores, the overall-state aggregates, the change feed and
/// the queries. Every mutation method (<see cref="ApplyRecords"/>, <see cref="ApplyTouch"/>, <see cref="Tick"/>,
/// <see cref="InitializeEntity"/>, <see cref="RemoveEntity"/>, <see cref="UpdateEntity"/>,
/// <see cref="SetServerUnavailable"/>) must be called only on the worker thread of the partition it names — the
/// single writer per partition (spec STA §6.1) — so none of them takes a lock.
/// </summary>
internal sealed class StateStore : IStateReader, IAggregateReader
{
    private static readonly IReadOnlyDictionary<PropertyPath, PropertyState> EmptyProperties = new Dictionary<PropertyPath, PropertyState>();
    private static readonly IReadOnlyDictionary<EventPath, LastEvent> EmptyLastEvents = new Dictionary<EventPath, LastEvent>();
    private static readonly StateSummary UnknownSummary = new(OverallState.Unknown, OverallState.Unknown, null, new Dictionary<PropertyPath, Severity>());

    private readonly PartitionStateStore[] _partitions;
    private readonly ITypeCatalog _typeCatalog;
    private readonly IStateSummaryEvaluator _evaluator;
    private readonly IRegistryReader _registry;
    private readonly ILogRecordTypeRegistry _recordTypes;
    private readonly AggregateCounters _aggregates;
    private readonly RecentChangesRing _ring;
    private readonly ChangeFanout _fanout;

    public StateStore(
        ITypeCatalog typeCatalog,
        IStateSummaryEvaluator evaluator,
        IRegistryReader registry,
        ILogRecordTypeRegistry recordTypes,
        AggregateCounters aggregates,
        RecentChangesRing ring,
        ChangeFanout fanout)
    {
        _typeCatalog = typeCatalog;
        _evaluator = evaluator;
        _registry = registry;
        _recordTypes = recordTypes;
        _aggregates = aggregates;
        _ring = ring;
        _fanout = fanout;
        _partitions = new PartitionStateStore[Partitioning.PartitionCount];
        for (var p = 0; p < _partitions.Length; p++)
        {
            _partitions[p] = new PartitionStateStore(p);
        }
    }

    /// <inheritdoc />
    public long CurrentSequence
    {
        get
        {
            var max = 0L;
            foreach (var partition in _partitions)
            {
                if (partition.Sequence > max)
                {
                    max = partition.Sequence;
                }
            }

            return max;
        }
    }

    /// <inheritdoc />
    public Result<DeviceState> Get(Guid entityId) =>
        PartitionOf(entityId).TryGet(entityId, out var state) ? state : StateErrors.EntityNotFound(entityId);

    /// <inheritdoc />
    public IReadOnlyList<DeviceState> GetMany(IReadOnlyCollection<Guid> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var result = new List<DeviceState>(entityIds.Count);
        foreach (var id in entityIds)
        {
            if (PartitionOf(id).TryGet(id, out var state))
            {
                result.Add(state);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public AggregateCounts Get(AggregateScope scope) => _aggregates.Get(scope);

    /// <inheritdoc />
    public IReadOnlyDictionary<AggregateScope, AggregateCounts> GetMany(IReadOnlyCollection<AggregateScope> scopes) => _aggregates.GetMany(scopes);

    /// <inheritdoc />
    public Result<Page<DeviceState>> Query(StateQuery query, PageRequest page)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);

        if (query.Where is not null && !IsKnownPropertyPath(query.Where.Path))
        {
            return StateErrors.QueryInvalidCondition(query.Where.Path.Value);
        }

        var ids = new List<Guid>();
        var cursor = (string?)null;
        do
        {
            var devicePage = _registry.QueryDevices(query.Devices, new PageRequest(PageRequest.MaxLimit, cursor));
            foreach (var device in devicePage.Items)
            {
                ids.Add(device.Id.Value);
            }

            cursor = devicePage.NextCursor;
        }
        while (cursor is not null);

        ids.Sort();
        var after = Guid.Empty;
        if (page.Cursor is { } encoded)
        {
            var decoded = CursorCodec.Decode(encoded, 1);
            if (!decoded.TryGetValue(out var value, out var error))
            {
                return error;
            }

            after = value.LastId;
        }

        var matched = new List<DeviceState>();
        foreach (var id in ids)
        {
            if (id.CompareTo(after) <= 0)
            {
                continue;
            }

            if (!PartitionOf(id).TryGet(id, out var state))
            {
                continue;
            }

            if (query.States is { } states && !states.Contains(state.Summary.State))
            {
                continue;
            }

            if (query.Where is { } where && !MatchesCondition(state, where))
            {
                continue;
            }

            matched.Add(state);
            if (matched.Count > page.Limit)
            {
                break;
            }
        }

        var hasMore = matched.Count > page.Limit;
        if (hasMore)
        {
            matched.RemoveAt(matched.Count - 1);
        }

        var nextCursor = hasMore && matched.Count > 0 ? CursorCodec.Encode(1, [], matched[^1].EntityId) : null;
        return new Page<DeviceState>(matched, nextCursor);
    }

    /// <summary>Returns whether any known device or server type declares a property path (spec STA §6.6).</summary>
    public bool IsKnownPropertyPath(PropertyPath path)
    {
        foreach (var type in _typeCatalog.List(new TypeQuery()))
        {
            if (type.Properties.ContainsKey(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether an entity is currently tracked.</summary>
    public bool HasEntity(Guid id) => PartitionOf(id).TryGet(id, out _);

    /// <summary>Returns the ids of every currently tracked entity, for the registry-change orphan sweep (spec STA §6.3).</summary>
    public IEnumerable<Guid> AllEntityIds()
    {
        foreach (var partition in _partitions)
        {
            foreach (var state in partition.Snapshot())
            {
                yield return state.EntityId;
            }
        }
    }

    /// <summary>Initialises the state of a newly created device or server (spec STA §6.3).</summary>
    public void InitializeEntity(EntityInfo info, DateTimeOffset now)
    {
        var store = PartitionOf(info.Id);
        var flags = new DeviceFlags(!info.Enabled, info.Maintenance);
        IReadOnlyDictionary<PropertyPath, PropertyState> properties;
        StateSummary summary;
        if (_typeCatalog.Get(info.Type).TryGetValue(out var type, out _))
        {
            var seeded = new Dictionary<PropertyPath, PropertyState>(type.Properties.Count);
            foreach (var (path, definition) in type.Properties)
            {
                seeded[path] = SeedProperty(path, definition);
            }

            properties = seeded;
            summary = _evaluator.Evaluate(type, new PropertyMapView(properties), flags, info.Attributes, null);
        }
        else
        {
            properties = EmptyProperties;
            summary = UnknownSummary;
        }

        var state = new DeviceState(info.Id, info.Kind, info.Type, properties, HealthState.Unknown, summary, false, EmptyLastEvents, 0, now);
        store.Set(info.Id, state);
        var scopes = MembershipScopes.Compute(_registry, info);
        store.SetMeta(info.Id, new EntityMeta(scopes, flags, info.Attributes));
        AdjustAggregates(null, null, scopes, summary.State);
    }

    /// <summary>Removes the state of a deleted device or server and its aggregate contribution (spec STA §6.3).</summary>
    public void RemoveEntity(Guid id, DateTimeOffset now)
    {
        _ = now;
        var store = PartitionOf(id);
        var meta = store.GetMeta(id);
        if (store.Remove(id, out var state) && meta is not null)
        {
            AdjustAggregates(meta.Scopes, state.Summary.State, null, null);
        }
    }

    /// <summary>Refreshes the membership, flags and type of an updated device or server (spec STA §6.3).</summary>
    public void UpdateEntity(EntityInfo info, DateTimeOffset now)
    {
        var store = PartitionOf(info.Id);
        if (!store.TryGet(info.Id, out var current))
        {
            InitializeEntity(info, now);
            return;
        }

        var oldMeta = store.GetMeta(info.Id);
        var newScopes = MembershipScopes.Compute(_registry, info);
        var newFlags = new DeviceFlags(!info.Enabled, info.Maintenance);
        var properties = current.Properties;
        DeviceTypeDefinition? typeDef = null;
        if (current.Type != info.Type && _typeCatalog.Get(info.Type).TryGetValue(out var newType, out _))
        {
            typeDef = newType;
            properties = RebuildProperties(newType, current.Properties);
        }
        else
        {
            _typeCatalog.Get(current.Type).TryGetValue(out typeDef, out _);
        }

        var summary = typeDef is not null
            ? _evaluator.Evaluate(typeDef, new PropertyMapView(properties), newFlags, info.Attributes, current.Summary)
            : current.Summary;

        var newState = current with { Type = info.Type, Properties = properties, Summary = summary, UpdatedAt = now };
        store.Set(info.Id, newState);
        store.SetMeta(info.Id, new EntityMeta(newScopes, newFlags, info.Attributes));

        var oldScopes = oldMeta?.Scopes ?? [];
        var membershipChanged = !ScopesEqual(oldScopes, newScopes);
        var stateChanged = current.Summary.State != summary.State;
        if (membershipChanged || stateChanged)
        {
            AdjustAggregates(oldScopes, current.Summary.State, newScopes, summary.State);
            EmitChange(info.Id, [], false, stateChanged ? summary.State : null, now);
        }
    }

    /// <summary>
    /// Applies ingested log records to this partition in order (spec STA §6.1).
    /// </summary>
    /// <returns>
    /// The server-unavailable cascades a health record of a server caused (spec STA §6.1): the caller
    /// (<see cref="StateSubscriber"/>) runs them through <see cref="ServerAvailabilityCascade"/>, which needs this
    /// partition's work to have completed first, so it is never done here.
    /// </returns>
    public IReadOnlyList<(ServerId Server, bool Unavailable)> ApplyRecords(int partition, DateTimeOffset now, bool isRecovering, IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var store = _partitions[partition];
        List<(ServerId Server, bool Unavailable)>? transitions = null;
        foreach (var record in records)
        {
            if (ApplyRecord(store, record, now, isRecovering) is { } transition)
            {
                (transitions ??= []).Add(transition);
            }

            if (record.Sequence > store.Sequence)
            {
                store.Sequence = record.Sequence;
            }
        }

        return transitions ?? [];
    }

    /// <summary>Applies an observation that changed nothing: refreshes <c>LastSeen</c> and restores quality after staleness (spec STA §6.1, §6.2).</summary>
    public void ApplyTouch(int partition, DateTimeOffset now, TouchInfo touch)
    {
        ArgumentNullException.ThrowIfNull(touch);
        var store = _partitions[partition];
        var target = touch.Device?.Value ?? touch.Server!.Value.Value;
        if (!store.TryGet(target, out var current))
        {
            return;
        }

        if (touch.Property is not { } path)
        {
            store.Set(target, current with { Health = current.Health with { LastContact = touch.ReceivedTime }, UpdatedAt = now });
            return;
        }

        if (!current.Properties.TryGetValue(path, out var property))
        {
            return;
        }

        var restored = store.TryTakePreStaleQuality(target, path, out var preStale);
        var quality = restored ? preStale : property.Quality;
        var newProperty = property with { LastSeen = touch.ReceivedTime, Quality = quality };
        var properties = Clone(current.Properties, path, newProperty);
        RescheduleStale(store, target, current.Type, path, touch.ReceivedTime);

        var qualityChanged = restored && quality != property.Quality;
        var summary = current.Summary;
        if (qualityChanged)
        {
            summary = Recompute(store, target, current, properties) ?? current.Summary;
        }

        store.Set(target, current with { Properties = properties, Summary = summary, UpdatedAt = now });
        if (qualityChanged)
        {
            if (summary.State != current.Summary.State)
            {
                var meta = store.GetMeta(target);
                if (meta is not null)
                {
                    AdjustAggregates(meta.Scopes, current.Summary.State, meta.Scopes, summary.State);
                }
            }

            EmitChange(target, [path], false, summary.State != current.Summary.State ? summary.State : null, now);
        }
    }

    /// <summary>Marks expired properties stale (spec STA §6.2); called every <c>Ingest:TickInterval</c>.</summary>
    public void Tick(int partition, DateTimeOffset now)
    {
        var store = _partitions[partition];
        foreach (var (entityId, path, scheduledDeadline) in store.StaleTracker.PopExpired(now))
        {
            if (!store.TryGet(entityId, out var current) || !current.Properties.TryGetValue(path, out var property))
            {
                continue;
            }

            if (property.LastSeen is not { } lastSeen
                || !_typeCatalog.Get(current.Type).TryGetValue(out var typeDef, out _)
                || !typeDef.Properties.TryGetValue(path, out var definition)
                || definition.StaleAfter is not { } staleAfter)
            {
                continue;
            }

            var currentDeadline = lastSeen + staleAfter;
            if (currentDeadline != scheduledDeadline || currentDeadline > now || property.Quality == Quality.Stale)
            {
                continue;
            }

            store.MarkStale(entityId, path, property.Quality);
            var properties = Clone(current.Properties, path, property with { Quality = Quality.Stale });
            var summary = Recompute(store, entityId, current, properties) ?? current.Summary;
            store.Set(entityId, current with { Properties = properties, Summary = summary, UpdatedAt = now });

            if (summary.State != current.Summary.State)
            {
                var meta = store.GetMeta(entityId);
                if (meta is not null)
                {
                    AdjustAggregates(meta.Scopes, current.Summary.State, meta.Scopes, summary.State);
                }
            }

            EmitChange(entityId, [path], false, summary.State != current.Summary.State ? summary.State : null, now);
        }
    }

    /// <summary>Sets whether a device's hosting server is unavailable (spec STA §6.1); the cascade's unit of work.</summary>
    public void SetServerUnavailable(int partition, Guid deviceId, bool unavailable, DateTimeOffset now)
    {
        var store = _partitions[partition];
        if (store.TryGet(deviceId, out var current) && current.ServerUnavailable != unavailable)
        {
            store.Set(deviceId, current with { ServerUnavailable = unavailable, UpdatedAt = now });
            EmitChange(deviceId, [], true, null, now);
        }
    }

    /// <summary>Runs the aggregate consistency check across every partition (spec STA §6.4).</summary>
    public bool RunAggregateConsistencyCheck(Microsoft.Extensions.Logging.ILogger logger)
    {
        var entries = new List<(IReadOnlyList<AggregateScope> Scopes, OverallState State)>();
        foreach (var partition in _partitions)
        {
            foreach (var state in partition.Snapshot())
            {
                var meta = partition.GetMeta(state.EntityId);
                if (meta is not null)
                {
                    entries.Add((meta.Scopes, state.Summary.State));
                }
            }
        }

        return _aggregates.RunConsistencyCheck(entries, logger);
    }

    private (ServerId Server, bool Unavailable)? ApplyRecord(PartitionStateStore store, LogRecord record, DateTimeOffset now, bool isRecovering)
    {
        var envelope = record.Envelope;
        var target = envelope.Dev?.Value ?? envelope.Srv!.Value.Value;
        if (!store.TryGet(target, out var current))
        {
            return null;
        }

        var properties = current.Properties;
        var lastEvents = current.LastEvents;
        var health = current.Health;
        PropertyPath? changedProperty = null;
        var healthChanged = false;
        (ServerId Server, bool Unavailable)? transition = null;

        switch (envelope.Kind)
        {
            case RecordKinds.Property:
            {
                var body = record.BodyAs<PropertyRecordBody>(_recordTypes);
                var path = PropertyPath.FromTrusted(body.Path);
                var property = new PropertyState(path, body.Value, body.Quality, envelope.Src, envelope.Ts, envelope.Ts, record.Sequence, envelope.Corr.Command);
                properties = Clone(properties, path, property);
                RescheduleStale(store, target, current.Type, path, envelope.Ts);
                changedProperty = path;
                break;
            }

            case RecordKinds.Event:
            {
                var body = record.BodyAs<EventRecordBody>(_recordTypes);
                var path = EventPath.FromTrusted(body.Path);
                var dict = new Dictionary<EventPath, LastEvent>(lastEvents) { [path] = new LastEvent(path, envelope.Ts, body.Severity, record.Sequence) };
                lastEvents = dict;
                break;
            }

            case RecordKinds.Health:
            {
                var body = record.BodyAs<HealthRecordBody>(_recordTypes);
                var wasUnavailable = IsUnavailable(current.Health.Connectivity);
                var since = body.Connectivity != current.Health.Connectivity ? envelope.Ts : current.Health.Since;
                health = new HealthState(body.Connectivity, body.Faults, body.Reason, envelope.Ts, since);
                healthChanged = true;
                if (envelope.Srv is not null && envelope.Dev is null && !isRecovering)
                {
                    var isUnavailable = IsUnavailable(body.Connectivity);
                    if (isUnavailable != wasUnavailable)
                    {
                        transition = (ServerId.From(target), isUnavailable);
                    }
                }

                break;
            }

            default:
                return null;
        }

        var summary = Recompute(store, target, current, properties) ?? current.Summary;
        store.Set(target, current with { Properties = properties, LastEvents = lastEvents, Health = health, Summary = summary, Sequence = record.Sequence, UpdatedAt = now });

        if (summary.State != current.Summary.State)
        {
            var meta = store.GetMeta(target);
            if (meta is not null)
            {
                AdjustAggregates(meta.Scopes, current.Summary.State, meta.Scopes, summary.State);
            }
        }

        var changedProperties = changedProperty is { } cp ? (IReadOnlyList<PropertyPath>)[cp] : [];
        EmitChange(target, changedProperties, healthChanged, summary.State != current.Summary.State ? summary.State : null, now);
        return transition;
    }

    private StateSummary? Recompute(PartitionStateStore store, Guid id, DeviceState current, IReadOnlyDictionary<PropertyPath, PropertyState> properties)
    {
        var meta = store.GetMeta(id);
        if (meta is null || !_typeCatalog.Get(current.Type).TryGetValue(out var typeDef, out _))
        {
            return null;
        }

        return _evaluator.Evaluate(typeDef, new PropertyMapView(properties), meta.Flags, meta.Attributes, current.Summary);
    }

    private void RescheduleStale(PartitionStateStore store, Guid id, TypeRef type, PropertyPath path, DateTimeOffset lastSeen)
    {
        if (_typeCatalog.Get(type).TryGetValue(out var definition, out _)
            && definition.Properties.TryGetValue(path, out var property)
            && property.StaleAfter is { } staleAfter)
        {
            store.StaleTracker.Schedule(id, path, lastSeen + staleAfter);
        }
    }

    private void EmitChange(Guid entityId, IReadOnlyList<PropertyPath> changedProperties, bool healthChanged, OverallState? newOverallState, DateTimeOffset at)
    {
        var change = _ring.Append(entityId, changedProperties, healthChanged, newOverallState, at);
        _fanout.Publish(change);
    }

    private void AdjustAggregates(IReadOnlyList<AggregateScope>? oldScopes, OverallState? oldState, IReadOnlyList<AggregateScope>? newScopes, OverallState? newState)
    {
        if (oldScopes is not null && oldState is { } os)
        {
            foreach (var scope in oldScopes)
            {
                _aggregates.Decrement(scope, os);
            }
        }

        if (newScopes is not null && newState is { } ns)
        {
            foreach (var scope in newScopes)
            {
                _aggregates.Increment(scope, ns);
            }
        }
    }

    private PartitionStateStore PartitionOf(Guid id) => _partitions[Partitioning.Of(id)];

    private static bool MatchesCondition(DeviceState state, PropertyCondition where) =>
        state.Properties.TryGetValue(where.Path, out var property) && property.Value is { } value && value.Equals(where.EqualsValue);

    private static bool ScopesEqual(IReadOnlyList<AggregateScope> a, IReadOnlyList<AggregateScope> b) =>
        a.Count == b.Count && a.ToHashSet().SetEquals(b);

    private static Dictionary<TKey, TValue> Clone<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> source, TKey key, TValue value)
        where TKey : notnull
    {
        var dict = new Dictionary<TKey, TValue>(source) { [key] = value };
        return dict;
    }

    private static PropertyState SeedProperty(PropertyPath path, PropertyDefinition definition) =>
        definition.Default is { } value
            ? new PropertyState(path, value, Quality.Uncertain, null, null, null, 0, null)
            : new PropertyState(path, null, Quality.Bad, null, null, null, 0, null);

    private static Dictionary<PropertyPath, PropertyState> RebuildProperties(DeviceTypeDefinition type, IReadOnlyDictionary<PropertyPath, PropertyState> old)
    {
        var result = new Dictionary<PropertyPath, PropertyState>(type.Properties.Count);
        foreach (var (path, definition) in type.Properties)
        {
            result[path] = old.TryGetValue(path, out var existing) ? existing : SeedProperty(path, definition);
        }

        return result;
    }

    private static bool IsUnavailable(Connectivity connectivity) => connectivity is Connectivity.Offline or Connectivity.Unknown;
}
