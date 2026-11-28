using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal.Versions;

/// <summary>
/// The structural diff of two documents (spec CFG §7.4): objects compared by key, arrays by index, or by the <c>id</c>
/// field when every item of both arrays is an object with one (the path segment is then the id). Paths are JSON
/// pointers (RFC 6901 escaping); a value missing on one side is <see langword="null"/> there.
/// </summary>
internal static class JsonDiff
{
    public static IReadOnlyList<DiffChange> Diff(JsonElement? before, JsonElement? after)
    {
        var changes = new List<DiffChange>();
        Walk(string.Empty, Node(before), Node(after), before is not null, after is not null, changes);
        return changes;
    }

    private static void Walk(string path, JsonNode? before, JsonNode? after, bool hasBefore, bool hasAfter, List<DiffChange> changes)
    {
        if (hasBefore && hasAfter && JsonNode.DeepEquals(before, after))
        {
            return;
        }

        if (hasBefore && hasAfter && before is JsonObject b && after is JsonObject a)
        {
            foreach (var key in b.Select(p => p.Key).Union(a.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                Walk(path + "/" + Escape(key), b[key], a[key], b.ContainsKey(key), a.ContainsKey(key), changes);
            }

            return;
        }

        if (hasBefore && hasAfter && before is JsonArray ba && after is JsonArray aa)
        {
            if (ById(ba) is { } beforeById && ById(aa) is { } afterById)
            {
                foreach (var id in beforeById.Keys.Union(afterById.Keys, StringComparer.Ordinal))
                {
                    Walk(path + "/" + Escape(id), beforeById.GetValueOrDefault(id), afterById.GetValueOrDefault(id),
                        beforeById.ContainsKey(id), afterById.ContainsKey(id), changes);
                }

                return;
            }

            for (var i = 0; i < Math.Max(ba.Count, aa.Count); i++)
            {
                Walk(path + "/" + i.ToString(CultureInfo.InvariantCulture), i < ba.Count ? ba[i] : null, i < aa.Count ? aa[i] : null,
                    i < ba.Count, i < aa.Count, changes);
            }

            return;
        }

        changes.Add(new DiffChange(path.Length == 0 ? "/" : path, hasBefore ? Element(before) : null, hasAfter ? Element(after) : null));
    }

    /// <summary>The items by their <c>id</c> (text), or <see langword="null"/> unless every item is an object with a unique one.</summary>
    private static Dictionary<string, JsonNode?>? ById(JsonArray array)
    {
        var items = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonObject obj || obj["id"] is not JsonValue id || !items.TryAdd(id.ToJsonString(), item))
            {
                return null;
            }
        }

        return items.Count > 0 ? items.ToDictionary(p => p.Key.Trim('"'), p => p.Value, StringComparer.Ordinal) : null;
    }

    internal static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    internal static JsonNode? Node(JsonElement? element) => element is { } e ? JsonNode.Parse(e.GetRawText()) : null;

    internal static JsonElement Element(JsonNode? node) => JsonSerializer.SerializeToElement(node);
}
