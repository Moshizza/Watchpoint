using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Watchpoint.Integration.Modbus.Protocol;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly bool _rtuOverTcp;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;

    public int Port { get; }
    public object ServerLock { get; } = new();

    public ushort[] HoldingRegisters { get; } = new ushort[65536];
    public ushort[] InputRegisters { get; } = new ushort[65536];
    public bool[] Coils { get; } = new bool[65536];
    public bool[] DiscreteInputs { get; } = new bool[65536];

    public int RequestCount { get; private set; }
    public TimeSpan InjectedDelay { get; set; } = TimeSpan.Zero;
    public byte? InjectedExceptionCode { get; set; }

    public ModbusTestServer(bool rtuOverTcp = false, TimeProvider? timeProvider = null, int port = 0)
    {
        _rtuOverTcp = rtuOverTcp;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _listenTask = Task.Run(AcceptClientsAsync);
    }

    private async Task AcceptClientsAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var buffer = new byte[4096];
            var offset = 0;

            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }

                if (read == 0) break;
                offset += read;

                while (offset > 0)
                {
                    if (_rtuOverTcp)
                    {
                        if (offset < 4) break;
                        var rawFc = (byte)(buffer[1] & 0x7F);
                        if (!RtuOverTcpFrame.TryDecode(buffer.AsSpan(0, offset), buffer[0], rawFc, out var pdu, out var consumed, isResponse: false))
                        {
                            if (consumed > 0)
                            {
                                Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                                offset -= consumed;
                            }
                            else
                            {
                                break;
                            }
                            continue;
                        }

                        Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                        offset -= consumed;

                        var responsePdu = ProcessPdu(pdu);
                        if (InjectedDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(InjectedDelay, _timeProvider, ct).ConfigureAwait(false);
                        }

                        var responseBytes = RtuOverTcpFrame.Encode(buffer[0], responsePdu);
                        await stream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!MbapFrame.TryDecode(buffer.AsSpan(0, offset), out var tid, out var uid, out var pdu, out var consumed))
                        {
                            break;
                        }

                        Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                        offset -= consumed;

                        var responsePdu = ProcessPdu(pdu);
                        if (InjectedDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(InjectedDelay, _timeProvider, ct).ConfigureAwait(false);
                        }

                        var responseBytes = MbapFrame.Encode(tid, uid, responsePdu);
                        await stream.WriteAsync(responseBytes, ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private ModbusPdu ProcessPdu(ModbusPdu req)
    {
        lock (ServerLock)
        {
            RequestCount++;

            if (InjectedExceptionCode.HasValue && (req.FunctionCode == FunctionCodes.WriteSingleCoil || req.FunctionCode == FunctionCodes.WriteSingleRegister || req.FunctionCode == FunctionCodes.WriteMultipleCoils || req.FunctionCode == FunctionCodes.WriteMultipleRegisters))
            {
                var code = InjectedExceptionCode.Value;
                InjectedExceptionCode = null;
                return new ModbusPdu((byte)(req.FunctionCode | 0x80), new byte[] { code });
            }

            switch (req.FunctionCode)
            {
                case FunctionCodes.ReadCoils:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        var byteCount = (qty + 7) / 8;
                        var res = new byte[1 + byteCount];
                        res[0] = (byte)byteCount;
                        for (var i = 0; i < qty; i++)
                        {
                            if (Coils[addr + i])
                            {
                                res[1 + (i / 8)] |= (byte)(1 << (i % 8));
                            }
                        }
                        return new ModbusPdu(req.FunctionCode, res);
                    }

                case FunctionCodes.ReadDiscreteInputs:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        var byteCount = (qty + 7) / 8;
                        var res = new byte[1 + byteCount];
                        res[0] = (byte)byteCount;
                        for (var i = 0; i < qty; i++)
                        {
                            if (DiscreteInputs[addr + i])
                            {
                                res[1 + (i / 8)] |= (byte)(1 << (i % 8));
                            }
                        }
                        return new ModbusPdu(req.FunctionCode, res);
                    }

                case FunctionCodes.ReadHoldingRegisters:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        var res = new byte[1 + (qty * 2)];
                        res[0] = (byte)(qty * 2);
                        for (var i = 0; i < qty; i++)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(1 + (i * 2), 2), HoldingRegisters[addr + i]);
                        }
                        return new ModbusPdu(req.FunctionCode, res);
                    }

                case FunctionCodes.ReadInputRegisters:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        var res = new byte[1 + (qty * 2)];
                        res[0] = (byte)(qty * 2);
                        for (var i = 0; i < qty; i++)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(res.AsSpan(1 + (i * 2), 2), InputRegisters[addr + i]);
                        }
                        return new ModbusPdu(req.FunctionCode, res);
                    }

                case FunctionCodes.WriteSingleCoil:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var val = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        Coils[addr] = val == 0xFF00;
                        return req;
                    }

                case FunctionCodes.WriteSingleRegister:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var val = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        HoldingRegisters[addr] = val;
                        return req;
                    }

                case FunctionCodes.WriteMultipleCoils:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        for (var i = 0; i < qty; i++)
                        {
                            var b = req.Data.Span[5 + (i / 8)];
                            Coils[addr + i] = (b & (1 << (i % 8))) != 0;
                        }
                        var echo = new byte[4];
                        BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(0, 2), addr);
                        BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(2, 2), qty);
                        return new ModbusPdu(req.FunctionCode, echo);
                    }

                case FunctionCodes.WriteMultipleRegisters:
                    {
                        var addr = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(0, 2));
                        var qty = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(2, 2));
                        for (var i = 0; i < qty; i++)
                        {
                            var regVal = BinaryPrimitives.ReadUInt16BigEndian(req.Data.Span.Slice(5 + (i * 2), 2));
                            HoldingRegisters[addr + i] = regVal;
                        }
                        var echo = new byte[4];
                        BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(0, 2), addr);
                        BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(2, 2), qty);
                        return new ModbusPdu(req.FunctionCode, echo);
                    }

                default:
                    return new ModbusPdu((byte)(req.FunctionCode | 0x80), new byte[] { 0x01 });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch
        {
        }
        _cts.Dispose();
    }
}
