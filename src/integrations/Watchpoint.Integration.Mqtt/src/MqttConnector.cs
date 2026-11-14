using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Mqtt.Commands;
using Watchpoint.Integration.Mqtt.Mapping;
using Watchpoint.Integration.Sdk;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Mqtt;

public sealed class MqttConnector : ConnectorBase, ICommandExecutor
{
    private readonly IMqttClient? _customClient;
    private MqttSettings _settings = null!;
    private readonly List<MappingRule> _rules = new();
    private readonly TopicTrie<MappingRule> _trie = new();
    private readonly Dictionary<string, (MappingRule Rule, CommandRuleMapping Command)> _commandRules = new(StringComparer.Ordinal);
    private DynamicInventory _inventory = null!;
    private MqttSession? _session;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastMessageTime = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (WpValue Value, DateTimeOffset? Time)> _coalescedProperties = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _messageCountPerSec = new(StringComparer.Ordinal);
    private DateTimeOffset _lastSecWindow = DateTimeOffset.MinValue;
    private int _burstCount;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MqttApplicationMessage>> _pendingResponses = new(StringComparer.Ordinal);

    public MqttConnector(IMqttClient? customClient = null)
    {
        _customClient = customClient;
    }

    public override async ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
    {
        await base.InitializeAsync(context, ct).ConfigureAwait(false);

        var settingsRes = ParseAndValidateSettings(context.Settings);
        if (!settingsRes.IsSuccess) return Result.Failure(settingsRes.Error);

        _settings = settingsRes.Value;
        _rules.Clear();

        if (context.Settings.TryGetProperty("mappings", out var mappingsEl) && mappingsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in mappingsEl.EnumerateArray())
            {
                var ruleRes = RuleValidator.ParseRule(item);
                if (!ruleRes.IsSuccess) return Result.Failure(ruleRes.Error);

                var rule = ruleRes.Value;
                _rules.Add(rule);
                _trie.Add(rule.Pattern, rule);

                if (rule.Command != null)
                {
                    _commandRules[rule.Command.CommandName] = (rule, rule.Command);
                }
            }
        }

        _inventory = new DynamicInventory(context, _settings.MaxDevices);
        _session = new MqttSession(_customClient);

