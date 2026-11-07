using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchpoint.Spikes.ConnectorIsolation.Protocol;

namespace Watchpoint.Spikes.ConnectorIsolation.Runner;

/// <summary>The node side of HostLink: a gRPC server on a Unix domain socket; one session per host process.</summary>
internal sealed class HostLinkServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private HostLinkServer(WebApplication app, string socket)
    {
        _app = app;
        Socket = socket;
    }

    public string Socket { get; }

    /// <summary>Raised when a host opens its session.</summary>
    public Channel<HostSession> Sessions { get; } = Channel.CreateUnbounded<HostSession>();

    public static async Task<HostLinkServer> StartAsync(Ingest ingest)
    {
        var socket = Path.Combine(Path.GetTempPath(), $"wp-spike-{Environment.ProcessId}.sock");
        File.Delete(socket);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socket, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        var app = builder.Build();
        var server = new HostLinkServer(app, socket);
        app.MapGrpcService<HostLinkService>();
        HostLinkService.Server = server;
        HostLinkService.IngestInstance = ingest;
        await app.StartAsync().ConfigureAwait(false);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        File.Delete(Socket);
    }
}

/// <summary>gRPC service; static wiring is acceptable in spike code.</summary>
internal sealed class HostLinkService : HostLink.HostLinkBase
{
    public static HostLinkServer? Server { get; set; }

    public static Ingest? IngestInstance { get; set; }

    public override async Task Session(IAsyncStreamReader<HostToNode> requestStream, IServerStreamWriter<NodeToHost> responseStream,
        ServerCallContext context)
    {
        var session = new HostSession();
        var writer = Task.Run(async () =>
        {
            await foreach (var message in session.Outgoing.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(message).ConfigureAwait(false);
            }
        });
        var ingest = IngestInstance!.Writer;
        try
        {
            while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false))
            {
                var message = requestStream.Current;
                switch (message.BodyCase)
                {
                    case HostToNode.BodyOneofCase.Hello:
                        session.HelloReceived(message.Hello);
                        await Server!.Sessions.Writer.WriteAsync(session).ConfigureAwait(false);
                        break;
                    case HostToNode.BodyOneofCase.Observations:
                        foreach (var o in message.Observations.Items)
                        {
                            await ingest.WriteAsync(new Contract.Observation(o.Device, o.Value, o.Timestamp)).ConfigureAwait(false);
                        }

                        break;
                    case HostToNode.BodyOneofCase.Reply:
                        session.Complete(message.Reply);
                        break;
                    case HostToNode.BodyOneofCase.Status:
                        session.LastStatus = Stopwatch.GetTimestamp();
                        break;
                    default:
                        break;
                }
            }
        }
        catch (IOException)
        {
            // host died: the link ends
        }
        catch (OperationCanceledException)
        {
            // host died or node stopping
        }
        finally
        {
            session.End();
            session.Outgoing.Writer.TryComplete();
        }

        try
        {
            await writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected when the host is gone
        }
    }
}

/// <summary>One host's session: requests with correlation ids, hello, end of link.</summary>
internal sealed class HostSession
{
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<string>> _pending = new();
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _nextId;

    public Channel<NodeToHost> Outgoing { get; } = Channel.CreateUnbounded<NodeToHost>(new UnboundedChannelOptions { SingleReader = true });

    public Hello? Hello { get; private set; }

    public long LastStatus { get; set; }

    /// <summary>Completes when the link ends (host exit, crash, or shutdown).</summary>
    public Task Ended => _ended.Task;

    /// <summary>Stopwatch timestamp of the end of the link.</summary>
    public long EndedAt { get; private set; }

    public void HelloReceived(Hello hello) => Hello = hello;

    public Task<string> SendAsync(Request request)
    {
        request.Id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;
        if (!Outgoing.Writer.TryWrite(new NodeToHost { Request = request }))
        {
            tcs.TrySetException(new IOException("link closed"));
        }

        return tcs.Task;
    }

    public void Complete(Reply reply)
    {
        if (_pending.TryRemove(reply.Id, out var tcs))
        {
            tcs.TrySetResult(reply.Result);
        }
    }

    public void End()
    {
        EndedAt = Stopwatch.GetTimestamp();
        foreach (var (_, tcs) in _pending)
        {
            tcs.TrySetException(new IOException("link lost"));
        }

        _ended.TrySetResult();
    }
}
