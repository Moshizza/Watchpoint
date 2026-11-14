using System.Net.Sockets;

namespace Watchpoint.Hosting.Supervision;

/// <summary>Runs readiness probes for the supervisor.</summary>
internal interface IReadinessProber
{
    /// <summary>
    /// Returns <see langword="true"/> when the probe succeeded and <see langword="false"/> when its timeout elapsed.
    /// Cancelled through <paramref name="ct"/> when the child exits or a stop is requested.
    /// </summary>
    Task<bool> WaitReadyAsync(ReadinessProbe probe, ChildOutputPump output, CancellationToken ct);
}

/// <summary>Readiness probes of spec HST §7.1: repeated attempts until success or the probe's timeout, on the <see cref="TimeProvider"/>.</summary>
internal sealed class ReadinessProbes : IReadinessProber, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;
    private readonly Func<string, int, CancellationToken, Task<bool>> _tcpConnect;
    private readonly HttpMessageInvoker _http;

    public ReadinessProbes(
        TimeProvider timeProvider,
        TimeSpan interval,
        Func<string, int, CancellationToken, Task<bool>>? tcpConnect = null,
        HttpMessageHandler? httpHandler = null)
    {
        _timeProvider = timeProvider;
        _interval = interval;
        _tcpConnect = tcpConnect ?? TcpConnectAsync;
        _http = new HttpMessageInvoker(httpHandler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
    }

    public Task<bool> WaitReadyAsync(ReadinessProbe probe, ChildOutputPump output, CancellationToken ct) => probe switch
    {
        ReadinessProbe.Tcp tcp => PollAsync(tcp.Timeout, attempt => _tcpConnect(tcp.Host, tcp.Port, attempt), ct),
        ReadinessProbe.Http http => PollAsync(http.Timeout, attempt => HttpGetAsync(http.Url, attempt), ct),
        ReadinessProbe.StdoutLine line => WaitForLineAsync(line, output, ct),
        _ => Task.FromResult(true),
    };

    public void Dispose() => _http.Dispose();

    private static async Task<bool> TcpConnectAsync(string host, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HttpGetAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> WaitForLineAsync(ReadinessProbe.StdoutLine probe, ChildOutputPump output, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(probe.Timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await output.WaitForLineAsync(probe.Contains, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> PollAsync(TimeSpan timeout, Func<CancellationToken, Task<bool>> attempt, CancellationToken ct)
    {
        // One timer bounds the whole probe, including an attempt that hangs.
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        while (true)
        {
            try
            {
                if (await attempt(linked.Token).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException)
            {
                // Not ready yet.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                await Task.Delay(_interval, _timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }
}
