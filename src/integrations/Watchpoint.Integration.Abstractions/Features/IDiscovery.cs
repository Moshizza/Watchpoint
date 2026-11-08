using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Thorough discovery of the system's inventory, for the wizard and "rediscover" (INT §6).</summary>
public interface IDiscovery
{
    /// <summary>Discovers the inventory; may be slower and more thorough than <see cref="IConnector.DescribeAsync"/>.</summary>
    /// <param name="progress">Receives progress.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inventory.</returns>
    ValueTask<InventorySnapshot> DiscoverAsync(IProgress<DiscoveryProgress> progress, CancellationToken ct);
}

/// <summary>Progress of a discovery.</summary>
/// <param name="Done">Items processed so far.</param>
/// <param name="Total">Total items, when known.</param>
/// <param name="Stage">What is being read, for display.</param>
public sealed record DiscoveryProgress(int Done, int? Total, ConnectorMessage? Stage);
