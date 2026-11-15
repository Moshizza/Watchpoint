using NATS.Client.Core;
using NATS.Client.JetStream;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Nats;

/// <summary>
/// The NATS connections of the pipeline (spec PIP §5.4): one for the writer, so that publishes are never queued behind
/// replayed records, and one for readers, consumers and stream management. Connections are opened on first use and
/// reconnect by themselves, without limit.
/// </summary>
internal sealed class NatsConnectionProvider(EventLogEndpoint endpoint, PipelineOptions options) : IAsyncDisposable
{
    private readonly Lazy<NatsConnection> _writer = new(() => new NatsConnection(BuildOptions(endpoint, "watchpoint-pipeline-writer", options)));
    private readonly Lazy<NatsConnection> _reader = new(() => new NatsConnection(BuildOptions(endpoint, "watchpoint-pipeline-reader", options)));

    public NatsConnection Writer => _writer.Value;

    public NatsConnection Reader => _reader.Value;

    /// <summary>Gets a value indicating whether every connection in use is open.</summary>
    public bool IsConnected =>
        (_writer.IsValueCreated || _reader.IsValueCreated)
        && (!_writer.IsValueCreated || _writer.Value.ConnectionState == NatsConnectionState.Open)
        && (!_reader.IsValueCreated || _reader.Value.ConnectionState == NatsConnectionState.Open);

    public NatsJSContext WriterContext() => new(Writer);

    public NatsJSContext ReaderContext() => new(Reader);

    public async ValueTask DisposeAsync()
    {
        if (_writer.IsValueCreated)
        {
            await _writer.Value.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader.IsValueCreated)
        {
            await _reader.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The connection options (spec PIP §13): token authentication, and TLS required unless every server of the URL is
    /// on loopback (the bundled server) or <c>Log:AllowInsecureRemote</c> is set. On loopback TLS is used when the
    /// server offers it.
    /// </summary>
    internal static NatsOpts BuildOptions(EventLogEndpoint endpoint, string name, PipelineOptions options)
    {
        var requireTls = !options.AllowInsecureRemote && !IsLoopback(endpoint.Url);
        return NatsOpts.Default with
        {
            Url = endpoint.Url,
            Name = name,
            AuthOpts = endpoint.Token is null ? NatsAuthOpts.Default : new NatsAuthOpts { Token = endpoint.Token },
            TlsOpts = requireTls ? new NatsTlsOpts { Mode = TlsMode.Require } : NatsTlsOpts.Default,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            RetryOnInitialConnect = true,
            MaxReconnectRetry = -1,
            ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
            ReconnectWaitMax = TimeSpan.FromSeconds(2),
        };
    }

    /// <summary>Whether every server of a NATS URL (one, or several separated by commas) is a loopback address.</summary>
    internal static bool IsLoopback(string url)
    {
        var servers = url.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length == 0)
        {
            return false;
        }

        foreach (var server in servers)
        {
            var text = server.Contains("://", StringComparison.Ordinal) ? server : "nats://" + server;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var host = uri.Host.Trim('[', ']');
            var loopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
            if (!loopback)
            {
                return false;
            }
        }

        return true;
    }
}
