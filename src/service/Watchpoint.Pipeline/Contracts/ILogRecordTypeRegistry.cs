using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// The record kinds a node knows (spec PIP §5.3). Modules register their kinds at start-up; a record can only be
/// appended with a registered kind, and readers skip kinds (or schema versions) they do not know.
/// </summary>
public interface ILogRecordTypeRegistry
{
    /// <summary>Registers a kind. Registering the same kind, version and type again is allowed.</summary>
    /// <typeparam name="TBody">The body type.</typeparam>
    /// <param name="kind">The record kind (<see cref="LogEnvelope.IsValidKind"/>).</param>
    /// <param name="schemaVersion">The body schema version this node writes and the highest it reads (≥ 1).</param>
    /// <param name="typeInfo">The source-generated JSON metadata of <typeparamref name="TBody"/>.</param>
    /// <exception cref="ArgumentException">The kind is malformed or the version is below 1.</exception>
    /// <exception cref="InvalidOperationException">The kind is already registered with another type or version.</exception>
    void Register<TBody>(string kind, int schemaVersion, JsonTypeInfo<TBody> typeInfo);

    /// <summary>Returns the schema version registered for a kind.</summary>
    /// <param name="kind">The record kind.</param>
    /// <param name="schemaVersion">The registered schema version.</param>
    /// <returns><see langword="false"/> when the kind is not registered.</returns>
    bool TryGetSchemaVersion(string kind, out int schemaVersion);

    /// <summary>Returns whether this node can process a record: its kind is registered and its schema is not newer.</summary>
    /// <param name="record">The record.</param>
    /// <returns><see langword="true"/> when <see cref="LogRecord.BodyAs{T}"/> can be used.</returns>
    bool CanRead(LogRecord record);

    /// <summary>Serialises a body for <see cref="LogEnvelope.Body"/>.</summary>
    /// <typeparam name="TBody">The body type registered for the kind.</typeparam>
    /// <param name="kind">The record kind.</param>
    /// <param name="body">The body.</param>
    /// <returns>The JSON object.</returns>
    /// <exception cref="InvalidOperationException">The kind is not registered, or registered with another type.</exception>
    JsonElement SerializeBody<TBody>(string kind, TBody body);

    /// <summary>Deserialises a body.</summary>
    /// <typeparam name="TBody">The body type registered for the kind.</typeparam>
    /// <param name="kind">The record kind.</param>
    /// <param name="body">The JSON body.</param>
    /// <returns>The body.</returns>
    /// <exception cref="InvalidOperationException">The kind is not registered, or registered with another type.</exception>
    TBody DeserializeBody<TBody>(string kind, JsonElement body);
}
