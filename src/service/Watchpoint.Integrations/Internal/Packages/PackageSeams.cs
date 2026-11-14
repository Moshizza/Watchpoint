using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>What the package catalogue needs from the connections (implemented by the connection manager, T1.16).</summary>
internal interface IPackageConnections
{
    /// <summary>Gets whether a connection is configured on the package version.</summary>
    bool IsInUse(PackageId id, SemVersion version);

    /// <summary>Stops the connections of the package version (state <c>Disabled</c> by package).</summary>
    Task StopForPackageAsync(PackageId id, SemVersion version, string reason, CancellationToken ct);
}

/// <summary>No connections exist until the connection manager is registered (T1.16).</summary>
internal sealed class NoPackageConnections : IPackageConnections
{
    public bool IsInUse(PackageId id, SemVersion version) => false;

    public Task StopForPackageAsync(PackageId id, SemVersion version, string reason, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A row of <c>wp_integrations.packages</c> (spec IRT §6).</summary>
internal sealed record PackageRow(string Id, string Version, string State, string Source, string? FilePath, string Problems,
    bool SignedByDev, bool Enabled, DateTimeOffset DiscoveredAt);

/// <summary>Persistence of the package states.</summary>
internal interface IPackageStore
{
    Task<IReadOnlyList<PackageRow>> ListAsync(CancellationToken ct);

    Task SaveAsync(PackageRow row, CancellationToken ct);
}

/// <summary>Store used when the node has no database (tests, tools).</summary>
internal sealed class InMemoryPackageStore : IPackageStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string, string), PackageRow> _rows = [];

    public Task<IReadOnlyList<PackageRow>> ListAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<PackageRow>>([.. _rows.Values]);
        }
    }

    public Task SaveAsync(PackageRow row, CancellationToken ct)
    {
        lock (_lock)
        {
            _rows[(row.Id, row.Version)] = row;
        }

        return Task.CompletedTask;
    }
}
