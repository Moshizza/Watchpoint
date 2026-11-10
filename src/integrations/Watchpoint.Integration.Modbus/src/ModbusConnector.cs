using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Modbus.Engine;
using Watchpoint.Integration.Modbus.Mapping;
using Watchpoint.Integration.Modbus.Protocol;
using Watchpoint.Integration.Modbus.Transport;
using Watchpoint.Integration.Sdk;
using Watchpoint.Integration.Sdk.Polling;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Modbus;

/// <summary>Modbus TCP and RTU-over-TCP connector implementation.</summary>
public sealed class ModbusConnector : ConnectorBase, IDiscovery, IStateRefresh, ICommandExecutor
{
    private string _host = "127.0.0.1";
    private int _port = 502;
    private string _transportKind = "tcp";
    private TimeSpan _connectTimeout = TimeSpan.FromSeconds(5);
    private TimeSpan _requestTimeout = TimeSpan.FromSeconds(1);
    private int _maxInFlight = 1;

    private readonly List<UnitExecutionPlan> _units = new();
    private ITransport? _transport;
    private PollingLoop? _pollingLoop;
    private readonly TimeProvider? _customTimeProvider;

    /// <summary>Initializes a new instance of the <see cref="ModbusConnector"/> class.</summary>
    public ModbusConnector()
    {
    }

    /// <summary>Internal constructor for passing an injected transport and time provider in tests.</summary>
    internal ModbusConnector(ITransport transport, TimeProvider timeProvider)
    {
        _transport = transport;
        _customTimeProvider = timeProvider;
    }

    private sealed record UnitExecutionPlan(
        byte UnitId,
        string UnitNativeId,
        IReadOnlyList<DeviceTemplate> Devices,
        IReadOnlyList<ReadBlock> Blocks,
        IReadOnlyDictionary<string, CommandDefinition> Commands);

    /// <inheritdoc />
    public override async ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
    {
        var baseRes = await base.InitializeAsync(context, ct);
        if (!baseRes.IsSuccess)
        {
            return baseRes;
        }

        var parseRes = ParseSettings();
        if (!parseRes.IsSuccess)
        {
            return parseRes;
        }

        var effectiveTime = _customTimeProvider ?? Context.Time;

        _transport ??= new ModbusTcpClient(
            _host,
            _port,
            rtuOverTcp: _transportKind.Equals("rtuOverTcp", StringComparison.OrdinalIgnoreCase),
            connectTimeout: _connectTimeout,
            requestTimeout: _requestTimeout,
            maxInFlight: _maxInFlight,
            timeProvider: effectiveTime);

        return Result.Success();
    }

