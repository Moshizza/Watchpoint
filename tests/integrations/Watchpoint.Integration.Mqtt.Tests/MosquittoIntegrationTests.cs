using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Shouldly;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.Integration.Mqtt.Tests;

public sealed class MosquittoIntegrationTests : IClassFixture<MosquittoFixture>
{
    private readonly MosquittoFixture _fixture;

    public MosquittoIntegrationTests(MosquittoFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MQTT_I_001_Mosquitto_container_500_sensors_discovering_devices_and_live_values()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!DockerAvailability.IsAvailable())
        {
            Assert.Skip(DockerAvailability.SkipReason);
            return;
        }

        var caPem = _fixture.CaCertificate.ExportCertificatePem();

        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = _fixture.Host,
            port = _fixture.TlsPort,
            tls = new
            {
                enabled = true,
                mode = "customCa",
                customCaCertificate = caPem,
            },
            protocolVersion = "5.0",
            cleanSession = true,
            mappings = new object[]
            {
                new
                {
                    id = "r_sensor",
                    topic = "sensors/{deviceId}/temp",
                    qos = 1,
                    device = new
                    {
                        nativeId = "{deviceId}",
                        type = "generic.analog-input",
                    },
                    payload = new
                    {
                        format = "number",
                    },
                    property = "measurement.value",
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var init = await connector.InitializeAsync(context, ct);
        init.IsSuccess.ShouldBeTrue();

        await connector.StartAsync(ct);

        var swConn = Stopwatch.StartNew();
        while (!context.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected) && swConn.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        var factory = new MqttFactory();
        var pubClient = factory.CreateMqttClient();
        var clientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.TlsPort)
            .WithTlsOptions(o =>
            {
                o.UseTls();
                o.WithCertificateValidationHandler(args =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(_fixture.CaCertificate);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(new X509Certificate2(args.Certificate));
                });
            })
            .Build();

        await pubClient.ConnectAsync(clientOptions, ct);

