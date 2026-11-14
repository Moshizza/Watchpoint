using NATS.Client.Core;
using NATS.Client.JetStream;
using Watchpoint.Hosting;

namespace Watchpoint.Pipeline.Internal.Nats;

/// <summary>
/// The NATS connections of the pipeline (spec PIP §5.4): one for the writer, so that publishes are never queued behind
/// replayed records, and one for readers, consumers and stream management. Connections are opened on first use and
/// reconnect by themselves, without limit.
/// </summary>
internal sealed class NatsConnectionProvider(NatsEndpoint endpoint) : IAsyncDisposable
{
    private readonly Lazy<NatsConnection> _writer = new(() => Create(endpoint, "watchpoint-pipeline-writer"));
    private readonly Lazy<NatsConnection> _reader = new(() => Create(endpoint, "watchpoint-pipeline-reader"));

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

    private static NatsConnection Create(NatsEndpoint endpoint, string name) => new(NatsOpts.Default with
    {
        Url = endpoint.Url,
        Name = name,
        AuthOpts = endpoint.Token is null ? NatsAuthOpts.Default : new NatsAuthOpts { Token = endpoint.Token },
        ConnectTimeout = TimeSpan.FromSeconds(2),
        RetryOnInitialConnect = true,
        MaxReconnectRetry = -1,
        ReconnectWaitMin = TimeSpan.FromMilliseconds(100),
        ReconnectWaitMax = TimeSpan.FromSeconds(2),
    });
}
