namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// What a subscriber or posted work may do on a partition worker (spec PIP §7.3). Only valid during the call it is
/// passed to, on the worker.
/// </summary>
public sealed class PartitionContext
{
    private readonly ISideEffectQueue? _sideEffects;
    private List<LogEnvelope>? _followUps;

    internal PartitionContext(int partition, ISideEffectQueue? sideEffects)
    {
        Partition = partition;
        _sideEffects = sideEffects;
    }

    /// <summary>Gets the partition.</summary>
    public int Partition { get; }

    /// <summary>Gets the sequence of the last record of this partition that was appended or replayed.</summary>
    public long CurrentSequence { get; internal set; }

    /// <summary>Gets the time of the call (node clock, UTC).</summary>
    public DateTimeOffset Now { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the call replays records at recovery (spec PIP §7.4): side effects are not
    /// enqueued, and follow-up records are appended after the replay unless a record with the same id is replayed.
    /// </summary>
    public bool IsRecovering { get; internal set; }

    /// <summary>Gets the number of side effects dropped because they were enqueued during a replay.</summary>
    internal int SideEffectsSkipped { get; private set; }

    /// <summary>
    /// Emits a follow-up record (e.g. <c>alarm.raised</c>), appended to the same partition after the current batch and
    /// before new input. At most four levels of follow-ups are appended (a deeper one is dropped, log 1640).
    /// </summary>
    /// <param name="followUp">The record; its partition must be <see cref="Partition"/>.</param>
    /// <exception cref="ArgumentException">The record belongs to another partition.</exception>
    public void Emit(LogEnvelope followUp)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        if (followUp.P != Partition || (followUp.Dev is null && followUp.Srv is null))
        {
            throw new ArgumentException($"A follow-up record must be bound to a device or server of partition {Partition}.", nameof(followUp));
        }

        (_followUps ??= []).Add(followUp);
    }

    /// <summary>
    /// Enqueues slow work (spec PIP §9). The records of the current call are durable. Ignored during a replay: a side
    /// effect runs at most once.
    /// </summary>
    /// <param name="effect">The side effect.</param>
    public void EnqueueSideEffect(SideEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (IsRecovering || _sideEffects is null)
        {
            SideEffectsSkipped++;
            return;
        }

        _sideEffects.TryEnqueue(effect);
    }

    /// <summary>Returns the follow-up records emitted since the last call, or <see langword="null"/>.</summary>
    internal List<LogEnvelope>? TakeFollowUps()
    {
        var taken = _followUps;
        _followUps = null;
        return taken is { Count: > 0 } ? taken : null;
    }
}