        await _inventory.RestoreAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private static Result<MqttSettings> ParseAndValidateSettings(JsonElement element)
    {
        var errors = new List<FieldError>();

        if (!element.TryGetProperty("host", out var hostEl) || string.IsNullOrWhiteSpace(hostEl.GetString()))
        {
            errors.Add(CommonErrors.RequiredAt("/host"));
        }

        if (!element.TryGetProperty("mappings", out var mapsEl) || mapsEl.ValueKind != JsonValueKind.Array || mapsEl.GetArrayLength() == 0)
        {
            errors.Add(CommonErrors.RequiredAt("/mappings"));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<MqttSettings>(CommonErrors.ValidationFailed(errors));
        }

        var host = hostEl.GetString()!;
        var port = element.TryGetProperty("port", out var pEl) ? pEl.GetInt32() : 1883;
        var protoVer = element.TryGetProperty("protocolVersion", out var pvEl) ? pvEl.GetString()! : "5.0";
        var allowPlain = element.TryGetProperty("allowPlain", out var apEl) && apEl.GetBoolean();

        var tls = new MqttTlsSettings();
        if (element.TryGetProperty("tls", out var tlsEl) && tlsEl.ValueKind == JsonValueKind.Object)
        {
            var en = tlsEl.TryGetProperty("enabled", out var enEl) && enEl.GetBoolean();
            var mode = tlsEl.TryGetProperty("mode", out var mEl) ? mEl.GetString()! : "system";
            var ca = tlsEl.TryGetProperty("customCaCertificate", out var caEl) ? caEl.GetString() : null;
            var pin = tlsEl.TryGetProperty("pinnedCertificateThumbprint", out var pinEl) ? pinEl.GetString() : null;
            tls = new MqttTlsSettings
            {
                Enabled = en,
                Mode = mode,
                CustomCaCertificate = ca,
                PinnedCertificateThumbprint = pin,
            };
        }

        if (!tls.Enabled && !allowPlain)
        {
            return Result.Failure<MqttSettings>(CommonErrors.ValidationFailed([
                CommonErrors.InvalidFormatAt("/allowPlain", "Plain text MQTT connection requires allowPlain=true.")
            ]));
        }

        var clientId = element.TryGetProperty("clientId", out var cIdEl) ? cIdEl.GetString() : null;
        var cleanSession = element.TryGetProperty("cleanSession", out var csEl) && csEl.GetBoolean();
        var keepAlive = element.TryGetProperty("keepAlive", out var kaEl) ? kaEl.GetString()! : "PT30S";

        var auth = new MqttAuthSettings();
        if (element.TryGetProperty("auth", out var authEl) && authEl.ValueKind == JsonValueKind.Object)
        {
            var u = authEl.TryGetProperty("username", out var uEl) ? uEl.GetString() : null;
            var cert = authEl.TryGetProperty("clientCertificate", out var cEl) && cEl.GetBoolean();
            auth = new MqttAuthSettings { Username = u, ClientCertificate = cert };
        }

        MqttAvailabilitySettings? avail = null;
        if (element.TryGetProperty("availability", out var avEl) && avEl.ValueKind == JsonValueKind.Object)
        {
            avail = new MqttAvailabilitySettings
            {
                Topic = avEl.GetProperty("topic").GetString()!,
                OnlinePayload = avEl.GetProperty("onlinePayload").GetString()!,
                OfflinePayload = avEl.GetProperty("offlinePayload").GetString()!,
            };
        }

        var maxMsgs = element.TryGetProperty("maxMessagesPerSecond", out var mmEl) ? mmEl.GetInt32() : 5000;
        var maxDevices = element.TryGetProperty("maxDevices", out var mdEl) ? mdEl.GetInt32() : 10000;

        TimeSpan? staleAfter = null;
        if (element.TryGetProperty("staleAfter", out var saEl) && saEl.GetString() is { } saStr && saStr.StartsWith("PT", StringComparison.Ordinal) && int.TryParse(saStr[2..^1], CultureInfo.InvariantCulture, out var saSec))
        {
            staleAfter = TimeSpan.FromSeconds(saSec);
        }

        TimeSpan? devOffAfter = null;
        if (element.TryGetProperty("deviceOfflineAfter", out var doaEl) && doaEl.GetString() is { } doaStr && doaStr.StartsWith("PT", StringComparison.Ordinal) && int.TryParse(doaStr[2..^1], CultureInfo.InvariantCulture, out var doaSec))
        {
            devOffAfter = TimeSpan.FromSeconds(doaSec);
        }

        return Result.Success(new MqttSettings
        {
            Host = host,
            Port = port,
            ProtocolVersion = protoVer,
            AllowPlain = allowPlain,
            Tls = tls,
            ClientId = clientId,
            CleanSession = cleanSession,
            KeepAlive = keepAlive,
            Auth = auth,
            Availability = avail,
            MaxMessagesPerSecond = maxMsgs,
            MaxDevices = maxDevices,
            StaleAfter = staleAfter,
            DeviceOfflineAfter = devOffAfter,
        });
    }

