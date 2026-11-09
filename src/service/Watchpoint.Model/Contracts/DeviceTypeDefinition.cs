using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>Flattened, immutable definition of a device or server type (spec MOD §4.1).</summary>
public sealed record DeviceTypeDefinition
{
    /// <summary>Gets the unique reference (type id and version).</summary>
    public required TypeRef Ref { get; init; }

    /// <summary>Gets the role (Device or Server).</summary>
    public required TypeRole Role { get; init; }

    /// <summary>Gets the category of the device type.</summary>
    public required Category Category { get; init; }

    /// <summary>Gets the source of the definition (<c>"standard"</c> or a package id, or marked removed).</summary>
    public required string Source { get; init; }

    /// <summary>Gets the lineage: self first, then ancestors in inheritance order.</summary>
    public required IReadOnlyList<TypeRef> Lineage { get; init; }

    /// <summary>Gets the capabilities included in this type definition.</summary>
    public required IReadOnlyDictionary<CapabilityId, CapabilityUseDefinition> Capabilities { get; init; }

    /// <summary>Gets all flattened properties declared across capabilities.</summary>
    public required IReadOnlyDictionary<PropertyPath, PropertyDefinition> Properties { get; init; }

    /// <summary>Gets all flattened commands declared across capabilities.</summary>
    public required IReadOnlyDictionary<CommandPath, CommandDefinition> Commands { get; init; }

    /// <summary>Gets all flattened events declared across capabilities.</summary>
    public required IReadOnlyDictionary<EventPath, EventDefinition> Events { get; init; }

    /// <summary>Gets the merged schema for device attributes.</summary>
    public required ParameterSchema Attributes { get; init; }

    /// <summary>Gets the allowed child type ids.</summary>
    public required IReadOnlyList<string> AllowedChildTypes { get; init; }

    /// <summary>Gets the licensing class.</summary>
    public required LicenceClass LicenceClass { get; init; }

    /// <summary>Gets the display settings (icon, symbol, primary property).</summary>
    public required TypeDisplay Display { get; init; }

    /// <summary>Gets the state overrides in priority order (descendant first).</summary>
    public required IReadOnlyList<StateOverride> StateOverrides { get; init; }

    /// <summary>Gets the default alarm rules.</summary>
    public required IReadOnlyList<string> DefaultAlarmRules { get; init; }

    /// <summary>Gets the default links (descendant first).</summary>
    public required IReadOnlyList<DefaultLink> DefaultLinks { get; init; }

    /// <summary>Returns whether this type uses the given capability.</summary>
    /// <param name="capability">The capability id.</param>
    /// <returns><see langword="true"/> if the capability is present; otherwise <see langword="false"/>.</returns>
    public bool Has(CapabilityId capability) => Capabilities.ContainsKey(capability);

    /// <summary>Checks whether this type is or inherits from the given ancestor type id.</summary>
    /// <param name="ancestorOrSelf">The type id to check against.</param>
    /// <returns><see langword="true"/> if found in the lineage; otherwise <see langword="false"/>.</returns>
    public bool IsA(TypeId ancestorOrSelf)
    {
        for (var i = 0; i < Lineage.Count; i++)
        {
            if (Lineage[i].Id == ancestorOrSelf)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Definition of capability usage within a device type.</summary>
/// <param name="Id">The capability id.</param>
/// <param name="Version">The semantic version of the capability.</param>
/// <param name="Optional">Whether the capability is optional on the device.</param>
public sealed record CapabilityUseDefinition(CapabilityId Id, SemVersion Version, bool Optional);

/// <summary>Flattened capability definition (spec MOD §4.1).</summary>
/// <param name="Id">The capability identifier.</param>
/// <param name="Version">The semantic version.</param>
/// <param name="Properties">The properties declared in this capability.</param>
/// <param name="Commands">The commands declared in this capability.</param>
/// <param name="Events">The events declared in this capability.</param>
public sealed record CapabilityDefinition(
    CapabilityId Id,
    SemVersion Version,
    IReadOnlyDictionary<MemberId, PropertyDefinition> Properties,
    IReadOnlyDictionary<MemberId, CommandDefinition> Commands,
    IReadOnlyDictionary<MemberId, EventDefinition> Events);

/// <summary>Definition of a state override rule.</summary>
/// <param name="Property">The property path that triggers this override.</param>
/// <param name="Values">The values that match this override.</param>
/// <param name="State">The overall state to set when matched.</param>
public sealed record StateOverride(PropertyPath Property, IReadOnlyList<WpValue> Values, OverallState State);
