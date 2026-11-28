using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Watchpoint.Audit.Contracts;

/// <summary>Registration of the default audit sink.</summary>
public static class AuditContractsServiceCollectionExtensions
{
    /// <summary>Registers <see cref="LoggingAuditSink"/> as <see cref="IAuditSink"/> unless a sink is already registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddLoggingAuditSink(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAuditSink, LoggingAuditSink>();
        return services;
    }
}
