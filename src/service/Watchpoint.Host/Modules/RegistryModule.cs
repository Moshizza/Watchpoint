using Watchpoint.Hosting;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry;

namespace Watchpoint.Host.Modules;

/// <summary>Registry module (spec REG §3, HST §5.2): servers, devices, locations and tags, which the ingest validates observations against.</summary>
public sealed class RegistryModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Registry";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException"><c>Site:Id</c> is not a UUID v7 (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Node.Site.Id == Guid.Empty || !NodeSettings.IsValidV7(context.Node.Site.Id))
        {
            throw new InvalidSettingsException(Name, "Site:Id must be a valid UUIDv7 (generated at first start)");
        }

        services.AddWatchpointRegistry(SiteId.From(context.Node.Site.Id));
    }
}
