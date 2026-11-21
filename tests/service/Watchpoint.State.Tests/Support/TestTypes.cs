using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.State.Tests.Support;

/// <summary>Builds minimal <see cref="DeviceTypeDefinition"/>s for state module unit tests.</summary>
internal static class TestTypes
{
    public static readonly TypeRef DoorV1 = new(TypeId.FromTrusted("ac.door"), new SemVersion(1, 0, 0));
    public static readonly TypeRef DoorV2 = new(TypeId.FromTrusted("ac.door"), new SemVersion(2, 0, 0));
    public static readonly TypeRef ServerType = new(TypeId.FromTrusted("ac.server"), new SemVersion(1, 0, 0));
    public static readonly PropertyPath Position = PropertyPath.FromTrusted("doorPosition.position");
    public static readonly PropertyPath Locked = PropertyPath.FromTrusted("doorPosition.locked");
    public static readonly EventPath ForcedEvent = EventPath.FromTrusted("doorPosition.forced");

    /// <summary>
    /// A door type whose <c>doorPosition.position</c> property is <c>Alarm</c> when forced, with an optional
    /// <c>StaleAfter</c> and default value for staleness and default-value tests (STA-U-002, 011…013).
    /// </summary>
    public static DeviceTypeDefinition Door(TimeSpan? staleAfter = null, WpValue? defaultValue = null, TypeRef? @ref = null) =>
        new()
        {
            Ref = @ref ?? DoorV1,
            Role = TypeRole.Device,
            Category = Category.AccessControl,
            Source = "standard",
            Lineage = [@ref ?? DoorV1],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>
            {
                [CapabilityId.FromTrusted("doorPosition")] = new(CapabilityId.FromTrusted("doorPosition"), new SemVersion(1, 0, 0), false),
            },
            Properties = new Dictionary<PropertyPath, PropertyDefinition>
            {
                [Position] = new(
                    Position,
                    "enum",
                    [new EnumValueDefinition("open", Severity.Normal), new EnumValueDefinition("closed", Severity.Normal), new EnumValueDefinition("forced", Severity.Alarm)],
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    new HistoryPolicy("all"),
                    staleAfter,
                    defaultValue,
                    null),
            },
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = new Dictionary<EventPath, EventDefinition>
            {
                [ForcedEvent] = new(ForcedEvent, Severity.Alarm, new ParameterSchema(JsonDocument.Parse("{}").RootElement), []),
            },
            Attributes = new ParameterSchema(JsonDocument.Parse("{}").RootElement),
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Free,
            Display = new TypeDisplay("door", "door", Position.Value),
            StateOverrides = [new StateOverride(Position, [WpValue.Enum("forced")], OverallState.Alarm)],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };

    /// <summary>
    /// A second door version that drops <c>doorPosition.position</c> and declares <c>doorPosition.locked</c> instead
    /// (STA-U-016: "properties no longer in the type are dropped; new ones initialised", spec STA §6.3).
    /// </summary>
    public static DeviceTypeDefinition DoorV2DroppingPosition()
    {
        var v1 = Door(@ref: DoorV2);
        var properties = new Dictionary<PropertyPath, PropertyDefinition>
        {
            [Locked] = new(Locked, "boolean", [], null, null, null, null, null, null, false, new HistoryPolicy("all"), null, null, null),
        };
        return v1 with { Properties = properties, StateOverrides = [] };
    }

    /// <summary>A minimal server type, with no properties and no state overrides.</summary>
    public static DeviceTypeDefinition Server() =>
        new()
        {
            Ref = ServerType,
            Role = TypeRole.Server,
            Category = Category.System,
            Source = "standard",
            Lineage = [ServerType],
            Capabilities = new Dictionary<CapabilityId, CapabilityUseDefinition>(),
            Properties = new Dictionary<PropertyPath, PropertyDefinition>(),
            Commands = new Dictionary<CommandPath, CommandDefinition>(),
            Events = new Dictionary<EventPath, EventDefinition>(),
            Attributes = new ParameterSchema(JsonDocument.Parse("{}").RootElement),
            AllowedChildTypes = [],
            LicenceClass = LicenceClass.Free,
            Display = new TypeDisplay("server", "server", string.Empty),
            StateOverrides = [],
            DefaultAlarmRules = [],
            DefaultLinks = [],
        };
}
