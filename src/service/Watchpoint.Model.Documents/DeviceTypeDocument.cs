using System.Text.Json;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model.Documents;

public enum TypeRole { Device, Server }

public enum Category
{
    AccessControl,
    Video,
    Intrusion,
    Fire,
    IntercomAudio,
    Perimeter,
    BuildingManagement,
    SmartCity,
    ItInfrastructure,
    Generic,
    System
}

public enum LicenceClass { Full, Light, Free }

public sealed record TypeReference(TypeId Id, SemVersionRange Version);

public sealed record CapabilityUse(
    CapabilityId Id,
    SemVersionRange Version,
    bool Optional = false,
    CapabilityExclude? Exclude = null);

public sealed record CapabilityExclude(
    IReadOnlyList<string>? Commands = null,
    IReadOnlyList<string>? Events = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? EnumValues = null);

public sealed record TypeDisplay(string Icon, string Symbol, string PrimaryProperty);

public sealed record StateSummaryRules(IReadOnlyList<StateSummaryOverride>? Overrides = null);

public sealed record StateSummaryOverride(string Property, IReadOnlyList<JsonElement> Values, string State);

public sealed record DefaultLink(string Kind, Category? PeerCategory = null, TypeId? PeerType = null);

public sealed record DeviceTypeDocument(
    TypeId Id,
    SemVersion Version,
    TypeRole Role,
    Category Category,
    TypeReference? Extends,
    IReadOnlyList<CapabilityUse> Capabilities,
    ParameterSchema? Attributes,
    IReadOnlyList<string> AllowedChildTypes,
    LicenceClass LicenceClass,
    TypeDisplay Display,
    StateSummaryRules? StateSummary,
    IReadOnlyList<string>? DefaultAlarmRules,
    IReadOnlyList<DefaultLink>? DefaultLinks);
