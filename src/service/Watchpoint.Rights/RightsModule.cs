using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Watchpoint.Api.Contracts;
using Watchpoint.Commands.Contracts;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Adapters;
using Watchpoint.Rights.Internal.Catalogue;
using Watchpoint.Rights.Internal.Configuration;
using Watchpoint.Rights.Internal.Evaluation;
using Watchpoint.Rights.Internal.Groups;
using Watchpoint.Rights.Internal.Storage;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Rights;

/// <summary>Registration of the rights module (spec RGT).</summary>
public static class RightsModule
{
    /// <summary>
    /// Registers the catalogue, the grant store, the evaluator, user groups and the adapters that replace the M1–M2 fallbacks
    /// (<see cref="IPermissionEvaluator"/>, <see cref="ICommandAuthorizer"/>). Needs identity, registry, model, scheduling and storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddWatchpointRights(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleStorage<RightsDbContext>(RightsDbContext.ModuleName, providers => providers.Add<RightsPostgresDbContext>("postgres"));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPermissionCatalogue, PermissionCatalogue>();
        services.TryAddSingleton<IRightsPersistence, EfRightsPersistence>();
        services.TryAddSingleton<GrantStore>();
        services.TryAddSingleton<IGrantStore>(sp => sp.GetRequiredService<GrantStore>());
        services.TryAddSingleton<IRightsUserLookup, DirectoryUserLookup>();
        services.TryAddSingleton<RightsEvaluator>();
        services.TryAddSingleton<IRightsEvaluator>(sp => sp.GetRequiredService<RightsEvaluator>());
        services.TryAddSingleton<IUserBulkActions, IdentityBulkActions>();
        services.TryAddSingleton<UserGroupService>();
        services.TryAddSingleton<IUserGroupService>(sp => sp.GetRequiredService<UserGroupService>());
        services.TryAddSingleton<CommandPermissionGenerator>();
        services.TryAddSingleton(sp => new GrantsObjectType(sp.GetRequiredService<IGrantStore>(), sp.GetRequiredService<IPermissionCatalogue>(),
            key => sp.GetRequiredService<Watchpoint.Scheduling.ICalendarService>().Get(key).IsSuccess));
        services.AddHostedService<RightsStartup>();

        // These replace the fallbacks the API and CMD register with TryAdd.
        services.Replace(ServiceDescriptor.Singleton<IPermissionEvaluator, PermissionEvaluatorAdapter>());
        services.Replace(ServiceDescriptor.Singleton<ICommandAuthorizer, CommandAuthorizer>());
        return services;
    }

    private sealed class RightsStartup(GrantStore grants, UserGroupService groups, IServiceProvider services) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await grants.LoadAsync(cancellationToken).ConfigureAwait(false);
            await groups.LoadAsync(cancellationToken).ConfigureAwait(false);
            _ = services.GetService<CommandPermissionGenerator>();
            Register(services);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static void Register(IServiceProvider sp)
        {
            var registry = sp.GetService<IConfigObjectTypeRegistry>();
            if (registry is not null && registry.Find(GrantsObjectType.TypeName) is null)
            {
                registry.Register(sp.GetRequiredService<GrantsObjectType>());
            }
        }
    }
}
