using System.Reflection;
using Watchpoint.Api.Contracts;
using Watchpoint.Hosting;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>The REST framework and its system endpoints (spec API §4–§7). Last module: it validates every mapped endpoint.</summary>
public sealed class ApiModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Api";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        var node = context.Node;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        var info = new SystemInfo(node.Node.Id, node.Node.Name, node.Node.Environment.ToString().ToLowerInvariant(), version,
            [.. ModuleCatalog.All.Select(m => m.Name.ToLowerInvariant())], [.. node.Http.Urls]);
        services.AddWatchpointApi(context.Configuration, info, isProduction: !context.IsDevelopment);
        services.AddSingleton<ISiteNameProvider, RegistrySiteName>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapWatchpointApi();

    private sealed class RegistrySiteName(IServiceProvider services) : ISiteNameProvider
    {
        public string? GetSiteName()
        {
            try
            {
                return services.GetService<IRegistryReader>()?.Site.Name.Default;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
