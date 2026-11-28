using Watchpoint.Kernel.Ids;

namespace Watchpoint.Rights.Contracts;

/// <summary>The kind of scope a permission is granted on (spec RGT §4).</summary>
public enum PermissionScopeKind
{
    /// <summary>Site-wide only: the grant must cover everything.</summary>
    None,

    /// <summary>Devices (a scope of locations, servers, categories, types, tags, devices).</summary>
    Devices,

    /// <summary>Locations.</summary>
    Locations,

    /// <summary>The system (no device or location).</summary>
    System,
}

/// <summary>The constraints a grant may carry (spec RGT §4).</summary>
public enum ConstraintKind
{
    /// <summary>A reason must be given.</summary>
    RequireReason,

    /// <summary>A second person must approve.</summary>
    RequireFourEyes,

    /// <summary>A step-up proof is required.</summary>
    RequireStepUp,

    /// <summary>Maximum duration of the action.</summary>
    MaxDuration,

    /// <summary>Range of alarm priorities.</summary>
    PriorityRange,

    /// <summary>Clients the permission may be used from.</summary>
    ClientKinds,

    /// <summary>Maximum look-back of a playback.</summary>
    MaxLookback,
}

/// <summary>A permission of the catalogue (spec RGT §4).</summary>
/// <param name="Code">The code.</param>
/// <param name="Family">The family, e.g. <c>operation.alarms</c>.</param>
/// <param name="Module">The module that brings it.</param>
/// <param name="LabelKey">Translation key of the label.</param>
/// <param name="DescriptionKey">Translation key of the description.</param>
/// <param name="ScopeKind">The scope kind.</param>
/// <param name="AllowedConstraints">The constraints a grant of it may carry.</param>
/// <param name="Sensitive">Shown with a warning when granted.</param>
public sealed record PermissionDefinition(PermissionCode Code, string Family, string Module, string LabelKey, string DescriptionKey,
    PermissionScopeKind ScopeKind, IReadOnlySet<ConstraintKind> AllowedConstraints, bool Sensitive);
