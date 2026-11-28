using System.Text.Json.Serialization;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Storage;

namespace Watchpoint.Rights.Internal.Configuration;

/// <summary>The JSON document of one user's grants (configuration object type <c>rights.grants</c>, REST body of the grants API).</summary>
/// <param name="Grants">The grants.</param>
internal sealed record GrantsDocument(List<GrantDto> Grants);

/// <summary>One grant in JSON.</summary>
internal sealed record GrantDto(Guid? Id, string Permission, ScopeDto Scope, string? Schedule, ConstraintsDto? Constraints, string? SourceJson, DateTimeOffset? CreatedAt, Guid? CreatedBy)
{
    public static GrantDto Of(RightGrant g) => new(g.Id.Value, g.Permission.Value, JsonScope(g.Scope), g.ScheduleCalendar, JsonConstraints(g.Constraints),
        RightsJson.Source(g.Source), g.CreatedAt, g.CreatedBy.Value);

    public RightGrant ToGrant(UserId user) => new(Id is { } id ? RightGrantId.From(id) : RightGrantId.New(), user, PermissionCode.FromTrusted(Permission),
        RightsJson.Scope(System.Text.Json.JsonSerializer.Serialize(Scope, RightsJsonContext.Default.ScopeDto)), Schedule,
        RightsJson.Constraints(System.Text.Json.JsonSerializer.Serialize(Constraints ?? new ConstraintsDto(), RightsJsonContext.Default.ConstraintsDto)),
        SourceJson is null ? new GrantSource.Manual() : RightsJson.Source(SourceJson), CreatedAt ?? DateTimeOffset.UnixEpoch, CreatedBy is { } c ? UserId.From(c) : default);

    private static ScopeDto JsonScope(GrantScope s) => System.Text.Json.JsonSerializer.Deserialize(RightsJson.Scope(s), RightsJsonContext.Default.ScopeDto)!;

    private static ConstraintsDto JsonConstraints(GrantConstraints c) => System.Text.Json.JsonSerializer.Deserialize(RightsJson.Constraints(c), RightsJsonContext.Default.ConstraintsDto)!;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GrantsDocument))]
internal sealed partial class GrantDocumentContext : JsonSerializerContext;
