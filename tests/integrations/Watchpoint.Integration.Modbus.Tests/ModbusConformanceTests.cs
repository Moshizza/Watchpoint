using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Testing;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusConformanceTests : ConnectorConformanceTests<ModbusConnector>, IAsyncDisposable
{
    private ModbusVendorSimulator? _simulator;

    protected override IVendorSimulator CreateSimulator()
    {
        _simulator = new ModbusVendorSimulator();
        return _simulator;
    }

    protected override JsonElement CreateSettings(IVendorSimulator sim)
    {
        var modbusSim = (ModbusVendorSimulator)sim;
        var json = $$"""
        {
          "host": "127.0.0.1",
          "port": {{modbusSim.Port}},
          "units": [
            {
              "unitId": 1,
              "template": "{{modbusSim.TemplatePath.Replace("\\", "/")}}"
            }
          ]
        }
        """;
        return JsonDocument.Parse(json).RootElement;
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
        SupportsCommands = true,
        SupportsBatchCommands = false,
        SupportsPaging = false,
        SupportsPerDevicePolling = false
    };

    protected override JsonElement CreateInvalidSettings(IVendorSimulator sim)
    {
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["host"] = "127.0.0.1",
            ["port"] = -1,
        });
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
        for (var i = 0; i < 50 && !context.Health.History.Any(h => h.Status == ConnectionStatus.Connected); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        context.Health.History.Any(h => h.Status == ConnectionStatus.Connected).ShouldBeTrue();
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

        for (var i = 0; i < 50 && (context1.Health.Status != ConnectionStatus.Connected || context2.Health.Status != ConnectionStatus.Connected); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        context1.Health.Status.ShouldBe(ConnectionStatus.Connected);
        context2.Health.Status.ShouldBe(ConnectionStatus.Connected);

        await connector1.StopAsync(CancellationToken.None);
        await connector2.StopAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_simulator is not null)
        {
            await _simulator.DisposeAsync();
        }
    }

    private sealed class ModbusVendorSimulator : IVendorSimulator
    {
        private ModbusTestServer? _server;
        private readonly string? _tempTemplatePath;
        private long _requestCount;

        private int _port;
        public int Port => _port;
        public string TemplatePath => _tempTemplatePath ?? "";

        public ModbusVendorSimulator()
        {
            var tpl = """
            {
              "id": "conf-template",
              "name": { "en": "Conformance Template" },
              "devices": [
                {
                  "key": "door-0001",
                  "type": "ac.door",
                  "points": [
                    { "property": "doorPosition.position", "table": "holding", "address": 0, "dataType": "uint16", "enumMap": { "0": "closed", "1": "open" } },
                    { "property": "state", "table": "coil", "address": 0, "dataType": "bit" }
                  ],
                  "commands": {
                    "openable.open": {
                      "write": [{ "table": "coil", "address": 0, "value": true }]
                    }
                  }
                }
              ]
            }
            """;

            _tempTemplatePath = Path.Combine(Path.GetTempPath(), $"modbus-conf-{Guid.NewGuid():N}.json");
            File.WriteAllText(_tempTemplatePath, tpl);
            _server = new ModbusTestServer();
            _port = _server.Port;
            _server.HoldingRegisters[0] = 0;
            _server.DiscreteInputs[0] = true;
            _server.Coils[0] = false;
        }

        public ValueTask StartAsync(CancellationToken ct = default)
        {
            if (_server is null)
            {
                _server = new ModbusTestServer();
                _port = _server.Port;
                _server.HoldingRegisters[0] = 0;
                _server.DiscreteInputs[0] = true;
                _server.Coils[0] = false;
            }
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(CancellationToken ct = default)
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
                _server = null;
            }
        }

        public async ValueTask DropConnectionAsync(CancellationToken ct = default)
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
                _server = null;
            }
        }

        public ValueTask RestoreConnectionAsync(CancellationToken ct = default)
        {
            if (_server is null)
            {
                _server = new ModbusTestServer();
                _port = _server.Port;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask InjectStateChangeAsync(string nativeId, string propertyPath, object? value, CancellationToken ct = default)
        {
            _requestCount++;
            if (_server is not null)
            {
                lock (_server.ServerLock)
                {
                    if (propertyPath == "doorPosition.position")
                    {
                        _server.HoldingRegisters[0] = value?.ToString() == "open" ? (ushort)1 : (ushort)0;
                    }
                    else if (propertyPath == "state")
                    {
                        _server.Coils[0] = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    }
                }
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask InjectEventAsync(string nativeId, string eventPath, IReadOnlyDictionary<string, object?>? payload = null, CancellationToken ct = default)
        {
            _requestCount++;
            return ValueTask.CompletedTask;
        }

        public void FailNextCommand(string? failureCategory = null, string? messageKey = null, IReadOnlyDictionary<string, object?>? messageArgs = null)
        {
            if (_server is not null)
            {
                lock (_server.ServerLock)
                {
                    _server.InjectedExceptionCode = 0x02;
                }
            }
        }

        public void DelayNextCommand(TimeSpan delay)
        {
            if (_server is not null)
            {
                lock (_server.ServerLock)
                {
                    _server.InjectedDelay = delay;
                }
            }
        }

        public async ValueTask RenameDeviceAsync(string nativeId, string newDisplayName, CancellationToken ct = default)
        {
            if (_tempTemplatePath is not null)
            {
                var text = await File.ReadAllTextAsync(_tempTemplatePath, ct);
                var doc = JsonNode.Parse(text)!.AsObject();
                foreach (var dev in doc["devices"]!.AsArray())
                {
                    if (dev!["key"]!.GetValue<string>() == nativeId)
                    {
                        dev["name"] = new JsonObject { ["en"] = newDisplayName };
                    }
                }
                await File.WriteAllTextAsync(_tempTemplatePath, doc.ToJsonString(), ct);
            }
        }

        public long GetRequestCount() => _server?.RequestCount ?? _requestCount;

        public void ResetRequestCount()
        {
            _requestCount = 0;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            if (_tempTemplatePath is not null && File.Exists(_tempTemplatePath))
            {
                File.Delete(_tempTemplatePath);
            }
        }
    }
}
