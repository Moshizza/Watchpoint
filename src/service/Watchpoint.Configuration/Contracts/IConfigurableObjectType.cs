using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Configuration.Contracts;

/// <summary>The kind of access a configurable object type needs permissions for.</summary>
public enum ConfigAccess
{
    /// <summary>Reading.</summary>
    View,

    /// <summary>Changing (drafts).</summary>
    Edit,

    /// <summary>Publishing.</summary>
    Publish,
}

/// <summary>A filter of <see cref="IConfigurableObjectType.ListAsync"/>.</summary>
/// <param name="Take">The most objects to return; <see langword="null"/> for all.</param>
public sealed record ConfigListQuery(int? Take = null);

/// <summary>What the draft looks like to a type's validator: the other entries of the draft and the live objects.</summary>
public interface IDraftView
{
    /// <summary>Gets an object as the draft would leave it: the entry's content, or the live document.</summary>
    /// <param name="reference">The object.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The state and document of the object.</returns>
    ValueTask<DraftObjectState> GetAsync(ConfigObjectRef reference, CancellationToken ct);
}

/// <summary>Where an object comes from in a draft view.</summary>
public enum DraftObjectSource
{
    /// <summary>The object does not exist, live or in the draft.</summary>
    Missing,

    /// <summary>The draft changes (creates or updates) it: the document is the entry's.</summary>
    Draft,

    /// <summary>The draft does not touch it: the document is the live one.</summary>
    Live,

    /// <summary>The draft deletes it.</summary>
    Deleted,
}

/// <summary>An object as seen through a draft.</summary>
/// <param name="Source">Where it comes from.</param>
/// <param name="Document">The document (draft or live); <see langword="null"/> when missing or deleted.</param>
/// <param name="LiveVersion">The live version, when the object exists live.</param>
public sealed record DraftObjectState(DraftObjectSource Source, ConfigDocument? Document, long? LiveVersion);

/// <summary>The result of applying an entry (T3.02).</summary>
/// <param name="Ref">The object.</param>
/// <param name="Operation">The operation.</param>
/// <param name="NewVersion">The new version.</param>
/// <param name="Before">The document before.</param>
/// <param name="After">The document after.</param>
public sealed record AppliedEntry(ConfigObjectRef Ref, EntryOperation Operation, long NewVersion, JsonElement? Before, JsonElement? After);

/// <summary>
/// A kind of configuration object a module makes configurable (spec CFG §4). Modules register their types at start-up
/// (<see cref="IConfigObjectTypeRegistry"/>). The apply methods of the spec (<c>PrepareApply</c>, <c>CompleteApply</c>,
/// <c>Reload</c>) arrive with publishing (T3.02); everything here is read-only towards the live configuration.
/// </summary>
public interface IConfigurableObjectType
{
    /// <summary>Gets the type name, <c>&lt;module&gt;.&lt;object&gt;</c>.</summary>
    string Type { get; }

    /// <summary>Gets the owning module.</summary>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec CFG §4.")]
    string Module { get; }

    /// <summary>Gets the permission needed for <paramref name="access"/>.</summary>
    /// <param name="access">The access.</param>
    /// <returns>The permission code.</returns>
    PermissionCode PermissionFor(ConfigAccess access);

    /// <summary>Gets a value indicating whether documents of this type are large enough to need soft locks (T3.03).</summary>
    bool SupportsSoftLock { get; }

    /// <summary>Gets a value indicating whether CSV/XLSX import and export is supported (T3.03).</summary>
    bool Tabular { get; }

    /// <summary>Gets the JSON Schema subset the documents must satisfy (see <c>DocumentSchema</c> in the spec), or <see langword="null"/> for none.</summary>
    JsonElement? Schema { get; }

    /// <summary>Loads a live object.</summary>
    /// <param name="id">The object id.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The document, or <see langword="null"/> when it does not exist.</returns>
    Task<ConfigDocument?> LoadAsync(Guid id, CancellationToken ct);

    /// <summary>Lists live objects.</summary>
    /// <param name="query">The query.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The documents.</returns>
    IAsyncEnumerable<ConfigDocument> ListAsync(ConfigListQuery query, CancellationToken ct);

    /// <summary>
    /// Validates one entry in the context of the whole draft (module rules beyond the schema). Must be deterministic: the same
    /// entry and the same view give the same problems. Problems carry code, arguments and path, never message text; the
    /// framework adds the object reference.
    /// </summary>
    /// <param name="entry">The entry (never a delete).</param>
    /// <param name="draft">The draft view.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The problems.</returns>
    ValueTask<IReadOnlyList<Problem>> ValidateAsync(DraftEntry entry, IDraftView draft, CancellationToken ct);

    /// <summary>Returns the references a document makes to other configuration objects (for delete checks).</summary>
    /// <param name="content">The document.</param>
    /// <returns>The references.</returns>
    IReadOnlyList<ConfigObjectRef> GetReferences(JsonElement content);
}

/// <summary>The object types of the node, registered by the modules at start-up.</summary>
public interface IConfigObjectTypeRegistry
{
    /// <summary>Registers a type.</summary>
    /// <param name="type">The type.</param>
    /// <exception cref="InvalidOperationException">A type with that name is registered already.</exception>
    void Register(IConfigurableObjectType type);

    /// <summary>Gets all types, in registration order (the order publishing applies them in).</summary>
    IReadOnlyList<IConfigurableObjectType> All { get; }

    /// <summary>Finds a type.</summary>
    /// <param name="name">The type name.</param>
    /// <returns>The type, or <see langword="null"/>.</returns>
    IConfigurableObjectType? Find(string name);
}
