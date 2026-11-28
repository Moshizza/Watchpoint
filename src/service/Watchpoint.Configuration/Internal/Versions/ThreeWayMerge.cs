using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Configuration.Internal.Versions;

/// <summary>
/// Three-way merge of a document edited in a draft with the live document changed meanwhile (spec CFG §8). Objects are
/// merged by key, recursively; any other value (arrays included) is merged as a whole. A path changed on one side only
/// takes that side; a path changed differently on both sides is a conflict and keeps <i>mine</i> until resolved.
/// </summary>
internal static class ThreeWayMerge
{
    public static MergeResult Merge(JsonElement? @base, JsonElement mine, JsonElement theirs)
    {
        var conflicts = new List<string>();
        var merged = Merge(string.Empty, JsonDiff.Node(@base), JsonDiff.Node(mine), JsonDiff.Node(theirs), @base is not null, true, true, conflicts, out var present);
        return new MergeResult(present ? JsonDiff.Element(merged) : JsonDiff.Element(null), conflicts);
    }

    private static JsonNode? Merge(string path, JsonNode? b, JsonNode? m, JsonNode? t, bool hasB, bool hasM, bool hasT, List<string> conflicts, out bool present)
    {
        if (Same(m, hasM, t, hasT))
        {
            present = hasM;
            return Copy(m);
        }

        if (Same(m, hasM, b, hasB))
        {
            present = hasT;
            return Copy(t);
        }

        if (Same(t, hasT, b, hasB))
        {
            present = hasM;
            return Copy(m);
        }

        if (m is JsonObject mo && t is JsonObject to)
        {
            var bo = b as JsonObject;
            var result = new JsonObject();
            foreach (var key in mo.Select(p => p.Key).Union(to.Select(p => p.Key), StringComparer.Ordinal).Union(bo?.Select(p => p.Key) ?? [], StringComparer.Ordinal))
            {
                var value = Merge(path + "/" + JsonDiff.Escape(key), bo?[key], mo[key], to[key], bo?.ContainsKey(key) == true, mo.ContainsKey(key), to.ContainsKey(key),
                    conflicts, out var keep);
                if (keep)
                {
                    result[key] = value;
                }
            }

            present = true;
            return result;
        }

        conflicts.Add(path.Length == 0 ? "/" : path);
        present = hasM;
        return Copy(m);
    }

    private static bool Same(JsonNode? a, bool hasA, JsonNode? b, bool hasB) => hasA == hasB && (!hasA || JsonNode.DeepEquals(a, b));

    private static JsonNode? Copy(JsonNode? node) => node?.DeepClone();
}

/// <summary>The merged document and the JSON pointers changed differently on both sides.</summary>
/// <param name="Merged">The merged document (conflicting paths keep the draft's value).</param>
/// <param name="Conflicts">The conflicting paths.</param>
internal sealed record MergeResult(JsonElement Merged, IReadOnlyList<string> Conflicts);
