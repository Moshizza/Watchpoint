using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Commands.Contracts;

/// <summary>
/// The rights hook of commands (spec CMD §5 step 6), implemented by RGT (RGT §6.1). Until RGT is registered the module
/// uses its fallback (administrators may run everything, other users normal and sensitive commands). Called on the
/// request path: in memory, no I/O.
/// </summary>
public interface ICommandAuthorizer
{
    /// <summary>Decides whether <paramref name="issuer"/> may run a command on a device, and under which constraints.</summary>
    /// <param name="issuer">The issuer (its <see cref="CommandIssuer.Identity"/> is evaluated).</param>
    /// <param name="device">The target device.</param>
    /// <param name="command">The command definition.</param>
    /// <param name="effectiveRisk">The effective risk of this invocation.</param>
    /// <param name="arguments">The validated arguments.</param>
    /// <param name="now">The request time (grant schedules).</param>
    /// <returns>The decision.</returns>
    CommandAuthorization Authorize(CommandIssuer issuer, Device device, CommandDefinition command, Risk effectiveRisk,
        IReadOnlyDictionary<string, WpValue> arguments, DateTimeOffset now);

    /// <summary>Whether <paramref name="approver"/> may approve or reject <paramref name="command"/> (<c>commands.approve</c> on its scope).</summary>
    /// <param name="approver">The approver.</param>
    /// <param name="command">The command awaiting approval.</param>
    /// <param name="now">The decision time.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    bool CanApprove(UserId approver, Command command, DateTimeOffset now);
}

/// <summary>An authorisation decision (spec CMD §5 step 6).</summary>
public sealed record CommandAuthorization
{
    private CommandAuthorization(Error? denial, CommandConstraints constraints)
    {
        Denial = denial;
        Constraints = constraints;
    }

    /// <summary>Whether the command may run.</summary>
    public bool Allowed => Denial is null;

    /// <summary>Why not: <c>common.forbidden</c>, <c>commands.outside_schedule</c>…</summary>
    public Error? Denial { get; }

    /// <summary>The constraints that apply when allowed.</summary>
    public CommandConstraints Constraints { get; }

    /// <summary>Allows the command under <paramref name="constraints"/>.</summary>
    /// <param name="constraints">The constraints.</param>
    /// <returns>The decision.</returns>
    public static CommandAuthorization Allow(CommandConstraints constraints) => new(null, constraints ?? throw new ArgumentNullException(nameof(constraints)));

    /// <summary>Denies the command.</summary>
    /// <param name="denial">The error.</param>
    /// <returns>The decision.</returns>
    public static CommandAuthorization Deny(Error denial) => new(denial ?? throw new ArgumentNullException(nameof(denial)), CommandConstraints.None);
}

/// <summary>Constraints on an allowed command (spec CMD §5 step 6).</summary>
/// <param name="ReasonRequired">A reason is required.</param>
/// <param name="StepUpRequired">A step-up proof is required.</param>
/// <param name="ApprovalRequired">A second person must approve (four-eyes).</param>
/// <param name="AllowedClientKinds">The clients a user may run it from; <see langword="null"/> for any.</param>
/// <param name="MayRaiseToCritical">The issuer holds <c>commands.priority.critical</c>.</param>
public sealed record CommandConstraints(bool ReasonRequired = false, bool StepUpRequired = false, bool ApprovalRequired = false,
    IReadOnlySet<ClientKind>? AllowedClientKinds = null, bool MayRaiseToCritical = false)
{
    /// <summary>No constraint.</summary>
    public static CommandConstraints None { get; } = new();
}
