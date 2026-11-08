using System.Text.Json;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Wptypes;

/// <summary>Shared helpers of the generators: ordered documents, English texts, parameter fields.</summary>
internal static class Catalogue
{
    public static IEnumerable<CapabilityDocument> Capabilities(LibrarySet set) =>
        set.Primary.Capabilities.Select(c => c.Document).OrderBy(c => c.Id.Value, StringComparer.Ordinal).ThenBy(c => c.Version);

    public static IEnumerable<DeviceTypeDocument> Types(LibrarySet set) =>
        set.Primary.Types.Select(t => t.Document).OrderBy(t => t.Id.Value, StringComparer.Ordinal).ThenBy(t => t.Version);

    /// <summary>The English text of a derived key, or <see langword="null"/>.</summary>
    public static string? English(LibrarySet set, string key)
    {
        foreach (var folder in set.All)
        {
            if (folder.Bundles.TryGetValue("en", out var bundle) && Lookup(bundle.Document, key) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    public static IReadOnlyList<(string Name, JsonElement Definition)> Fields(ParameterSchema? schema) => Fields(schema?.RawElement);

    public static IReadOnlyList<(string Name, JsonElement Definition)> Fields(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(p => (p.Name, p.Value))]
            : [];

    public static IReadOnlySet<string> Required(JsonElement? schema) =>
        schema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            ? required.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static string? Lookup(JsonElement node, string key)
    {
        foreach (var segment in key.Split('.'))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
            {
                return null;
            }
        }

        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }
}
