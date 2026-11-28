using Watchpoint.Configuration;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Hosting;
using Watchpoint.Scheduling;

namespace Watchpoint.Host.Modules;

/// <summary>
/// Configuration module (spec CFG §3, HST §5.2): drafts and validation, publish with versions and rollback on PostgreSQL
/// (<c>wp_config</c>), and the object types of the modules that can publish (REG; the others follow with T3.02b).
/// </summary>
public sealed class ConfigurationModule : IWatchpointModule
{
    /// <inheritdoc />
    public string Name => "Configuration";

    /// <inheritdoc />
    /// <exception cref="InvalidSettingsException">A <c>Configuration</c> setting is invalid (exit code 78).</exception>
    public void ConfigureServices(IServiceCollection services, ModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(context);
        ConfigurationSettings settings;
        try
        {
            settings = context.Configuration.GetSection(ConfigurationSettings.SectionName).Get<ConfigurationSettings>() ?? new ConfigurationSettings();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidSettingsException(Name, ex.Message);
        }

        services.AddWatchpointConfiguration(settings);
        if (services.Any(d => d.ServiceType == typeof(ITimeTriggerService)))
        {
            // Scheduled publish through SCH one-shot triggers (owner "config"), when SCH is composed.
            services.AddSingleton<IConfigScheduler>(sp => new SchedulingConfigScheduler(sp.GetRequiredService<ITimeTriggerService>()));
        }

        services.AddConfigurationPublishing();
        services.AddStartupTask<ConfigurationTypesStartupTask>();
        services.AddStartupTask<ConfigurationSchedulesStartupTask>();
    }
}

/// <summary>Registers the publishable object types once the registry is loaded (phase <c>Configuration</c>).</summary>
/// <param name="services">The services.</param>
public sealed class ConfigurationTypesStartupTask(IServiceProvider services) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Configuration;

    /// <inheritdoc />
    public string Name => "ConfigurationTypes";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct)
    {
        Watchpoint.Configuration.ConfigurationModule.RegisterPublishableTypes(services);
        return Task.CompletedTask;
    }
}

/// <summary>Re-arms the schedules of scheduled drafts (phase <c>Runtime</c>).</summary>
/// <param name="services">The services.</param>
public sealed class ConfigurationSchedulesStartupTask(IServiceProvider services) : IStartupTask
{
    /// <inheritdoc />
    public StartupPhase Phase => StartupPhase.Runtime;

    /// <inheritdoc />
    public string Name => "ConfigurationSchedules";

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken ct) => Watchpoint.Configuration.ConfigurationModule.ResumeScheduledPublishesAsync(services, ct);
}

/// <summary>Scheduled publish through SCH one-shot triggers (spec CFG §8).</summary>
/// <param name="triggers">SCH's trigger service.</param>
public sealed class SchedulingConfigScheduler(ITimeTriggerService triggers) : IConfigScheduler
{
    /// <inheritdoc />
    public IDisposable At(string key, DateTimeOffset instant, Func<CancellationToken, ValueTask> callback) =>
        triggers.Register("config", key, new TriggerSpec.At(instant), MissedPolicy.FireOnce, (_, ct) => callback(ct));
}
