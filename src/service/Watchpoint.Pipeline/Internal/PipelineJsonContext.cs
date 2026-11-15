using System.Text.Json.Serialization;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal;

/// <summary>Source-generated JSON metadata of the M1 record bodies (spec PIP §5.3), used with the <c>Log</c> JSON profile.</summary>
[JsonSerializable(typeof(PropertyRecordBody))]
[JsonSerializable(typeof(EventRecordBody))]
[JsonSerializable(typeof(HealthRecordBody))]
[JsonSerializable(typeof(RegistryChangedBody))]
internal sealed partial class PipelineJsonContext : JsonSerializerContext;
