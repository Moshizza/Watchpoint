using System.Text.Json.Serialization;
using Watchpoint.Audit.Contracts;

namespace Watchpoint.Audit.Internal;

/// <summary>An audit record after validation, redaction and canonicalisation: what the writer queues and the fallback file stores.</summary>
internal sealed record PreparedRecord(
    DateTimeOffset OccurredAt,
    string Action,
    short Outcome,
    short Severity,
    string ActorJson,
    Guid? ActorId,
    string ObjectsJson,
    Guid[] ObjectIds,
    string? Reason,
    string? DetailsJson,
    Guid? CorrelationId);

/// <summary>An object as stored: ids only, never labels (spec AUD §13).</summary>
internal sealed record StoredObject(string Type, Guid? Id);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PreparedRecord))]
[JsonSerializable(typeof(AuditActor))]
[JsonSerializable(typeof(StoredObject[]))]
[JsonSerializable(typeof(VerificationReport))]
internal sealed partial class AuditJsonContext : JsonSerializerContext
{
}
