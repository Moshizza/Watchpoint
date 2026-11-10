using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Modbus.Protocol;
using Watchpoint.Integration.Modbus.Transport;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusIntegrationTests
{
    [Fact]
    public async Task MODB_I_001_RealLoopback_ReadCoilsAndRegisters()
    {
        await using var server = new ModbusTestServer();
        server.Coils[0] = true;
        server.Coils[1] = false;
        server.HoldingRegisters[0] = 1234;

        var client = new ModbusTcpClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var coilsPdu = ModbusPdu.BuildReadRequest(FunctionCodes.ReadCoils, 0, 2);
        var coilsResp = await client.SendAsync(1, coilsPdu, CancellationToken.None);
        coilsResp.Data.Span[1].ShouldBe((byte)0x01);

        var regsPdu = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
        var regsResp = await client.SendAsync(1, regsPdu, CancellationToken.None);
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(regsResp.Data.Span.Slice(1, 2)).ShouldBe((ushort)1234);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MODB_I_003_RealLoopback_WriteSingleAndMultipleRegisters()
    {
        await using var server = new ModbusTestServer();
        var client = new ModbusTcpClient("127.0.0.1", server.Port);
        await client.ConnectAsync(CancellationToken.None);

        var writeSingle = ModbusPdu.BuildWriteSingleRegister(5, 555);
        await client.SendAsync(1, writeSingle, CancellationToken.None);
        server.HoldingRegisters[5].ShouldBe((ushort)555);

        var writeMulti = ModbusPdu.BuildWriteMultipleRegisters(10, new byte[] { 0x01, 0x00, 0x02, 0x00 });
        await client.SendAsync(1, writeMulti, CancellationToken.None);
        server.HoldingRegisters[10].ShouldBe((ushort)0x0100);
        server.HoldingRegisters[11].ShouldBe((ushort)0x0200);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MODB_I_004_RealLoopback_RtuOverTcp()
    {
        await using var server = new ModbusTestServer(rtuOverTcp: true);
        server.HoldingRegisters[0] = 777;

        var client = new ModbusTcpClient("127.0.0.1", server.Port, rtuOverTcp: true);
        await client.ConnectAsync(CancellationToken.None);

        var req = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
        var resp = await client.SendAsync(1, req, CancellationToken.None);
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(resp.Data.Span.Slice(1, 2)).ShouldBe((ushort)777);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MODB_I_005_Pymodbus_ContainerInteroperability()
    {
        const string image = "python:3.12-alpine@sha256:e064671ca452259bab50e8e2c7bd896e39e9a343bb06ca25234adf6fe18d0fc7";

        var container = new ContainerBuilder(image)
            .WithPortBinding(5020, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5020))
            .WithCommand("sh", "-c", "pip install --no-cache-dir pymodbus==3.8.3 && python -c 'from pymodbus.server import StartAsyncTcpServer; from pymodbus.datastore import ModbusSequentialDataBlock, ModbusServerContext, ModbusSlaveContext; import asyncio; block = ModbusSequentialDataBlock(0, [100]*10); context = ModbusServerContext(slaves=ModbusSlaveContext(di=block, co=block, hr=block, ir=block), single=True); asyncio.run(StartAsyncTcpServer(context=context, address=(\"0.0.0.0\", 5020)))'")
            .Build();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await container.StartAsync(cts.Token);

            var mappedPort = container.GetMappedPublicPort(5020);
            var client = new ModbusTcpClient("127.0.0.1", mappedPort);
            await client.ConnectAsync(cts.Token);

            var req = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
            var resp = await client.SendAsync(1, req, cts.Token);
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(resp.Data.Span.Slice(1, 2)).ShouldBe((ushort)100);

            await client.DisposeAsync();
        }
        catch (DockerUnavailableException ex)
        {
            Assert.Skip($"Docker environment unavailable for pymodbus test: {ex.Message}");
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task MODB_I_006_RealLoopback_ReconnectionAfterDrop()
    {
        var tempTemplatePath = Path.Combine(Path.GetTempPath(), $"modbus-reconn-{Guid.NewGuid():N}.json");
        var templateContent = """
        {
          "id": "tpl-reconn",
          "name": { "en": "Reconn Template" },
          "devices": [
            {
              "key": "door-1",
              "type": "ac.door",
              "points": [
                { "property": "state", "table": "coil", "address": 0, "dataType": "bit" }
              ]
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(tempTemplatePath, templateContent, TestContext.Current.CancellationToken);

        try
        {
            var server = new ModbusTestServer();
            server.Coils[0] = true;

            var settingsJson = $$"""
            {
              "host": "127.0.0.1",
              "port": {{server.Port}},
              "units": [
                {
                  "unitId": 1,
                  "template": "{{tempTemplatePath.Replace("\\", "/")}}"
                }
              ]
            }
            """;
            var settings = JsonDocument.Parse(settingsJson).RootElement;
            var time = TestTime.CreateProvider();
            var connector = new ModbusConnector();

            using var context = new TestConnectorContext(settings, time, new Dictionary<string, string>());
            await connector.InitializeAsync(context, CancellationToken.None);
            await connector.StartAsync(CancellationToken.None);

            for (var i = 0; i < 50 && context.Health.Status != ConnectionStatus.Connected; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(50));
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
            }
            context.Health.Status.ShouldBe(ConnectionStatus.Connected);

            var port = server.Port;
            await server.DisposeAsync();

            time.Advance(TimeSpan.FromMilliseconds(600));
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, TestContext.Current.CancellationToken);

            var newServer = new ModbusTestServer(port: port);
            newServer.Coils[0] = true;

            for (var i = 0; i < 50 && context.Health.Status != ConnectionStatus.Connected; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
            }

            context.Health.Status.ShouldBe(ConnectionStatus.Connected);

            await connector.StopAsync(CancellationToken.None);
            await newServer.DisposeAsync();
        }
        finally
        {
            if (File.Exists(tempTemplatePath)) File.Delete(tempTemplatePath);
        }
    }
}
