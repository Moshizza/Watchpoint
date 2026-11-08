using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Secrets.Contracts;

/// <summary>The secrets vault (spec SEC §5). Read, replace and delete require the caller to be the owner.</summary>
public interface ISecretVault
{
    /// <summary>Creates a secret.</summary>
    /// <param name="name">Name for humans.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="owner">The owner.</param>
    /// <param name="value">The value (≤ <c>Secrets:MaxValueBytes</c>).</param>
    /// <param name="expiresAt">Expiry, if any.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The id, or <c>secrets.value.too_large</c>.</returns>
    Task<Result<SecretId>> CreateAsync(string name, SecretKind kind, SecretOwner owner, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt,
        CancellationToken ct);

    /// <summary>Replaces the value (new version; the old value is not kept).</summary>
    /// <param name="id">The secret.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="value">The new value.</param>
    /// <param name="expiresAt">The new expiry.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, <c>secrets.not_found</c>, <c>secrets.access.denied</c> or <c>secrets.value.too_large</c>.</returns>
    Task<Result> ReplaceAsync(SecretId id, SecretOwner caller, ReadOnlyMemory<byte> value, DateTimeOffset? expiresAt, CancellationToken ct);

    /// <summary>Decrypts a secret; the caller disposes the value.</summary>
    /// <param name="id">The secret.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="purpose">Why it is read (logged at debug level), e.g. <c>connect</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The value, <c>secrets.not_found</c>, <c>secrets.access.denied</c> or <c>secrets.integrity.failed</c>.</returns>
    Task<Result<SecretValue>> ReadAsync(SecretId id, SecretOwner caller, string purpose, CancellationToken ct);

    /// <summary>Deletes a secret.</summary>
    /// <param name="id">The secret.</param>
    /// <param name="caller">The caller.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success, <c>secrets.not_found</c> or <c>secrets.access.denied</c>.</returns>
    Task<Result> DeleteAsync(SecretId id, SecretOwner caller, CancellationToken ct);

    /// <summary>Metadata of a secret.</summary>
    /// <param name="id">The secret.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The metadata or <c>secrets.not_found</c>.</returns>
    Task<Result<SecretMetadata>> GetMetadataAsync(SecretId id, CancellationToken ct);

    /// <summary>Metadata of all secrets, or of one owner's (module, and entity when given).</summary>
    /// <param name="owner">The owner filter.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The metadata.</returns>
    Task<IReadOnlyList<SecretMetadata>> ListAsync(SecretOwner? owner, CancellationToken ct);

    /// <summary>Re-encrypts every data key not wrapped with the current master key.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of secrets rewrapped.</returns>
    Task<Result<int>> RewrapAllAsync(CancellationToken ct);
}

/// <summary>Secrets needed before the database is reachable, in a node-local encrypted file (spec SEC §6.3).</summary>
public interface IBootstrapSecretStore
{
    /// <summary>Reads a secret; the caller disposes the value.</summary>
    /// <param name="name">The name, e.g. <c>database.default</c>.</param>
    /// <returns>The value, <c>secrets.not_found</c> or <c>secrets.integrity.failed</c>.</returns>
    Result<SecretValue> Read(string name);

    /// <summary>Creates or replaces a secret (atomic file write).</summary>
    /// <param name="name">The name.</param>
    /// <param name="value">The value.</param>
    /// <returns>Success or <c>secrets.ref.invalid</c> for an invalid name.</returns>
    Result Write(string name, ReadOnlySpan<byte> value);

    /// <summary>Whether a secret exists.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it exists.</returns>
    bool Exists(string name);
}

/// <summary>Receives audit records of the vault. Implemented by the audit module (AUD); until then a logging implementation is used.</summary>
public interface ISecretsAuditSink
{
    /// <summary>Records an access.</summary>
    /// <param name="record">The record.</param>
    void Record(SecretAuditRecord record);
}

/// <summary>An audit record of the vault (never contains the value).</summary>
/// <param name="Action"><c>create</c>, <c>replace</c>, <c>delete</c>, <c>read</c> (aggregated per hour), <c>denied</c>.</param>
/// <param name="SecretId">The secret.</param>
/// <param name="Module">The calling module.</param>
/// <param name="EntityId">The calling entity.</param>
/// <param name="At">When (for aggregated reads: the start of the hour).</param>
/// <param name="Count">Number of accesses (1 except aggregated reads).</param>
public sealed record SecretAuditRecord(string Action, SecretId SecretId, string Module, Guid? EntityId, DateTimeOffset At, int Count);
