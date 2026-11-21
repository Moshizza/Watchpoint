namespace Watchpoint.Registry.Contracts;

/// <summary>
/// The event log's record limit for <c>registry.changed</c> records (spec REG §6, PIP §5.5). Implemented by the module
/// that publishes the outbox (the pipeline), with the same serialiser and envelope its publisher uses, so the registry
/// writer refuses, before commit, a batch whose record the log would refuse (<c>registry.batch.too_large</c>, arg
/// <c>bytes</c>). Without an implementation (no pipeline module) no record is ever published and no size is checked.
/// </summary>
public interface IRegistryRecordLimit
{
    /// <summary>The largest message the log stores, in bytes (header block and payload).</summary>
    int MaxBytes { get; }

    /// <summary>
    /// The exact size, in bytes, of the log message the publisher would append for <paramref name="record"/>: header
    /// block (with every optional header the publisher may add) and payload.
    /// </summary>
    /// <param name="record">The record, as the writer is about to store it in the outbox.</param>
    /// <returns>The message size in bytes.</returns>
    int Measure(RegistryOutboxRecord record);
}
