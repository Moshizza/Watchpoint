using System.Globalization;
using System.Text;
using System.Text.Json;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Mqtt.Mapping;

public static class PayloadDecoder
{
    public static bool TryDecode(
        byte[] payload,
        MappingRule rule,
        out WpValue value,
        out DateTimeOffset? sourceTime,
        out Dictionary<string, WpValue>? eventPayload,
        out string? error)
    {
        value = WpValue.Null;
        sourceTime = null;
        eventPayload = null;
        error = null;

        var text = Encoding.UTF8.GetString(payload);

        if (rule.PayloadFormat == PayloadFormat.Raw)
        {
            value = WpValue.String(Convert.ToBase64String(payload));
            return true;
        }

        if (rule.PayloadFormat == PayloadFormat.Text)
        {
            value = WpValue.String(text);
            return true;
        }

        if (rule.PayloadFormat == PayloadFormat.Number)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                if (rule.Scale.HasValue) num *= rule.Scale.Value;
                if (rule.Offset.HasValue) num += rule.Offset.Value;
                value = WpValue.Decimal((decimal)num);
                return true;
            }
            error = $"Unable to parse number from payload '{text}'.";
            return false;
        }

        if (rule.PayloadFormat == PayloadFormat.Boolean)
        {
            var trimmed = text.Trim();
            if (string.Equals(trimmed, rule.TrueValue, StringComparison.OrdinalIgnoreCase))
            {
                value = WpValue.Bool(true);
                return true;
            }
            if (string.Equals(trimmed, rule.FalseValue, StringComparison.OrdinalIgnoreCase))
            {
                value = WpValue.Bool(false);
                return true;
            }

            if (bool.TryParse(trimmed, out var bVal))
            {
                value = WpValue.Bool(bVal);
                return true;
            }

            error = $"Unable to parse boolean from payload '{text}'.";
            return false;
        }

        if (rule.PayloadFormat == PayloadFormat.Json)
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (!string.IsNullOrEmpty(rule.TimestampPath))
                {
                    if (JsonPathLite.TryEvaluate(root, rule.TimestampPath, out var tsEl))
                    {
                        sourceTime = ParseTimestamp(tsEl, rule.TimestampFormat);
                    }
                }

                if (!string.IsNullOrEmpty(rule.EventPath))
                {
                    eventPayload = ExtractEventPayload(root);
                    return true;
                }

                if (!string.IsNullOrEmpty(rule.ValuePath))
                {
                    if (!JsonPathLite.TryEvaluate(root, rule.ValuePath, out var valEl))
                    {
                        error = $"Value path '{rule.ValuePath}' not found in JSON payload.";
                        return false;
                    }

                    return TryConvertJsonElement(valEl, rule, out value, out error);
                }
                else
                {
                    return TryConvertJsonElement(root, rule, out value, out error);
                }
            }
            catch (JsonException ex)
            {
                error = $"Invalid JSON payload: {ex.Message}";
                return false;
            }
        }

        error = $"Unsupported payload format {rule.PayloadFormat}.";
        return false;
    }

    private static bool TryConvertJsonElement(JsonElement el, MappingRule rule, out WpValue value, out string? error)
    {
        value = WpValue.Null;
        error = null;

        if (rule.MapEnum != null)
        {
            var rawStr = el.ValueKind == JsonValueKind.String ? el.GetString()! : el.GetRawText();
            if (rule.MapEnum.TryGetValue(rawStr, out var mapped))
            {
                value = WpValue.String(mapped);
                return true;
            }
        }

        switch (el.ValueKind)
        {
            case JsonValueKind.Number:
                var num = el.GetDouble();
                if (rule.Scale.HasValue) num *= rule.Scale.Value;
                if (rule.Offset.HasValue) num += rule.Offset.Value;
                value = WpValue.Decimal((decimal)num);
                return true;

            case JsonValueKind.True:
                value = WpValue.Bool(true);
                return true;

            case JsonValueKind.False:
                value = WpValue.Bool(false);
                return true;

            case JsonValueKind.String:
                var s = el.GetString()!;
                if (rule.Scale.HasValue || rule.Offset.HasValue)
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNum))
                    {
                        if (rule.Scale.HasValue) parsedNum *= rule.Scale.Value;
                        if (rule.Offset.HasValue) parsedNum += rule.Offset.Value;
                        value = WpValue.Decimal((decimal)parsedNum);
                        return true;
                    }
                }
                value = WpValue.String(s);
                return true;

            case JsonValueKind.Null:
                value = WpValue.Null;
                return true;

            default:
                value = WpValue.String(el.GetRawText());
                return true;
        }
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement el, string format)
    {
        try
        {
            if (format == "unixS" || format == "unix_s")
            {
                var s = el.ValueKind == JsonValueKind.Number ? el.GetInt64() : long.Parse(el.GetString()!, CultureInfo.InvariantCulture);
                return DateTimeOffset.FromUnixTimeSeconds(s);
            }
            if (format == "unixMs" || format == "unix_ms")
            {
                var ms = el.ValueKind == JsonValueKind.Number ? el.GetInt64() : long.Parse(el.GetString()!, CultureInfo.InvariantCulture);
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }

            var text = el.GetString();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }
        }
        catch
        {
        }
        return null;
    }

    private static Dictionary<string, WpValue> ExtractEventPayload(JsonElement root)
    {
        var dict = new Dictionary<string, WpValue>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                dict[prop.Name] = ConvertJsonElementToWpValue(prop.Value);
            }
        }
        return dict;
    }

    private static WpValue ConvertJsonElementToWpValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => WpValue.String(el.GetString()!),
        JsonValueKind.Number => WpValue.Decimal((decimal)el.GetDouble()),
        JsonValueKind.True => WpValue.Bool(true),
        JsonValueKind.False => WpValue.Bool(false),
        JsonValueKind.Null => WpValue.Null,
        _ => WpValue.String(el.GetRawText())
    };
}
