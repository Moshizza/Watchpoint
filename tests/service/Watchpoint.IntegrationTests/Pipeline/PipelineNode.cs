using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

        // The test server has no TLS, and its address is not always a loopback one (Docker in Docker).
        AllowInsecureRemote = true,
    };

    /// <summary>Builds the module and provisions the streams, like the host does at start-up.</summary>
    public static async Task<PipelineNode> StartAsync(
        string natsUrl, PipelineOptions options, CancellationToken ct, Action<IServiceCollection>? configure = null, IngestOptions? ingest = null)
    {
        var logs = new LogCollector();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(new EventLogEndpoint(natsUrl, null));
        configure?.Invoke(services);
        services.AddPipeline(options, ingest);
        var provider = services.BuildServiceProvider();
        var node = new PipelineNode(provider, options, logs);
        try
        {
            await provider.GetRequiredService<IEventLogSetup>().EnsureStreamsAsync(ct);
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
