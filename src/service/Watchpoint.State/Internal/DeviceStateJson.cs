using System.Text.Json.Serialization;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>Maps a <see cref="DeviceState"/> to its JSON form (spec STA §7.1).</summary>
internal static class DeviceStateJson
{
    private static readonly System.Text.Json.JsonSerializerOptions _options = WatchpointJson.Create(JsonProfile.Api, StateJsonContext.Default);

    /// <summary>Serialises a state to the §7.1 JSON form, applying the effective-quality rule (spec STA §4).</summary>
    public static string ToJson(DeviceState state) => System.Text.Json.JsonSerializer.Serialize(ToDto(state), _options);

    /// <summary>Builds the DTO of a state, applying the effective-quality rule (spec STA §4).</summary>
    public static DeviceStateDto ToDto(DeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var properties = new Dictionary<string, PropertyStateDto>(state.Properties.Count);
        foreach (var (path, property) in state.Properties)
        {
            var quality = state.ServerUnavailable ? Quality.Stale : property.Quality;
            properties[path.Value] = new PropertyStateDto(property.Value, quality, property.SourceTime, property.ReceivedTime, property.LastSeen, property.Sequence);
        }

        var lastEvents = new Dictionary<string, LastEventDto>(state.LastEvents.Count);
        foreach (var (path, lastEvent) in state.LastEvents)
        {
            lastEvents[path.Value] = new LastEventDto(lastEvent.Time, lastEvent.Severity);
        }

        return new DeviceStateDto(
            state.EntityId,
            state.Kind,
            new TypeRefDto(state.Type.Id.Value, state.Type.Version.ToString()),
            state.Summary.State,
            state.Summary.UnderlyingState,
            state.Summary.Cause?.Value,
            state.ServerUnavailable,
            new HealthStateDto(state.Health.Connectivity, state.Health.Faults, state.Health.Reason, state.Health.LastContact, state.Health.Since),
            properties,
            lastEvents,
            state.Sequence,
            state.UpdatedAt);
    }
}

/// <summary>The §7.1 JSON form of a <see cref="DeviceState"/>.</summary>
internal sealed record DeviceStateDto(
    Guid Id,
    EntityKind Kind,
    TypeRefDto Type,
    OverallState Overall,
    OverallState Underlying,
    string? Cause,
    bool ServerUnavailable,
    HealthStateDto Health,
    IReadOnlyDictionary<string, PropertyStateDto> Properties,
    IReadOnlyDictionary<string, LastEventDto> LastEvents,
    long Sequence,
    DateTimeOffset UpdatedAt);

/// <summary>The §7.1 JSON form of a <see cref="Model.Contracts.TypeRef"/>.</summary>
internal sealed record TypeRefDto(string Id, string Version);

/// <summary>The §7.1 JSON form of a <see cref="Contracts.HealthState"/>.</summary>
internal sealed record HealthStateDto(Connectivity Connectivity, IReadOnlyList<string> Faults, string? Reason, DateTimeOffset? LastContact, DateTimeOffset? Since);

/// <summary>The §7.1 JSON form of a <see cref="Contracts.PropertyState"/>.</summary>
internal sealed record PropertyStateDto(WpValue? Value, Quality Quality, DateTimeOffset? SourceTime, DateTimeOffset? ReceivedTime, DateTimeOffset? LastSeen, long Sequence);

/// <summary>The §7.1 JSON form of a <see cref="Contracts.LastEvent"/>.</summary>
internal sealed record LastEventDto(DateTimeOffset Time, Severity Severity);

/// <summary>Source-generated JSON metadata of the §7.1 DTOs, used with the <c>Api</c> JSON profile.</summary>
[JsonSerializable(typeof(DeviceStateDto))]
internal sealed partial class StateJsonContext : JsonSerializerContext;
