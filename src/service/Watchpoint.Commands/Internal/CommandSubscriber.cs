using Microsoft.Extensions.DependencyInjection;
using Watchpoint.Commands.Internal.Dispatch;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>
/// The partition subscriber of the commands module (spec CMD §6, PIP §7.3: order 200). The dispatch (CMD §7) posts work
/// through the pipeline, which owns its subscribers: it is resolved on the first call, not injected, to keep the
/// service graph acyclic; it registers itself as the processor's sink before any record is applied.
/// </summary>
internal sealed class CommandSubscriber(CommandProcessor processor, IServiceProvider services) : IPartitionSubscriber
{
    private readonly Lazy<CommandDispatch> _dispatch = new(services.GetRequiredService<CommandDispatch>);

    public int Order => 200;

    public string Name => "commands";

    public void OnRecoveryStarting(int partition) => _ = _dispatch.Value;

    public void OnRecords(PartitionContext context, IReadOnlyList<LogRecord> records)
    {
        ArgumentNullException.ThrowIfNull(context);
        _ = _dispatch.Value;
        processor.OnRecords(context.Partition, records, context.IsRecovering);
    }

    /// <summary>CMD §8: requeue or settle what the recovered pending store holds.</summary>
    public void OnRecoveryCompleted(int partition, long lastSequence) => _dispatch.Value.Recovered(partition);

    public void OnTick(PartitionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        processor.OnTick(new PartitionEmitter(context));
    }
}
