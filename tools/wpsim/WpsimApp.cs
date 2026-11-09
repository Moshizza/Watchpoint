using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Watchpoint.Integration.Simulator.Engine;

namespace Watchpoint.Wpsim;

public static class WpsimApp
{
    public static WebApplication Build(int port = 5090, SimulatorSettings? initialSettings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(System.Net.IPAddress.Loopback, port);
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();

        var app = builder.Build();
        app.UseWebSockets();

        var settings = initialSettings ?? new SimulatorSettings();
        var site = new SimulatedSite(settings);

        var activeSockets = new ConcurrentDictionary<Guid, WebSocket>();
        bool isPaused = false;
        int simulatedLatencyMs = 0;
        var requestCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (!cts.IsCancellationRequested && await timer.WaitForNextTickAsync(cts.Token))
            {
                if (!isPaused)
                {
                    site.Tick(TimeSpan.FromMilliseconds(250), DateTimeOffset.UtcNow);
                }
            }
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            cts.Cancel();
            cts.Dispose();
        });

        site.EventEmitted += record =>
        {
            if (isPaused) return;
            var msg = new Dictionary<string, object?>
            {
                ["t"] = "event",
                ["seq"] = record.Seq,
                ["device"] = record.Device,
                ["event"] = record.Event,
                ["payload"] = record.Payload,
                ["at"] = record.At.ToString("O"),
            };
            Broadcast(activeSockets, msg);
        };

        site.ChangeEmitted += record =>
        {
            if (isPaused) return;
            var msg = new Dictionary<string, object?>
            {
                ["t"] = "change",
                ["seq"] = record.Seq,
                ["device"] = record.Device,
                ["props"] = record.Props,
                ["at"] = record.At.ToString("O"),
            };
            Broadcast(activeSockets, msg);
        };

        _ = Task.Run(async () =>
        {
            using var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!cts.IsCancellationRequested && await pingTimer.WaitForNextTickAsync(cts.Token))
            {
                Broadcast(activeSockets, new Dictionary<string, object?> { ["t"] = "ping" });
            }
        });

        app.Map("/sim", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var socketId = Guid.NewGuid();
            activeSockets[socketId] = ws;

            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();

            try
            {
                while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", context.RequestAborted);
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    await HandleClientMessageAsync(ws, text, site, isPaused, simulatedLatencyMs, requestCounts, context.RequestAborted);
                }
            }
            finally
            {
                activeSockets.TryRemove(socketId, out _);
            }
        });

        app.MapPost("/control/drop", async () =>
        {
            foreach (var (_, ws) in activeSockets)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Dropped by control", CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
            activeSockets.Clear();
            return Results.Ok(new { dropped = true });
        });

        app.MapPost("/control/pause", () =>
        {
            isPaused = true;
            site.PauseActivity();
            return Results.Ok(new { paused = true });
        });

        app.MapPost("/control/resume", () =>
        {
            isPaused = false;
            site.ResumeActivity();
            return Results.Ok(new { resumed = true });
        });

        app.MapPost("/control/latency", (JsonElement body) =>
        {
            if (body.TryGetProperty("ms", out var msProp))
            {
                simulatedLatencyMs = msProp.GetInt32();
            }
            return Results.Ok(new { latencyMs = simulatedLatencyMs });
        });

        app.MapPost("/control/inject", (JsonElement body) =>
        {
            string device = body.GetProperty("device").GetString()!;
            var now = DateTimeOffset.UtcNow;
            if (body.TryGetProperty("event", out var evProp))
            {
                string ev = evProp.GetString()!;
                var payload = new Dictionary<string, object?>();
                if (body.TryGetProperty("payload", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in pEl.EnumerateObject())
                    {
                        payload[prop.Name] = prop.Value.GetString();
                    }
                }
                site.EmitEvent(device, ev, payload, now);
            }
            else if (body.TryGetProperty("props", out var propsProp) && propsProp.ValueKind == JsonValueKind.Object)
            {
                var props = new Dictionary<string, object?>();
                foreach (var prop in propsProp.EnumerateObject())
                {
                    props[prop.Name] = prop.Value.GetString();
                }
                site.EmitChange(device, props, now);
            }

            return Results.Ok(new { injected = true });
        });

        app.MapPost("/control/failures", (JsonElement body) =>
        {
            var rules = new List<FailureRule>();
            if (body.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in body.EnumerateArray())
                {
                    string cmd = item.GetProperty("command").GetString()!;
                    string modeStr = item.GetProperty("mode").GetString()!;
                    double prob = item.GetProperty("probability").GetDouble();
                    if (Enum.TryParse<FailureMode>(modeStr, true, out var fMode))
                    {
                        rules.Add(new FailureRule(cmd, fMode, prob));
                    }
                }
            }
            site.FailureInjector.SetRules(rules);
            return Results.Ok(new { count = rules.Count });
        });

        app.MapPost("/control/rename", (JsonElement body) =>
        {
            string device = body.GetProperty("device").GetString()!;
            string name = body.GetProperty("name").GetString()!;
            var dev = site.Inventory.Devices.FirstOrDefault(d => d.NativeId == device);
            if (dev != null)
            {
                site.Regenerate();
            }
            return Results.Ok(new { renamed = device, name });
        });

        app.MapGet("/control/stats", () => Results.Ok(requestCounts));

        return app;
    }

    private static async Task HandleClientMessageAsync(
        WebSocket ws,
        string text,
        SimulatedSite site,
        bool isPaused,
        int latencyMs,
        ConcurrentDictionary<string, int> stats,
        CancellationToken ct)
    {
        if (isPaused)
        {
            return;
        }

        if (latencyMs > 0)
        {
            await Task.Delay(latencyMs, ct);
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string t = root.GetProperty("t").GetString()!;
        stats.AddOrUpdate(t, 1, (_, c) => c + 1);

        switch (t)
        {
            case "hello":
                {
                    var welcome = new Dictionary<string, object?>
                    {
                        ["t"] = "welcome",
                        ["v"] = 1,
                        ["site"] = new Dictionary<string, object?>
                        {
                            ["seed"] = 1,
                            ["profile"] = "office",
                        },
                        ["lastSeq"] = site.LastSeq,
                    };
                    await SendJsonAsync(ws, welcome, ct);
                    break;
                }

            case "inventory":
                {
                    var invMsg = new Dictionary<string, object?>
                    {
                        ["t"] = "inventory",
                        ["servers"] = site.Inventory.Servers,
                        ["devices"] = site.Inventory.Devices,
                    };
                    await SendJsonAsync(ws, invMsg, ct);
                    break;
                }

            case "snapshot":
                {
                    IReadOnlyList<string>? requested = null;
                    if (root.TryGetProperty("devices", out var devEl) && devEl.ValueKind == JsonValueKind.Array)
                    {
                        requested = devEl.EnumerateArray().Select(d => d.GetString()!).ToList();
                    }
                    var snapshot = site.GetSnapshot(requested);
                    var snapMsg = new Dictionary<string, object?>
                    {
                        ["t"] = "snapshot",
                        ["devices"] = snapshot,
                    };
                    await SendJsonAsync(ws, snapMsg, ct);
                    break;
                }

            case "catchUp":
                {
                    ulong afterSeq = root.GetProperty("afterSeq").GetUInt64();
                    var events = site.GetEventsAfter(afterSeq);
                    foreach (var ev in events)
                    {
                        var evMsg = new Dictionary<string, object?>
                        {
                            ["t"] = "event",
                            ["seq"] = ev.Seq,
                            ["device"] = ev.Device,
                            ["event"] = ev.Event,
                            ["payload"] = ev.Payload,
                            ["at"] = ev.At.ToString("O"),
                        };
                        await SendJsonAsync(ws, evMsg, ct);
                    }
                    await SendJsonAsync(ws, new Dictionary<string, object?> { ["t"] = "catchUpDone" }, ct);
                    break;
                }

            case "command":
                {
                    string id = root.GetProperty("id").GetString()!;
                    string device = root.GetProperty("device").GetString()!;
                    string command = root.GetProperty("command").GetString()!;
                    var args = new Dictionary<string, object?>();
                    if (root.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in aEl.EnumerateObject())
                        {
                            args[prop.Name] = prop.Value.GetString();
                        }
                    }

                    var res = await site.ExecuteCommandAsync(device, command, args, DateTimeOffset.UtcNow, ct);
                    var cmdResult = new Dictionary<string, object?>
                    {
                        ["t"] = "commandResult",
                        ["id"] = id,
                        ["outcome"] = res.Success ? "succeeded" : "failed",
                    };
                    if (!res.Success)
                    {
                        cmdResult["failure"] = res.FailureCategory ?? "RefusedBySystem";
                        cmdResult["message"] = res.MessageKey ?? "errors.refused";
                        cmdResult["args"] = res.MessageArgs;
                    }
                    await SendJsonAsync(ws, cmdResult, ct);
                    break;
                }

            case "pong":
                break;
        }
    }

    private static void Broadcast(ConcurrentDictionary<Guid, WebSocket> sockets, object message)
    {
        string json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var (_, ws) in sockets)
        {
            if (ws.State == WebSocketState.Open)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                    }
                });
            }
        }
    }

    private static async Task SendJsonAsync(WebSocket ws, object message, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        string json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }
}
