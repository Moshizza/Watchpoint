using System.Text.Json.Serialization;
using Watchpoint.Commands.Contracts;

namespace Watchpoint.Commands.Internal;

/// <summary>Source-generated serialisation of the command record bodies and snapshot items (JSON profile <c>Log</c>).</summary>
[JsonSerializable(typeof(Command))]
[JsonSerializable(typeof(CommandRejectedBody))]
[JsonSerializable(typeof(CommandAwaitingApprovalBody))]
[JsonSerializable(typeof(CommandApprovalBody))]
[JsonSerializable(typeof(CommandQueuedBody))]
[JsonSerializable(typeof(CommandDispatchedBody))]
[JsonSerializable(typeof(CommandSucceededBody))]
[JsonSerializable(typeof(CommandFailedBody))]
[JsonSerializable(typeof(CommandIdBody))]
[JsonSerializable(typeof(CommandByBody))]
[JsonSerializable(typeof(CommandVerificationBody))]
[JsonSerializable(typeof(CommandGroupRequestedBody))]
[JsonSerializable(typeof(CommandGroupProgressBody))]
internal sealed partial class CommandsJsonContext : JsonSerializerContext;

/// <summary>The serialised form of an expected effect (CMD §9): the property path as text.</summary>
internal sealed record ExpectedEffectJson(string Property, Watchpoint.Kernel.Values.WpValue? EqualsValue, IReadOnlyList<Watchpoint.Kernel.Values.WpValue>? In, TimeSpan? Within);