    private Result ParseSettings()
    {
        _units.Clear();
        if (Context.Settings.ValueKind != JsonValueKind.Object) return Result.Success();

        var rawJson = Context.Settings.GetRawText();
        if (string.IsNullOrWhiteSpace(rawJson)) return Result.Success();

        var node = JsonNode.Parse(rawJson)?.AsObject();
        if (node is null) return Result.Success();

        if (node["host"] is JsonNode h) _host = h.GetValue<string>();
        if (node["port"] is JsonNode p)
        {
            _port = p.GetValue<int>();
            if (_port <= 0 || _port > 65535)
            {
                return Result.Failure(CommonErrors.ValidationFailed([CommonErrors.OutOfRangeAt("/port", 1, 65535)]));
            }
        }
        if (node["transport"] is JsonNode t) _transportKind = t.GetValue<string>();
        if (node["connectTimeoutMs"] is JsonNode ct) _connectTimeout = TimeSpan.FromMilliseconds(ct.GetValue<int>());
        if (node["requestTimeoutMs"] is JsonNode rt) _requestTimeout = TimeSpan.FromMilliseconds(rt.GetValue<int>());
        if (node["maxInFlight"] is JsonNode mif) _maxInFlight = mif.GetValue<int>();

        if (node["units"] is JsonArray uArr)
        {
            foreach (var u in uArr)
            {
                if (u is not JsonObject uObj) continue;
                var unitId = (byte)(uObj["unitId"]?.GetValue<int>() ?? 1);
                var tplPath = uObj["template"]?.GetValue<string>() ?? "";
                var offset = uObj["offset"]?.GetValue<int>() ?? 0;
                var prefix = uObj["prefix"]?.GetValue<string>() ?? "";
                var maxGap = uObj["maxGap"]?.GetValue<int>() ?? 10;
                var maxRegs = uObj["maxRegisters"]?.GetValue<int>() ?? 120;

                Dictionary<string, string>? customNames = null;
                if (uObj["customNames"] is JsonObject cnObj)
                {
                    customNames = cnObj.ToDictionary(x => x.Key, x => x.Value!.GetValue<string>());
                }

                HashSet<string>? exclude = null;
                if (uObj["exclude"] is JsonArray exArr)
                {
                    exclude = new HashSet<string>(exArr.Select(x => x!.GetValue<string>()));
                }

                string templateText;
                try
                {
                    templateText = LoadTemplateFile(tplPath);
                }
                catch (Exception)
                {
                    return Result.Failure(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt("/units", "Template not found: " + tplPath)]));
                }

                var template = TemplateLoader.ParseTemplate(templateText);
                var expanded = TemplateLoader.ExpandTemplate(template, offset, prefix, customNames, exclude);

                var unitNativeId = $"unit-{unitId}";
                var allPoints = new List<PointDefinition>();
                var devices = new List<DeviceTemplate>();
                var commands = new Dictionary<string, CommandDefinition>();

                foreach (var (dev, devPoints) in expanded)
                {
                    devices.Add(dev);
                    allPoints.AddRange(devPoints);
                    if (dev.Commands is not null)
                    {
                        foreach (var (cmdType, cmdDef) in dev.Commands)
                        {
                            commands[$"{dev.Key}:{cmdType}"] = cmdDef;
                        }
                    }
                }

                var blocks = ReadPlanner.PlanReads(allPoints, maxGap, maxRegs);
                _units.Add(new UnitExecutionPlan(unitId, unitNativeId, devices, blocks, commands));
            }
        }

        return Result.Success();
    }

    private static string LoadTemplateFile(string templatePath)
    {
        if (File.Exists(templatePath))
        {
            return File.ReadAllText(templatePath);
        }

        var candidate = Path.Combine(AppContext.BaseDirectory, templatePath);
        if (File.Exists(candidate))
        {
            return File.ReadAllText(candidate);
        }

        var candidateTemplates = Path.Combine(AppContext.BaseDirectory, "templates", Path.GetFileName(templatePath));
        if (File.Exists(candidateTemplates))
        {
            return File.ReadAllText(candidateTemplates);
        }

        throw new FileNotFoundException($"Template file not found: {templatePath}");
    }

    /// <inheritdoc />
    public override async ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            if (_transport is not null && !_transport.IsConnected)
            {
                await _transport.ConnectAsync(ct);
            }
            return ConnectionTestResult.Succeeded(new ServerIdentity("ModbusServer", "1.0", "MODBUS-001", new Dictionary<string, string>()));
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Failed(FailureCategory.Unreachable, ConnectorMessage.Of("connectionLost", ("reason", ex.Message)));
        }
    }

    /// <inheritdoc />
    public override ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct)
    {
        ParseSettings();
        var servers = new List<InventoryServerItem>();
        var devices = new List<InventoryDeviceItem>();

        servers.Add(new InventoryServerItem(
            NativeId: "gateway",
            ParentNativeId: null,
            TypeId: "modbus.server",
            TypeVersion: "1.0.0",
            VendorName: $"Modbus Server ({_host}:{_port})",
            VendorAttributes: new Dictionary<string, WpValue>()));

        foreach (var unit in _units)
        {
            servers.Add(new InventoryServerItem(
                NativeId: unit.UnitNativeId,
                ParentNativeId: "gateway",
                TypeId: "modbus.unit",
                TypeVersion: "1.0.0",
                VendorName: $"Modbus Unit {unit.UnitId}",
                VendorAttributes: new Dictionary<string, WpValue>()));

            foreach (var dev in unit.Devices)
            {
                devices.Add(new InventoryDeviceItem(
                    NativeId: dev.Key,
                    ServerNativeId: unit.UnitNativeId,
                    ParentNativeId: null,
                    TypeId: dev.Type,
                    TypeVersion: "1.0.0",
                    VendorName: dev.Name is not null && dev.Name.TryGetValue("en", out var enName) ? enName : dev.Key,
                    VendorAttributes: new Dictionary<string, WpValue>(),
                    UnsupportedOptionalCapabilities: [],
                    Links: []));
            }
        }

        return ValueTask.FromResult(new InventorySnapshot(servers, devices, IsComplete: true));
    }

    /// <inheritdoc />
    public ValueTask<InventorySnapshot> DiscoverAsync(IProgress<DiscoveryProgress> progress, CancellationToken ct)
    {
        progress.Report(new DiscoveryProgress(1, 1, ConnectorMessage.Of("discovering")));
        return DescribeAsync(ct);
    }

    /// <inheritdoc />
    protected override async ValueTask ConnectOnceAsync(CancellationToken ct)
    {
        var effectiveTime = _customTimeProvider ?? Context.Time;
        ModbusLog.Connecting(Logger, _host, _port, _transportKind);
        await _transport!.ConnectAsync(ct);
        ModbusLog.Connected(Logger, _host, _port);
    }

    /// <inheritdoc />
    protected override async ValueTask RunConnectedAsync(CancellationToken ct)
    {
        var effectiveTime = _customTimeProvider ?? Context.Time;

        _pollingLoop = new PollingLoop(
            "modbus-poller",
            TimeSpan.FromMilliseconds(500),
            PollAllUnitsAsync,
            Context.Sink,
            effectiveTime);

        try
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetResult()))
            {
                await tcs.Task;
            }
        }
        finally
        {
            if (_pollingLoop is not null)
            {
                _pollingLoop.Dispose();
                _pollingLoop = null;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask RefreshAsync(RefreshScope scope, CancellationToken ct)
    {
        return PollAllUnitsAsync(ct);
    }

    private async ValueTask PollAllUnitsAsync(CancellationToken cancellationToken)
    {
        var effectiveTime = _customTimeProvider ?? Context.Time;

        if (_transport is not null && !_transport.IsConnected)
        {
            await _transport.ConnectAsync(cancellationToken);
        }

        foreach (var unit in _units)
        {
            var totalPolled = 0;

            foreach (var block in unit.Blocks)
            {
                var reqPdu = CreateReadPdu(block.Table, block.StartingAddress, block.Quantity);
                ModbusPdu respPdu;
                try
                {
                    respPdu = await _transport!.SendAsync(unit.UnitId, reqPdu, cancellationToken);
                }
                catch (Exception ex)
                {
                    ModbusLog.UnitOffline(Logger, unit.UnitId, ex.Message);
                    continue;
                }

                if (respPdu.IsError)
                {
                    continue;
                }

                var data = respPdu.Data;
                if (data.Length < 1) continue;
                var payloadBytes = data.Slice(1);
                var payloadSpan = payloadBytes.Span;

                var deviceUpdates = new Dictionary<string, Dictionary<string, object?>>();

                foreach (var pt in block.Points)
                {
                    var offset = pt.Address - block.StartingAddress;
                    object decodedVal;

                    if (block.Table == ModbusTable.Coil || block.Table == ModbusTable.DiscreteInput)
                    {
                        var byteIdx = offset / 8;
                        var bitIdx = offset % 8;
                        var bitVal = byteIdx < payloadSpan.Length && (payloadSpan[byteIdx] & (1 << bitIdx)) != 0;
                        decodedVal = bitVal;
                    }
                    else
                    {
                        var byteOffset = offset * 2;
                        var requiredBytes = pt.RegisterCount * 2;
                        if (byteOffset + requiredBytes <= payloadSpan.Length)
                        {
                            var ptSpan = payloadSpan.Slice(byteOffset, requiredBytes);
                            decodedVal = DataTypes.Decode(pt.DataType, ptSpan, pt.WordOrder, pt.ByteOrder, pt.Bit, pt.Scale, pt.Offset, pt.EnumMap);
                        }
                        else
                        {
                            continue;
                        }
                    }

                    var targetDeviceKey = pt.DeviceKey ?? (unit.Devices.Count > 0 ? unit.Devices[0].Key : null);
                    if (targetDeviceKey is not null)
                    {
                        if (!deviceUpdates.TryGetValue(targetDeviceKey, out var props))
                        {
                            props = new Dictionary<string, object?>();
                            deviceUpdates[targetDeviceKey] = props;
                        }
                        props[pt.Property] = decodedVal;
                    }

                    totalPolled++;
                }

                foreach (var (devId, props) in deviceUpdates)
                {
                    if (Context != null)
                    {
                        var key = new DeviceKey(unit.UnitNativeId, devId);
                        foreach (var kvp in props)
                        {
                            var wpVal = ToWpValue(kvp.Value);
                            Context.Sink.ReportProperty(key, kvp.Key, wpVal, Quality.Good, effectiveTime.GetUtcNow());
                        }
                    }
                }
            }

            ModbusLog.UnitPolled(Logger, unit.UnitId, totalPolled);
        }

        if (Context != null)
        {
            await Context.Sink.FlushAsync(cancellationToken);
        }
    }

    private static WpValue ToWpValue(object? value)
    {
        return value switch
        {
            null => WpValue.Null,
            bool b => WpValue.Bool(b),
            short s => WpValue.Int(s),
            ushort us => WpValue.Int(us),
            int i => WpValue.Int(i),
            uint u => WpValue.Int(u),
            long l => WpValue.Int(l),
            ulong ul => WpValue.Int((long)ul),
            float f => WpValue.Decimal((decimal)f),
            double d => WpValue.Decimal((decimal)d),
            decimal dec => WpValue.Decimal(dec),
            string s => WpValue.String(s),
            _ => WpValue.String(value.ToString() ?? string.Empty)
        };
    }

    private static ModbusPdu CreateReadPdu(ModbusTable table, ushort startingAddress, ushort quantity)
    {
        var fc = table switch
        {
            ModbusTable.Coil => FunctionCodes.ReadCoils,
            ModbusTable.DiscreteInput => FunctionCodes.ReadDiscreteInputs,
            ModbusTable.Holding => FunctionCodes.ReadHoldingRegisters,
            ModbusTable.Input => FunctionCodes.ReadInputRegisters,
            _ => throw new ArgumentOutOfRangeException(nameof(table))
        };
        return ModbusPdu.BuildReadRequest(fc, startingAddress, quantity);
    }

    /// <inheritdoc />
    public async ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct)
    {
        var targetDevice = command.Target.Key.NativeId;
        var commandType = command.Command;

        UnitExecutionPlan? targetUnit = null;
        CommandDefinition? cmdDef = null;

        foreach (var unit in _units)
        {
            if (unit.Commands.TryGetValue($"{targetDevice}:{commandType}", out var cd))
            {
                targetUnit = unit;
                cmdDef = cd;
                break;
            }
        }

        if (targetUnit is null || cmdDef is null)
        {
            return ConnectorCommandResult.NotSupported();
        }

        var effectiveTime = _customTimeProvider ?? Context.Time;

        try
        {
            if (_transport is not null && !_transport.IsConnected)
            {
                await _transport.ConnectAsync(ct);
            }

            await CommandMapper.ExecuteCommandAsync(
                _transport!,
                targetUnit.UnitId,
                cmdDef,
                command,
                readBack: async (tbl, addr, qty) =>
                {
                    var pdu = CreateReadPdu(tbl, addr, qty);
                    var resp = await _transport!.SendAsync(targetUnit.UnitId, pdu, ct);
                    return resp.Data.Length > 1 ? resp.Data.Slice(1) : resp.Data;
                },
                timeProvider: effectiveTime,
                cancellationToken: ct);

            if (Logger.IsEnabled(LogLevel.Information))
            {
                var cmdIdStr = command.CommandId.ToString();
                ModbusLog.CommandExecuted(Logger, cmdIdStr, commandType, targetUnit.UnitId);
            }
            return ConnectorCommandResult.Succeeded();
        }
        catch (ModbusException ex)
        {
            return ConnectorCommandResult.Failed(FailureCategory.RefusedBySystem, ConnectorMessage.Of("errors.refused", ("reason", ex.Message)));
        }
        catch (TimeoutException ex)
        {
            return ConnectorCommandResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("errors.timeout", ("reason", ex.Message)));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested || effectiveTime.GetUtcNow() >= command.Deadline)
        {
            return ConnectorCommandResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("errors.timeout", ("reason", ex.Message)));
        }
        catch (Exception ex)
        {
            if (Logger.IsEnabled(LogLevel.Error))
            {
                var cmdIdStr = command.CommandId.ToString();
                ModbusLog.CommandFailed(Logger, cmdIdStr, commandType, targetUnit.UnitId, ex.Message, ex);
            }
            return ConnectorCommandResult.Failed(FailureCategory.InternalError, ConnectorMessage.Of("errors.commandFailed", ("reason", ex.Message)));
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisconnectAsync(CancellationToken ct)
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }
    }
}
