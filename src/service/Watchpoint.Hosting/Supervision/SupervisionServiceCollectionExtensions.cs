using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>Registration of the process supervisor (spec HST §7).</summary>
public static class SupervisionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISupervisor"/>, the janitor of <c>&lt;Data&gt;/run</c>, the leftover cleanup at start-up
    /// and the shutdown task that stops every child.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="paths">The node paths.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddProcessSupervisor(this IServiceCollection services, NodePaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);
        var options = new SupervisorOptions { RunDirectory = Path.Combine(paths.Data, "run") };
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new ChildProcessJanitor(
            options.RunDirectory, sp.GetRequiredService<ILogger<ChildProcessJanitor>>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton(sp => new Supervisor(
            options, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ChildProcessJanitor>()));
        services.TryAddSingleton<ISupervisor>(sp => sp.GetRequiredService<Supervisor>());
        services.AddStartupTask<LeftoverCleanupStartupTask>();
        services.AddShutdownTask<SupervisorShutdownTask>();
        return services;
    }
}
