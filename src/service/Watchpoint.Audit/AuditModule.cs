using System.Globalization;
using System.Xml;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.Identity.Contracts;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Audit;

/// <summary>Service registration of the audit module (spec AUD §3).</summary>
public static class AuditModule
{
    /// <summary>
    /// Adds the audit trail: the writer (which replaces the log-only <see cref="IAuditSink"/>, <see cref="IIdentityAuditSink"/> and
    /// <see cref="ISecretsAuditSink"/> without any caller change), checkpoints, continuous verification, retention, query, export and
    /// the health check. Settings come from the <c>Audit</c> section.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="chainId">The chain of this node (its node id).</param>
    /// <param name="dataDirectory">The node data directory; the fallback files go to its <c>audit</c> folder.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static IServiceCollection AddWatchpointAudit(this IServiceCollection services, IConfiguration configuration, string chainId, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(chainId);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        var settings = ReadSettings(configuration) with { ChainId = chainId, FallbackDirectory = Path.Combine(dataDirectory, "audit") };
        return services.AddAudit(settings);
    }

    internal static IServiceCollection AddAudit(this IServiceCollection services, AuditSettings settings)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(settings);
        services.AddSingleton<AuditMetrics>();
        services.AddSingleton<AuditIntegrityState>();
        services.TryAddSingleton<IAuditLabelResolver, DefaultAuditLabelResolver>();
        services.TryAddSingleton<IAuditIntegrityPublisher, NullAuditIntegrityPublisher>();
        services.TryAddSingleton<IAuditStore>(sp =>
        {
            var connections = sp.GetRequiredService<IStoreConnectionFactory>();
            return connections.GetProvider(StoreKind.Audit).CreateAuditStore(connections.GetDatabase(StoreKind.Audit));
        });
        services.TryAddSingleton<IAuditSigner, VaultAuditSigner>();

        // Replace the log-only sinks: callers keep their interfaces.
        services.AddSingleton<AuditWriter>();
        services.Replace(ServiceDescriptor.Singleton<IAuditSink>(sp => sp.GetRequiredService<AuditWriter>()));
        services.Replace(ServiceDescriptor.Singleton<IIdentityAuditSink, IdentityAuditAdapter>());
        services.Replace(ServiceDescriptor.Singleton<ISecretsAuditSink, SecretsAuditAdapter>());

        services.AddSingleton<AuditVerifier>();
        services.AddSingleton<IAuditVerifier>(sp => sp.GetRequiredService<AuditVerifier>());
        services.AddSingleton<AuditQueryService>();
        services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<AuditQueryService>());
        services.AddSingleton<IAuditExporter, AuditExporter>();
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<ContinuousVerifier>();
        services.AddSingleton<AuditRetention>();

        // Hosted services stop in reverse order: the writer drains first, then the final checkpoint is taken.
        services.AddHostedService(sp => sp.GetRequiredService<CheckpointService>());
        services.AddHostedService(sp => sp.GetRequiredService<ContinuousVerifier>());
        services.AddHostedService(sp => sp.GetRequiredService<AuditRetention>());
        services.AddHostedService(sp => sp.GetRequiredService<AuditWriter>());
        services.AddHealthChecks().AddCheck<AuditHealthCheck>(AuditHealthCheck.Name, tags: ["ready"]);
        services.AddSingleton<AuditHealthCheck>();
        return services;
    }

    internal static AuditSettings ReadSettings(IConfiguration configuration)
    {
        var section = configuration.GetSection(AuditSettings.SectionName);
        var settings = new AuditSettings();
        List<string> problems = [];
        settings = settings with
        {
            RetentionDays = Number(section, "RetentionDays", settings.RetentionDays, 365, 36_500, problems),
            ChannelCapacity = Number(section, "ChannelCapacity", settings.ChannelCapacity, 1, 1_000_000, problems),
            BatchSize = Number(section, "BatchSize", settings.BatchSize, 1, 10_000, problems),
            BatchDelay = Duration(section, "BatchDelay", settings.BatchDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10), problems),
            CheckpointInterval = Duration(section, "CheckpointInterval", settings.CheckpointInterval, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), problems),
            VerifyInterval = Duration(section, "VerifyInterval", settings.VerifyInterval, TimeSpan.FromSeconds(10), TimeSpan.FromDays(1), problems),
            FullVerifyInterval = Duration(section, "FullVerifyInterval", settings.FullVerifyInterval, TimeSpan.FromHours(1), TimeSpan.FromDays(365), problems),
            ShutdownTimeout = Duration(section, "ShutdownTimeout", settings.ShutdownTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), problems),
        };
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(' ', problems));
        }

        return settings;
    }

    private static int Number(IConfigurationSection section, string name, int fallback, int min, int max, List<string> problems)
    {
        if (section[name] is not { } text)
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
        {
            return value;
        }

        problems.Add($"Audit:{name} '{text}' must be a number from {min} to {max}.");
        return fallback;
    }

    private static TimeSpan Duration(IConfigurationSection section, string name, TimeSpan fallback, TimeSpan min, TimeSpan max, List<string> problems)
    {
        if (section[name] is not { } text)
        {
            return fallback;
        }

        try
        {
            var value = XmlConvert.ToTimeSpan(text);
            if (value >= min && value <= max)
            {
                return value;
            }
        }
        catch (FormatException)
        {
            // Reported below.
        }

        problems.Add($"Audit:{name} '{text}' must be an ISO 8601 duration from {XmlConvert.ToString(min)} to {XmlConvert.ToString(max)}.");
        return fallback;
    }
}
