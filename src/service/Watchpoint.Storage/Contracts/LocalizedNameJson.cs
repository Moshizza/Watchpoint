using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Storage.Contracts;

/// <summary>Stores a <see cref="LocalizedName"/> as JSON <c>{"default": "…", "translations": {"fr": "…"}}</c> (jsonb on PostgreSQL).</summary>
public sealed class LocalizedNameJsonConverter() : ValueConverter<LocalizedName, string>(v => ToJson(v), v => FromJson(v))
{
    /// <summary>Serialises a name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(LocalizedName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("default", name.Default);
            json.WriteStartObject("translations");
            foreach (var (culture, text) in name.Translations.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                json.WriteString(culture, text);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Parses a name written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The name.</returns>
    public static LocalizedName FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("translations", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in t.EnumerateObject())
            {
                translations[p.Name] = p.Value.GetString() ?? string.Empty;
            }
        }

        return new LocalizedName(root.GetProperty("default").GetString() ?? string.Empty, translations);
    }
}

/// <summary>Value comparison of <see cref="LocalizedName"/> (the record compares its dictionary by reference).</summary>
public sealed class LocalizedNameComparer() : ValueComparer<LocalizedName>(
    (a, b) => LocalizedNameJsonConverter.ToJson(a!) == LocalizedNameJsonConverter.ToJson(b!),
    v => LocalizedNameJsonConverter.ToJson(v).GetHashCode(StringComparison.Ordinal),
    v => LocalizedNameJsonConverter.FromJson(LocalizedNameJsonConverter.ToJson(v)));
