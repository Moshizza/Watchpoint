using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Watchpoint.Audit.Internal;

/// <summary>JSON canonicalisation after RFC 8785 (JCS): sorted member names (UTF-16 order), no whitespace, minimal string escapes, ECMAScript numbers.</summary>
internal static class CanonicalJson
{
    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalize(document.RootElement);
    }

    public static string Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(builder, element);
        return builder.ToString();
    }

    public static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>Formats a number like ECMAScript <c>Number.prototype.toString</c>.</summary>
    public static string FormatNumber(double value)
    {
        if (value == 0)
        {
            return "0";
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "JSON numbers must be finite.");
        }

        var sign = value < 0 ? "-" : string.Empty;
        // The shortest round-trip digits: take "R" and decompose it.
        var round = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentIndex = round.IndexOfAny(['E', 'e']);
        var mantissa = exponentIndex >= 0 ? round[..exponentIndex] : round;
        var exponent = exponentIndex >= 0 ? int.Parse(round[(exponentIndex + 1)..], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var digits = dot >= 0 ? mantissa.Remove(dot, 1) : mantissa;
        var pointPosition = (dot >= 0 ? dot : mantissa.Length) + exponent;

        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0')
        {
            leading++;
        }

        digits = digits[leading..];
        pointPosition -= leading;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }

        var k = digits.Length;
        var n = pointPosition;
        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            var e = n - 1;
            var expText = (e < 0 ? "-" : "+") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            result = k == 1 ? digits + "e" + expText : digits[0] + "." + digits[1..] + "e" + expText;
        }

        return sign + result;
    }

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(builder, property.Name);
                    builder.Append(':');
                    Write(builder, property.Value);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteString(builder, element.GetString()!);
                break;
            case JsonValueKind.Number:
                builder.Append(FormatNumber(element.GetDouble()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            default:
                builder.Append("null");
                break;
        }
    }
}
