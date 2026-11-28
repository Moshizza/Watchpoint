using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// The partition subscriber of the state module (spec STA §3, PIP §7.3): applies every record kind of M1 at
/// <see cref="Order"/> 100, refreshes staleness on <see cref="PartitionContext.IsRecovering"/>-free ticks, and
/// restores quality after a touch. At recovery it reports the snapshot checkpoint of each partition and, when the
/// partition completed its replay, recomputes the derived fields (spec STA §8.2). Thin by design: all behaviour
/// lives in <see cref="StateStore"/> and <see cref="StateRecovery"/>, which unit tests call directly without a
/// running pipeline.
/// </summary>
internal sealed class StateSubscriber(StateStore store, ServerAvailabilityCascade cascade, StateRecovery recovery) : IPartitionSubscriber
{
    public int Order => 100;

    public string Name => "state";

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        if (context.IsRecovering)
        {
            recovery.CountReplayed(records.Count);
        }

        var transitions = store.ApplyRecords(context.Partition, context.Now, context.IsRecovering, records);
        foreach (var (server, unavailable) in transitions)
        {
            cascade.OnServerHealthChanged(server, unavailable);
        }
    }

    public void OnTouched(PartitionContext context, TouchInfo touch) =>
        store.ApplyTouch(context.Partition, context.Now, touch);

    public void OnTick(PartitionContext context) => store.Tick(context.Partition, context.Now);

    public long? GetRecoveryCheckpoint(int partition) => recovery.Restored ? recovery.GetCheckpoint(partition) : null;

    public void OnRecoveryCompleted(int partition, long lastSequence) => recovery.OnPartitionRecovered(partition);
}
