using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Watchpoint.Kernel.Json;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Pipeline.Internal.Log;
using Watchpoint.Pipeline.Internal.Nats;
using Watchpoint.Pipeline.Internal.Partitions;
using Watchpoint.Pipeline.Internal.SideEffects;

namespace Watchpoint.Pipeline;

/// <summary>Service registration of the pipeline module (spec PIP §3).</summary>
public static class PipelineModule
{
    /// <summary>The name of the readiness health check: NATS connected and streams present.</summary>
    public const string HealthCheckName = "pipeline-log";

    /// <summary>
    /// Adds the event log: record kind registry, log writer and reader, durable consumers, stream provisioning
    /// (<see cref="IEventLogSetup"/>, run by the host at start-up), the readiness health check and the registry outbox
    /// publisher (a hosted service appending the registry's <c>registry.changed</c> records, PIP §5.5). Adds the ingest
    /// (<see cref="IObservationIngest"/>, validated against the registry and the type catalogue), the partition
    /// workers (<see cref="IPartitionDispatcher"/>; modules register <see cref="IPartitionSubscriber"/>s), the
    /// side-effect queue and <see cref="IPipelineLifecycle"/>, which the host runs at start-up and shutdown. The host
    /// registers the <see cref="EventLogEndpoint"/>; settings come from the <c>Log</c>, <c>Ingest</c> and
    /// <c>SideEffects</c> sections.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static IServiceCollection AddWatchpointPipeline(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddPipeline(PipelineOptions.FromConfiguration(configuration), IngestOptions.FromConfiguration(configuration));
    }

    internal static IServiceCollection AddPipeline(this IServiceCollection services, PipelineOptions options, IngestOptions? ingest = null)
    {
        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<PipelineMetrics>();
        services.AddSingleton<ILogRecordTypeRegistry>(_ => CreateRegistry());
        services.AddSingleton<NatsConnectionProvider>();
        services.AddSingleton<ILogBroker, NatsLogBroker>();
        services.AddSingleton<IEventLogWriter, EventLogWriter>();
        services.AddSingleton<IEventLogReader, EventLogReader>();
        services.AddSingleton<ILogConsumerFactory, LogConsumerFactory>();
        services.AddSingleton<StreamProvisioner>();
        services.AddSingleton<IEventLogSetup>(sp => sp.GetRequiredService<StreamProvisioner>());
        services.AddHealthChecks().AddCheck<PipelineLogHealthCheck>(HealthCheckName, HealthStatus.Unhealthy, ["ready"]);
        services.AddSingleton<RegistryOutboxPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<RegistryOutboxPublisher>());

        // Ingest (spec PIP §7–§9).
        services.AddSingleton(ingest ?? new IngestOptions());
        services.TryAddSingleton<IIngestDirectory, RegistryIngestDirectory>();
        services.AddSingleton<ObservationValidator>();
        services.AddSingleton<SideEffectQueue>();
        services.AddSingleton<ISideEffectQueue>(sp => sp.GetRequiredService<SideEffectQueue>());
        services.AddSingleton<PartitionRouter>();
        services.AddSingleton<IPartitionDispatcher>(sp => sp.GetRequiredService<PartitionRouter>());
        services.AddSingleton<IPipelineLifecycle>(sp => sp.GetRequiredService<PartitionRouter>());
        services.AddSingleton<IngestService>();
        services.AddSingleton<IObservationIngest>(sp => sp.GetRequiredService<IngestService>());
        return services;
    }

    /// <summary>A registry holding the M1 record kinds (spec PIP §5.3).</summary>
    internal static ILogRecordTypeRegistry CreateRegistry()
    {
        var json = WatchpointJson.Create(JsonProfile.Log, PipelineJsonContext.Default);
        var registry = new LogRecordTypeRegistry();
        registry.Register(RecordKinds.Property, 1, (JsonTypeInfo<PropertyRecordBody>)json.GetTypeInfo(typeof(PropertyRecordBody)));
        registry.Register(RecordKinds.Event, 1, (JsonTypeInfo<EventRecordBody>)json.GetTypeInfo(typeof(EventRecordBody)));
        registry.Register(RecordKinds.Health, 1, (JsonTypeInfo<HealthRecordBody>)json.GetTypeInfo(typeof(HealthRecordBody)));
        registry.Register(RecordKinds.RegistryChanged, 1, (JsonTypeInfo<RegistryChangedBody>)json.GetTypeInfo(typeof(RegistryChangedBody)));
        return registry;
    }
}
