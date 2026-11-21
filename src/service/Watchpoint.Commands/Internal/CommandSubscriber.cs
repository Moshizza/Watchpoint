using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>The partition subscriber of the commands module (spec CMD §6, PIP §7.3: order 200).</summary>
internal sealed class CommandSubscriber(CommandProcessor processor) : IPartitionSubscriber
{
    public int Order => 200;

    public string Name => "commands";

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        processor.OnRecords(context.Partition, records);
    }

    public void OnTick(PartitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        processor.OnTick(new PartitionEmitter(context));
    }
}
