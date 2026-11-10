using System.Net.Sockets;
using Watchpoint.Integration.Modbus.Protocol;

namespace Watchpoint.Integration.Modbus.Transport;

/// <summary>Client transport connecting via TCP supporting MBAP or RTU-over-TCP framing.</summary>
public sealed class ModbusTcpClient : ITransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _rtuOverTcp;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _inFlightGate;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    private readonly object _pendingLock = new();
    private readonly Dictionary<ushort, TaskCompletionSource<ModbusPdu>> _pending = new();
    private ushort _nextTransactionId;

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ModbusTcpClient"/> class.</summary>
    public ModbusTcpClient(
        string host,
        int port,
        bool rtuOverTcp = false,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        int maxInFlight = 1,
        TimeProvider? timeProvider = null)
    {
        _host = host;
        _port = port;
        _rtuOverTcp = rtuOverTcp;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(1);
        var effectiveMaxInFlight = _rtuOverTcp ? 1 : Math.Max(1, maxInFlight);
        _inFlightGate = new SemaphoreSlim(effectiveMaxInFlight, effectiveMaxInFlight);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsConnected => _tcpClient?.Connected == true && _stream is not null;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected) return;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;

            await CloseInternalAsync().ConfigureAwait(false);

            _tcpClient = new TcpClient();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(_connectTimeout);

            try
            {
                await _tcpClient.ConnectAsync(_host, _port, linkedCts.Token).ConfigureAwait(false);
                _stream = _tcpClient.GetStream();
                _readerCts = new CancellationTokenSource();
                _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), CancellationToken.None);
            }
            catch
            {
                await CloseInternalAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task CloseInternalAsync()
    {
        try
        {
            _readerCts?.Cancel();
            _readerCts?.Dispose();
        }
        catch { }
        finally { _readerCts = null; }

        if (_stream is not null)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
            _stream = null;
        }

        if (_tcpClient is not null)
        {
            try { _tcpClient.Dispose(); } catch { }
            _tcpClient = null;
        }

        FailAllPending(new IOException("Modbus transport closed."));

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch { }
            _readerTask = null;
        }
    }

    /// <inheritdoc />
    public async Task<ModbusPdu> SendAsync(byte unitId, ModbusPdu request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected || _stream is null)
        {
            throw new InvalidOperationException("Modbus transport is not connected.");
        }

        await _inFlightGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var tcs = new TaskCompletionSource<ModbusPdu>(TaskCreationOptions.RunContinuationsAsynchronously);
        ushort tid = 0;

        try
        {
            byte[] encoded;
            if (_rtuOverTcp)
            {
                encoded = RtuOverTcpFrame.Encode(unitId, request);
                lock (_pendingLock)
                {
                    _pending[0] = tcs;
                }
            }
            else
            {
                lock (_pendingLock)
                {
                    tid = AllocateTransactionId();
                    _pending[tid] = tcs;
                }
                encoded = MbapFrame.Encode(tid, unitId, request);
            }

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = _timeProvider.CreateTimer(_ => timeoutCts.Cancel(), null, _requestTimeout, Timeout.InfiniteTimeSpan);

            var registration = timeoutCts.Token.Register(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                else
                {
                    tcs.TrySetException(new TimeoutException($"Modbus request timed out after {_requestTimeout.TotalMilliseconds}ms"));
                }
            });

            try
            {
                var response = await tcs.Task.ConfigureAwait(false);
                return response;
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(_rtuOverTcp ? (ushort)0 : tid);
            }
            _inFlightGate.Release();
        }
    }

    private ushort AllocateTransactionId()
    {
        for (var i = 0; i < 65535; i++)
        {
            var id = _nextTransactionId++;
            if (!_pending.ContainsKey(id))
            {
                return id;
            }
        }

        throw new InvalidOperationException("All transaction IDs are in flight.");
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var offset = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream is not null)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;

                while (offset > 0)
                {
                    if (_rtuOverTcp)
                    {
                        TaskCompletionSource<ModbusPdu>? tcs;
                        lock (_pendingLock)
                        {
                            _pending.TryGetValue(0, out tcs);
                        }

                        if (offset < 3) break;
                        var fc = buffer[1];
                        var rawFc = (byte)(fc & 0x7F);

                        if (RtuOverTcpFrame.TryDecode(buffer.AsSpan(0, offset), 0, rawFc, out var pdu, out var consumed))
                        {
                            Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                            offset -= consumed;

                            if (pdu.FunctionCode == (rawFc | 0x80))
                            {
                                var errCode = pdu.Data.Length > 0 ? pdu.Data.Span[0] : (byte)0;
                                tcs?.TrySetException(new ModbusException(rawFc, errCode));
                            }
                            else
                            {
                                tcs?.TrySetResult(pdu);
                            }
                        }
                        else
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
                        }
                    }
                    else
                    {
                        if (MbapFrame.TryDecode(buffer.AsSpan(0, offset), out var tid, out _, out var pdu, out var consumed))
                        {
                            Array.Copy(buffer, consumed, buffer, 0, offset - consumed);
                            offset -= consumed;

                            TaskCompletionSource<ModbusPdu>? tcs;
                            lock (_pendingLock)
                            {
                                _pending.TryGetValue(tid, out tcs);
                            }

                            if (tcs is not null)
                            {
                                if (pdu.IsError)
                                {
                                    var origFc = (byte)(pdu.FunctionCode & 0x7F);
                                    var errCode = pdu.Data.Length > 0 ? pdu.Data.Span[0] : (byte)0;
                                    tcs.TrySetException(new ModbusException(origFc, errCode));
                                }
                                else
                                {
                                    tcs.TrySetResult(pdu);
                                }
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            FailAllPending(new IOException("Modbus connection closed."));
        }
    }

    private void FailAllPending(Exception exception)
    {
        lock (_pendingLock)
        {
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(exception);
            }
            _pending.Clear();
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        await CloseInternalAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _inFlightGate.Dispose();
        _writeGate.Dispose();
        _connectGate.Dispose();
    }
}
