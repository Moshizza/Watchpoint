using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using Watchpoint.Spikes.ConnectorIsolation.Contract;
using Watchpoint.Spikes.ConnectorIsolation.Host;
using Watchpoint.Spikes.ConnectorIsolation.Protocol;

// Isolated host: watchpoint-integration-host --socket <path> --connector <main.dll> --batching eager|timer
var arguments = Arguments.Parse(args);
using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(arguments.Socket), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    },
});

using var session = new HostLink.HostLinkClient(channel).Session();
var outgoing = Channel.CreateBounded<HostToNode>(new BoundedChannelOptions(4) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
var writer = WriteLoopAsync(session.RequestStream, outgoing.Reader);

var loadStart = Stopwatch.GetTimestamp();
var (_, connector) = PackageLoadContext.LoadConnector(arguments.Connector);
var sink = new BatchingSink(outgoing.Writer, arguments.TimerBatching);
connector.Initialize(sink);
var loadMs = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
await outgoing.Writer.WriteAsync(new HostToNode { Hello = new Hello { Pid = Environment.ProcessId, LoadMs = loadMs } });

using var shutdown = new CancellationTokenSource();
var heartbeat = HeartbeatAsync(outgoing.Writer, shutdown.Token);
await foreach (var message in session.ResponseStream.ReadAllAsync())
{
    var request = message.Request;
    switch (request.KindCase)
    {
        case Request.KindOneofCase.Emit:
            _ = EmitAsync(request.Id, request.Emit);
            break;
        case Request.KindOneofCase.Execute:
            var result = await connector.ExecuteAsync(request.Execute.Command, CancellationToken.None);
            await outgoing.Writer.WriteAsync(new HostToNode { Reply = new Reply { Id = request.Id, Result = result } });
            break;
        case Request.KindOneofCase.Misbehave:
            connector.Misbehave((Misbehaviour)request.Misbehave);
            await outgoing.Writer.WriteAsync(new HostToNode { Reply = new Reply { Id = request.Id, Result = "ok" } });
            break;
        case Request.KindOneofCase.Shutdown:
            await shutdown.CancelAsync();
            break;
        default:
            break;
    }

    if (shutdown.IsCancellationRequested)
    {
        break;
    }
}

await sink.DisposeAsync();
outgoing.Writer.TryComplete();
await writer;
await session.RequestStream.CompleteAsync();
return 0;

async Task EmitAsync(ulong id, Emit emit)
{
    await connector.EmitAsync(emit.Count, emit.RatePerSecond, CancellationToken.None);
    await outgoing.Writer.WriteAsync(new HostToNode { Reply = new Reply { Id = id, Result = "emitted" } });
}

static async Task WriteLoopAsync(IClientStreamWriter<HostToNode> stream, ChannelReader<HostToNode> reader)
{
    await foreach (var message in reader.ReadAllAsync())
    {
        await stream.WriteAsync(message);
    }
}

static async Task HeartbeatAsync(ChannelWriter<HostToNode> writer, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
    {
        await writer.WriteAsync(new HostToNode { Status = new HostStatus { WorkingSet = Environment.WorkingSet } }, ct);
    }
}

namespace Watchpoint.Spikes.ConnectorIsolation.Host
{
    internal sealed record Arguments(string Socket, string Connector, bool TimerBatching)
    {
        public static Arguments Parse(string[] args)
        {
            string Value(string name) =>
                args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? throw new ArgumentException($"missing {name}");
            return new Arguments(Value("--socket"), Value("--connector"),
                string.Equals(Value("--batching"), "timer", StringComparison.Ordinal));
        }
    }
}
