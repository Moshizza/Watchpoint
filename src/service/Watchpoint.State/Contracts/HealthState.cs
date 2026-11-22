using Watchpoint.Kernel.Values;

namespace Watchpoint.State.Contracts;

/// <summary>The connectivity and faults of a device or server (spec STA §4).</summary>
/// <param name="Connectivity">The connectivity.</param>
/// <param name="Faults">The active fault codes.</param>
/// <param name="Reason">An optional reason text.</param>
/// <param name="LastContact">The last time the entity was heard from.</param>
/// <param name="Since">When <paramref name="Connectivity"/> last changed.</param>
public sealed record HealthState(
    Connectivity Connectivity,
    IReadOnlyList<string> Faults,
    string? Reason,
    DateTimeOffset? LastContact,
    DateTimeOffset? Since)
{
    /// <summary>The initial health of a newly created entity: unknown connectivity, no faults.</summary>
    public static HealthState Unknown { get; } = new(Connectivity.Unknown, [], null, null, null);
}
