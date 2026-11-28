using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Configuration.Internal.Validation;

/// <summary>Applies the JSON Patch subset fix suggestions use (RFC 6902: <c>add</c>, <c>remove</c>, <c>replace</c>).</summary>
internal static class JsonPatch
{
    /// <summary>Builds a one-operation patch.</summary>
    /// <param name="op"><c>add</c>, <c>remove</c> or <c>replace</c>.</param>
    /// <param name="path">The JSON pointer.</param>
    /// <param name="value">The value (not for <c>remove</c>).</param>
    /// <returns>The patch document.</returns>
    public static JsonElement One(string op, string path, JsonNode? value = null)
    {
        var operation = new JsonObject { ["op"] = op, ["path"] = path };
        if (!string.Equals(op, "remove", StringComparison.Ordinal))
        {
            operation["value"] = value?.DeepClone();
        }

        return NodeJson.ToElement(new JsonArray(operation));
    }

    /// <summary>Applies a patch to a document.</summary>
    /// <param name="document">The document.</param>
    /// <param name="patch">The patch (an array of operations).</param>
    /// <returns>The patched document.</returns>
    /// <exception cref="InvalidOperationException">The patch does not apply (unknown operation or a path that does not exist).</exception>
    public static JsonElement Apply(JsonElement document, JsonElement patch)
    {
        var root = JsonNode.Parse(document.GetRawText());
        if (patch.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("A patch is an array of operations.");
        }

        foreach (var operation in patch.EnumerateArray())
        {
            var op = operation.GetProperty("op").GetString();
            var path = operation.GetProperty("path").GetString() ?? string.Empty;
            var value = operation.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : null;
            root = ApplyOne(root, op, path, value);
        }

        return NodeJson.ToElement(root);
    }

    private static JsonNode? ApplyOne(JsonNode? root, string? op, string path, JsonNode? value)
    {
        if (path.Length == 0)
        {
            return op == "remove" ? null : value;
        }

        var tokens = path.Split('/').Skip(1).Select(t => t.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToList();
        var parent = root;
        foreach (var token in tokens.Take(tokens.Count - 1))
        {
            parent = parent switch
            {
                JsonObject o when o.TryGetPropertyValue(token, out var child) => child,
                JsonArray a when int.TryParse(token, out var i) && i >= 0 && i < a.Count => a[i],
                _ => throw new InvalidOperationException($"Path '{path}' does not exist."),
            };
        }

        var last = tokens[^1];
        switch (parent)
        {
            case JsonObject obj:
                switch (op)
                {
                    case "add":
                    case "replace":
                        if (op == "replace" && !obj.ContainsKey(last))
                        {
                            throw new InvalidOperationException($"Path '{path}' does not exist.");
                        }

                        obj[last] = value?.DeepClone();
                        break;
                    case "remove":
                        if (!obj.Remove(last))
                        {
                            throw new InvalidOperationException($"Path '{path}' does not exist.");
                        }

                        break;
                    default:
                        throw new InvalidOperationException($"Unknown operation '{op}'.");
                }

                break;
            case JsonArray array:
                var index = last == "-" ? array.Count : int.TryParse(last, out var i) ? i : -1;
                switch (op)
                {
                    case "add" when index >= 0 && index <= array.Count:
                        array.Insert(index, value?.DeepClone());
                        break;
                    case "replace" when index >= 0 && index < array.Count:
                        array[index] = value?.DeepClone();
                        break;
                    case "remove" when index >= 0 && index < array.Count:
                        array.RemoveAt(index);
                        break;
                    default:
                        throw new InvalidOperationException($"Operation '{op}' does not apply at '{path}'.");
                }

                break;
            default:
                throw new InvalidOperationException($"Path '{path}' does not exist.");
        }

        return root;
    }
}
