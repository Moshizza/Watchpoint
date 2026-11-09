using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Comprehensive fake test implementation of <see cref="IConnectorContext"/> for conformance testing.
/// </summary>
public sealed class TestConnectorContext : IConnectorContext, IDisposable
{
    /// <inheritdoc />
    public ConnectionInfo Connection { get; set; } = new(Guid.NewGuid(), "ConformanceTest", "watchpoint.integration.test", "1.0.0", "test.server", "en-US");

    /// <inheritdoc />
    public JsonElement Settings { get; set; }

    /// <inheritdoc />
    public ISecretAccessor Secrets { get; set; }

    /// <summary>Test observation sink.</summary>
    public TestObservationSink Sink { get; }

    /// <summary>Test device map.</summary>
    public TestDeviceMap Devices { get; }

    /// <summary>Test connection store.</summary>
    public TestConnectionStore Store { get; }

    /// <summary>Test scheduler.</summary>
    public TestConnectorScheduler Scheduler { get; }

    /// <summary>Test request gate.</summary>
    public TestRequestGate Gate { get; set; }

    /// <summary>Test connection health tracker.</summary>
    public TestConnectionHealth Health { get; }

    /// <inheritdoc />
    public ISecureHttp Http { get; set; }

    /// <inheritdoc />
    public IInboundEndpoints Inbound { get; set; }

    /// <summary>Test metrics sink.</summary>
    public TestConnectorMetrics Metrics { get; }

    /// <inheritdoc />
    public ILoggerFactory Logging { get; set; }

    /// <inheritdoc />
    public TimeProvider Time => FakeTime;

    /// <summary>Fake time provider driving time in tests.</summary>
    public FakeTimeProvider FakeTime { get; }

    /// <inheritdoc />
    public CancellationToken Stopping => StoppingCts.Token;

    /// <summary>Cancellation token source for Stopping.</summary>
    public CancellationTokenSource StoppingCts { get; } = new();

    IObservationSink IConnectorContext.Sink => Sink;
    IDeviceMap IConnectorContext.Devices => Devices;
    IConnectionStore IConnectorContext.Store => Store;
    IConnectorScheduler IConnectorContext.Scheduler => Scheduler;
    IRequestGate IConnectorContext.Gate => Gate;
    IConnectionHealth IConnectorContext.Health => Health;
    IConnectorMetrics IConnectorContext.Metrics => Metrics;

    /// <summary>Initializes a new instance of <see cref="TestConnectorContext"/>.</summary>
    public TestConnectorContext(JsonElement settings, FakeTimeProvider time, IReadOnlyDictionary<string, string>? secrets = null, ILoggerFactory? loggerFactory = null)
    {
        Settings = settings;
        FakeTime = time;
        Sink = new TestObservationSink();
        Devices = new TestDeviceMap();
        Store = new TestConnectionStore(Sink);
        Scheduler = new TestConnectorScheduler(time);
        Secrets = new TestSecretAccessor(secrets);
        Gate = new TestRequestGate();
        Health = new TestConnectionHealth();
        Http = new TestSecureHttp();
        Inbound = new TestInboundEndpoints();
        Metrics = new TestConnectorMetrics();
        Logging = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public T GetSettings<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Settings, typeInfo)!;

    /// <inheritdoc />
    public void Dispose()
    {
        Gate.Dispose();
        StoppingCts.Dispose();
    }
}
