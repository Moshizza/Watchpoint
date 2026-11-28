using Watchpoint.Kernel.Results;

namespace Watchpoint.Registry.Contracts;

/// <summary>
/// Transactional write interface for mutating registry configuration (REG §6).
/// </summary>
public interface IRegistryWriter
{
    /// <summary>
    /// Applies an atomic batch of mutations.
    /// </summary>
    /// <param name="batch">The batch of changes.</param>
    /// <param name="context">Change context metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or validation/conflict error.</returns>
    Task<Result> ApplyAsync(RegistryChangeBatch batch, ChangeContext context, CancellationToken ct = default);

    /// <summary>
    /// Event fired after a change batch has been committed and in-memory snapshot updated (in-process, after the
    /// snapshot swap). The same event is also exposed on <see cref="IRegistryReader"/>.
    /// </summary>
    event EventHandler<RegistryChangedEventArgs>? Changed;
}

/// <summary>
/// Writes a batch inside a transaction of the configuration database that the caller owns (CFG §7.1, publish of a
/// change set across modules). Implemented by the registry writer.
/// </summary>
public interface IRegistryTransactionalWriter
{
    /// <summary>
    /// Stages the batch, checks it as <see cref="IRegistryWriter.ApplyAsync"/> does, and writes it (with its revision and
    /// <c>registry.changed</c> outbox record) through <paramref name="connection"/> in <paramref name="transaction"/>,
    /// without committing. Other registry writes wait until the batch is completed or aborted.
    /// </summary>
    /// <param name="batch">The changes.</param>
    /// <param name="context">Who and why (origin <c>publish:&lt;changeSetId&gt;</c>).</param>
    /// <param name="connection">The open connection of the configuration database.</param>
    /// <param name="transaction">The caller's transaction on it.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>The prepared batch, or the reason it is refused (nothing written).</returns>
    Task<Result<IPreparedRegistryBatch>> PrepareAsync(RegistryChangeBatch batch, ChangeContext context, System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction, CancellationToken ct);
}

/// <summary>A registry batch written in a caller's transaction, not visible yet.</summary>
public interface IPreparedRegistryBatch
{
    /// <summary>Gets the registry as it will be once the batch is completed (new versions, computed paths).</summary>
    IRegistryReader Staged { get; }

    /// <summary>After the commit: makes the batch visible (snapshot, enrichment, outbox, change event) and lets other writes go.</summary>
    void Complete();

    /// <summary>After a rollback: forgets the batch and lets other writes go.</summary>
    void Abort();
}
