using System.Text.Json;
using System.Text.Json.Nodes;

namespace Watchpoint.Configuration.Internal;

/// <summary>JSON nodes to elements without the serializer (the host runs without reflection-based JSON).</summary>
internal static class NodeJson
{
    public static JsonElement ToElement(JsonNode? node)
    {
        using var document = JsonDocument.Parse(node?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }
}
