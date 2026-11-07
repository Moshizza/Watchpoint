using Json.Schema;

namespace Watchpoint.Model.Documents;

/// <summary>Creates a <see cref="DocumentReader"/> from the schemas embedded in this assembly (no file-system access).</summary>
public static class DocumentReaderFactory
{
    private static readonly Lazy<DocumentReader> _embedded = new(() => new DocumentReader(
        Load("capability.schema.json"), Load("device-type.schema.json"), Load("manifest.schema.json"), Load("parameter.schema.json")));

    /// <summary>The reader using the embedded copies of <c>schemas/*.schema.json</c>.</summary>
    /// <returns>The shared reader (thread-safe: it holds no mutable state).</returns>
    public static DocumentReader FromEmbeddedSchemas() => _embedded.Value;

    private static JsonSchema Load(string name)
    {
        using var stream = typeof(DocumentReaderFactory).Assembly.GetManifestResourceStream("schemas/" + name)
            ?? throw new InvalidOperationException($"Embedded schema {name} is missing.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
