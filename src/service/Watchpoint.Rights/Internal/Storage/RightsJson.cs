using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Model.Documents;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Storage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<ClientKind>), typeof(JsonStringEnumConverter<Category>)])]
[JsonSerializable(typeof(ScopeDto))]
[JsonSerializable(typeof(ConstraintsDto))]
[JsonSerializable(typeof(SourceDto))]
[JsonSerializable(typeof(List<Guid>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class RightsJsonContext : JsonSerializerContext;

/// <summary>JSON form of <see cref="GrantScope"/>.</summary>
internal sealed record ScopeDto(bool Everything, List<Guid>? Locations = null, List<Guid>? Servers = null, List<string>? Categories = null,
    List<string>? Types = null, List<Guid>? TagsAny = null, List<Guid>? TagsExclude = null, List<Guid>? Devices = null, List<Guid>? Sites = null);

/// <summary>JSON form of <see cref="GrantConstraints"/> (durations in seconds).</summary>
internal sealed record ConstraintsDto(bool RequireReason = false, bool RequireFourEyes = false, bool RequireStepUp = false, double? MaxDurationSeconds = null,
    int? PriorityMin = null, int? PriorityMax = null, List<string>? ClientKinds = null, double? MaxLookbackSeconds = null);

/// <summary>JSON form of <see cref="GrantSource"/> (no type names are read from input).</summary>
internal sealed record SourceDto(string Kind, Guid? SourceUser = null, Guid? OperationId = null, DateTimeOffset? At = null, bool SourceWasTemplate = false,
    Guid? ProviderId = null, string? GroupDn = null, Guid? TemplateUser = null);

/// <summary>Converts rights values to and from their stored JSON.</summary>
internal static class RightsJson
{
    public static string Scope(GrantScope s) => JsonSerializer.Serialize(new ScopeDto(s.Everything,
        s.LocationsSubtree?.Select(x => x.Value).ToList(), s.Servers?.Select(x => x.Value).ToList(), s.Categories?.Select(x => x.ToString()).ToList(),
        s.Types?.Select(x => x.Value).ToList(), s.TagsAny?.Select(x => x.Value).ToList(), s.TagsExclude?.Select(x => x.Value).ToList(),
        s.Devices?.Select(x => x.Value).ToList(), s.Sites?.Select(x => x.Value).ToList()), RightsJsonContext.Default.ScopeDto);

    public static GrantScope Scope(string json)
    {
        var d = JsonSerializer.Deserialize(json, RightsJsonContext.Default.ScopeDto) ?? new ScopeDto(true);
        return new GrantScope(d.Everything, d.Locations?.Select(LocationId.From).ToList(), d.Servers?.Select(ServerId.From).ToList(),
            d.Categories?.Select(Enum.Parse<Category>).ToList(), d.Types?.Select(TypeId.FromTrusted).ToList(), d.TagsAny?.Select(TagId.From).ToList(),
            d.TagsExclude?.Select(TagId.From).ToList(), d.Devices?.Select(DeviceId.From).ToList(), d.Sites?.Select(SiteId.From).ToList());
    }

    public static string Constraints(GrantConstraints c) => JsonSerializer.Serialize(new ConstraintsDto(c.RequireReason, c.RequireFourEyes, c.RequireStepUp,
        c.MaxDuration?.TotalSeconds, c.Priorities?.Min, c.Priorities?.Max, c.ClientKinds?.Select(x => x.ToString()).ToList(), c.MaxLookback?.TotalSeconds),
        RightsJsonContext.Default.ConstraintsDto);

    public static GrantConstraints Constraints(string json)
    {
        var d = JsonSerializer.Deserialize(json, RightsJsonContext.Default.ConstraintsDto) ?? new ConstraintsDto();
        return new GrantConstraints(d.RequireReason, d.RequireFourEyes, d.RequireStepUp,
            d.MaxDurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            d.PriorityMin is { } min && d.PriorityMax is { } max ? new PriorityRange(min, max) : null,
            d.ClientKinds is { } k ? new HashSet<ClientKind>(k.Select(Enum.Parse<ClientKind>)) : null,
            d.MaxLookbackSeconds is { } l ? TimeSpan.FromSeconds(l) : null);
    }

    public static string Source(GrantSource s) => JsonSerializer.Serialize(s switch
    {
        GrantSource.CopiedFrom c => new SourceDto("copiedFrom", c.SourceUser.Value, c.CopyOperationId, c.At, c.SourceWasTemplate),
        GrantSource.DirectoryMapping d => new SourceDto("directoryMapping", ProviderId: d.ProviderId, GroupDn: d.GroupDn, TemplateUser: d.TemplateUser.Value),
        _ => new SourceDto("manual"),
    }, RightsJsonContext.Default.SourceDto);

    public static GrantSource Source(string json)
    {
        var d = JsonSerializer.Deserialize(json, RightsJsonContext.Default.SourceDto);
        return d?.Kind switch
        {
            "copiedFrom" => new GrantSource.CopiedFrom(UserId.From(d.SourceUser!.Value), d.OperationId!.Value, d.At!.Value, d.SourceWasTemplate),
            "directoryMapping" => new GrantSource.DirectoryMapping(d.ProviderId!.Value, d.GroupDn!, UserId.From(d.TemplateUser!.Value)),
            _ => new GrantSource.Manual(),
        };
    }

    public static string Name(LocalizedName n) => JsonSerializer.Serialize(new Dictionary<string, string>(n.Translations) { ["default"] = n.Default }, RightsJsonContext.Default.DictionaryStringString);

    public static LocalizedName Name(string json)
    {
        var d = JsonSerializer.Deserialize(json, RightsJsonContext.Default.DictionaryStringString) ?? [];
        var def = d.TryGetValue("default", out var x) ? x : string.Empty;
        d.Remove("default");
        return new LocalizedName(def, d);
    }

    public static string Guids(IEnumerable<Guid> g) => JsonSerializer.Serialize(g.ToList(), RightsJsonContext.Default.ListGuid);

    public static List<Guid> Guids(string json) => JsonSerializer.Deserialize(json, RightsJsonContext.Default.ListGuid) ?? [];
}
