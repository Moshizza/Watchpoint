namespace Watchpoint.Storage.Contracts;

/// <summary>A stored audit record (spec AUD §8, <c>wp_audit.records</c>); the JSON columns are opaque to the store.</summary>
/// <param name="Id">Store-assigned id (0 before the row is inserted).</param>
/// <param name="ChainId">The chain (node id).</param>
/// <param name="Sequence">Monotonic position in the chain, starting at 1.</param>
/// <param name="OccurredAt">When the action happened.</param>
/// <param name="RecordedAt">When the writer stored it.</param>
/// <param name="Action">The action, <c>&lt;module&gt;.&lt;verb&gt;</c>.</param>
/// <param name="Outcome">The outcome as a number (0 success, 1 failure, 2 denied).</param>
/// <param name="Severity">The severity as a number (0 normal, 1 sensitive, 2 critical).</param>
/// <param name="ActorJson">The actor as JSON.</param>
/// <param name="ActorId">The actor id, for the actor index.</param>
/// <param name="ObjectsJson">The affected objects as a JSON array.</param>
/// <param name="ObjectIds">The ids of the affected objects, for the object index.</param>
/// <param name="Reason">The reason.</param>
/// <param name="DetailsJson">The details as canonical JSON.</param>
/// <param name="CorrelationId">The correlation id.</param>
/// <param name="PrevHash">The hash of the previous record (32 bytes).</param>
/// <param name="Hash">The hash of this record (32 bytes).</param>
public sealed record AuditRow(
    long Id,
    string ChainId,
    long Sequence,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string Action,
    short Outcome,
    short Severity,
    string ActorJson,
    Guid? ActorId,
    string ObjectsJson,
    IReadOnlyList<Guid> ObjectIds,
    string? Reason,
    string? DetailsJson,
    Guid? CorrelationId,
    byte[] PrevHash,
    byte[] Hash);

/// <summary>A signed checkpoint of a chain (spec AUD §6).</summary>
/// <param name="ChainId">The chain.</param>
/// <param name="Sequence">The checkpointed sequence.</param>
/// <param name="Hash">The hash of the record at <paramref name="Sequence"/>.</param>
/// <param name="At">When the checkpoint was taken.</param>
/// <param name="Signature">Ed25519 signature over <c>chainId|sequence|hash|at</c>.</param>
/// <param name="KeyId">Id of the signing key.</param>
public sealed record AuditCheckpointRow(string ChainId, long Sequence, byte[] Hash, DateTimeOffset At, byte[] Signature, string KeyId);

/// <summary>The newest record of a chain.</summary>
/// <param name="Sequence">Its sequence.</param>
/// <param name="Hash">Its hash.</param>
public sealed record AuditChainHead(long Sequence, byte[] Hash);

/// <summary>Verification progress of a chain (<c>wp_audit.verification_state</c>).</summary>
/// <param name="ChainId">The chain.</param>
/// <param name="LastVerifiedSequence">Highest sequence verified without issue.</param>
/// <param name="LastFullVerificationAt">When the whole chain was last verified.</param>
/// <param name="LastResultJson">The last verification report as JSON.</param>
public sealed record AuditVerificationState(string ChainId, long LastVerifiedSequence, DateTimeOffset? LastFullVerificationAt, string? LastResultJson);

/// <summary>Filter of an audit query at store level.</summary>
/// <param name="From">Inclusive lower bound of <c>occurred_at</c>.</param>
/// <param name="To">Exclusive upper bound of <c>occurred_at</c>.</param>
/// <param name="Actors">Actor ids, or <see langword="null"/> for any.</param>
/// <param name="ActionPrefixes">Action prefixes, or <see langword="null"/> for any.</param>
/// <param name="ObjectType">Object type among the objects, or <see langword="null"/>.</param>
/// <param name="ObjectId">Object id among the objects, or <see langword="null"/>.</param>
/// <param name="Outcome">Outcome number, or <see langword="null"/>.</param>
/// <param name="MinSeverity">Minimum severity number, or <see langword="null"/>.</param>
/// <param name="CorrelationId">Correlation id, or <see langword="null"/>.</param>
/// <param name="Text">Case-insensitive text in the reason or the object labels, or <see langword="null"/>.</param>
/// <param name="Descending">Newest first when <see langword="true"/>.</param>
public sealed record AuditRowFilter(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<Guid>? Actors,
    IReadOnlyList<string>? ActionPrefixes,
    string? ObjectType,
    Guid? ObjectId,
    short? Outcome,
    short? MinSeverity,
    Guid? CorrelationId,
    string? Text,
    bool Descending);

