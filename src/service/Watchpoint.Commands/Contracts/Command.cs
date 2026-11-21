using System.Text.Json.Serialization;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Commands.Contracts;

/// <summary>A command to one device and its lifecycle (spec CMD §4). Immutable: every transition is a new value.</summary>
public sealed record Command
{
    /// <summary>The command id.</summary>
    public required CommandId Id { get; init; }

    /// <summary>The issuer's idempotency key (at most 64 characters).</summary>
    public string? ClientCommandId { get; init; }

    /// <summary>The group the command belongs to (T2.03).</summary>
    public CommandId? GroupId { get; init; }

    /// <summary>The target device.</summary>
    public required DeviceId Target { get; init; }

    /// <summary>The command path, e.g. <c>openable.open</c>.</summary>
    public required CommandPath Path { get; init; }

    /// <summary>The validated arguments, defaults applied; only parameters the definition declares.</summary>
    public required IReadOnlyDictionary<string, WpValue> Arguments { get; init; }

    /// <summary>The issuer.</summary>
    public required CommandIssuer Issuer { get; init; }

    /// <summary>The reason given by the issuer.</summary>
    public string? Reason { get; init; }

    /// <summary>The effective risk (definition, <c>riskByArgument</c>).</summary>
    public required Risk EffectiveRisk { get; init; }

    /// <summary>The dispatch priority.</summary>
    public required CommandPriority Priority { get; init; }

    /// <summary>The execution timeout.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Not dispatched before this instant.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Not dispatched after this instant (<c>Commands:DefaultExpiry</c> after the request or <see cref="NotBefore"/>, plus the approval window with four-eyes).</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The expected effect to verify after success (T2.03).</summary>
    [JsonIgnore]
    public ExpectedEffect? Verification { get; init; }

    /// <summary>The expected effect as serialised in records and snapshots.</summary>
    [JsonInclude]
    [JsonPropertyName("verification")]
    internal Internal.ExpectedEffectJson? VerificationJson
    {
        get => Verification is { } v ? new Internal.ExpectedEffectJson(v.Property.Value, v.EqualsValue, v.In, v.Within) : null;
        init => Verification = value is { } v ? new ExpectedEffect(PropertyPath.FromTrusted(v.Property), v.EqualsValue, v.In, v.Within) : null;
    }

    /// <summary>The approval, when the command needs one.</summary>
    public CommandApproval? Approval { get; init; }

    /// <summary>The correlation carried by every record of the command (<c>Command</c> is the command's own id).</summary>
    public RecordCorrelation Correlation { get; init; } = RecordCorrelation.None;

    /// <summary>The current state.</summary>
    public required CommandState State { get; init; }

    /// <summary>When the command was requested.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the command was queued.</summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>When the command was dispatched.</summary>
    public DateTimeOffset? DispatchedAt { get; init; }

    /// <summary>When the command reached its outcome (terminal state or success).</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When the expected effect was verified or found missing.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>The dispatch attempt (2 after a recovery requeue, CMD §8).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Why the command failed, was rejected, or timed out.</summary>
    public CommandFailure? Failure { get; init; }
}

/// <summary>The approval of a four-eyes command (spec CMD §4).</summary>
/// <param name="ApprovedBy">The approver, once approved or rejected.</param>
/// <param name="ApprovedAt">When it was approved or rejected.</param>
/// <param name="RequestExpiresAt">The end of the approval window.</param>
public sealed record CommandApproval(UserId? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset RequestExpiresAt);

/// <summary>Why a command did not succeed (spec CMD §4).</summary>
/// <param name="Category">The failure category.</param>
/// <param name="MessageKey"><c>errors.&lt;code&gt;</c> or a package key <c>packages.&lt;id&gt;.&lt;key&gt;</c>.</param>
/// <param name="Args">The message arguments, as invariant-culture text.</param>
/// <param name="VendorCode">The connector's own code, if any.</param>
public sealed record CommandFailure(FailureCategory Category, string MessageKey, IReadOnlyDictionary<string, string?> Args, string? VendorCode);
