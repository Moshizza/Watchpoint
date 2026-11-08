using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Abstractions;

/// <summary>Source-generated JSON metadata of the contract types used in manifests and settings references.</summary>
public static class IntegrationJson
{
    /// <summary>Gets the resolver to pass to <c>WatchpointJson.Create</c>.</summary>
    public static IJsonTypeInfoResolver Resolver => IntegrationJsonContext.Default;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InboundAuth))]
internal sealed partial class IntegrationJsonContext : JsonSerializerContext;