/// <summary>Keyset position of a query page: the last row returned.</summary>
/// <param name="OccurredAt">Its <c>occurred_at</c>.</param>
/// <param name="Id">Its id.</param>
public sealed record AuditCursorPosition(DateTimeOffset OccurredAt, long Id);

/// <summary>
/// The audit store (spec STO §3, AUD §8): append-only records, checkpoints and verification state. Records are never updated;
/// only <see cref="PurgeUpToAsync"/> deletes, and only whole chain segments up to a checkpoint.
/// </summary>
public interface IAuditStore
{
    /// <summary>Creates the tables, indexes, roles and the update/delete guards if missing; safe to call repeatedly.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task EnsureSchemaAsync(CancellationToken ct);

    /// <summary>Gets the newest record of a chain.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The head, or <see langword="null"/> when the chain has no record.</returns>
    Task<AuditChainHead?> GetHeadAsync(string chainId, CancellationToken ct);

    /// <summary>Inserts rows in one transaction; a duplicate <c>(chain, sequence)</c> fails the whole batch.</summary>
    /// <param name="rows">The rows, in sequence order.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AppendAsync(IReadOnlyList<AuditRow> rows, CancellationToken ct);

    /// <summary>Reads chain rows in sequence order.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="fromSequence">First sequence (inclusive).</param>
    /// <param name="toSequence">Last sequence (inclusive), or <see langword="null"/> for the end of the chain.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<AuditRow>> ReadChainAsync(string chainId, long fromSequence, long? toSequence, int limit, CancellationToken ct);

    /// <summary>Lists the chain ids that have records or checkpoints.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chain ids.</returns>
    Task<IReadOnlyList<string>> ListChainsAsync(CancellationToken ct);

    /// <summary>Queries records by time with keyset paging on <c>(occurred_at, id)</c>.</summary>
    /// <param name="filter">The filter.</param>
    /// <param name="after">The last row of the previous page, or <see langword="null"/>.</param>
    /// <param name="limit">Maximum number of rows.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    Task<IReadOnlyList<AuditRow>> QueryAsync(AuditRowFilter filter, AuditCursorPosition? after, int limit, CancellationToken ct);

    /// <summary>Gets one record by id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    Task<AuditRow?> GetAsync(long id, CancellationToken ct);

    /// <summary>Stores a checkpoint (idempotent on <c>(chain, sequence)</c>).</summary>
    /// <param name="checkpoint">The checkpoint.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AddCheckpointAsync(AuditCheckpointRow checkpoint, CancellationToken ct);

    /// <summary>Gets the checkpoints of a chain in sequence order.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The checkpoints.</returns>
    Task<IReadOnlyList<AuditCheckpointRow>> GetCheckpointsAsync(string chainId, CancellationToken ct);

    /// <summary>Gets the verification state of a chain.</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state, or <see langword="null"/>.</returns>
    Task<AuditVerificationState?> GetVerificationStateAsync(string chainId, CancellationToken ct);

    /// <summary>Stores the verification state of a chain.</summary>
    /// <param name="state">The state.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SaveVerificationStateAsync(AuditVerificationState state, CancellationToken ct);

    /// <summary>Deletes the records of a chain up to and including <paramref name="sequence"/> (retention, maintenance role).</summary>
    /// <param name="chainId">The chain.</param>
    /// <param name="sequence">The last sequence to delete; it must be covered by a checkpoint.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of records deleted.</returns>
    Task<long> PurgeUpToAsync(string chainId, long sequence, CancellationToken ct);
}
