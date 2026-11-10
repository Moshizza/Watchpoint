using System.Buffers.Binary;
using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Modbus.Engine;
using Watchpoint.Integration.Modbus.Mapping;
using Watchpoint.Integration.Modbus.Protocol;
using Watchpoint.Integration.Modbus.Transport;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusCommandsAndPipeliningUnitTests
{
    private sealed class FakeTransport : ITransport
    {
        public List<ModbusPdu> Sent { get; } = new();
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<ModbusPdu> SendAsync(byte unitId, ModbusPdu request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(request);
        }
    }

    [Fact]
    public async Task MODB_U_024_Command_PulseExecution()
    {
        var transport = new FakeTransport();
        var time = TestTime.CreateProvider();

        var cmdDef = new CommandDefinition(
            Write: [new WriteActionDefinition("coil", 0, true)],
            Pulse: new PulseDefinition(100, [new WriteActionDefinition("coil", 0, false)]),
            Verify: null);

        var target = new DeviceHandle(new DeviceKey("u1", "d1"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var cmd = new ConnectorCommand(Guid.NewGuid(), target, "open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddMinutes(1), 1, CommandPriority.Normal);

        var task = CommandMapper.ExecuteCommandAsync(transport, 1, cmdDef, cmd, (t, a, q) => Task.FromResult(ReadOnlyMemory<byte>.Empty), time, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(150));
        await task;

        transport.Sent.Count.ShouldBe(2);
        transport.Sent[0].FunctionCode.ShouldBe(FunctionCodes.WriteSingleCoil);
        BinaryPrimitives.ReadUInt16BigEndian(transport.Sent[0].Data.Span.Slice(2, 2)).ShouldBe((ushort)0xFF00);
        transport.Sent[1].FunctionCode.ShouldBe(FunctionCodes.WriteSingleCoil);
        BinaryPrimitives.ReadUInt16BigEndian(transport.Sent[1].Data.Span.Slice(2, 2)).ShouldBe((ushort)0x0000);
    }

    [Fact]
    public async Task MODB_U_025_Command_VerificationFailure()
    {
        var transport = new FakeTransport();
        var time = TestTime.CreateProvider();

        var cmdDef = new CommandDefinition(
            Write: [new WriteActionDefinition("coil", 0, true)],
            Pulse: null,
            Verify: new VerifyDefinition("coil", 0, true, 200));

        var target = new DeviceHandle(new DeviceKey("u1", "d1"), "ac.door", "1.0", new Dictionary<string, WpValue>(), []);
        var cmd = new ConnectorCommand(Guid.NewGuid(), target, "open", new Dictionary<string, WpValue>(), time.GetUtcNow().AddMinutes(1), 1, CommandPriority.Normal);

        var task = CommandMapper.ExecuteCommandAsync(transport, 1, cmdDef, cmd, (t, a, q) => Task.FromResult(new ReadOnlyMemory<byte>(new byte[] { 0x00 })), time, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(300));

        await Should.ThrowAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task MODB_U_026_ModbusTcpClient_Pipelining()
    {
        await using var server = new ModbusTestServer();
        var client = new ModbusTcpClient("127.0.0.1", server.Port, maxInFlight: 4);
        await client.ConnectAsync(CancellationToken.None);

        var pdu = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
        var tasks = Enumerable.Range(0, 10).Select(_ => client.SendAsync(1, pdu, CancellationToken.None)).ToArray();

        var responses = await Task.WhenAll(tasks);
        responses.Length.ShouldBe(10);
        foreach (var r in responses)
        {
            r.FunctionCode.ShouldBe(FunctionCodes.ReadHoldingRegisters);
        }

        await client.DisposeAsync();
    }

    [Fact]
    public async Task MODB_U_027_ModbusTcpClient_Timeout()
    {
        await using var server = new ModbusTestServer();
        server.InjectedDelay = TimeSpan.FromSeconds(2);

        var time = TestTime.CreateProvider();
        var client = new ModbusTcpClient("127.0.0.1", server.Port, requestTimeout: TimeSpan.FromMilliseconds(50), timeProvider: time);
        await client.ConnectAsync(CancellationToken.None);

        var pdu = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
        var sendTask = client.SendAsync(1, pdu, CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<TimeoutException>(() => sendTask);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task MODB_U_028_ModbusTcpClient_DisconnectFailsPending()
    {
        await using var server = new ModbusTestServer();
        server.InjectedDelay = TimeSpan.FromSeconds(5);

        var client = new ModbusTcpClient("127.0.0.1", server.Port, requestTimeout: TimeSpan.FromSeconds(10));
        await client.ConnectAsync(CancellationToken.None);

        var pdu = ModbusPdu.BuildReadRequest(FunctionCodes.ReadHoldingRegisters, 0, 1);
        var sendTask = client.SendAsync(1, pdu, CancellationToken.None);

        await client.CloseAsync(CancellationToken.None);

        await Should.ThrowAsync<IOException>(() => sendTask);
        await client.DisposeAsync();
    }
}