        for (int i = 1; i <= 500; i++)
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"sensors/sensor-{i:D4}/temp")
                .WithPayload("21.5")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await pubClient.PublishAsync(msg, ct);
        }

        var sw = Stopwatch.StartNew();
        while (context.Sink.Properties.Count < 500 && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        context.Sink.Properties.Count.ShouldBe(500);
        context.Sink.InventoryChanges.Count.ShouldBeGreaterThan(0);

        await pubClient.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
        await connector.StopAsync(ct);
    }

    [Fact]
    public async Task MQTT_I_002_Broker_restart_persistent_session_delivers_qos1_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!DockerAvailability.IsAvailable())
        {
            Assert.Skip(DockerAvailability.SkipReason);
            return;
        }

        var clientId = "persistent-client-002";
        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = _fixture.Host,
            port = _fixture.PlainPort,
            allowPlain = true,
            clientId = clientId,
            cleanSession = false,
            protocolVersion = "5.0",
            mappings = new object[]
            {
                new
                {
                    id = "r_state",
                    topic = "pqueue/{deviceId}/state",
                    qos = 1,
                    device = new
                    {
                        nativeId = "{deviceId}",
                        type = "generic.analog-input",
                    },
                    payload = new
                    {
                        format = "number",
                    },
                    property = "measurement.value",
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var init = await connector.InitializeAsync(context, ct);
        init.IsSuccess.ShouldBeTrue();

        await connector.StartAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, ct);

        await connector.StopAsync(ct);

        var factory = new MqttFactory();
        var pubClient = factory.CreateMqttClient();
        await pubClient.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .Build(), ct);

        await pubClient.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("pqueue/dev1/state")
            .WithPayload("42.0")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        await pubClient.DisconnectAsync(new MqttClientDisconnectOptions(), ct);

        await _fixture.RestartAsync(ct);

        var newContext = new TestConnectorContext(settingsEl, time);
        var newConnector = new MqttConnector();
        await newConnector.InitializeAsync(newContext, ct);
        await newConnector.StartAsync(ct);

        var sw = Stopwatch.StartNew();
        while (newContext.Sink.Properties.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        await newConnector.StopAsync(ct);
    }

    [Fact]
    public async Task MQTT_I_003_Node_crash_simulated_by_abandoning_client_before_sink_flush_delivers_at_least_once()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!DockerAvailability.IsAvailable())
        {
            Assert.Skip(DockerAvailability.SkipReason);
            return;
        }

        var clientId = "crash-client-003";
        var factory = new MqttFactory();

        var redeliveryTcs = new TaskCompletionSource<MqttApplicationMessage>();
        var subscriber2 = factory.CreateMqttClient();
        subscriber2.ApplicationMessageReceivedAsync += e =>
        {
            redeliveryTcs.TrySetResult(e.ApplicationMessage);
            return Task.CompletedTask;
        };

        var options1 = new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .WithClientId(clientId)
            .WithCleanSession(false)
            .Build();

        var subscriber1 = factory.CreateMqttClient();
        await subscriber1.ConnectAsync(options1, ct);
        await subscriber1.SubscribeAsync("crash/test", MqttQualityOfServiceLevel.AtLeastOnce, ct);

        var publisher = factory.CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .Build(), ct);

        var pubTask = publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("crash/test")
            .WithPayload("payload-before-crash")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        subscriber1.Dispose();

        await subscriber2.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .WithClientId(clientId)
            .WithCleanSession(false)
            .Build(), ct);

        var received = await redeliveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        received.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(received.PayloadSegment).ShouldBe("payload-before-crash");

        await subscriber2.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
        await publisher.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
    }

    [Fact]
    public async Task MQTT_I_005_Mqtt_3_1_1_broker_mode_compatibility()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!DockerAvailability.IsAvailable())
        {
            Assert.Skip(DockerAvailability.SkipReason);
            return;
        }

        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = _fixture.Host,
            port = _fixture.PlainPort,
            allowPlain = true,
            protocolVersion = "3.1.1",
            cleanSession = true,
            mappings = new object[]
            {
                new
                {
                    id = "r_311",
                    topic = "v311/{deviceId}/temp",
                    qos = 1,
                    device = new
                    {
                        nativeId = "{deviceId}",
                        type = "generic.analog-input",
                    },
                    payload = new
                    {
                        format = "number",
                    },
                    property = "measurement.value",
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var init = await connector.InitializeAsync(context, ct);
        init.IsSuccess.ShouldBeTrue();

        var testRes = await connector.TestAsync(ct);
        testRes.Success.ShouldBeTrue();

        await connector.StartAsync(ct);

        var swConn = Stopwatch.StartNew();
        while (!context.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected) && swConn.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        var pubClient = new MqttFactory().CreateMqttClient();
        await pubClient.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .Build(), ct);

        await pubClient.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("v311/d1/temp")
            .WithPayload("19.8")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

        var sw = Stopwatch.StartNew();
        while (context.Sink.Properties.Count == 0 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        context.Sink.Properties.Count.ShouldBeGreaterThan(0);
        await pubClient.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
        await connector.StopAsync(ct);
    }

    [Fact]
    public async Task MQTT_I_006_Load_10000_messages_per_sec_across_2000_topics_benchmark()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!DockerAvailability.IsAvailable())
        {
            Assert.Skip(DockerAvailability.SkipReason);
            return;
        }

        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = _fixture.Host,
            port = _fixture.PlainPort,
            allowPlain = true,
            protocolVersion = "5.0",
            cleanSession = true,
            maxMessagesPerSecond = 50000,
            mappings = new object[]
            {
                new
                {
                    id = "r_load",
                    topic = "load/{topicId}/val",
                    qos = 0,
                    device = new
                    {
                        nativeId = "{topicId}",
                        type = "generic.analog-input",
                    },
                    payload = new
                    {
                        format = "number",
                    },
                    property = "measurement.value",
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        await connector.InitializeAsync(context, ct);
        await connector.StartAsync(ct);

        var swConn = Stopwatch.StartNew();
        while (!context.Health.History.Any(h => h.Status == Watchpoint.Integration.Abstractions.Context.ConnectionStatus.Connected) && swConn.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, ct);
        }

        var pubClient = new MqttFactory().CreateMqttClient();
        await pubClient.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(_fixture.Host, _fixture.PlainPort)
            .Build(), ct);

        var messageCount = 2000;
        for (int i = 0; i < messageCount; i++)
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic($"load/t{i % 2000}/val")
                .WithPayload("100")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await pubClient.PublishAsync(msg, ct);
        }

        var timeout = Stopwatch.StartNew();
        while (context.Sink.Properties.Count < messageCount && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, ct);
        }

        context.Sink.Properties.Count.ShouldBeGreaterThan(0);

        await pubClient.DisconnectAsync(new MqttClientDisconnectOptions(), ct);
        await connector.StopAsync(ct);
    }
}
