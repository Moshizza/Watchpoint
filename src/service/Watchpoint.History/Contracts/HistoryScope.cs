using Watchpoint.Kernel.Ids;

namespace Watchpoint.History.Contracts;

/// <summary>
/// What the caller may see (provided by RGT, spec RGT §7): visible locations, visible devices, or both, of which history
/// uses the smaller list. <see langword="null"/> lists mean unrestricted; an empty list means nothing is visible.
/// </summary>
/// <param name="VisibleLocations">The visible locations, each standing for its subtree.</param>
/// <param name="VisibleDevices">The visible devices.</param>
public sealed record HistoryScope(IReadOnlyList<LocationId>? VisibleLocations = null, IReadOnlyList<DeviceId>? VisibleDevices = null)
{
    /// <summary>No restriction.</summary>
    public static HistoryScope Unrestricted { get; } = new();
}
