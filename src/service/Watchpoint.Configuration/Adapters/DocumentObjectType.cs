using System.Runtime.CompilerServices;
using System.Text.Json;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Configuration.Adapters;

/// <summary>A reference a document makes: the JSON pointer of a UUID string (or an array of UUID strings) and the type of the object it names.</summary>
/// <param name="FieldPath">The JSON pointer, e.g. <c>/locationId</c> or <c>/tags</c>.</param>
/// <param name="TargetType">The object type, e.g. <c>registry.location</c>.</param>
public sealed record ReferenceRule(string FieldPath, string TargetType);

/// <summary>The description of a configurable object type of a module: permissions, schema, references, and the module-specific validator.</summary>
/// <param name="Type">The type name.</param>
/// <param name="Module">The module.</param>
/// <param name="PermissionPrefix">The permission prefix: <c>&lt;prefix&gt;.view</c>, <c>.edit</c>, <c>.publish</c>.</param>
/// <param name="SchemaJson">The document schema (subset of <c>DocumentSchema</c>).</param>
/// <param name="References">The references the document makes.</param>
public sealed record ObjectTypeDefinition(string Type, string Module, string PermissionPrefix, string SchemaJson, IReadOnlyList<ReferenceRule> References)
{
    /// <summary>Gets a value indicating whether the type needs soft locks (large documents).</summary>
    public bool SupportsSoftLock { get; init; }

    /// <summary>Gets a value indicating whether CSV/XLSX import and export is supported.</summary>
    public bool Tabular { get; init; }

    /// <summary>Gets a module rule beyond the schema, or <see langword="null"/>.</summary>
    public Func<DraftEntry, IDraftView, CancellationToken, ValueTask<IReadOnlyList<Problem>>>? Rule { get; init; }
}

/// <summary>
/// A configurable object type described by an <see cref="ObjectTypeDefinition"/> and reading its live documents from an
/// <see cref="IConfigDocumentSource"/>: the adapter of a module whose own service is not document-oriented. References are
/// extracted by the declared JSON pointers; validation is the schema plus the module's rule.
/// </summary>
public sealed class DocumentObjectType : IConfigurableObjectType
{
    private readonly ObjectTypeDefinition _definition;
    private readonly IConfigDocumentSource _source;

    /// <summary>Initializes a new instance of the <see cref="DocumentObjectType"/> class.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="source">The live documents.</param>
    public DocumentObjectType(ObjectTypeDefinition definition, IConfigDocumentSource source)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(source);
        _definition = definition;
        _source = source;
        Schema = JsonDocument.Parse(definition.SchemaJson).RootElement.Clone();
    }

    /// <inheritdoc />
    public string Type => _definition.Type;

    /// <inheritdoc />
    public string Module => _definition.Module;

    /// <inheritdoc />
    public bool SupportsSoftLock => _definition.SupportsSoftLock;

    /// <inheritdoc />
    public bool Tabular => _definition.Tabular;

    /// <inheritdoc />
    public JsonElement? Schema { get; }

    /// <inheritdoc />
    public PermissionCode PermissionFor(ConfigAccess access) =>
        PermissionCode.FromTrusted($"{_definition.PermissionPrefix}.{access.ToString().ToLowerInvariant()}");

    /// <inheritdoc />
    public Task<ConfigDocument?> LoadAsync(Guid id, CancellationToken ct) => _source.LoadAsync(Type, id, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<ConfigDocument> ListAsync(ConfigListQuery query, CancellationToken ct) => TakeAsync(_source.ListAsync(Type, ct), query?.Take, ct);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Problem>> ValidateAsync(DraftEntry entry, IDraftView draft, CancellationToken ct) =>
        _definition.Rule is { } rule ? rule(entry, draft, ct) : ValueTask.FromResult<IReadOnlyList<Problem>>([]);

    /// <inheritdoc />
    public IReadOnlyList<ConfigObjectRef> GetReferences(JsonElement content)
    {
        List<ConfigObjectRef> references = [];
        foreach (var rule in _definition.References)
        {
            if (!TryGet(content, rule.FieldPath, out var value))
            {
                continue;
            }

            var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
            foreach (var item in items)
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParseExact(item.GetString(), "D", out var id))
                {
                    references.Add(new ConfigObjectRef(rule.TargetType, id));
                }
            }
        }

        return references;
    }

    private static async IAsyncEnumerable<ConfigDocument> TakeAsync(IAsyncEnumerable<ConfigDocument> source, int? take, [EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        await foreach (var document in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (take is { } limit && count++ >= limit)
            {
                yield break;
            }

            yield return document;
        }
    }

    private static bool TryGet(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        foreach (var token in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(token, out value))
            {
                return false;
            }
        }

        return true;
    }
}
