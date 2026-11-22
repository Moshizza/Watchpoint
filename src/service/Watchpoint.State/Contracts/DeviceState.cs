using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>
/// The current state of one device or server (spec STA §4): its properties, health, overall state summary and last
/// events. Immutable; a change replaces the instance held for the entity.
/// </summary>
/// <param name="EntityId">The device or server id.</param>
/// <param name="Kind">Whether this is a device or a server.</param>
/// <param name="Type">The type of the entity.</param>
/// <param name="Properties">The current value of every property declared by the type.</param>
/// <param name="Health">The connectivity and faults of the entity.</param>
/// <param name="Summary">The computed overall state summary.</param>
/// <param name="ServerUnavailable">Whether the hosting server (or an ancestor server) is offline or unknown.</param>
/// <param name="LastEvents">The last occurrence of every event path that has occurred, keyed by path.</param>
/// <param name="Sequence">The last event log sequence applied to this entity.</param>
/// <param name="UpdatedAt">When this instance was produced.</param>
public sealed record DeviceState(
    Guid EntityId,
    EntityKind Kind,
    TypeRef Type,
    IReadOnlyDictionary<PropertyPath, PropertyState> Properties,
    HealthState Health,
    StateSummary Summary,
    bool ServerUnavailable,
    IReadOnlyDictionary<EventPath, LastEvent> LastEvents,
    long Sequence,
    DateTimeOffset UpdatedAt);
