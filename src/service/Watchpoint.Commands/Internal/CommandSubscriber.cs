using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>The partition subscriber of the commands module (spec CMD §6, PIP §7.3: order 200).</summary>
internal sealed class CommandSubscriber(CommandProcessor processor, Dispatch.CommandDispatch dispatch) : IPartitionSubscriber
{
    public int Order => 200;

    public string Name => "commands";

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        processor.OnRecords(context.Partition, records, context.IsRecovering);
    }

    /// <summary>CMD §8: requeue or settle what the recovered pending store holds.</summary>
    public void OnRecoveryCompleted(int partition, long lastSequence) => dispatch.Recovered(partition);

    public void OnTick(PartitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        processor.OnTick(new PartitionEmitter(context));
    }
}
