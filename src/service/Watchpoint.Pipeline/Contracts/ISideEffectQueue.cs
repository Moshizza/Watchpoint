namespace Watchpoint.Pipeline.Contracts;

/// <summary>The classes of side effects (spec PIP §9); each has its own queue and workers, so a lower class never blocks a higher one.</summary>
public enum SideEffectClass
{
    /// <summary>Actions of alarms.</summary>
    AlarmAction = 0,

    /// <summary>Notifications.</summary>
    Notification = 1,

    /// <summary>Snapshots.</summary>
    Snapshot = 2,

    /// <summary>Scripts.</summary>
    Script = 3,

    /// <summary>Webhooks.</summary>
    Webhook = 4,

    /// <summary>Background work.</summary>
    Background = 5,
}

/// <summary>Slow work run outside the partition workers (spec PIP §9).</summary>
/// <param name="Id">The idempotency key: an effect whose id was already enqueued on this node is not run again.</param>
/// <param name="Class">The class, which selects the queue.</param>
/// <param name="Kind">The kind, for logs and metrics (e.g. <c>mail</c>).</param>
/// <param name="Run">The work; must honour the cancellation token.</param>
/// <param name="Timeout">The longest duration of one run.</param>
/// <param name="MaxAttempts">How many times the work is run until it succeeds (at least 1).</param>
public sealed record SideEffect(Guid Id, SideEffectClass Class, string Kind, Func<CancellationToken, Task> Run, TimeSpan Timeout, int MaxAttempts = 1);

/// <summary>The side-effect queue (spec PIP §9): one bounded queue and a set of workers per class.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by spec PIP §9.")]
public interface ISideEffectQueue
{
    /// <summary>Enqueues an effect without waiting.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>
    /// <see langword="true"/> when the effect is queued or its id was already enqueued (it is not run twice);
    /// <see langword="false"/> when the queue of its class is full or the queue stopped (counted and logged).
    /// </returns>
    bool TryEnqueue(SideEffect effect);

    /// <summary>Returns the counters of every class.</summary>
    /// <returns>The statistics.</returns>
    SideEffectQueueStats GetStats();
}

/// <summary>Counters of the side-effect queue.</summary>
/// <param name="Classes">The counters per class.</param>
public sealed record SideEffectQueueStats(IReadOnlyDictionary<SideEffectClass, SideEffectClassStats> Classes);

/// <summary>Counters of one side-effect class since the node started.</summary>
/// <param name="Queued">Effects waiting.</param>
/// <param name="Running">Effects running or waiting for a retry.</param>
/// <param name="Completed">Effects that succeeded.</param>
/// <param name="Failed">Effects that failed on their last attempt.</param>
/// <param name="Rejected">Effects refused because the queue was full or stopped.</param>
/// <param name="Duplicates">Effects ignored because their id was already enqueued.</param>
public sealed record SideEffectClassStats(int Queued, int Running, long Completed, long Failed, long Rejected, long Duplicates);
