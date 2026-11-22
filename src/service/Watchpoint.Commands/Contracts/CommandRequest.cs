using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Commands.Contracts;

/// <summary>A command request (spec CMD §5).</summary>
/// <param name="Target">One device, several devices or a selector (several targets make a group).</param>
/// <param name="Path">The command path.</param>
/// <param name="Arguments">The arguments as JSON (an object, or undefined for none).</param>
/// <param name="Issuer">The issuer.</param>
/// <param name="Reason">The reason (at most 500 characters; required for critical risk or when the authorizer says so).</param>
/// <param name="StepUpToken">The step-up proof (IDN), when required.</param>
/// <param name="ClientCommandId">The idempotency key (at most 64 characters).</param>
/// <param name="Priority">A requested priority; lowering is always allowed, raising to critical needs a permission (CMD §4).</param>
/// <param name="NotBefore">Not dispatched before this instant (at most 24 h ahead).</param>
/// <param name="Timeout">The execution timeout; the definition's when absent.</param>
/// <param name="Correlation">The correlation of the cause (alarm, automation run).</param>
public sealed record CommandRequest(CommandTargetSpec Target, CommandPath Path, JsonElement Arguments,
    CommandIssuer Issuer, string? Reason, string? StepUpToken, string? ClientCommandId,
    CommandPriority? Priority, DateTimeOffset? NotBefore, TimeSpan? Timeout, RecordCorrelation? Correlation);

/// <summary>The target of a request (spec CMD §5).</summary>
public abstract record CommandTargetSpec
{
    private CommandTargetSpec()
    {
    }

    /// <summary>One device.</summary>
    /// <param name="Id">The device.</param>
    public sealed record Device(DeviceId Id) : CommandTargetSpec;

    /// <summary>Several devices (at most 5,000): a group.</summary>
    /// <param name="Ids">The devices.</param>
    public sealed record Devices(IReadOnlyList<DeviceId> Ids) : CommandTargetSpec;

    /// <summary>The devices a registry query selects, resolved once at request time: a group.</summary>
    /// <param name="Query">The query.</param>
    public sealed record Selector(DeviceQuery Query) : CommandTargetSpec;
}

/// <summary>The answer to an accepted request (spec CMD §5).</summary>
/// <param name="Id">The command id, or the group id.</param>
/// <param name="IsGroup">Whether a group was created.</param>
/// <param name="State">The state after validation (<see cref="CommandState.Queued"/> or <see cref="CommandState.AwaitingApproval"/>; for a group, queued when any child is).</param>
/// <param name="TargetCount">The number of target devices.</param>
public sealed record CommandAccepted(CommandId Id, bool IsGroup, CommandState State, int TargetCount);

/// <summary>A command a user may run on a device, with what running it requires (spec CMD §5).</summary>
/// <param name="Path">The command path.</param>
/// <param name="Parameters">The parameter schema.</param>
/// <param name="EffectiveRisk">The risk of the definition (arguments may raise it).</param>
/// <param name="RequiresReason">Whether a reason is required.</param>
/// <param name="RequiresStepUp">Whether a step-up proof is required.</param>
/// <param name="RequiresApproval">Whether a second person must approve.</param>
/// <param name="Batchable">Whether the command can be batched in a group.</param>
public sealed record AvailableCommand(CommandPath Path, ParameterSchema Parameters, Risk EffectiveRisk,
    bool RequiresReason, bool RequiresStepUp, bool RequiresApproval, bool Batchable);
