using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Secrets.Internal;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Secrets.Contracts;

/// <summary>Registration of the secrets module.</summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the vault, bootstrap store, certificates, start-up, maintenance and the module storage; replaces the storage
    /// module's password provider so <c>bootstrap:</c> database passwords work.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">Configuration root (sections <c>Secrets</c>, <c>Http:Certificate</c>).</param>
    /// <param name="node">Sets the node facts (<see cref="SecretsOptions.DataDirectory"/>, <see cref="SecretsOptions.NodeId"/>, site name).</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddWatchpointSecrets(this IServiceCollection services, IConfiguration configuration, Action<SecretsOptions> node)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(node);
        var options = new SecretsOptions
        {
            MasterKeyProvider = configuration["Secrets:MasterKeyProvider"],
            MaxValueBytes = configuration.GetValue("Secrets:MaxValueBytes", 65536),
            CertificatePath = configuration["Http:Certificate:Path"],
            CertificateKeyPath = configuration["Http:Certificate:KeyPath"],
            CertificatePasswordRef = configuration["Http:Certificate:PasswordRef"],
        };
        foreach (var name in configuration.GetSection("Http:Certificate:ExtraNames").GetChildren().Select(c => c.Value).OfType<string>())
        {
            options.CertificateExtraNames.Add(name);
        }

        node(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton(new MasterKeyAccessorEnvironment(Environment.GetEnvironmentVariable));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MasterKeyAccessor>(sp, sp.GetRequiredService<MasterKeyAccessorEnvironment>().Read));
        services.AddSingleton<BootstrapSecretStore>();
        services.AddSingleton<IBootstrapSecretStore>(sp => sp.GetRequiredService<BootstrapSecretStore>());
        services.AddMetrics();
        services.AddSingleton<SecretsMetrics>();
        services.TryAddSingleton<ISecretsAuditSink, LoggingSecretsAuditSink>();
        services.AddSingleton<SecretVault>();
        services.AddSingleton<ISecretVault>(sp => sp.GetRequiredService<SecretVault>());
        services.AddSingleton<SiteCertificateService>();
        services.AddSingleton<ISiteCertificateService>(sp => sp.GetRequiredService<SiteCertificateService>());
        services.AddSingleton<ISecretsStartup, SecretsStartup>();
        services.AddHostedService<SecretsMaintenanceService>();
        services.AddModuleStorage<SecretsDbContext>(SecretsDbContext.ModuleName, p => p.Add<SecretsPostgresDbContext>("postgres"));
        services.RemoveAll<IDatabasePasswordProvider>();
        services.AddSingleton<IDatabasePasswordProvider, BootstrapDatabasePasswordProvider>();
        return services;
    }
}
