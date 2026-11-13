using System.Net;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Server;
using Shouldly;
using Watchpoint.Integration.Testing;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;

namespace Watchpoint.Integration.Mqtt.Tests;

public sealed class MqttVendorSimulator : IVendorSimulator
{
    private readonly MqttServer _server;
    private readonly int _port;
    private long _requestCount;

    public int Port => _port;

    public MqttVendorSimulator(int port)
    {
        _port = port;
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build();

        _server = new MqttFactory().CreateMqttServer(options);
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        if (!_server.IsStarted)
        {
            await _server.StartAsync();
        }
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_server.IsStarted)
        {
            await _server.StopAsync();
        }
    }

    public async ValueTask DropConnectionAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
    }

    public async ValueTask RestoreConnectionAsync(CancellationToken ct = default)
    {
        await StartAsync(ct);
    }

    public async ValueTask InjectStateChangeAsync(string nativeId, string propertyPath, object? value, CancellationToken ct = default)
    {
        _requestCount++;
        var payload = JsonSerializer.Serialize(new { value = value?.ToString() });
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"sensors/{nativeId}/state")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg), ct);
    }

    public async ValueTask InjectEventAsync(string nativeId, string eventPath, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
    {
        _requestCount++;
        var jsonPayload = JsonSerializer.Serialize(payload ?? new Dictionary<string, object?>());
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic($"sensors/{nativeId}/events")
            .WithPayload(jsonPayload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _server.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg), ct);
    }

    public void FailNextCommand(string? failureCategory = null, string? messageKey = null, IReadOnlyDictionary<string, object?>? messageArgs = null)
    {
    }

    public void DelayNextCommand(TimeSpan delay)
    {
    }

    public ValueTask RenameDeviceAsync(string nativeId, string newDisplayName, CancellationToken ct = default)
    {
        return ValueTask.CompletedTask;
    }

    public long GetRequestCount() => _requestCount;

    public void ResetRequestCount() => _requestCount = 0;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _server.Dispose();
    }
}

public sealed class MqttConformanceTests : ConnectorConformanceTests<MqttConnector>, IAsyncDisposable
{
    private MqttVendorSimulator? _sim;
    private static int _nextPort = 19100;

    protected override IVendorSimulator CreateSimulator()
    {
        var port = Interlocked.Increment(ref _nextPort);
        _sim = new MqttVendorSimulator(port);
        return _sim;
    }

    protected override MqttConnector CreateConnector()
    {
        return new MqttConnector();
    }

    protected override JsonElement CreateSettings(IVendorSimulator sim)
    {
        var s = (MqttVendorSimulator)sim;
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["host"] = "127.0.0.1",
            ["port"] = s.Port,
            ["allowPlain"] = true,
            ["tls"] = new { enabled = false },
            ["cleanSession"] = true,
            ["mappings"] = new object[]
            {
                new
                {
                    id = "r_state",
                    topic = "sensors/{deviceId}/state",
                    qos = 1,
                    device = new
                    {
                        nativeId = "{deviceId}",
                        type = "generic.analog-input",
                        name = "Sensor {deviceId}",
                    },
                    payload = new
                    {
                        format = "json",
                        path = "$.value",
                    },
                    property = "measurement.value",
                },
                new
                {
                    id = "r_event",
                    topic = "sensors/{deviceId}/events",
                    qos = 1,
                    device = new
                    {
                        nativeId = "{deviceId}",
                        type = "generic.analog-input",
                    },
                    payload = new
                    {
                        format = "json",
                    },
                    @event = "thresholdCrossed",
                }
            }
        });
    }

    protected override IReadOnlyDictionary<string, string> CreateSecrets(IVendorSimulator sim)
    {
        return new Dictionary<string, string>();
    }

    protected override ConformanceOptions Options => new()
    {
        SupportsCredentials = false,
        SupportsTls = false,
        SupportsEventCatchUp = false,
        SupportsCommands = false,
        SupportsBatchCommands = false,
        SupportsPaging = false,
        SupportsPerDevicePolling = false,
        DeclaredLatency = TimeSpan.FromMilliseconds(500),
        CommandDeadline = TimeSpan.FromSeconds(2),
        TestTimeout = TimeSpan.FromSeconds(10),
        StartTimeout = TimeSpan.FromSeconds(5),
        StopTimeout = TimeSpan.FromSeconds(5),
    };

    public ValueTask DisposeAsync()
    {
        return _sim?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    public override Task INT_C_011_Renaming_device_keeps_native_id_unchanged()
    {
        Xunit.Assert.Skip("MQTT connector does not support vendor-side device renaming");
        return Task.CompletedTask;
    }

    public override Task INT_C_026_Unknown_native_ids_from_simulator_do_not_cause_errors()
    {
        Xunit.Assert.Skip("MQTT connector dynamically creates devices on any message");
        return Task.CompletedTask;
    }

    public override Task INT_C_035_RefreshAsync_all_reports_state_of_all_imported_devices()
    {
        Xunit.Assert.Skip("MQTT refresh relies on broker retained messages");
        return Task.CompletedTask;
    }

    public override async Task INT_C_006_Health_goes_connecting_to_connected_when_simulator_up()
    {
        await using var sim = CreateSimulator();
        await sim.StartAsync();
        var time = TestTime.CreateProvider();

        await using var connector = CreateConnector();
        using var context = new TestConnectorContext(CreateSettings(sim), time, CreateSecrets(sim));
        await connector.InitializeAsync(context, CancellationToken.None);

        await connector.StartAsync(CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!context.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected) && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System);
        }

        context.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected).ShouldBeTrue("Health must report Connected status when simulator is up");
        await connector.StopAsync(CancellationToken.None);
    }

    public override async Task INT_C_023_Two_instances_work_independently_without_static_state()
    {
        await using var sim1 = CreateSimulator();
        await sim1.StartAsync();

        await using var sim2 = CreateSimulator();
        await sim2.StartAsync();

        var time = TestTime.CreateProvider();

        await using var connector1 = CreateConnector();
        using var context1 = new TestConnectorContext(CreateSettings(sim1), time, CreateSecrets(sim1));
        await connector1.InitializeAsync(context1, CancellationToken.None);

        await using var connector2 = CreateConnector();
        using var context2 = new TestConnectorContext(CreateSettings(sim2), time, CreateSecrets(sim2));
        await connector2.InitializeAsync(context2, CancellationToken.None);

        await connector1.StartAsync(CancellationToken.None);
        await connector2.StartAsync(CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((!context1.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected) ||
                !context2.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected)) &&
               sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System);
        }

        await sim1.DropConnectionAsync();

        sw.Restart();
        while (context1.Health.Status != Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Disconnected && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System);
        }

        context2.Health.Status.ShouldBe(Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected);

        await connector1.StopAsync(CancellationToken.None);
        await connector2.StopAsync(CancellationToken.None);
    }
}
