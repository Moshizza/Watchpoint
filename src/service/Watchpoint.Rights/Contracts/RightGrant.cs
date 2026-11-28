using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Documents;

namespace Watchpoint.Rights.Contracts;

/// <summary>A right granted to a user (spec RGT §5).</summary>
/// <param name="Id">The grant id.</param>
/// <param name="User">The user.</param>
/// <param name="Permission">The permission code or pattern (<c>prefix.*</c>, <c>*</c>).</param>
/// <param name="Scope">The scope.</param>
/// <param name="ScheduleCalendar">SCH calendar key; <see langword="null"/> = always.</param>
/// <param name="Constraints">The constraints.</param>
/// <param name="Source">Where the grant comes from.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="CreatedBy">Creator.</param>
public sealed record RightGrant(RightGrantId Id, UserId User, PermissionCode Permission, GrantScope Scope, string? ScheduleCalendar,
    GrantConstraints Constraints, GrantSource Source, DateTimeOffset CreatedAt, UserId CreatedBy);

/// <summary>The objects a grant covers (spec RGT §5): non-null lists are ANDed, explicit devices are OR'ed with the rest.</summary>
/// <param name="Everything">All objects; every list is then <see langword="null"/>.</param>
/// <param name="LocationsSubtree">Locations including their descendants.</param>
/// <param name="Servers">Servers.</param>
/// <param name="Categories">Device categories.</param>
/// <param name="Types">Device types (or any descendant type).</param>
/// <param name="TagsAny">At least one of these tags.</param>
/// <param name="TagsExclude">None of these tags.</param>
/// <param name="Devices">Explicit devices, always included.</param>
/// <param name="Sites">Sites (M9).</param>
public sealed record GrantScope(bool Everything = false, IReadOnlyList<LocationId>? LocationsSubtree = null, IReadOnlyList<ServerId>? Servers = null,
    IReadOnlyList<Category>? Categories = null, IReadOnlyList<TypeId>? Types = null, IReadOnlyList<TagId>? TagsAny = null,
    IReadOnlyList<TagId>? TagsExclude = null, IReadOnlyList<DeviceId>? Devices = null, IReadOnlyList<SiteId>? Sites = null)
{
    /// <summary>The scope that covers everything.</summary>
    public static GrantScope All { get; } = new(true);
}

/// <summary>Constraints of a grant (spec RGT §5).</summary>
/// <param name="RequireReason">A reason is required.</param>
/// <param name="RequireFourEyes">Four-eyes approval is required.</param>
/// <param name="RequireStepUp">A step-up is required.</param>
/// <param name="MaxDuration">Maximum duration.</param>
/// <param name="Priorities">Alarm priority range.</param>
/// <param name="ClientKinds">Allowed clients; <see langword="null"/> = any.</param>
/// <param name="MaxLookback">Maximum playback look-back.</param>
public sealed record GrantConstraints(bool RequireReason = false, bool RequireFourEyes = false, bool RequireStepUp = false,
    TimeSpan? MaxDuration = null, PriorityRange? Priorities = null, IReadOnlySet<ClientKind>? ClientKinds = null, TimeSpan? MaxLookback = null)
{
    /// <summary>No constraint.</summary>
    public static GrantConstraints None { get; } = new();
}

/// <summary>An inclusive range of alarm priorities.</summary>
/// <param name="Min">Lowest priority.</param>
/// <param name="Max">Highest priority.</param>
public sealed record PriorityRange(int Min, int Max);

/// <summary>Where a grant comes from (spec RGT §5).</summary>
public abstract record GrantSource
{
    private GrantSource()
    {
    }

    /// <summary>Entered by hand.</summary>
    public sealed record Manual : GrantSource;

    /// <summary>Copied by the rights-copy tool.</summary>
    /// <param name="SourceUser">The user copied from.</param>
    /// <param name="CopyOperationId">The operation.</param>
    /// <param name="At">When.</param>
    /// <param name="SourceWasTemplate">The source is a template user.</param>
    public sealed record CopiedFrom(UserId SourceUser, Guid CopyOperationId, DateTimeOffset At, bool SourceWasTemplate) : GrantSource;

    /// <summary>Mapped from a directory group.</summary>
    /// <param name="ProviderId">The identity provider.</param>
    /// <param name="GroupDn">The group.</param>
    /// <param name="TemplateUser">The template user.</param>
    public sealed record DirectoryMapping(Guid ProviderId, string GroupDn, UserId TemplateUser) : GrantSource;
}
