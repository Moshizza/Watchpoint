using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Mapping;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Simulator.Remote;

public sealed class RemoteTransport : IAsyncDisposable
{
    private readonly IConnectorContext _context;
    private readonly SimulatorSettings _settings;
    private ClientWebSocket? _webSocket;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingCommands = new();
    private readonly object _sync = new();

    public RemoteTransport(IConnectorContext context, SimulatorSettings settings)
    {
        _context = context;
        _settings = settings;
    }

    public async ValueTask ConnectAsync(CancellationToken ct)
    {
        _webSocket = new ClientWebSocket();
        var uri = new Uri(_settings.Url);
        await _webSocket.ConnectAsync(uri, ct);

        var helloMsg = new Dictionary<string, object?>
        {
            ["t"] = "hello",
            ["v"] = 1,
        };
        if (!string.IsNullOrEmpty(_settings.Token))
        {
            helloMsg["token"] = _settings.Token;
        }

        await SendMessageAsync(helloMsg, ct);
    }

    public async ValueTask RunAsync(CancellationToken ct)
    {
        if (_webSocket == null)
        {
            return;
        }

        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            HandleMessage(json);
        }
    }

    private void HandleMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("t", out var tProp))
        {
            return;
        }

        string type = tProp.GetString()!;
        switch (type)
        {
            case "ping":
                _ = Task.Run(async () => await SendMessageAsync(new Dictionary<string, object?> { ["t"] = "pong" }, CancellationToken.None));
                break;

            case "change":
                {
                    string device = root.GetProperty("device").GetString()!;
                    var deviceKey = FindDeviceKey(device);
                    if (root.TryGetProperty("props", out var propsEl))
                    {
                        foreach (var prop in propsEl.EnumerateObject())
                        {
                            var wpVal = SimMapping.ToWpValue(prop.Value);
                            _context.Sink.ReportProperty(deviceKey, prop.Name, wpVal);
                        }
                    }
                    break;
                }

            case "event":
                {
                    string device = root.GetProperty("device").GetString()!;
                    string ev = root.GetProperty("event").GetString()!;
                    var deviceKey = FindDeviceKey(device);
                    var payload = new Dictionary<string, WpValue>();
                    if (root.TryGetProperty("payload", out var pEl))
                    {
                        foreach (var prop in pEl.EnumerateObject())
                        {
                            payload[prop.Name] = SimMapping.ToWpValue(prop.Value);
                        }
                    }
                    _context.Sink.ReportEvent(deviceKey, ev, payload);
                    break;
                }

            case "commandResult":
                {
                    string id = root.GetProperty("id").GetString()!;
                    lock (_sync)
                    {
                        if (_pendingCommands.TryGetValue(id, out var tcs))
                        {
                            tcs.TrySetResult(root.Clone());
                            _pendingCommands.Remove(id);
                        }
                    }
                    break;
                }
        }
    }

    public async ValueTask SendMessageAsync(object payload, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            return;
        }

        string json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask<JsonElement> SendCommandAsync(string commandId, string device, string command, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pendingCommands[commandId] = tcs;
        }

        var msg = new Dictionary<string, object?>
        {
            ["t"] = "command",
            ["id"] = commandId,
            ["device"] = device,
            ["command"] = command,
            ["args"] = args,
        };

        await SendMessageAsync(msg, ct);

        using (ct.Register(() => tcs.TrySetCanceled()))
        {
            return await tcs.Task;
        }
    }

    private DeviceKey FindDeviceKey(string nativeId)
    {
        foreach (var d in _context.Devices.All)
        {
            if (d.Key.NativeId == nativeId)
            {
                return d.Key;
            }
        }
        return new DeviceKey("sim-root", nativeId);
    }

    public async ValueTask DisconnectAsync(CancellationToken ct)
    {
        if (_webSocket != null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                }
            }
            catch
            {
            }
            _webSocket.Dispose();
            _webSocket = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None);
    }
}
