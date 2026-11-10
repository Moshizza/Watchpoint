using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Expressions.Contracts;
using Watchpoint.Expressions.Internal;

namespace Watchpoint.Expressions;

/// <summary>Service registration extensions for Watchpoint.Expressions (spec EXP §3).</summary>
public static class ExpressionsModule
{
    /// <summary>Adds the Watchpoint expressions compiler services to the service collection.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointExpressions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IExpressionCompiler, ExpressionCompiler>();

        return services;
    }
}
