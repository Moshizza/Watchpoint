using System.Text.Json;

namespace Watchpoint.Testing.Json;

/// <summary>Semantic JSON comparison: property order and whitespace are ignored, numbers are compared by value.</summary>
public static class JsonAssert
{
    /// <summary>Asserts that two JSON documents are equivalent.</summary>
    /// <param name="expectedJson">The expected JSON.</param>
    /// <param name="actualJson">The actual JSON.</param>
    /// <param name="options">Comparison options; <see langword="null"/> for none.</param>
    /// <exception cref="TestAssertionException">The documents differ; the message gives the JSON pointer of the first difference.</exception>
    public static void Equivalent(string expectedJson, string actualJson, JsonAssertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expectedJson);
        ArgumentNullException.ThrowIfNull(actualJson);
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var ignored = new HashSet<string>(options?.IgnoredPaths ?? [], StringComparer.Ordinal);
        if (Compare(expected.RootElement, actual.RootElement, string.Empty, ignored) is { } difference)
        {
            throw new TestAssertionException($"JSON differs at '{difference.Path}': {difference.Reason}");
        }
    }

    private static (string Path, string Reason)? Compare(JsonElement expected, JsonElement actual, string path, HashSet<string> ignored)
    {
        if (ignored.Contains(path))
        {
            return null;
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            return (path, $"expected {expected.ValueKind} {Short(expected)}, got {actual.ValueKind} {Short(actual)}");
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var name in expectedProps.Keys.Union(actualProps.Keys).Order(StringComparer.Ordinal))
                {
                    var child = path + "/" + Escape(name);
                    if (ignored.Contains(child))
                    {
                        continue;
                    }

                    if (!actualProps.TryGetValue(name, out var a))
                    {
                        return (child, "missing in actual");
                    }

                    if (!expectedProps.TryGetValue(name, out var e))
                    {
                        return (child, $"unexpected property with value {Short(a)}");
                    }

                    if (Compare(e, a, child, ignored) is { } diff)
                    {
                        return diff;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    return (path, $"expected {expectedItems.Count} items, got {actualItems.Count}");
                }

                for (var i = 0; i < expectedItems.Count; i++)
                {
                    if (Compare(expectedItems[i], actualItems[i], $"{path}/{i}", ignored) is { } diff)
                    {
                        return diff;
                    }
                }

                return null;
            case JsonValueKind.Number:
                var same = expected.TryGetDecimal(out var ed) && actual.TryGetDecimal(out var ad)
                    ? ed == ad
                    : expected.GetDouble().Equals(actual.GetDouble());
                return same ? null : (path, $"expected {Short(expected)}, got {Short(actual)}");
            case JsonValueKind.String:
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal)
                    ? null
                    : (path, $"expected {Short(expected)}, got {Short(actual)}");
            default:
                return null;
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string Short(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 77), "...");
    }
}
