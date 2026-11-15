namespace Watchpoint.Registry.Contracts;

/// <summary>
/// The registry's transactional outbox (spec REG §6, §10; PIP §5.5). The writer stores one <see cref="RegistryOutboxRecord"/>
/// per applied batch in the transaction of the batch; the pipeline module's outbox publisher reads the unsent records in
/// revision order, appends them to the event log and marks each one sent after the log acknowledged it. Sent records are
/// kept for a bounded time (PIP §5.5) and then deleted by the publisher.
/// </summary>
public interface IRegistryOutbox
{
    /// <summary>Reads the oldest unsent records, in revision order.</summary>
    /// <param name="limit">The maximum number of records to return (at least 1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The unsent records with the lowest revisions, at most <paramref name="limit"/>; empty when everything is sent.</returns>
    Task<IReadOnlyList<RegistryOutboxRecord>> ReadUnsentAsync(int limit, CancellationToken ct);

    /// <summary>Marks a record sent (the log acknowledged it). Marking a sent or unknown revision does nothing.</summary>
    /// <param name="revision">The revision of the record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task completing when the mark is stored.</returns>
    Task MarkSentAsync(long revision, CancellationToken ct);

    /// <summary>Deletes the records sent before <paramref name="threshold"/>. Unsent records are never deleted.</summary>
    /// <param name="threshold">Records with a sent time before this instant are deleted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    Task<int> DeleteSentBeforeAsync(DateTimeOffset threshold, CancellationToken ct);

    /// <summary>
    /// Waits until a record was added by this node since the previous wait completed (a signal, not a queue: several
    /// commits before the next wait complete it once). Lets the publisher react at once without polling; records written
    /// by another node sharing the database are only seen by the publisher's periodic poll.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task completing when at least one record was added.</returns>
    Task WaitForRecordsAsync(CancellationToken ct);
}
