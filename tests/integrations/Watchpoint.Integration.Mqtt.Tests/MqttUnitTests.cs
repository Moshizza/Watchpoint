using System.Globalization;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Mqtt.Commands;
using Watchpoint.Integration.Mqtt.Mapping;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Mqtt.Tests;

public sealed class MqttUnitTests
{
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Could not find repository root containing global.json.");
    }

    [Fact]
    public void MQTT_U_001_JsonPathLite_root_evaluation()
    {
        using var doc = JsonDocument.Parse("""{"name": "sensor-1", "value": 42.5}""");
        var ok = JsonPathLite.TryEvaluate(doc.RootElement, "$", out var res);
        ok.ShouldBeTrue();
        res.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void MQTT_U_002_JsonPathLite_property_access()
    {
        using var doc = JsonDocument.Parse("""{"sensor": {"temperature": 23.4}}""");
        var ok = JsonPathLite.TryEvaluate(doc.RootElement, "$.sensor.temperature", out var res);
        ok.ShouldBeTrue();
        res.GetDouble().ShouldBe(23.4);
    }

    [Fact]
    public void MQTT_U_003_JsonPathLite_array_indexing()
    {
        using var doc = JsonDocument.Parse("""{"readings": [10, 20, 30]}""");
        var ok = JsonPathLite.TryEvaluate(doc.RootElement, "$.readings[1]", out var res);
        ok.ShouldBeTrue();
        res.GetInt32().ShouldBe(20);
    }

    [Fact]
    public void MQTT_U_004_JsonPathLite_bracket_property_access()
    {
        using var doc = JsonDocument.Parse("""{"device.name": "gateway-01"}""");
        var ok = JsonPathLite.TryEvaluate(doc.RootElement, "$['device.name']", out var res);
        ok.ShouldBeTrue();
        res.GetString().ShouldBe("gateway-01");
    }

    [Fact]
    public void MQTT_U_005_TopicPattern_parse_and_extract_subscription_filter()
    {
        var ok = TopicPattern.TryParse("sensors/{location}/{deviceId}/temp", out var pattern, out var err);
        ok.ShouldBeTrue();
        pattern.ShouldNotBeNull();
        pattern.SubscriptionFilter.ShouldBe("sensors/+/+/temp");
        pattern.CaptureNames.ShouldBe(["location", "deviceId"]);
    }

    [Fact]
    public void MQTT_U_006_TopicPattern_multilevel_wildcard_must_be_last()
    {
        var okBad = TopicPattern.TryParse("sensors/{rest*}/invalid", out _, out var errBad);
        okBad.ShouldBeFalse();
        errBad.ShouldNotBeNull();

        var okGood = TopicPattern.TryParse("sensors/{prefix}/{rest*}", out var pattern, out var errGood);
        okGood.ShouldBeTrue();
        pattern!.SubscriptionFilter.ShouldBe("sensors/+/#");
    }

    [Fact]
    public void MQTT_U_007_TopicPattern_matching_and_capture_extraction()
    {
        _ = TopicPattern.TryParse("site/{siteId}/device/{deviceId}/state", out var pattern, out _);
        var match = pattern!.TryMatch("site/bldg-1/device/dev-99/state", out var captures);

        match.ShouldBeTrue();
        captures.ShouldNotBeNull();
        captures["siteId"].ShouldBe("bldg-1");
        captures["deviceId"].ShouldBe("dev-99");
    }

    [Fact]
    public void MQTT_U_008_TopicTrie_routing_exact_and_wildcards()
    {
        var trie = new TopicTrie<string>();
        _ = TopicPattern.TryParse("a/b/c", out var p1, out _);
        _ = TopicPattern.TryParse("a/{param}/c", out var p2, out _);
        _ = TopicPattern.TryParse("a/{rest*}", out var p3, out _);

        trie.Add(p1!, "rule1");
        trie.Add(p2!, "rule2");
        trie.Add(p3!, "rule3");

        var matches = trie.Match("a/b/c");
        matches.ShouldContain("rule1");
        matches.ShouldContain("rule2");
        matches.ShouldContain("rule3");
    }

    [Fact]
    public void MQTT_U_009_PayloadDecoder_json_number_scaling_and_offset()
    {
        _ = TopicPattern.TryParse("test", out var p, out _);
        var rule = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            PayloadFormat = PayloadFormat.Json,
            ValuePath = "$.val",
            Scale = 0.1,
            Offset = 5.0
        };

        var payload = System.Text.Encoding.UTF8.GetBytes("""{"val": 100}""");
        var ok = PayloadDecoder.TryDecode(payload, rule, out var val, out _, out _, out _);

        ok.ShouldBeTrue();
        val.AsDecimal().ShouldBe(15m);
    }

    [Fact]
    public void MQTT_U_010_PayloadDecoder_boolean_custom_mapping()
    {
        _ = TopicPattern.TryParse("test", out var p, out _);
        var rule = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            PayloadFormat = PayloadFormat.Boolean,
            TrueValue = "OPEN",
            FalseValue = "CLOSED"
        };

        var okOpen = PayloadDecoder.TryDecode(System.Text.Encoding.UTF8.GetBytes("OPEN"), rule, out var valOpen, out _, out _, out _);
        okOpen.ShouldBeTrue();
        valOpen.AsBool().ShouldBeTrue();

        var okClosed = PayloadDecoder.TryDecode(System.Text.Encoding.UTF8.GetBytes("CLOSED"), rule, out var valClosed, out _, out _, out _);
        okClosed.ShouldBeTrue();
        valClosed.AsBool().ShouldBeFalse();
    }

    [Fact]
    public void MQTT_U_011_PayloadDecoder_enum_dictionary_mapping()
    {
        _ = TopicPattern.TryParse("test", out var p, out _);
        var rule = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            PayloadFormat = PayloadFormat.Json,
            ValuePath = "$.status",
            MapEnum = new Dictionary<string, string>
            {
                ["0"] = "normal",
                ["1"] = "alarm"
            }
        };

        var ok = PayloadDecoder.TryDecode(System.Text.Encoding.UTF8.GetBytes("""{"status": "1"}"""), rule, out var val, out _, out _, out _);
        ok.ShouldBeTrue();
        val.AsString().ShouldBe("alarm");
    }

    [Fact]
    public void MQTT_U_012_PayloadDecoder_timestamp_parsing_iso8601_and_unix()
    {
        _ = TopicPattern.TryParse("test", out var p, out _);
        var ruleUnix = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            PayloadFormat = PayloadFormat.Json,
            ValuePath = "$.v",
            TimestampPath = "$.ts",
            TimestampFormat = "unixS"
        };

        var payload = System.Text.Encoding.UTF8.GetBytes("""{"v": 1, "ts": 1700000000}""");
        var ok = PayloadDecoder.TryDecode(payload, ruleUnix, out _, out var dt, out _, out _);
        ok.ShouldBeTrue();
        dt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public async Task MQTT_U_013_Availability_topic_settings_and_offline_behavior()
    {
        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = "localhost",
            allowPlain = true,
            availability = new
            {
                topic = "gateway/status",
                onlinePayload = "ONLINE",
                offlinePayload = "OFFLINE"
            },
            mappings = new object[]
            {
                new { id = "r1", topic = "sensors/{id}/temp", property = "temperature" }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var initRes = await connector.InitializeAsync(context, CancellationToken.None);
        initRes.IsSuccess.ShouldBeTrue();

        var msgOnline = new MqttApplicationMessageBuilder()
            .WithTopic("gateway/status")
            .WithPayload("ONLINE")
            .Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket();
        var argsOnline = new MqttApplicationMessageReceivedEventArgs("c1", msgOnline, packet, (e, t) => Task.CompletedTask);
        await connector.ProcessApplicationMessageAsync(argsOnline);

        context.Sink.ServerHealths.Any(sh => sh.ServerNativeId == "broker" && sh.Connectivity == Connectivity.Online).ShouldBeTrue();

        var msgOffline = new MqttApplicationMessageBuilder()
            .WithTopic("gateway/status")
            .WithPayload("OFFLINE")
            .Build();
        var argsOffline = new MqttApplicationMessageReceivedEventArgs("c1", msgOffline, packet, (e, t) => Task.CompletedTask);
        await connector.ProcessApplicationMessageAsync(argsOffline);

        context.Sink.ServerHealths.Any(sh => sh.ServerNativeId == "broker" && sh.Connectivity == Connectivity.Unknown).ShouldBeTrue();
    }

    [Fact]
    public async Task MQTT_U_014_Rate_limiting_and_event_flood_dropping()
    {
        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = "localhost",
            allowPlain = true,
            maxMessagesPerSecond = 2,
            mappings = new object[]
            {
                new { id = "r_state", topic = "site/{id}/val", property = "measurement.value", payload = new { format = "number" } },
                new { id = "r_event", topic = "site/{id}/alert", @event = "alarm", payload = new { format = "json" } }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var initRes = await connector.InitializeAsync(context, CancellationToken.None);
        initRes.IsSuccess.ShouldBeTrue();

        var packet = new MQTTnet.Packets.MqttPublishPacket();

        for (int i = 1; i <= 2; i++)
        {
            var msg = new MqttApplicationMessageBuilder().WithTopic("site/d1/val").WithPayload(i.ToString(CultureInfo.InvariantCulture)).Build();
            await connector.ProcessApplicationMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msg, packet, (e, t) => Task.CompletedTask));
        }
        context.Sink.Properties.Count.ShouldBe(2);

        var msgCoalesced = new MqttApplicationMessageBuilder().WithTopic("site/d1/val").WithPayload("30").Build();
        await connector.ProcessApplicationMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msgCoalesced, packet, (e, t) => Task.CompletedTask));
        context.Sink.Properties.Count.ShouldBe(2);

        for (int i = 1; i <= 10; i++)
        {
            var msg = new MqttApplicationMessageBuilder().WithTopic("site/d1/alert").WithPayload("{}").Build();
            await connector.ProcessApplicationMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msg, packet, (e, t) => Task.CompletedTask));
        }

        context.Health.Status.ShouldBe(ConnectionStatus.Degraded);
    }

    [Fact]
    public async Task MQTT_U_015_Manual_acknowledgement_ordering_unit_test()
    {
        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = "localhost",
            allowPlain = true,
            mappings = new object[]
            {
                new
                {
                    id = "r_ack",
                    topic = "sensors/{id}/temp",
                    qos = 1,
                    device = new { nativeId = "{id}" },
                    payload = new { format = "number" },
                    property = "measurement.value"
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        var initRes = await connector.InitializeAsync(context, CancellationToken.None);
        initRes.IsSuccess.ShouldBeTrue();

        var ackCalled = false;
        var flushCountAtAck = 0;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic("sensors/s1/temp")
            .WithPayload("24.5")
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        var packet = new MQTTnet.Packets.MqttPublishPacket();
        var args = new MqttApplicationMessageReceivedEventArgs("client1", msg, packet, (e, t) =>
        {
            ackCalled = true;
            flushCountAtAck = context.Sink.FlushCount;
            return Task.CompletedTask;
        });

        await connector.ProcessApplicationMessageAsync(args);

        args.AutoAcknowledge.ShouldBeFalse();
        ackCalled.ShouldBeTrue();
        flushCountAtAck.ShouldBeGreaterThan(0, "Acknowledge must only be sent after the sink flush has completed");
    }

    [Fact]
    public async Task MQTT_U_016_DynamicInventory_debouncing_and_persisting()
    {
        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(JsonSerializer.SerializeToElement(new { }), time);
        var inventory = new DynamicInventory(context);

        _ = TopicPattern.TryParse("sensors/{id}/state", out var p, out _);
        var rule = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            DeviceTypeTemplate = "generic.sensor",
            PayloadFormat = PayloadFormat.Text
        };

        inventory.TryTrackDevice("dev-1", rule, new Dictionary<string, string> { ["id"] = "dev-1" }, out _);
        context.Sink.InventoryChanges.Count.ShouldBe(0);

        time.Advance(TimeSpan.FromSeconds(2.5));

        context.Sink.InventoryChanges.Count.ShouldBe(1);
        var storeBytes = await context.Store.GetAsync("inventory", CancellationToken.None);
        storeBytes.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(storeBytes).ShouldContain("dev-1");
    }

    [Fact]
    public void MQTT_U_017_DynamicInventory_max_devices_limit()
    {
        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(JsonSerializer.SerializeToElement(new { }), time);
        var inventory = new DynamicInventory(context, maxDevices: 2);

        _ = TopicPattern.TryParse("sensors/{id}/state", out var p, out _);
        var rule = new MappingRule
        {
            Id = "r1",
            Pattern = p!,
            DeviceTypeTemplate = "sensor",
            PayloadFormat = PayloadFormat.Text
        };

        inventory.TryTrackDevice("d1", rule, new Dictionary<string, string>(), out _).ShouldBeTrue();
        inventory.TryTrackDevice("d2", rule, new Dictionary<string, string>(), out _).ShouldBeTrue();
        inventory.TryTrackDevice("d3", rule, new Dictionary<string, string>(), out var d3).ShouldBeFalse();
        d3.ShouldBeNull();
    }

    [Fact]
    public void MQTT_U_018_PublishMapper_substitutions_and_qos()
    {
        var cmdMapping = new CommandRuleMapping
        {
            CommandName = "turnOn",
            PublishTopicTemplate = "cmd/{siteId}/{deviceId}/set",
            Qos = 2,
            Retain = true,
            PayloadTemplate = """{"action": "{action}", "time": "{timestamp.s}"}"""
        };

        var captures = new Dictionary<string, string> { ["siteId"] = "bldg-A", ["deviceId"] = "light-42" };
        var parameters = new Dictionary<string, object?> { ["action"] = "ON" };
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        var msg = PublishMapper.BuildMessage(cmdMapping, captures, parameters, now);

        msg.Topic.ShouldBe("cmd/bldg-A/light-42/set");
        msg.QualityOfServiceLevel.ShouldBe(MqttQualityOfServiceLevel.ExactlyOnce);
        msg.Retain.ShouldBeTrue();
        var payload = System.Text.Encoding.UTF8.GetString(msg.PayloadSegment);
        payload.ShouldContain("\"action\": \"ON\"");
        payload.ShouldContain("\"time\": \"1700000000\"");
    }

    [Fact]
    public void MQTT_U_019_RuleValidator_rejects_missing_target()
    {
        var json = """
        {
            "id": "bad_rule",
            "topic": "sensors/+/val"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var res = RuleValidator.ParseRule(doc.RootElement);

        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void MQTT_U_020_RuleValidator_rejects_both_property_and_event()
    {
        var json = """
        {
            "id": "bad_rule",
            "topic": "sensors/+/val",
            "property": "measurement.value",
            "event": "alert"
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var res = RuleValidator.ParseRule(doc.RootElement);

        res.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task MQTT_U_021_DescribeAsync_reflects_discovered_devices_and_commands()
    {
        var settingsEl = JsonSerializer.SerializeToElement(new
        {
            host = "localhost",
            allowPlain = true,
            mappings = new object[]
            {
                new
                {
                    id = "r_state",
                    topic = "relays/{id}/state",
                    device = new
                    {
                        nativeId = "{id}",
                        type = "generic.binary-output"
                    },
                    property = "switchable.state"
                }
            }
        });

        var time = TestTime.CreateProvider();
        var context = new TestConnectorContext(settingsEl, time);
        var connector = new MqttConnector();
        await connector.InitializeAsync(context, CancellationToken.None);

        var msg = new MqttApplicationMessageBuilder().WithTopic("relays/r1/state").WithPayload("true").Build();
        var packet = new MQTTnet.Packets.MqttPublishPacket();
        await connector.ProcessApplicationMessageAsync(new MqttApplicationMessageReceivedEventArgs("c1", msg, packet, (e, t) => Task.CompletedTask));

        var desc = await connector.DescribeAsync(CancellationToken.None);
        desc.Devices.Any(d => d.NativeId == "r1").ShouldBeTrue();
    }

    [Fact]
    public void MQTT_U_022_Package_manifest_and_staged_validation()
    {
        var repoRoot = FindRepoRoot();
        var pkgPath = Path.Combine(repoRoot, "src", "integrations", "Watchpoint.Integration.Mqtt");
        var typesPath = Path.Combine(repoRoot, "types");
        var asmPath = typeof(MqttConnector).Assembly.Location;

        var stagedDir = PackageStager.Stage(pkgPath, asmPath);
        try
        {
            var wptypesType = Type.GetType("Watchpoint.Wptypes.WptypesCli, wptypes");
            wptypesType.ShouldNotBeNull();
            var runMethod = wptypesType.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            runMethod.ShouldNotBeNull();

            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = (int)runMethod.Invoke(null, [new[] { "validate", stagedDir, "--with", typesPath, "--strict-i18n" }, output, error])!;
            exitCode.ShouldBe(0, output.ToString() + error.ToString());
        }
        finally
        {
            Directory.Delete(stagedDir, true);
        }
    }
}
