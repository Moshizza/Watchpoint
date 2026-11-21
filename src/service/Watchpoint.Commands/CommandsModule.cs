using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Commands.Contracts;
using Watchpoint.Commands.Internal;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands;

/// <summary>Service registration of the commands module (spec CMD §3).</summary>
public static class CommandsModule
{
    /// <summary>
    /// Adds the command service (<see cref="ICommandService"/>), its partition subscriber (order 200) and the M2 fallback
    /// <see cref="ICommandAuthorizer"/> unless one is registered (RGT). Needs the registry, model, pipeline, identity and
    /// integration runtime modules. Settings come from the <c>Commands</c> section (CMD §12).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">A setting is invalid.</exception>
    public static IServiceCollection AddWatchpointCommands(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(CommandsOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICommandAuthorizer, FallbackCommandAuthorizer>();
        services.TryAddSingleton<IStepUpValidator, IdentityStepUpValidator>();
        services.TryAddSingleton<ICommandConnections, RuntimeCommandConnections>();
        services.AddSingleton<StepUpGuard>();
        services.AddSingleton<CommandMetrics>();
        services.AddSingleton<CommandWaiters>();
        services.AddSingleton<CommandDirectory>();
        services.AddSingleton<CommandRecordCodec>();
        services.AddSingleton<CommandProcessor>();
        services.AddSingleton<PendingCommandSnapshot>();
        services.AddSingleton<IPartitionSubscriber, CommandSubscriber>();
        services.TryAddSingleton<ICommandPartitions, PipelineCommandPartitions>();
        services.AddSingleton<CommandValidator>();
        services.AddSingleton<ICommandService, CommandService>();
        return services;
    }
}
