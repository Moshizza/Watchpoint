using System.Globalization;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Protocol;
using Watchpoint.Integration.Mqtt.Mapping;

namespace Watchpoint.Integration.Mqtt.Commands;

public static class PublishMapper
{
    public static MqttApplicationMessage BuildMessage(
        CommandRuleMapping mapping,
        IReadOnlyDictionary<string, string> captures,
        IReadOnlyDictionary<string, object?> parameters,
        DateTimeOffset now)
    {
        var topic = SubstituteWithParameters(mapping.PublishTopicTemplate, captures, parameters, now);

        string? payloadStr = null;
        if (!string.IsNullOrEmpty(mapping.PayloadTemplate))
        {
            payloadStr = SubstituteWithParameters(mapping.PayloadTemplate, captures, parameters, now);
        }

        var builder = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)mapping.Qos)
            .WithRetainFlag(mapping.Retain);

        if (payloadStr != null)
        {
            builder.WithPayload(payloadStr);
        }

        if (!string.IsNullOrEmpty(mapping.ResponseTopicTemplate))
        {
            var respTopic = SubstituteWithParameters(mapping.ResponseTopicTemplate, captures, parameters, now);
            builder.WithResponseTopic(respTopic);
        }

        if (!string.IsNullOrEmpty(mapping.CorrelationDataTemplate))
        {
            var corr = SubstituteWithParameters(mapping.CorrelationDataTemplate, captures, parameters, now);
            builder.WithCorrelationData(System.Text.Encoding.UTF8.GetBytes(corr));
        }

        return builder.Build();
    }

    private static string SubstituteWithParameters(
        string template,
        IReadOnlyDictionary<string, string> captures,
        IReadOnlyDictionary<string, object?> parameters,
        DateTimeOffset now)
    {
        return Regex.Replace(template, "{" + @"([a-zA-Z0-9_\*\.]+)" + "}", match =>
        {
            var key = match.Groups[1].Value;

            if (key == "timestamp") return now.ToString("o");
            if (key == "timestamp.ms") return now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            if (key == "timestamp.s") return now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            if (parameters.TryGetValue(key, out var paramVal) && paramVal != null)
            {
                return paramVal.ToString()!;
            }

            var cleanKey = key.EndsWith('*') ? key[..^1] : key;
            if (captures.TryGetValue(cleanKey, out var capVal))
            {
                return capVal;
            }

            return match.Value;
        });
    }
}
