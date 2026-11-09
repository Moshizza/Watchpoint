using System.Net.WebSockets;
using System.Text.Json;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk;
using Watchpoint.Integration.Simulator.Embedded;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Mapping;
using Watchpoint.Integration.Simulator.Remote;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Simulator;

[Connector("sim.server")]
public sealed class SimulatorConnector : ConnectorBase,
    IDiscovery,
    IStateRefresh,
    ICommandExecutor,
    IBatchCommandExecutor,
    IEventCatchUp,
    IDiagnostics
{
    private readonly SimulatedSite? _preconfiguredSite;
    private SimulatorSettings _settings = new();
    private EmbeddedTransport? _embeddedTransport;
    private RemoteTransport? _remoteTransport;

    public SimulatedSite? EmbeddedSite => _embeddedTransport?.Site;

    public SimulatorConnector() : this(null)
    {
    }

    public SimulatorConnector(SimulatedSite? site)
    {
        _preconfiguredSite = site;
    }

    public override async ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
    {
        var baseRes = await base.InitializeAsync(context, ct);
        if (!baseRes.IsSuccess)
        {
            return baseRes;
        }

        _settings = SimMapping.ParseSettings(context.Settings);

        if (_settings.Doors is < 0 or > 20_000)
        {
            return Result.Failure(CommonErrors.ValidationFailed([CommonErrors.OutOfRangeAt("/doors", 0, 20_000)]));
        }

        if (string.Equals(_settings.Mode, "embedded", StringComparison.OrdinalIgnoreCase))
        {
            _embeddedTransport = new EmbeddedTransport(Context, _settings, _preconfiguredSite);
        }
        else
        {
            _remoteTransport = new RemoteTransport(Context, _settings);
        }

        return Result.Success();
    }

    public override async ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        if (string.Equals(_settings.Mode, "remote", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var ws = new ClientWebSocket();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
                await ws.ConnectAsync(new Uri(_settings.Url), timeoutCts.Token);
                return ConnectionTestResult.Succeeded(new ServerIdentity("Simulator", "1.0", "SIM-REMOTE", new Dictionary<string, string>()));
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Failed(FailureCategory.Unreachable, ConnectorMessage.Of("connectionLost", ("reason", ex.Message)));
            }
        }

        return ConnectionTestResult.Succeeded(new ServerIdentity("Simulator", "1.0", "SIM-001", new Dictionary<string, string>()));
    }

    public override ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct)
    {
        if (_embeddedTransport != null)
        {
            return ValueTask.FromResult(SimMapping.ToInventorySnapshot(_embeddedTransport.Site.Inventory));
        }

        var inventory = SiteGenerator.Generate(_settings);
        return ValueTask.FromResult(SimMapping.ToInventorySnapshot(inventory));
    }

    public ValueTask<InventorySnapshot> DiscoverAsync(IProgress<DiscoveryProgress> progress, CancellationToken ct)
    {
        progress.Report(new DiscoveryProgress(1, 1, ConnectorMessage.Of("connected")));
        return DescribeAsync(ct);
    }

    protected override async ValueTask ConnectOnceAsync(CancellationToken ct)
    {
        if (_remoteTransport != null)
        {
            await _remoteTransport.ConnectAsync(ct);
            SimLog.SimulatorConnected(Logger, _settings.Url);
        }
        else if (_embeddedTransport != null)
        {
            _embeddedTransport.Start();
            string prof = _settings.Profile.ToString().ToLowerInvariant();
            int devCount = _embeddedTransport.Site.Inventory.Devices.Count;
            SimLog.SimulatorSiteGenerated(
                Logger,
                prof,
                _settings.Seed,
                devCount);
        }
    }

    protected override async ValueTask RunConnectedAsync(CancellationToken ct)
    {
        if (_remoteTransport != null)
        {
            await _remoteTransport.RunAsync(ct);
        }
        else
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task;
            }
        }
    }

    protected override async ValueTask DisconnectAsync(CancellationToken ct)
    {
        if (_embeddedTransport != null)
        {
            await _embeddedTransport.DisposeAsync();
        }
        if (_remoteTransport != null)
        {
            await _remoteTransport.DisconnectAsync(ct);
        }
        SimLog.SimulatorConnectionLost(Logger, "Disconnect requested");
    }

    public async ValueTask RefreshAsync(RefreshScope scope, CancellationToken ct)
    {
        if (_embeddedTransport != null)
        {
            var deviceIds = scope.Devices?.Select(d => d.Key.NativeId).ToList();
            var snapshot = _embeddedTransport.Site.GetSnapshot(deviceIds);
            var now = Context.Time.GetUtcNow();

            foreach (var (devId, props) in snapshot)
            {
                var key = new DeviceKey("sim-root", devId);
                foreach (var d in Context.Devices.All)
                {
                    if (d.Key.NativeId == devId)
                    {
                        key = d.Key;
                        break;
                    }
                }

                foreach (var (propName, propVal) in props)
                {
                    Context.Sink.ReportProperty(key, propName, SimMapping.ToWpValue(propVal), Quality.Good, now);
                }
            }
        }
        else if (_remoteTransport != null)
        {
            var msg = new Dictionary<string, object?>
            {
                ["t"] = "snapshot",
                ["devices"] = scope.Devices?.Select(d => d.Key.NativeId).ToArray(),
            };
            await _remoteTransport.SendMessageAsync(msg, ct);
        }
    }

    public async ValueTask CatchUpAsync(CancellationToken ct)
    {
        var cursorBytes = await Context.Store.GetAsync("cursor", ct);
        ulong lastSeq = 0;
        if (cursorBytes != null && cursorBytes.Length == sizeof(ulong))
        {
            lastSeq = BitConverter.ToUInt64(cursorBytes);
        }

        if (_embeddedTransport != null)
        {
            var events = _embeddedTransport.Site.GetEventsAfter(lastSeq);
            ulong highestSeq = lastSeq;

            foreach (var ev in events)
            {
                var key = new DeviceKey("sim-root", ev.Device);
                foreach (var d in Context.Devices.All)
                {
                    if (d.Key.NativeId == ev.Device)
                    {
                        key = d.Key;
                        break;
                    }
                }

                Context.Sink.ReportEvent(key, ev.Event, SimMapping.ToWpValueDict(ev.Payload), ev.At);
                if (ev.Seq > highestSeq)
                {
                    highestSeq = ev.Seq;
                }
            }

            if (highestSeq > lastSeq)
            {
                await Context.Sink.FlushAsync(ct);
                var newCursor = BitConverter.GetBytes(highestSeq);
                await Context.Store.SetAsync("cursor", newCursor, ct);
            }
        }
        else if (_remoteTransport != null)
        {
            var msg = new Dictionary<string, object?>
            {
                ["t"] = "catchUp",
                ["afterSeq"] = lastSeq,
            };
            await _remoteTransport.SendMessageAsync(msg, ct);
        }
    }

    public async ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct)
    {
        DateTimeOffset now = Context.Time.GetUtcNow();

        if (_embeddedTransport != null)
        {
            if (_settings.EffectiveCommandLatency > TimeSpan.Zero)
            {
                await Task.Delay(_settings.EffectiveCommandLatency, Context.Time, ct);
            }

            var argsDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in command.Arguments)
            {
                argsDict[k] = v.Kind switch
                {
                    WpValueKind.Bool => v.AsBool(),
                    WpValueKind.Int => v.AsInt(),
                    WpValueKind.Decimal => v.AsDecimal(),
                    WpValueKind.String => v.AsString(),
                    WpValueKind.Enum => v.AsEnum(),
                    _ => v.ToString(),
                };
            }

            var result = await _embeddedTransport.Site.ExecuteCommandAsync(
                command.Target.Key.NativeId,
                command.Command,
                argsDict,
                now,
                ct);

            SimLog.SimulatorCommandExecuted(
                Logger,
                command.Command,
                command.Target.Key.NativeId,
                result.Success ? "Succeeded" : "Failed");

            if (result.Success)
            {
                return ConnectorCommandResult.Succeeded();
            }

            if (string.Equals(result.FailureCategory, "NotSupported", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectorCommandResult.NotSupported();
            }

            if (!Enum.TryParse<FailureCategory>(result.FailureCategory, out var cat))
            {
                cat = FailureCategory.RefusedBySystem;
            }

            var argsList = new List<(string, object?)>();
            if (result.MessageArgs != null)
            {
                foreach (var (k, v) in result.MessageArgs)
                {
                    argsList.Add((k, v));
                }
            }

            string relativeKey = result.MessageKey?.StartsWith("sim.", StringComparison.Ordinal) == true
                ? result.MessageKey.Substring(4)
                : result.MessageKey ?? "errors.refused";

            return ConnectorCommandResult.Failed(cat, ConnectorMessage.Of(relativeKey, argsList.ToArray()));
        }

        if (_remoteTransport != null)
        {
            var argsDict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in command.Arguments)
            {
                argsDict[k] = v.ToString();
            }

            string cmdId = command.CommandId.ToString();
            var jsonResp = await _remoteTransport.SendCommandAsync(cmdId, command.Target.Key.NativeId, command.Command, argsDict, ct);
            string outcome = jsonResp.GetProperty("outcome").GetString()!;

            if (string.Equals(outcome, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return ConnectorCommandResult.Succeeded();
            }

            string failureStr = jsonResp.TryGetProperty("failure", out var f) ? f.GetString() ?? "RefusedBySystem" : "RefusedBySystem";
            if (!Enum.TryParse<FailureCategory>(failureStr, out var cat))
            {
                cat = FailureCategory.RefusedBySystem;
            }

            string msgKey = jsonResp.TryGetProperty("message", out var m) ? m.GetString() ?? "errors.refused" : "errors.refused";
            string relativeKey = msgKey.StartsWith("sim.", StringComparison.Ordinal) ? msgKey.Substring(4) : msgKey;

            return ConnectorCommandResult.Failed(cat, ConnectorMessage.Of(relativeKey));
        }

        return ConnectorCommandResult.NotSupported();
    }

    public async ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(IReadOnlyList<ConnectorCommand> commands, CancellationToken ct)
    {
        var results = new List<ConnectorCommandResult>(commands.Count);
        foreach (var cmd in commands)
        {
            results.Add(await ExecuteAsync(cmd, ct));
        }
        return results;
    }

    public ValueTask<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(CancellationToken ct)
    {
        var items = new List<DiagnosticItem>
        {
            new("mode", ConnectorMessage.Of("diag.mode"), WpValue.String(_settings.Mode)),
            new("profile", ConnectorMessage.Of("diag.profile"), WpValue.String(_settings.Profile.ToString().ToLowerInvariant())),
        };

        if (_embeddedTransport != null)
        {
            items.Add(new("devicesCount", ConnectorMessage.Of("diag.devicesCount"), WpValue.Int(_embeddedTransport.Site.Inventory.Devices.Count)));
            items.Add(new("lastSeq", ConnectorMessage.Of("diag.lastSeq"), WpValue.Int((long)_embeddedTransport.Site.LastSeq)));
        }

        return ValueTask.FromResult<IReadOnlyList<DiagnosticItem>>(items);
    }

    public ValueTask<Result<ConnectorMessage>> RunActionAsync(string actionId, JsonElement args, CancellationToken ct)
    {
        if (_embeddedTransport == null)
        {
            return ValueTask.FromResult<Result<ConnectorMessage>>(Error.Unprocessable("sim.action.notEmbedded"));
        }

        var site = _embeddedTransport.Site;
        var now = Context.Time.GetUtcNow();

        switch (actionId)
        {
            case "injectEvent":
                {
                    string device = args.GetProperty("device").GetString()!;
                    string ev = args.GetProperty("event").GetString()!;
                    var payload = new Dictionary<string, object?>();
                    if (args.TryGetProperty("payload", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in pEl.EnumerateObject())
                        {
                            payload[prop.Name] = prop.Value.GetString();
                        }
                    }
                    site.EmitEvent(device, ev, payload, now);
                    return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));
                }

            case "setProperty":
                {
                    string device = args.GetProperty("device").GetString()!;
                    string path = args.GetProperty("path").GetString()!;
                    object? val = args.TryGetProperty("value", out var v) ? v.GetString() : null;
                    site.EmitChange(device, new Dictionary<string, object?> { [path] = val }, now);
                    return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));
                }

            case "pauseActivity":
                site.PauseActivity();
                return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));

            case "resumeActivity":
                site.ResumeActivity();
                return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));

            case "setFailures":
                {
                    var rules = new List<FailureRule>();
                    if (args.TryGetProperty("failures", out var fails) && fails.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in fails.EnumerateArray())
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
                    return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));
                }

            case "regenerate":
                {
                    int? seed = args.TryGetProperty("seed", out var s) ? s.GetInt32() : null;
                    site.Regenerate(seed);
                    return ValueTask.FromResult<Result<ConnectorMessage>>(ConnectorMessage.Of("diag.actionExecuted"));
                }

            default:
                return ValueTask.FromResult<Result<ConnectorMessage>>(Error.NotFound("sim.action.notFound", ("actionId", actionId)));
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (_embeddedTransport != null)
        {
            await _embeddedTransport.DisposeAsync();
        }
        if (_remoteTransport != null)
        {
            await _remoteTransport.DisposeAsync();
        }
    }
}
