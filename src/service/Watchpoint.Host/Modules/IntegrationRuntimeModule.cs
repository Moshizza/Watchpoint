using System.Reflection;
using Watchpoint.Hosting;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Integration runtime module (spec IRT §3, HST §5.2): package catalogue, watcher and load contexts, the package
/// endpoints, and the start-up task scanning the package folders.
/// </summary>
public sealed class IntegrationRuntimeModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Integrations";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        var section = context.Configuration.GetSection("Integrations");
        var integrations = Path.Combine(context.Paths.Data, "integrations");
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var options = new IntegrationsOptions
        {
            BuiltinPath = section["BuiltinPath"] is { Length: > 0 } builtin ? builtin : Path.Combine(AppContext.BaseDirectory, "integrations-builtin"),
            PackagesPath = context.Paths.Packages,
            CachePath = Path.Combine(integrations, ".cache"),
            DevKeysPath = Path.Combine(integrations, "dev-keys"),
            IsDevelopment = context.IsDevelopment,
            ProductVersion = new SemVersion(version.Major, version.Minor, Math.Max(version.Build, 0)),
        };
        if (section["RescanInterval"] is { Length: > 0 } rescan)
        {
            options.RescanInterval = System.Xml.XmlConvert.ToTimeSpan(rescan);
        }

        services.AddWatchpointIntegrations(options);
        services.AddWatchpointConnections(BindConnections(context.Configuration.GetSection("Connections")));
        services.AddStartupTask<PackageScanStartupTask>();
        services.AddStartupTask<ConnectionsLoadStartupTask>();
        services.AddStartupTask<ConnectionsStartupTask>();
        services.AddShutdownTask<ConnectionsShutdownTask>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPackageEndpoints();
        endpoints.MapConnectionEndpoints();
    }

    /// <summary>Binds the <c>Connections:*</c> settings (IRT §9; durations in ISO 8601).</summary>
    /// <param name="section">The <c>Connections</c> section.</param>
    /// <returns>The options.</returns>
    public static ConnectionsOptions BindConnections(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var options = new ConnectionsOptions();
        options.StartStagger = Duration(section, "StartStagger") ?? options.StartStagger;
        options.FaultAfter = Duration(section, "FaultAfter") ?? options.FaultAfter;
        options.SilenceTimeout = Duration(section, "SilenceTimeout") ?? options.SilenceTimeout;
        options.MaxRestartBackoff = Duration(section, "MaxRestartBackoff") ?? options.MaxRestartBackoff;
        if (section["SinkBuffer"] is { Length: > 0 } buffer)
        {
            options.SinkBuffer = int.Parse(buffer, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        }

        return options;
    }

    private static TimeSpan? Duration(IConfiguration section, string key) =>
        section[key] is { Length: > 0 } text ? System.Xml.XmlConvert.ToTimeSpan(text) : null;
}

/// <summary>Phase <c>Catalogue</c>, order 20: loads the stored connections after the package scan, before the dev seed creates any.</summary>
/// <param name="connections">The connections.</param>
public sealed class ConnectionsLoadStartupTask(IConnectionsLifecycle connections) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Catalogue;

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public string Name => "ConnectionsLoad";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => connections.LoadAsync(ct);
}

/// <summary>Phase <c>Connections</c>: starts the enabled connections, staggered (IRT §7.2), and the watchdog.</summary>
/// <param name="connections">The connections.</param>
public sealed class ConnectionsStartupTask(IConnectionsLifecycle connections) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Connections;

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public string Name => "Connections";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => connections.ActivateAsync(ct);
}

/// <summary>Stops the connectors at shutdown, before the pipeline drains (shutdown runs the phases in reverse order).</summary>
/// <param name="connections">The connections.</param>
public sealed class ConnectionsShutdownTask(IConnectionsLifecycle connections) : IShutdownTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Connections;

    /// <inheritdoc />
    public string Name => "ConnectionsStop";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => connections.StopAllAsync(ct);
}

/// <summary>Phase <c>Catalogue</c>, order 10: scans the builtin and user package folders, then starts watching (IRT §4.1).</summary>
/// <param name="watcher">The package watcher.</param>
public sealed class PackageScanStartupTask(IPackageWatcher watcher) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Catalogue;

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public string Name => "PackageScan";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => watcher.StartAsync(ct);
}
