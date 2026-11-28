using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Watchpoint.Configuration.Adapters;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Publish;
using Watchpoint.Configuration.Internal.Storage;
using Watchpoint.Configuration.Internal.Validation;
using Watchpoint.Configuration.Internal.Versions;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Configuration;

/// <summary>Service registration of the configuration module (spec CFG §3).</summary>
public static class ConfigurationModule
{
    /// <summary>
    /// Adds the object type registry, the draft service and the quick edit. The host provides the ports: <see cref="IConfigurationAuthorizer"/>
    /// (RGT), <see cref="IApprovalPolicy"/> (settings), <see cref="IDraftRepository"/> (an in-memory one unless <see cref="AddConfigurationPublishing"/>
    /// stores drafts in <c>wp_config</c>), and optionally <see cref="ILicenceProjection"/> (LIC). Secrets of drafts go to <c>ISecretVault</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="settings">The settings, or <see langword="null"/> for the defaults.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointConfiguration(this IServiceCollection services, ConfigurationSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings ?? new ConfigurationSettings());
        services.TryAddSingleton<IDraftRepository, InMemoryDraftRepository>();
        services.TryAddSingleton<IApprovalPolicy>(sp => new SettingsApprovalPolicy(sp.GetRequiredService<ConfigurationSettings>()));
        services.AddSingleton<ObjectTypeRegistry>();
        services.AddSingleton<IConfigObjectTypeRegistry>(sp => sp.GetRequiredService<ObjectTypeRegistry>());
        services.AddSingleton<DraftSecrets>();
        services.AddSingleton(sp => new DraftValidator(sp.GetRequiredService<IConfigObjectTypeRegistry>(), sp.GetService<ILicenceProjection>()));
        services.AddSingleton<DraftService>();
        services.AddSingleton<IDraftService>(sp => sp.GetRequiredService<DraftService>());
        services.AddSingleton<IQuickEdit>(sp => new QuickEdit(
            sp.GetRequiredService<DraftService>(),
            sp.GetRequiredService<IConfigObjectTypeRegistry>(),
            sp.GetRequiredService<IConfigurationAuthorizer>(),
            sp.GetRequiredService<IApprovalPolicy>(),
            sp.GetService<IPublishService>()));
        return services;
    }

    /// <summary>
    /// Adds publishing (spec CFG §7–§8) on PostgreSQL: drafts and versions in <c>wp_config</c> (replacing the in-memory draft
    /// repository), the publish orchestrator (<see cref="IPublishService"/>), <see cref="IDraftPublisher"/> (approval, scheduled
    /// publish when the host registers an <see cref="IConfigScheduler"/>, rebase), <see cref="IVersionService"/>, <c>config.published</c> on <c>WP_CFG</c>,
    /// the registry's object types and applier, and the M2 fallback <see cref="IConfigurationAuthorizer"/> (administrators) unless
    /// RGT registered one. Call after <see cref="AddWatchpointConfiguration"/>; needs STO, PIP, REG and IDN.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddConfigurationPublishing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddModuleStorage<ConfigDbContext>(ConfigDbContext.ModuleName, p => p.Add<ConfigPostgresDbContext>("postgres"));
        services.Replace(ServiceDescriptor.Singleton<IDraftRepository, PostgresDraftRepository>());
        services.TryAddSingleton<IConfigurationAuthorizer, FallbackConfigurationAuthorizer>();
        services.TryAddSingleton<IPublishStore, PostgresPublishStore>();
        services.TryAddSingleton<IConfigPublishedSink, LogConfigPublishedSink>();
        services.AddSingleton<IConfigApplier, RegistryConfigApplier>();
        services.AddSingleton<RegistryConfigSource>();
        services.TryAddSingleton(sp => new PublishOrchestrator(
            sp.GetRequiredService<IDraftRepository>(), sp.GetRequiredService<DraftService>(), sp.GetRequiredService<IConfigObjectTypeRegistry>(),
            sp.GetServices<IConfigApplier>(), sp.GetRequiredService<IPublishStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PublishOrchestrator>>(), sp.GetService<IConfigPublishedSink>(), sp.GetService<IConfigAudit>(), sp.GetService<IMeterFactory>()));
        services.TryAddSingleton<IPublishService>(sp => sp.GetRequiredService<PublishOrchestrator>());
        services.TryAddSingleton(sp => new DraftPublisher(
            sp.GetRequiredService<DraftService>(), sp.GetRequiredService<IDraftRepository>(), sp.GetRequiredService<IPublishService>(),
            sp.GetRequiredService<IConfigObjectTypeRegistry>(), sp.GetRequiredService<IConfigurationAuthorizer>(), sp.GetRequiredService<IApprovalPolicy>(),
            sp.GetRequiredService<IPublishStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<DraftPublisher>>(),
            sp.GetService<IConfigScheduler>()));
        services.TryAddSingleton<IDraftPublisher>(sp => sp.GetRequiredService<DraftPublisher>());
        services.TryAddSingleton<IVersionService, VersionService>();
        return services;
    }

    /// <summary>Registers the object types whose module can publish today (REG, CFG §4; the others follow with T3.02b).</summary>
    /// <param name="provider">The services.</param>
    public static void RegisterPublishableTypes(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var registry = provider.GetRequiredService<IConfigObjectTypeRegistry>();
        var source = provider.GetRequiredService<RegistryConfigSource>();
        foreach (var definition in ModuleObjectTypes.Registry)
        {
            if (registry.Find(definition.Type) is null)
            {
                registry.Register(new DocumentObjectType(definition, source));
            }
        }
    }

    /// <summary>Re-arms the schedules of scheduled drafts (start-up, phase Runtime).</summary>
    /// <param name="provider">The services.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task.</returns>
    public static Task ResumeScheduledPublishesAsync(IServiceProvider provider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.GetRequiredService<DraftPublisher>().ResumeSchedulesAsync(ct);
    }

    private sealed class SettingsApprovalPolicy(ConfigurationSettings settings) : IApprovalPolicy
    {
        public bool RequiresApproval(IReadOnlyCollection<string> objectTypes) => objectTypes.Any(t => settings.ApprovalRequiredFor.Contains(t, StringComparer.Ordinal));
    }
}
