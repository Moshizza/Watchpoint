using System.IO.Pipelines;
using System.Net.Sockets;

namespace Watchpoint.Integration.Sdk.Net;

/// <summary>
/// TCP request-response client with configurable timeouts and framing (INT §10).
/// Supports single-request mode or persistent connection reuse.
/// </summary>
public sealed class TcpRequestClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;
    private readonly bool _keepAlive;
    private readonly TimeProvider _time;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private PipeReader? _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Initializes a new instance of <see cref="TcpRequestClient"/>.</summary>
    /// <param name="host">Target host or IP address.</param>
    /// <param name="port">Target port number.</param>
    /// <param name="connectTimeout">Timeout for connecting (default 5 s).</param>
    /// <param name="readTimeout">Timeout for reading responses (default 10 s).</param>
    /// <param name="keepAlive">Whether to keep the TCP connection alive across requests.</param>
    /// <param name="time">Time provider.</param>
    public TcpRequestClient(
        string host,
        int port,
        TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null,
        bool keepAlive = true,
        TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        _host = host;
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(10);
        _keepAlive = keepAlive;
        _time = time ?? TimeProvider.System;
    }

    private async ValueTask EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client is not null && _client.Connected)
        {
            return;
        }

        CloseConnection();

        var client = new TcpClient();
        using var timeoutCts = new CancellationTokenSource(_connectTimeout, _time);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await client.ConnectAsync(_host, _port, linkedCts.Token).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            _reader = PipeReader.Create(_stream);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"TCP connection to {_host}:{_port} timed out after {_connectTimeout}.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a request and reads a framed response.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">Request payload bytes.</param>
    /// <param name="readResponse">Delegate reading the response from the PipeReader.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialised response.</returns>
    public async ValueTask<TResponse> SendRequestAsync<TResponse>(
        ReadOnlyMemory<byte> request,
        Func<PipeReader, CancellationToken, ValueTask<TResponse>> readResponse,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(readResponse);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);

            // Write request
            await _stream!.WriteAsync(request, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);

            // Read response with timeout
            using var timeoutCts = new CancellationTokenSource(_readTimeout, _time);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                var result = await readResponse(_reader!, linkedCts.Token).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                CloseConnection();
                throw new TimeoutException($"TCP read timed out after {_readTimeout}.");
            }
            catch
            {
                CloseConnection();
                throw;
            }
        }
        finally
        {
            if (!_keepAlive)
            {
                CloseConnection();
            }
            _gate.Release();
        }
    }

    private void CloseConnection()
    {
        _reader?.Complete();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        CloseConnection();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
