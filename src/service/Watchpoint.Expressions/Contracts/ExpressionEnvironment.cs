using Watchpoint.Model.Contracts;

namespace Watchpoint.Expressions.Contracts;

/// <summary>Declares the variables, device types, and available environment for expression checking (spec EXP §4.5).</summary>
public sealed class ExpressionEnvironment
{
    /// <summary>Initializes a new empty <see cref="ExpressionEnvironment"/>.</summary>
    public ExpressionEnvironment()
    {
        DeviceTypes = [];
        Variables = new Dictionary<string, ExprType>(StringComparer.Ordinal);
        Refs = new Dictionary<string, IReadOnlyList<DeviceTypeDefinition>>(StringComparer.Ordinal);
        KnownCalendars = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Gets or sets the device types in scope for the <c>device</c> variable (e.g. types matched by an alarm rule).</summary>
    public IReadOnlyList<DeviceTypeDefinition> DeviceTypes { get; init; }

    /// <summary>Gets or sets the event definition if evaluating in an event trigger context.</summary>
    public EventDefinition? Event { get; init; }

    /// <summary>Gets or sets declared reference device types (e.g. <c>refs.mainDoor</c> in synoptics or automations).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DeviceTypeDefinition>> Refs { get; init; }

    /// <summary>Gets or sets extra declared variable types (e.g. <c>vars</c> or <c>trigger</c> in automations).</summary>
    public IReadOnlyDictionary<string, ExprType> Variables { get; init; }

    /// <summary>Gets or sets the set of known calendar keys (from scheduling) to validate against.</summary>
    public IReadOnlySet<string> KnownCalendars { get; init; }

    /// <summary>Creates an environment for an alarm rule evaluated against a device of one or more device types.</summary>
    /// <param name="deviceTypes">The device types in scope.</param>
    /// <param name="knownCalendars">Known calendar keys.</param>
    /// <returns>The configured environment.</returns>
    public static ExpressionEnvironment ForAlarm(
        IReadOnlyList<DeviceTypeDefinition> deviceTypes,
        IReadOnlySet<string>? knownCalendars = null)
    {
        return new ExpressionEnvironment
        {
            DeviceTypes = deviceTypes ?? [],
            KnownCalendars = knownCalendars ?? new HashSet<string>(StringComparer.Ordinal),
        };
    }

    /// <summary>Creates an environment for an alarm rule triggered by an event on a device.</summary>
    /// <param name="deviceTypes">The device types in scope.</param>
    /// <param name="eventDef">The event definition in scope.</param>
    /// <param name="knownCalendars">Known calendar keys.</param>
    /// <returns>The configured environment.</returns>
    public static ExpressionEnvironment ForEventAlarm(
        IReadOnlyList<DeviceTypeDefinition> deviceTypes,
        EventDefinition eventDef,
        IReadOnlySet<string>? knownCalendars = null)
    {
        return new ExpressionEnvironment
        {
            DeviceTypes = deviceTypes ?? [],
            Event = eventDef,
            KnownCalendars = knownCalendars ?? new HashSet<string>(StringComparer.Ordinal),
        };
    }

    /// <summary>Creates an environment for a synoptic element binding.</summary>
    /// <param name="boundDeviceTypes">Device types for the bound device.</param>
    /// <param name="refs">Named referenced device definitions.</param>
    /// <returns>The configured environment.</returns>
    public static ExpressionEnvironment ForSynoptic(
        IReadOnlyList<DeviceTypeDefinition> boundDeviceTypes,
        IReadOnlyDictionary<string, IReadOnlyList<DeviceTypeDefinition>>? refs = null)
    {
        return new ExpressionEnvironment
        {
            DeviceTypes = boundDeviceTypes ?? [],
            Refs = refs ?? new Dictionary<string, IReadOnlyList<DeviceTypeDefinition>>(StringComparer.Ordinal),
        };
    }
}
