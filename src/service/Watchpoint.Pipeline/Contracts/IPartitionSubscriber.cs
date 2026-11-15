using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// An in-process consumer of the records of a partition (spec PIP §7.3), registered by a module in the service
/// collection. Every member is called on the partition's worker, one call at a time per partition, in ascending
/// <see cref="Order"/>; members must not do I/O and must be fast (below 1 ms typically). A member that throws is logged
/// (1641) and skipped for that call; the other subscribers still run.
/// </summary>
public interface IPartitionSubscriber
{
    /// <summary>Gets the position among the subscribers: STA = 100, CMD = 200, ALM = 300, AUT = 400…</summary>
    int Order { get; }

    /// <summary>Gets the name used in logs, metrics and checkpoints.</summary>
    string Name { get; }

    /// <summary>Receives records appended to the log, in sequence order.</summary>
    /// <param name="context">The partition context of this call.</param>
    /// <param name="records">The records, already durable.</param>
    void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records);

    /// <summary>Receives an observation that changed nothing: no record was appended (spec PIP §7.2).</summary>
    /// <param name="context">The partition context of this call.</param>
    /// <param name="touch">What was observed again.</param>
    void OnTouched(PartitionContext context, TouchInfo touch)
    {
    }

    /// <summary>Receives a source time that was too far in the future and was replaced by the received time (spec PIP §7.1, step 6).</summary>
    /// <param name="context">The partition context of this call.</param>
    /// <param name="hint">The offending source time.</param>
    void OnClockDrift(PartitionContext context, ClockDriftHint hint)
    {
    }

    /// <summary>Called before the partition is recovered; the subscriber restores its own snapshot (spec PIP §7.4).</summary>
    /// <param name="partition">The partition.</param>
    void OnRecoveryStarting(int partition)
    {
    }

    /// <summary>
    /// Returns the last <c>WP_LOG</c> sequence this subscriber already applied for a partition, after
    /// <see cref="OnRecoveryStarting"/>. Records after it are replayed to the subscriber.
    /// </summary>
    /// <param name="partition">The partition.</param>
    /// <returns>The checkpoint (0 = replay everything available), or <see langword="null"/> when the subscriber keeps no state and needs no replay.</returns>
    long? GetRecoveryCheckpoint(int partition) => null;

    /// <summary>Called when the partition is recovered, before new input is processed (spec PIP §7.4).</summary>
    /// <param name="partition">The partition.</param>
    /// <param name="lastSequence">The last sequence of the log at recovery.</param>
    void OnRecoveryCompleted(int partition, long lastSequence)
    {
    }

    /// <summary>Called every <c>Ingest:TickInterval</c> while the worker is not processing a batch.</summary>
    /// <param name="context">The partition context of this call.</param>
    void OnTick(PartitionContext context)
    {
    }
}

/// <summary>An observation that changed nothing (spec PIP §7.2).</summary>
/// <param name="Device">The device, or <see langword="null"/> for a server.</param>
/// <param name="Server">The server, or <see langword="null"/> for a device.</param>
/// <param name="Property">The property observed again, or <see langword="null"/> for an unchanged health state.</param>
/// <param name="ReceivedTime">When the observation was received.</param>
/// <param name="SourceTime">The time in the integrated system, if known.</param>
public sealed record TouchInfo(DeviceId? Device, ServerId? Server, PropertyPath? Property, DateTimeOffset ReceivedTime, DateTimeOffset? SourceTime);

/// <summary>A source time more than <c>Ingest:MaxFutureSkew</c> in the future (spec PIP §7.1, step 6).</summary>
/// <param name="Device">The device, or <see langword="null"/> for a server.</param>
/// <param name="Server">The server, or <see langword="null"/> for a device.</param>
/// <param name="Connection">The connection that reported.</param>
/// <param name="SourceTime">The source time that was reported.</param>
/// <param name="ReceivedTime">The received time that replaced it.</param>
public sealed record ClockDriftHint(DeviceId? Device, ServerId? Server, ConnectionId Connection, DateTimeOffset SourceTime, DateTimeOffset ReceivedTime);

