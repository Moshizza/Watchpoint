using System.Text.Json.Serialization;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Kernel.Json;

/// <summary>Source-generated metadata of the Kernel's serialisable types (appended by <see cref="WatchpointJson.Create"/>).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LocalizedName))]
internal sealed partial class KernelJsonContext : JsonSerializerContext;
