using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The persisted form of a <see cref="DeviceState"/> (spec STA §8.1): compact JSON, kind <c>device</c>. Derived
/// fields are left out and recomputed at recovery: the summary, <see cref="DeviceState.ServerUnavailable"/> and a
/// quality that is <see cref="Quality.Stale"/> only because of <c>staleAfter</c> (the quality stored before it went
/// stale is written instead).
/// </summary>
internal static class StateSerializer
{
    /// <summary>The state item kind of an entity (spec STA §8.1).</summary>
    public const string Kind = "device";

    /// <summary>The checkpoint consumer of the state module (spec STA §8.1).</summary>
    public const string Consumer = "state";

    private static readonly JsonSerializerOptions _options = WatchpointJson.Create(JsonProfile.Log, StateSnapshotJsonContext.Default);

    /// <summary>Serialises the persisted form of a state.</summary>
    /// <param name="state">The state.</param>
    /// <param name="storedQuality">Returns the stored quality of a property whose quality is stale-derived, if any.</param>
    /// <returns>UTF-8 JSON.</returns>
    public static byte[] Serialize(DeviceState state, Func<PropertyPath, Quality?> storedQuality)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(storedQuality);
        var properties = new Dictionary<string, SnapshotPropertyDto>(state.Properties.Count);
        foreach (var (path, property) in state.Properties)
        {
            var quality = property.Quality == Quality.Stale && storedQuality(path) is { } stored ? stored : property.Quality;
            properties[path.Value] = new SnapshotPropertyDto(property.Value, quality, property.SourceTime, property.ReceivedTime, property.LastSeen, property.Sequence, property.CausedBy?.Value);
        }

        var lastEvents = new Dictionary<string, SnapshotEventDto>(state.LastEvents.Count);
        foreach (var (path, lastEvent) in state.LastEvents)
        {
            lastEvents[path.Value] = new SnapshotEventDto(lastEvent.Time, lastEvent.Severity, lastEvent.Sequence);
        }

        var health = state.Health;
        var dto = new SnapshotEntityDto(
            state.EntityId,
            state.Kind,
            state.Type.Id.Value,
            state.Type.Version.ToString(),
            properties,
            new SnapshotHealthDto(health.Connectivity, health.Faults, health.Reason, health.LastContact, health.Since),
            lastEvents,
            state.Sequence,
            state.UpdatedAt);
        return JsonSerializer.SerializeToUtf8Bytes(dto, _options);
    }

    /// <summary>Reads the persisted form of a state.</summary>
    /// <param name="payload">UTF-8 JSON written by <see cref="Serialize"/>.</param>
    /// <returns>The persisted entity.</returns>
    /// <exception cref="JsonException">The payload is not a persisted state.</exception>
    public static PersistedEntity Deserialize(ReadOnlySpan<byte> payload)
    {
        var dto = JsonSerializer.Deserialize<SnapshotEntityDto>(payload, _options) ?? throw new JsonException("The snapshot item is empty.");
        if (!SemVersion.TryParse(dto.TypeVersion, out var version))
        {
            throw new JsonException($"The snapshot item {dto.Id} has an invalid type version '{dto.TypeVersion}'.");
        }

        var properties = new Dictionary<PropertyPath, PropertyState>(dto.Properties.Count);
        foreach (var (key, p) in dto.Properties)
        {
            var path = PropertyPath.FromTrusted(key);
            properties[path] = new PropertyState(path, p.Value, p.Quality, p.SourceTime, p.ReceivedTime, p.LastSeen, p.Sequence, p.CausedBy is { } command ? CommandId.From(command) : null);
        }

        var lastEvents = new Dictionary<EventPath, LastEvent>(dto.LastEvents.Count);
        foreach (var (key, e) in dto.LastEvents)
        {
            var path = EventPath.FromTrusted(key);
            lastEvents[path] = new LastEvent(path, e.Time, e.Severity, e.Sequence);
        }

        var h = dto.Health;
        return new PersistedEntity(
            dto.Id,
            dto.Kind,
            new TypeRef(TypeId.FromTrusted(dto.TypeId), version),
            properties,
            new HealthState(h.Connectivity, h.Faults, h.Reason, h.LastContact, h.Since),
            lastEvents,
            dto.Sequence,
            dto.UpdatedAt);
    }
}

/// <summary>A state read back from a snapshot: <see cref="DeviceState"/> without its derived fields (spec STA §8.1).</summary>
internal sealed record PersistedEntity(
    Guid EntityId,
    EntityKind Kind,
    TypeRef Type,
    IReadOnlyDictionary<PropertyPath, PropertyState> Properties,
    HealthState Health,
    IReadOnlyDictionary<EventPath, LastEvent> LastEvents,
    long Sequence,
    DateTimeOffset UpdatedAt);

/// <summary>The persisted JSON form of an entity.</summary>
internal sealed record SnapshotEntityDto(
    Guid Id,
    EntityKind Kind,
    string TypeId,
    string TypeVersion,
    IReadOnlyDictionary<string, SnapshotPropertyDto> Properties,
    SnapshotHealthDto Health,
    IReadOnlyDictionary<string, SnapshotEventDto> LastEvents,
    long Sequence,
    DateTimeOffset UpdatedAt);

/// <summary>The persisted JSON form of a property.</summary>
internal sealed record SnapshotPropertyDto(WpValue? Value, Quality Quality, DateTimeOffset? SourceTime, DateTimeOffset? ReceivedTime, DateTimeOffset? LastSeen, long Sequence, Guid? CausedBy);

/// <summary>The persisted JSON form of the health.</summary>
internal sealed record SnapshotHealthDto(Connectivity Connectivity, IReadOnlyList<string> Faults, string? Reason, DateTimeOffset? LastContact, DateTimeOffset? Since);

/// <summary>The persisted JSON form of a last event.</summary>
internal sealed record SnapshotEventDto(DateTimeOffset Time, Severity Severity, long Sequence);

/// <summary>Source-generated JSON metadata of the snapshot DTOs, used with the <c>Log</c> JSON profile.</summary>
[JsonSerializable(typeof(SnapshotEntityDto))]
internal sealed partial class StateSnapshotJsonContext : JsonSerializerContext;