/// <summary>
/// Runs work on a partition's worker, serialised with record processing (spec PIP §7.3). Used for registry changes,
/// snapshots and timers of modules. Posted work runs before queued observations and may emit follow-up records.
/// </summary>
public interface IPartitionDispatcher
{
    /// <summary>Queues work; completes when the work ran. Waits while the partition's control queue is full.</summary>
    /// <param name="partition">The partition, 0…63.</param>
    /// <param name="work">The work.</param>
    /// <param name="ct">Cancels waiting; work that already started still completes.</param>
    /// <returns>A task completing when the work ran; faulted with the exception of the work.</returns>
    ValueTask PostAsync(int partition, Func<PartitionContext, ValueTask> work, CancellationToken ct);

    /// <summary>Queues work and returns its result.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="partition">The partition, 0…63.</param>
    /// <param name="work">The work.</param>
    /// <param name="ct">Cancels waiting; work that already started still completes.</param>
    /// <returns>The result of the work.</returns>
    ValueTask<T> InvokeAsync<T>(int partition, Func<PartitionContext, ValueTask<T>> work, CancellationToken ct);
}

/// <summary>
/// Provides the current values to the change detection of the pipeline at recovery (spec PIP §7.2, §7.4);
/// implemented by STA. Called after the replay, before <see cref="IPartitionSubscriber.OnRecoveryCompleted"/>.
/// </summary>
public interface ILastValueSource
{
    /// <summary>Writes the current property values and health states of the targets of a partition.</summary>
    /// <param name="partition">The partition.</param>
    /// <param name="sink">Receives the values.</param>
    void ProvideLastValues(int partition, ILastValueSink sink);
}

/// <summary>Receives the current values of a partition (see <see cref="ILastValueSource"/>).</summary>
public interface ILastValueSink
{
    /// <summary>Sets the last value of a property of a device or server.</summary>
    /// <param name="target">The device or server id.</param>
    /// <param name="path">The property.</param>
    /// <param name="value">The value.</param>
    /// <param name="quality">The quality.</param>
    void SetProperty(Guid target, PropertyPath path, WpValue value, Quality quality);

    /// <summary>Sets the health state of a device or server.</summary>
    /// <param name="target">The device or server id.</param>
    /// <param name="health">The health state.</param>
    void SetHealth(Guid target, HealthRecordBody health);
}

/// <summary>
/// Told when a connection floods the ingest (spec PIP §8); implemented by IRT, which reports the health fault
/// <c>flooding</c> of the connection. Called from ingest threads: implementations must be thread-safe and fast.
/// </summary>
public interface IIngestFloodingListener
{
    /// <summary>The flooding state of a connection changed.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="flooding"><see langword="true"/> when flooding started, <see langword="false"/> when it ended.</param>
    /// <param name="pending">The events and health observations waiting for the quota.</param>
    void OnFloodingChanged(ConnectionId connection, bool flooding, int pending);
}

/// <summary>
/// Starts and stops the ingest (spec PIP §7.4). The host calls <see cref="RecoverAsync"/> in start-up phase
/// <c>Recovery</c>, <see cref="StartAsync"/> in phase <c>Runtime</c> and <see cref="StopAsync"/> at shutdown.
/// </summary>
public interface IPipelineLifecycle
{
    /// <summary>Gets a value indicating whether the partition workers process input.</summary>
    bool Running { get; }

    /// <summary>Runs the recovery handshake: subscribers restore, the log is replayed after their checkpoints.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when every partition is recovered.</returns>
    /// <exception cref="PipelineException"><c>pipeline.log.unavailable</c>: the log could not be read.</exception>
    Task RecoverAsync(CancellationToken ct);

    /// <summary>Starts the partition workers and the side-effect workers; recovers first if that was not done.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when the workers run.</returns>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Stops accepting observations, lets the workers finish what is queued for at most the drain time, then stops
    /// them; observations still queued are rejected with <c>pipeline.log.unavailable</c>.
    /// </summary>
    /// <param name="ct">Cancels the drain: the workers are stopped at once.</param>
    /// <returns>A task completing when every worker stopped.</returns>
    Task StopAsync(CancellationToken ct);
}
