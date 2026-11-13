using System.Text.RegularExpressions;

namespace Watchpoint.Integration.Mqtt.Mapping;

public static class ValueMapper
{
    public static string Substitute(string template, IReadOnlyDictionary<string, string> captures)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        return Regex.Replace(template, "{" + @"([a-zA-Z0-9_\*]+)" + "}", match =>
        {
            var key = match.Groups[1].Value;
            if (key.EndsWith('*')) key = key[..^1];
            return captures.TryGetValue(key, out var val) ? val : match.Value;
        });
    }
}
