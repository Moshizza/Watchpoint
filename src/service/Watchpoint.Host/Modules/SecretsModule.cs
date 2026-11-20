using Watchpoint.Hosting;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Host.Modules;

/// <summary>The secrets vault (spec SEC): connection credentials (IRT §8) are stored and read through it.</summary>
public sealed class SecretsModule : IWatchpointModule
{
    public string Name => "Secrets";

    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);

        services.AddWatchpointSecrets(context.Configuration, options =>
        {
            options.DataDirectory = context.Paths.Data;
            options.NodeId = context.Node.Node.Id.ToString("D");
            options.SiteName = context.Node.Node.Name;
        });
        services.AddStartupTask<SecretsStartupTask>();
    }
}

/// <summary>Master key, bootstrap secrets and site certificates (SEC §3), phase <c>Infrastructure</c>.</summary>
public sealed class SecretsStartupTask(ISecretsStartup secrets) : IStartupTask
{
    public StartupPhase Phase => StartupPhase.Infrastructure;

    public int Order => 5;

    public string Name => "Secrets";

    public Task ExecuteAsync(CancellationToken ct) => secrets.InitializeAsync(ct);
}
