using Microsoft.Extensions.DependencyInjection;

namespace Watchpoint.Hosting;

/// <summary>
/// Extension methods for registering startup and shutdown tasks on <see cref="IServiceCollection"/>.
/// </summary>
public static class ServiceCollectionTaskExtensions
{
    /// <summary>Registers an implementation of <see cref="IStartupTask"/>.</summary>
    /// <typeparam name="T">The startup task implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddStartupTask<T>(this IServiceCollection services)
        where T : class, IStartupTask
    {
        services.AddSingleton<IStartupTask, T>();
        return services;
    }

    /// <summary>Registers an implementation of <see cref="IShutdownTask"/>.</summary>
    /// <typeparam name="T">The shutdown task implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddShutdownTask<T>(this IServiceCollection services)
        where T : class, IShutdownTask
    {
        services.AddSingleton<IShutdownTask, T>();
        return services;
    }
}
