using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>One change published by <see cref="IStateChangeFeed"/> (spec STA §5, §6.5).</summary>
/// <param name="EntityId">The device or server id.</param>
/// <param name="Sequence">The event log sequence that produced the change.</param>
/// <param name="ChangedProperties">The properties whose value or quality changed.</param>
/// <param name="HealthChanged">Whether the health state changed.</param>
/// <param name="NewOverallState">The new overall state, when it changed; otherwise <see langword="null"/>.</param>
/// <param name="At">When the change was applied.</param>
public sealed record StateChange(
    Guid EntityId,
    long Sequence,
    IReadOnlyList<PropertyPath> ChangedProperties,
    bool HealthChanged,
    OverallState? NewOverallState,
    DateTimeOffset At);