    public override async ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct)
    {
        try
        {
            var client = new MqttFactory().CreateMqttClient();
            var testClientId = (_settings.ClientId ?? $"watchpoint-{Context.Connection.Id:N}") + "-test";
            var (password, clientCert, clientKey) = await ResolveSecretsAsync(ct).ConfigureAwait(false);

            var options = MqttSession.BuildClientOptions(
                _settings with { CleanSession = true },
                testClientId,
                password,
                clientCert,
                clientKey);

            var connResult = await client.ConnectAsync(options, ct).ConfigureAwait(false);

            if (connResult.ResultCode == MqttClientConnectResultCode.Success)
            {
                var filters = _rules.Select(r => new MqttTopicFilter
                {
                    Topic = r.Pattern.SubscriptionFilter,
                    QualityOfServiceLevel = (MqttQualityOfServiceLevel)r.Qos,
                }).ToList();

                if (filters.Count > 0)
                {
                    await client.SubscribeAsync(new MqttClientSubscribeOptions
                    {
                        TopicFilters = filters,
                    }, ct).ConfigureAwait(false);
                }

                await client.DisconnectAsync(new MqttClientDisconnectOptions(), ct).ConfigureAwait(false);

                return ConnectionTestResult.Succeeded(new ServerIdentity(
                    Model: "MQTTnet",
                    Version: _settings.ProtocolVersion,
                    Serial: null,
                    Extra: new Dictionary<string, string>()));
            }

            return ConnectionTestResult.Failed(
                FailureCategory.AuthenticationFailed,
                ConnectorMessage.Of("mqtt.connect_failed", ("reason", connResult.ReasonString ?? connResult.ResultCode.ToString())));
        }
        catch (AuthenticationException ex)
        {
            return ConnectionTestResult.Failed(FailureCategory.TlsError, ConnectorMessage.Of("mqtt.tls_error", ("reason", ex.Message)));
        }
        catch (SocketException ex)
        {
            return ConnectionTestResult.Failed(FailureCategory.Unreachable, ConnectorMessage.Of("mqtt.unreachable", ("reason", ex.Message)));
        }
        catch (MQTTnet.Exceptions.MqttCommunicationException ex) when (ex.InnerException is SocketException)
        {
            return ConnectionTestResult.Failed(FailureCategory.Unreachable, ConnectorMessage.Of("mqtt.unreachable", ("reason", ex.InnerException.Message)));
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Failed(FailureCategory.ConnectionDown, ConnectorMessage.Of("mqtt.error", ("reason", ex.Message)));
        }
    }

    public override ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct)
    {
        var devices = _inventory.GetAllDevices();
        return ValueTask.FromResult(new InventorySnapshot(
            Servers: [],
            Devices: devices,
            IsComplete: false));
    }

    protected override (FailureCategory Category, ConnectorMessage Message) MapFailure(Exception exception)
    {
        if (exception is MQTTnet.Exceptions.MqttCommunicationException commEx && commEx.InnerException is not null)
        {
            return base.MapFailure(commEx.InnerException);
        }

        return base.MapFailure(exception);
    }

    protected override async ValueTask ConnectOnceAsync(CancellationToken ct)
    {
        var clientId = _settings.ClientId ?? $"watchpoint-{Context.Connection.Id:N}";
        var (password, clientCert, clientKey) = await ResolveSecretsAsync(ct).ConfigureAwait(false);

        var options = MqttSession.BuildClientOptions(
            _settings,
            clientId,
            password,
            clientCert,
            clientKey);

        _session!.Client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;

        var result = await _session.Client.ConnectAsync(options, ct).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException($"MQTT connection failed: {result.ResultCode}");
        }

        MqttLog.MqttConnected(Logger, _settings.Host, _settings.Port, _settings.ProtocolVersion, result.IsSessionPresent);

        var filters = _rules.Select(r => new MqttTopicFilter
        {
            Topic = r.Pattern.SubscriptionFilter,
            QualityOfServiceLevel = (MqttQualityOfServiceLevel)r.Qos,
        }).ToList();

        if (_settings.Availability != null)
        {
            filters.Add(new MqttTopicFilter
            {
                Topic = _settings.Availability.Topic,
                QualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce,
            });
        }

        if (filters.Count > 0)
        {
            var subOptions = new MqttClientSubscribeOptions
            {
                TopicFilters = filters,
            };
            await _session.Client.SubscribeAsync(subOptions, ct).ConfigureAwait(false);
            var topicSummary = filters.Count.ToString(CultureInfo.InvariantCulture);
            MqttLog.MqttSubscribed(Logger, topicSummary);
        }

        _inventory.ReportBrokerServer("broker", _settings.Host, _settings.Port);
        Context.Sink.ReportServerHealth("broker", Connectivity.Online);
    }

    protected override async ValueTask RunConnectedAsync(CancellationToken ct)
    {
        var timer = Context.Time.CreateTimer(
            _ => CheckStaleDevices(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        try
        {
            while (!ct.IsCancellationRequested && _session!.Client.IsConnected)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), Context.Time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
            Context.Sink.ReportServerHealth("broker", Connectivity.Unknown);
        }
    }

    protected override async ValueTask DisconnectAsync(CancellationToken ct)
    {
        if (_session?.Client != null)
        {
            _session.Client.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;
            if (_session.Client.IsConnected)
            {
                try
                {
                    await _session.Client.DisconnectAsync(new MqttClientDisconnectOptions(), ct).ConfigureAwait(false);
                }
                catch (MQTTnet.Exceptions.MqttCommunicationException)
                {
                    // The broker dropped the socket between the IsConnected check and the DISCONNECT packet (broken
                    // pipe): the session is already gone, which is what stopping wants (INT-C-007).
                }
            }
        }
    }

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        return ProcessApplicationMessageAsync(e);
    }

    internal async Task ProcessApplicationMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        e.AutoAcknowledge = false;

        var topic = e.ApplicationMessage.Topic;
        var payloadBytes = e.ApplicationMessage.PayloadSegment.ToArray();

        if (e.ApplicationMessage.CorrelationData is { Length: > 0 } corrBytes)
        {
            var corrStr = System.Text.Encoding.UTF8.GetString(corrBytes);
            if (_pendingResponses.TryRemove(corrStr, out var tcs))
            {
                tcs.TrySetResult(e.ApplicationMessage);
            }
        }

        if (_settings.Availability != null && string.Equals(topic, _settings.Availability.Topic, StringComparison.Ordinal))
        {
            var text = System.Text.Encoding.UTF8.GetString(payloadBytes).Trim();
            if (string.Equals(text, _settings.Availability.OnlinePayload, StringComparison.OrdinalIgnoreCase))
            {
                Context.Sink.ReportServerHealth("broker", Connectivity.Online);
            }
            else if (string.Equals(text, _settings.Availability.OfflinePayload, StringComparison.OrdinalIgnoreCase))
            {
                Context.Sink.ReportServerHealth("broker", Connectivity.Unknown, reason: "MQTT availability offline payload received");
            }

            await FlushAndAckAsync(e).ConfigureAwait(false);
            return;
        }

        var matchingRules = _trie.Match(topic);
        if (matchingRules.Count == 0)
        {
            await FlushAndAckAsync(e).ConfigureAwait(false);
            return;
        }

        var now = Context.Time.GetUtcNow();

        if (!CheckRateLimit(topic, now))
        {
            MqttLog.MqttRateLimitExceeded(Logger, topic);

            foreach (var rule in matchingRules)
            {
                if (rule.PropertyPath != null && rule.Pattern.TryMatch(topic, out var caps))
                {
                    var nativeId = !string.IsNullOrEmpty(rule.DeviceNativeIdTemplate)
                        ? ValueMapper.Substitute(rule.DeviceNativeIdTemplate, caps!)
                        : caps!.Values.FirstOrDefault() ?? "unknown";

                    if (PayloadDecoder.TryDecode(payloadBytes, rule, out var val, out var srcTime, out _, out _))
                    {
                        var key = $"{nativeId}:{rule.PropertyPath}";
                        _coalescedProperties[key] = (val, srcTime);
                    }
                }
            }

            await FlushAndAckAsync(e).ConfigureAwait(false);
            return;
        }

        foreach (var rule in matchingRules)
        {
            if (!rule.Pattern.TryMatch(topic, out var captures)) continue;

            var nativeId = !string.IsNullOrEmpty(rule.DeviceNativeIdTemplate)
                ? ValueMapper.Substitute(rule.DeviceNativeIdTemplate, captures!)
                : (captures!.Values.FirstOrDefault() ?? "unknown");

            if (!_inventory.TryTrackDevice(nativeId, rule, captures!, out var devItem) && devItem == null)
            {
                MqttLog.MqttMaxDevicesExceeded(Logger, nativeId);
                continue;
            }

            var devKey = new DeviceKey("broker", nativeId);
            _lastMessageTime[nativeId] = now;

            if (!PayloadDecoder.TryDecode(payloadBytes, rule, out var val, out var sourceTime, out var evtPayload, out var decodeError))
            {
                MqttLog.MqttPayloadDecodeFailed(Logger, topic, decodeError ?? "Decode failed");
                continue;
            }

            if (!string.IsNullOrEmpty(rule.PropertyPath))
            {
                Context.Sink.ReportProperty(
                    devKey,
                    rule.PropertyPath,
                    val,
                    Quality.Good,
                    sourceTime,
                    rule.Unit);
            }
            else if (!string.IsNullOrEmpty(rule.EventPath))
            {
                Context.Sink.ReportEvent(
                    devKey,
                    rule.EventPath,
                    evtPayload,
                    sourceTime);
            }
        }

        await FlushAndAckAsync(e).ConfigureAwait(false);
    }

    private async Task FlushAndAckAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        try
        {
            await Context.Sink.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (e.ApplicationMessage.QualityOfServiceLevel > MqttQualityOfServiceLevel.AtMostOnce)
            {
                await e.AcknowledgeAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private bool CheckRateLimit(string topic, DateTimeOffset now)
    {
        if (now - _lastSecWindow >= TimeSpan.FromSeconds(1))
        {
            _lastSecWindow = now;
            _messageCountPerSec.Clear();
            _burstCount = 0;
            FlushCoalescedProperties();
        }

        _burstCount++;
        if (_burstCount > _settings.MaxMessagesPerSecond * 3)
        {
            MqttLog.MqttEventFloodDropped(Logger, topic);
            Context.Health.Report(ConnectionStatus.Degraded, ConnectorMessage.Of("mqtt.flood_detected", ("topic", topic)));
            return false;
        }

        var current = _messageCountPerSec.AddOrUpdate(topic, 1, (_, count) => count + 1);
        return current <= _settings.MaxMessagesPerSecond;
    }

    private void FlushCoalescedProperties()
    {
        foreach (var kvp in _coalescedProperties)
        {
            if (_coalescedProperties.TryRemove(kvp.Key, out var item))
            {
                var parts = kvp.Key.Split(':', 2);
                if (parts.Length == 2)
                {
                    var nativeId = parts[0];
                    var propPath = parts[1];
                    var devKey = new DeviceKey("broker", nativeId);
                    Context.Sink.ReportProperty(devKey, propPath, item.Value, Quality.Good, item.Time);
                }
            }
        }
    }

    private void CheckStaleDevices()
    {
        var now = Context.Time.GetUtcNow();

        if (_settings.DeviceOfflineAfter.HasValue)
        {
            foreach (var kvp in _lastMessageTime)
            {
                if (now - kvp.Value > _settings.DeviceOfflineAfter.Value)
                {
                    var devKey = new DeviceKey("broker", kvp.Key);
                    Context.Sink.ReportDeviceHealth(devKey, Connectivity.Offline, reason: "Device offline timeout exceeded");
                }
            }
        }
    }

    public async ValueTask<ConnectorCommandResult> ExecuteAsync(
        ConnectorCommand command,
        CancellationToken ct)
    {
        if (!_commandRules.TryGetValue(command.Command, out var entry))
        {
            return ConnectorCommandResult.NotSupported();
        }

        var (rule, cmdMapping) = entry;
        var now = Context.Time.GetUtcNow();
        var captures = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deviceId"] = command.Target.Key.NativeId,
            ["nativeId"] = command.Target.Key.NativeId
        };

        var parameters = command.Arguments.ToDictionary(k => k.Key, v => (object?)v.Value.ToString());
        var message = PublishMapper.BuildMessage(cmdMapping, captures, parameters, now);

        if (!string.IsNullOrEmpty(cmdMapping.ResponseTopicTemplate) && message.CorrelationData is { Length: > 0 } corrBytes)
        {
            var corrStr = System.Text.Encoding.UTF8.GetString(corrBytes);
            var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponses[corrStr] = tcs;

            await _session!.Client.PublishAsync(message, ct).ConfigureAwait(false);

            try
            {
                using var timeoutCts = new CancellationTokenSource(cmdMapping.Timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                var resp = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);

                return ConnectorCommandResult.Succeeded();
            }
            catch (OperationCanceledException)
            {
                _pendingResponses.TryRemove(corrStr, out _);
                return ConnectorCommandResult.Failed(FailureCategory.Timeout, ConnectorMessage.Of("mqtt.command_timeout"));
            }
        }
        else
        {
            await _session!.Client.PublishAsync(message, ct).ConfigureAwait(false);
            return ConnectorCommandResult.Succeeded();
        }
    }

    private async Task<(string? Password, X509Certificate2? ClientCert, string? ClientKey)> ResolveSecretsAsync(CancellationToken ct)
    {
        string? password = null;
        using var pwdSecret = await Context.Secrets.GetAsync("password", ct).ConfigureAwait(false);
        if (pwdSecret != null)
        {
            password = pwdSecret.UseAsString(s => s);
        }

        X509Certificate2? clientCert = null;
        string? clientKey = null;

        using var certSecret = await Context.Secrets.GetAsync("clientCertificate", ct).ConfigureAwait(false);
        if (certSecret != null)
        {
            var certPem = certSecret.UseAsString(s => s);
            using var keySecret = await Context.Secrets.GetAsync("clientKey", ct).ConfigureAwait(false);
            if (keySecret != null)
            {
                clientKey = keySecret.UseAsString(s => s);
            }

            clientCert = clientKey != null
                ? X509Certificate2.CreateFromPem(certPem, clientKey)
                : X509Certificate2.CreateFromPem(certPem);
        }

        return (password, clientCert, clientKey);
    }
}
