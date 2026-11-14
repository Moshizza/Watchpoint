using NATS.Client.Core;
using NATS.Client.JetStream;
using Watchpoint.Hosting;

namespace Watchpoint.Host.Nats;

/// <summary>
/// The last step of the bundled server's readiness (spec HST §7.4): a NATS client connects with the token and
/// JetStream answers. A listening port alone does not prove either.
/// </summary>
public static class NatsClientCheck
{
    /// <summary>Connects, pings and asks JetStream for the account information once.</summary>
    /// <param name="endpoint">The server.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the server answered.</returns>
    public static async Task ConnectOnceAsync(NatsEndpoint endpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var options = NatsOpts.Default with
        {
            Url = endpoint.Url,
            Name = "watchpoint-host-readiness",
            AuthOpts = endpoint.Token is null ? NatsAuthOpts.Default : new NatsAuthOpts { Token = endpoint.Token },
            ConnectTimeout = TimeSpan.FromSeconds(2),
        };
        await using var connection = new NatsConnection(options);
        await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        await connection.PingAsync(ct).ConfigureAwait(false);
        await new NatsJSContext(connection).GetAccountInfoAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Retries <see cref="ConnectOnceAsync"/> until it succeeds or <paramref name="timeout"/> elapsed.</summary>
    /// <param name="endpoint">The server.</param>
    /// <param name="timeout">The time allowed.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the server answered.</returns>
    /// <exception cref="TimeoutException">The server did not answer in time; the inner exception is the last failure.</exception>
    public static async Task WaitUntilUsableAsync(NatsEndpoint endpoint, TimeSpan timeout, TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        Exception? last = null;
        while (true)
        {
            try
            {
                await ConnectOnceAsync(endpoint, linked.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is NatsException or IOException or System.Net.Sockets.SocketException or TimeoutException)
            {
                last = ex;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"NATS at {endpoint.Url} did not accept a client connection within {timeout.TotalSeconds:0} s.", last);
    }
}
