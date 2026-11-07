using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Kernel.Json;

/// <summary>Factory of the <see cref="JsonSerializerOptions"/> used everywhere in Watchpoint (spec K §9.1).</summary>
public static class WatchpointJson
{
    /// <summary>The maximum nesting depth of JSON input.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// Creates read-only options: camelCase names, enums as camelCase strings (numbers rejected), UTC date and
    /// ISO 8601 duration converters, strict numbers (no <c>NaN</c>/<c>Infinity</c>, no numbers in strings), depth
    /// 64 and unknown-member handling per <paramref name="profile"/>. Ids and <c>WpValue</c> carry their own
    /// converters.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <param name="resolvers">
    /// The source-generated contexts of the calling module (reflection-based serialisation is disabled in the host);
    /// the Kernel's own context is appended.
    /// </param>
    /// <returns>New read-only options.</returns>
    public static JsonSerializerOptions Create(JsonProfile profile, params IJsonTypeInfoResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown JSON profile.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = MaxDepth,
            NumberHandling = JsonNumberHandling.Strict,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = profile == JsonProfile.Document
                ? JsonUnmappedMemberHandling.Disallow
                : JsonUnmappedMemberHandling.Skip,
            TypeInfoResolver = JsonTypeInfoResolver.Combine([.. resolvers, KernelJsonContext.Default]),
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new Iso8601DurationConverter());
        options.MakeReadOnly();
        return options;
    }
}
