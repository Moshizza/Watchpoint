using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>Body of a <see cref="RecordKinds.Property"/> record (spec PIP §5.3).</summary>
/// <param name="Path">The property path, <c>capability.member</c>.</param>
/// <param name="Value">The new value.</param>
/// <param name="Quality">The quality of the value.</param>
/// <param name="Prev">The previous value, if any.</param>
public sealed record PropertyRecordBody(string Path, WpValue Value, Quality Quality, WpValue? Prev);

/// <summary>Body of a <see cref="RecordKinds.Event"/> record (spec PIP §5.3).</summary>
/// <param name="Path">The event path, <c>capability.member</c>.</param>
/// <param name="Payload">The event fields.</param>
/// <param name="Severity">The severity of the event.</param>
public sealed record EventRecordBody(string Path, IReadOnlyDictionary<string, WpValue> Payload, Severity Severity);

/// <summary>Body of a <see cref="RecordKinds.Health"/> record: the full health state (spec PIP §5.3).</summary>
/// <param name="Connectivity">The connectivity.</param>
/// <param name="Faults">The active faults.</param>
/// <param name="Reason">An optional reason.</param>
public sealed record HealthRecordBody(Connectivity Connectivity, IReadOnlyList<string> Faults, string? Reason);

/// <summary>Body of a <see cref="RecordKinds.RegistryChanged"/> record (spec PIP §5.3, REG §6).</summary>
/// <param name="Revision">The registry revision after the batch.</param>
/// <param name="Context">Who changed the registry and why.</param>
/// <param name="Changes">The entities created, updated or deleted by the batch.</param>
public sealed record RegistryChangedBody(long Revision, RegistryChangedContext Context, IReadOnlyList<RegistryChangedEntity> Changes);

/// <summary>The change context of a <see cref="RegistryChangedBody"/>.</summary>
/// <param name="User">The user who made the change, or <see langword="null"/> for the system.</param>
/// <param name="Reason">The audit reason, if any.</param>
/// <param name="CorrelationId">The correlation id, if any.</param>
public sealed record RegistryChangedContext(string? User, string? Reason, string? CorrelationId);

/// <summary>One changed entity of a <see cref="RegistryChangedBody"/>.</summary>
/// <param name="Entity">The entity type: <c>site</c>, <c>location</c>, <c>tagFamily</c>, <c>tag</c>, <c>server</c>, <c>device</c>, <c>link</c>, <c>customField</c>.</param>
/// <param name="Id">The entity id.</param>
/// <param name="Kind">What happened: <c>created</c>, <c>updated</c> or <c>deleted</c>.</param>
public sealed record RegistryChangedEntity(string Entity, Guid Id, string Kind);
