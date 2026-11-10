using System.Reflection;
using Watchpoint.Hosting;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Model;
using Watchpoint.Model.Documents;

namespace Watchpoint.Host.Modules;

/// <summary>Model module (spec MOD §3, HST §5.2): the type catalogue the integration runtime registers package types with.</summary>
public sealed class ModelModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Model";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddWatchpointModel();
    }
}

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
        services.AddStartupTask<PackageScanStartupTask>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapPackageEndpoints();
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
