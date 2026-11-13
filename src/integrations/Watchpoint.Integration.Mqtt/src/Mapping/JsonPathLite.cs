using System.Globalization;
using System.Text.Json;

namespace Watchpoint.Integration.Mqtt.Mapping;

public static class JsonPathLite
{
    public static bool TryEvaluate(JsonElement root, string path, out JsonElement result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();
        if (trimmed == "$" || trimmed == "")
        {
            result = root;
            return true;
        }

        if (trimmed.StartsWith('/'))
        {
            return TryEvaluateJsonPointer(root, trimmed, out result);
        }

        if (trimmed.StartsWith('$'))
        {
            trimmed = trimmed[1..];
        }

        var current = root;
        var i = 0;
        while (i < trimmed.Length)
        {
            if (trimmed[i] == '.')
            {
                i++;
                var start = i;
                while (i < trimmed.Length && trimmed[i] != '.' && trimmed[i] != '[') i++;
                var propName = trimmed[start..i];
                if (string.IsNullOrEmpty(propName)) return false;
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propName, out current)) return false;
            }
            else if (trimmed[i] == '[')
            {
                i++;
                if (i >= trimmed.Length) return false;

                if (trimmed[i] == '\'' || trimmed[i] == '"')
                {
                    var quote = trimmed[i];
                    i++;
                    var start = i;
                    while (i < trimmed.Length && trimmed[i] != quote) i++;
                    if (i >= trimmed.Length) return false;
                    var propName = trimmed[start..i];
                    i++;
                    if (i >= trimmed.Length || trimmed[i] != ']') return false;
                    i++;
                    if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propName, out current)) return false;
                }
                else
                {
                    var start = i;
                    while (i < trimmed.Length && char.IsAsciiDigit(trimmed[i])) i++;
                    if (start == i || i >= trimmed.Length || trimmed[i] != ']') return false;
                    var index = int.Parse(trimmed[start..i], CultureInfo.InvariantCulture);
                    i++;

                    if (current.ValueKind != JsonValueKind.Array) return false;
                    var curIdx = 0;
                    var found = false;
                    foreach (var item in current.EnumerateArray())
                    {
                        if (curIdx == index)
                        {
                            current = item;
                            found = true;
                            break;
                        }
                        curIdx++;
                    }
                    if (!found) return false;
                }
            }
            else
            {
                var start = i;
                while (i < trimmed.Length && trimmed[i] != '.' && trimmed[i] != '[') i++;
                var propName = trimmed[start..i];
                if (string.IsNullOrEmpty(propName)) return false;
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(propName, out current)) return false;
            }
        }

        result = current;
        return true;
    }

    private static bool TryEvaluateJsonPointer(JsonElement root, string pointer, out JsonElement result)
    {
        result = default;
        var tokens = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var rawToken in tokens)
        {
            var token = rawToken.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(token, out current)) return false;
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(token, CultureInfo.InvariantCulture, out var index) || index < 0) return false;
                var curIdx = 0;
                var found = false;
                foreach (var item in current.EnumerateArray())
                {
                    if (curIdx == index)
                    {
                        current = item;
                        found = true;
                        break;
                    }
                    curIdx++;
                }
                if (!found) return false;
            }
            else
            {
                return false;
            }
        }

        result = current;
        return true;
    }
}
