using System.Globalization;
using System.Text.Json;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integration.Mqtt.Mapping;

public static class RuleValidator
{
    public static Result<MappingRule> ParseRule(JsonElement el)
    {
        var errors = new List<FieldError>();

        if (!el.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
        {
            errors.Add(CommonErrors.RequiredAt("/id"));
        }

        if (!el.TryGetProperty("topic", out var topicEl) || string.IsNullOrWhiteSpace(topicEl.GetString()))
        {
            errors.Add(CommonErrors.RequiredAt("/topic"));
        }

        var propPath = el.TryGetProperty("property", out var pEl) ? pEl.GetString() : null;
        var eventPath = el.TryGetProperty("event", out var eEl) ? eEl.GetString() : null;
        var cmdEl = el.TryGetProperty("command", out var cEl) ? cEl : default;

        var targetCount = (propPath != null ? 1 : 0) + (eventPath != null ? 1 : 0) + (cmdEl.ValueKind == JsonValueKind.Object ? 1 : 0);
        if (targetCount != 1)
        {
            errors.Add(CommonErrors.InvalidFormatAt("/rule", "Must declare exactly one of 'property', 'event', or 'command'."));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<MappingRule>(CommonErrors.ValidationFailed(errors));
        }

        var id = idEl.GetString()!;
        var topic = topicEl.GetString()!;

        if (!TopicPattern.TryParse(topic, out var pattern, out var patternError))
        {
            return Result.Failure<MappingRule>(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt("/topic", patternError!)]));
        }

        var qos = el.TryGetProperty("qos", out var qEl) ? qEl.GetInt32() : 1;
        if (qos < 0 || qos > 2)
        {
            return Result.Failure<MappingRule>(CommonErrors.ValidationFailed([CommonErrors.InvalidFormatAt("/qos", "QoS must be 0, 1, or 2.")]));
        }

        string? devNativeId = null;
        string? devType = null;
        string? devName = null;
        string? devLocation = null;

        if (el.TryGetProperty("device", out var dObj) && dObj.ValueKind == JsonValueKind.Object)
        {
            devNativeId = dObj.TryGetProperty("nativeId", out var dnEl) ? dnEl.GetString() : null;
            devType = dObj.TryGetProperty("type", out var dtEl) ? dtEl.GetString() : null;
            devName = dObj.TryGetProperty("name", out var dnmEl) ? dnmEl.GetString() : null;
            devLocation = dObj.TryGetProperty("locationHint", out var dlEl) ? dlEl.GetString() : null;
        }

        var format = PayloadFormat.Json;
        string? valPath = null;
        string? tsPath = null;
        string tsFormat = "iso8601";
        double? scale = null;
        double? offset = null;
        Dictionary<string, string>? mapEnum = null;
        string trueVal = "true";
        string falseVal = "false";
        string? unit = null;

        if (el.TryGetProperty("payload", out var plObj) && plObj.ValueKind == JsonValueKind.Object)
        {
            if (plObj.TryGetProperty("format", out var fEl))
            {
                var fStr = fEl.GetString()?.ToLowerInvariant();
                format = fStr switch
                {
                    "text" => PayloadFormat.Text,
                    "number" => PayloadFormat.Number,
                    "boolean" => PayloadFormat.Boolean,
                    "raw" => PayloadFormat.Raw,
                    _ => PayloadFormat.Json
                };
            }

            valPath = plObj.TryGetProperty("path", out var vpEl) ? vpEl.GetString() : null;
            tsPath = plObj.TryGetProperty("timestampPath", out var tpEl) ? tpEl.GetString() : null;
            if (plObj.TryGetProperty("timestampFormat", out var tfEl)) tsFormat = tfEl.GetString() ?? "iso8601";

            if (plObj.TryGetProperty("scale", out var scEl)) scale = scEl.GetDouble();
            if (plObj.TryGetProperty("offset", out var offEl)) offset = offEl.GetDouble();

            if (plObj.TryGetProperty("map", out var mEl) && mEl.ValueKind == JsonValueKind.Object)
            {
                mapEnum = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in mEl.EnumerateObject())
                {
                    mapEnum[prop.Name] = prop.Value.GetString() ?? prop.Value.GetRawText();
                }
            }

            if (plObj.TryGetProperty("trueValue", out var tvEl)) trueVal = tvEl.GetString() ?? "true";
            if (plObj.TryGetProperty("falseValue", out var fvEl)) falseVal = fvEl.GetString() ?? "false";
            if (plObj.TryGetProperty("unit", out var uEl)) unit = uEl.GetString();
        }

        CommandRuleMapping? cmdMapping = null;
        if (cmdEl.ValueKind == JsonValueKind.Object)
        {
            var cmdName = cmdEl.GetProperty("name").GetString()!;
            var pubTopic = cmdEl.GetProperty("publishTopic").GetString()!;
            var cmdQos = cmdEl.TryGetProperty("qos", out var cqEl) ? cqEl.GetInt32() : 1;
            var retain = cmdEl.TryGetProperty("retain", out var retEl) && retEl.GetBoolean();
            var plTemplate = cmdEl.TryGetProperty("payload", out var cptEl) ? cptEl.GetString() : null;
            var respTopic = cmdEl.TryGetProperty("responseTopic", out var rtEl) ? rtEl.GetString() : null;
            var corrData = cmdEl.TryGetProperty("correlationData", out var cdEl) ? cdEl.GetString() : null;
            var timeout = cmdEl.TryGetProperty("timeout", out var toEl) && toEl.TryGetInt32(out var toSec)
                ? TimeSpan.FromSeconds(toSec)
                : TimeSpan.FromSeconds(5);

            cmdMapping = new CommandRuleMapping
            {
                CommandName = cmdName,
                PublishTopicTemplate = pubTopic,
                Qos = cmdQos,
                Retain = retain,
                PayloadTemplate = plTemplate,
                ResponseTopicTemplate = respTopic,
                CorrelationDataTemplate = corrData,
                Timeout = timeout
            };
        }

        TimeSpan? staleAfter = null;
        if (el.TryGetProperty("staleAfter", out var saEl))
        {
            var saStr = saEl.GetString();
            if (!string.IsNullOrEmpty(saStr) && saStr.StartsWith("PT", StringComparison.Ordinal) && int.TryParse(saStr[2..^1], CultureInfo.InvariantCulture, out var sec))
            {
                staleAfter = TimeSpan.FromSeconds(sec);
            }
        }

        return Result.Success(new MappingRule
        {
            Id = id,
            Pattern = pattern!,
            Qos = qos,
            PropertyPath = propPath,
            EventPath = eventPath,
            DeviceNativeIdTemplate = devNativeId,
            DeviceTypeTemplate = devType,
            DeviceNameTemplate = devName,
            DeviceLocationHintTemplate = devLocation,
            PayloadFormat = format,
            ValuePath = valPath,
            TimestampPath = tsPath,
            TimestampFormat = tsFormat,
            Scale = scale,
            Offset = offset,
            MapEnum = mapEnum,
            TrueValue = trueVal,
            FalseValue = falseVal,
            Unit = unit,
            Command = cmdMapping,
            StaleAfter = staleAfter
        });
    }
}
