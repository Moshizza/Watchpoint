using System.Security.Cryptography;
using System.Text.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Model.Internal;

/// <summary>
/// Provides translations and symbols (spec MOD §4.5).
/// </summary>
internal sealed class TypeResources : ITypeResources
{
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _symbols;
    private readonly IReadOnlyDictionary<string, byte[]> _translationBundles;
    private readonly IReadOnlyDictionary<string, string> _etags;

    public TypeResources(
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> symbols,
        IReadOnlyDictionary<string, byte[]> translationBundles,
        IReadOnlyDictionary<string, string> etags)
    {
        _symbols = symbols;
        _translationBundles = translationBundles;
        _etags = etags;
    }

    public ReadOnlyMemory<byte> GetTranslationBundle(string culture, out string etag)
    {
        if (_translationBundles.TryGetValue(culture, out var bytes) && _etags.TryGetValue(culture, out var tag))
        {
            etag = tag;
            return bytes;
        }

        etag = "\"empty\"";
        return "{}"u8.ToArray();
    }

    public Result<ReadOnlyMemory<byte>> GetSymbol(string symbolId, OverallState state)
    {
        var stateStr = state.ToString();
        var camelState = char.ToLowerInvariant(stateStr[0]) + stateStr[1..];

        if (_symbols.TryGetValue($"{symbolId}/{camelState}.svg", out var exactSvg))
        {
            return exactSvg;
        }

        if (_symbols.TryGetValue($"{symbolId}/default.svg", out var defaultSvg))
        {
            return defaultSvg;
        }

        return Error.NotFound(ModelErrors.SymbolNotFound.Value, ("symbolId", symbolId), ("state", camelState));
    }

    public static TypeResources Build(
        IReadOnlyDictionary<string, JsonElement> standardBundles,
        IReadOnlyDictionary<string, IReadOnlyList<JsonDocument>> packageBundles,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> symbols)
    {
        var allCultures = standardBundles.Keys.Concat(packageBundles.Keys).Distinct(StringComparer.Ordinal);
        var bundles = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var etags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var culture in allCultures)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();

                if (standardBundles.TryGetValue(culture, out var stdElem) && stdElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in stdElem.EnumerateObject())
                    {
                        prop.WriteTo(writer);
                    }
                }

                if (packageBundles.TryGetValue(culture, out var pkgList))
                {
                    foreach (var pkgDoc in pkgList)
                    {
                        if (pkgDoc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in pkgDoc.RootElement.EnumerateObject())
                            {
                                prop.WriteTo(writer);
                            }
                        }
                    }
                }

                writer.WriteEndObject();
            }

            var bytes = stream.ToArray();
            bundles[culture] = bytes;

            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            etags[culture] = $"\"{hash}\"";
        }

        return new TypeResources(symbols, bundles, etags);
    }
}
