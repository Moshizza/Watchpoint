using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Kernel.Json;

namespace Watchpoint.Api.Contracts;

/// <summary>JSON options of the REST API (README §6.1, API §4.1): camelCase, strict, maximum depth 32.</summary>
public static class ApiJson
{
    /// <summary>The maximum depth of a request body (API §4.1); depth 33 → 400.</summary>
    public const int MaxDepth = 32;

    /// <summary>Gets the serializer options used for request bodies and responses.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Applies the API settings to options owned by ASP.NET Core (<c>ConfigureHttpJsonOptions</c>).</summary>
    /// <param name="options">The options to change.</param>
    public static void Apply(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = Options.PropertyNamingPolicy;
        options.MaxDepth = MaxDepth;
        options.NumberHandling = Options.NumberHandling;
        options.AllowTrailingCommas = false;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.UnmappedMemberHandling = Options.UnmappedMemberHandling;
        options.TypeInfoResolver = Options.TypeInfoResolver;
        foreach (var converter in Options.Converters)
        {
            options.Converters.Add(converter);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(WatchpointJson.Create(JsonProfile.Api, new DefaultJsonTypeInfoResolver())) { MaxDepth = MaxDepth };
        options.MakeReadOnly();
        return options;
    }
}
