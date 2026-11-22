using Watchpoint.Integration.Abstractions;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Internal.Hosting;

/// <summary>A connector instance created by a host, with what keeps its code loaded.</summary>
/// <param name="Connector">The instance.</param>
/// <param name="Permissions">The permissions the manifest grants the connector (e.g. <c>inboundTcp</c>).</param>
/// <param name="Release">Releases the code (the package load context lease); called once the instance is disposed or abandoned.</param>
internal sealed record HostedConnector(IConnector Connector, IReadOnlySet<string> Permissions, IDisposable? Release)
{
    /// <summary>Gets the limits the manifest declares for the connector.</summary>
    public Watchpoint.Model.Documents.ConnectorLimitsDoc? Limits { get; init; }
}

/// <summary>
/// Where connector instances run (spec IRT §3). M1 has the in-process host; IHO (M4) adds isolated and remote hosts
/// behind this abstraction.
/// </summary>
internal interface IConnectionHost
{
    HostMode Mode { get; }

    /// <summary>Creates a new connector instance of the connection's package version and server type.</summary>
    Result<HostedConnector> Create(Connection connection);
}

/// <summary>Runs connectors in the node process, in their package's load context (ADR-003: types are isolated, faults are not).</summary>
internal sealed class InProcessHost(PackageCatalog catalog, PackageLoadContextPool pool) : IConnectionHost
{
    public HostMode Mode => HostMode.InProcess;

    /// <summary>The permissions a manifest grants its connectors: exactly those it declares (<c>inboundTcp</c>, <c>inboundUdp</c>).</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The permissions.</returns>
    internal static IReadOnlySet<string> PermissionsOf(Watchpoint.Model.Documents.ManifestDocument manifest) =>
        new HashSet<string>(manifest.Permissions ?? [], StringComparer.Ordinal);

    public Result<HostedConnector> Create(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!catalog.GetLoadable(connection.PackageId, connection.PackageVersion).TryGetValue(out var loadable, out var error))
        {
            return error;
        }

        var (packageDir, manifest) = loadable;
        var definition = manifest.Connectors.FirstOrDefault(c => c.ServerType == connection.ServerType);
        if (definition is null)
        {
            return IntegrationErrors.NotFound(connection.PackageId, connection.PackageVersion.ToString()).WithArg("serverType", connection.ServerType);
        }

        var lease = pool.Acquire(connection.PackageId, connection.PackageVersion, packageDir, ConnectorCodeChecker.SplitClass(definition.Class).Assembly);
        try
        {
            // The permissions are those the manifest declares (inboundTcp, inboundUdp); a package that declares none is granted none.
            return new HostedConnector(lease.CreateConnector(definition.Class), PermissionsOf(manifest), lease) { Limits = definition.Limits };
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }
}
