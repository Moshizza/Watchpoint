using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Validation;

namespace Watchpoint.Configuration;

/// <summary>Service registration of the configuration module (spec CFG §3).</summary>
public static class ConfigurationModule
{
    /// <summary>
    /// Adds the object type registry, the draft service and the quick edit. The host provides the ports: <see cref="IConfigurationAuthorizer"/>
    /// (RGT), <see cref="IApprovalPolicy"/> (settings), <see cref="IDraftRepository"/> (an in-memory one is used until the PostgreSQL store of
    /// T3.02), and optionally <see cref="ILicenceProjection"/> (LIC) and <see cref="IPublishService"/> (T3.02). Secrets of drafts go to
    /// <c>ISecretVault</c>.
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

    private sealed class SettingsApprovalPolicy(ConfigurationSettings settings) : IApprovalPolicy
    {
        public bool RequiresApproval(IReadOnlyCollection<string> objectTypes) => objectTypes.Any(t => settings.ApprovalRequiredFor.Contains(t, StringComparer.Ordinal));
    }
}
