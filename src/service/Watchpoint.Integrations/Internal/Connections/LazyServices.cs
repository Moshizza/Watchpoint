using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Integrations.Internal.Connections;

/// <summary>The catalogue's view of the connections, resolved on first use (the manager depends on the catalogue).</summary>
internal sealed class LazyPackageConnections(IServiceProvider services) : IPackageConnections
{
    private readonly Lazy<ConnectionManager> _manager = new(services.GetRequiredService<ConnectionManager>);

    public bool IsInUse(PackageId id, SemVersion version) => _manager.Value.IsInUse(id, version);

    public Task StopForPackageAsync(PackageId id, SemVersion version, string reason, CancellationToken ct) =>
        _manager.Value.StopForPackageAsync(id, version, reason, ct);
}

/// <summary>The ingest's flooding listener, resolved on first signal (the manager depends on the ingest).</summary>
internal sealed class LazyFloodingListener(IServiceProvider services) : IIngestFloodingListener
{
    private readonly Lazy<ConnectionManager> _manager = new(services.GetRequiredService<ConnectionManager>);

    public void OnFloodingChanged(ConnectionId connection, bool flooding, int pending) => _manager.Value.OnFloodingChanged(connection, flooding, pending);
}
