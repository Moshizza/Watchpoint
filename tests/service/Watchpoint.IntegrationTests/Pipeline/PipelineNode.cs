using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchpoint.Hosting;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;

namespace Watchpoint.IntegrationTests.Pipeline;

/// <summary>The pipeline module on a real NATS server, isolated by a <see cref="NatsScope"/> (spec PIP §14).</summary>
internal sealed class PipelineNode : IAsyncDisposable
{
    private const long Mb = 1024 * 1024;

    private PipelineNode(ServiceProvider services, PipelineOptions options, LogCollector logs)
    {
        Services = services;
        Options = options;
        Logs = logs;
    }

    public ServiceProvider Services { get; }

    public PipelineOptions Options { get; }

    public LogCollector Logs { get; }

    public IEventLogWriter Writer => Services.GetRequiredService<IEventLogWriter>();

    public IEventLogReader Reader => Services.GetRequiredService<IEventLogReader>();

    public ILogConsumerFactory Consumers => Services.GetRequiredService<ILogConsumerFactory>();

    public ILogRecordTypeRegistry Registry => Services.GetRequiredService<ILogRecordTypeRegistry>();

    public string StreamName(LogStream stream) => Options.StreamPrefix + stream switch
    {
        LogStream.Log => "WP_LOG",
        LogStream.Cfg => "WP_CFG",
        _ => "WP_SYS",
    };

    /// <summary>
    /// Options for a test scope. Streams are small: JetStream reserves the maximum size of every stream, and many test
    /// scopes share one server.
    /// </summary>
    public static PipelineOptions OptionsFor(NatsScope scope, long logBytes = 64 * Mb) => new()
    {
        SubjectPrefix = scope.SubjectPrefix,
        StreamPrefix = scope.StreamPrefix,
        MaxBytes = logBytes,
        GlobalMaxBytes = 16 * Mb,
        MinStreamBytes = 8 * Mb,
    };

    /// <summary>Builds the module and runs its start-up tasks (stream provisioning), like the host does.</summary>
    public static async Task<PipelineNode> StartAsync(string natsUrl, PipelineOptions options, CancellationToken ct, Action<IServiceCollection>? configure = null)
    {
        var logs = new LogCollector();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(new NatsEndpoint(natsUrl, null));
        configure?.Invoke(services);
        services.AddPipeline(options);
        var provider = services.BuildServiceProvider();
        var node = new PipelineNode(provider, options, logs);
        try
        {
            foreach (var task in provider.GetServices<IStartupTask>().OrderBy(t => t.Phase).ThenBy(t => t.Order))
            {
                await task.ExecuteAsync(ct);
            }
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }

        return node;
    }

    /// <summary>A valid <c>health</c> record of a device; <paramref name="n"/> goes into the body to recognise the record.</summary>
    public LogEnvelope Envelope(DeviceId device, long n) => new(
        Guid.CreateVersion7(),
        RecordKinds.Health,
        TimeProvider.System.GetUtcNow(),
        Src: null,
        Partitioning.Of(device),
        device,
        Srv: null,
        new RecordOrigin(RecordOriginType.Connection, Guid.CreateVersion7()),
        RecordCorrelation.None,
        Registry.SerializeBody(RecordKinds.Health, new HealthRecordBody(Connectivity.Online, [], n.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    /// <summary>The number put into a record by <see cref="Envelope"/>.</summary>
    public long NumberOf(LogRecord record) =>
        long.Parse(record.BodyAs<HealthRecordBody>(Registry).Reason!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A device id whose partition is <paramref name="partition"/>.</summary>
    public static DeviceId DeviceIn(int partition)
    {
        while (true)
        {
            var id = DeviceId.New();
            if (Partitioning.Of(id) == partition)
            {
                return id;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Logs.Dispose();
    }
}
