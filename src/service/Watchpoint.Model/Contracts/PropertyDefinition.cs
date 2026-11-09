using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>Flattened property definition of a device type (spec MOD §4.1).</summary>
/// <param name="Path">The property path (<c>&lt;capabilityId&gt;.&lt;memberId&gt;</c>).</param>
/// <param name="ValueType">The property value kind name (e.g. <c>boolean</c>, <c>integer</c>, <c>decimal</c>).</param>
/// <param name="EnumValues">Allowed enum values and their severity mapping, or empty for non-enum properties.</param>
/// <param name="Unit">The UCUM unit for numeric properties, or <see langword="null"/>.</param>
/// <param name="Range">The numeric range limits, or <see langword="null"/>.</param>
/// <param name="Decimals">The maximum display decimals for numeric values.</param>
/// <param name="MaxLength">The maximum length for string properties.</param>
/// <param name="ItemType">The item type for list properties.</param>
/// <param name="ObjectSchema">The JSON schema for object properties.</param>
/// <param name="Writable">Whether the property is writable.</param>
/// <param name="History">The history recording policy.</param>
/// <param name="StaleAfter">The duration after which value is considered stale.</param>
/// <param name="Default">The default property value, or <see langword="null"/>.</param>
/// <param name="BooleanSeverity">The severity mapping for boolean properties, or <see langword="null"/>.</param>
public sealed record PropertyDefinition(
    PropertyPath Path,
    string ValueType,
    IReadOnlyList<EnumValueDefinition> EnumValues,
    string? Unit,
    NumericRange? Range,
    int? Decimals,
    int? MaxLength,
    string? ItemType,
    ParameterSchema? ObjectSchema,
    bool Writable,
    HistoryPolicy History,
    TimeSpan? StaleAfter,
    WpValue? Default,
    BooleanSeverity? BooleanSeverity);

/// <summary>Definition of an allowed enum value.</summary>
/// <param name="Code">The enum code string.</param>
/// <param name="Severity">The severity associated with this enum code.</param>
public sealed record EnumValueDefinition(string Code, Severity Severity);

/// <summary>Definition of numeric range bounds.</summary>
/// <param name="Min">The minimum allowed value, or <see langword="null"/> if unbounded.</param>
/// <param name="Max">The maximum allowed value, or <see langword="null"/> if unbounded.</param>
public sealed record NumericRange(double? Min, double? Max);

/// <summary>Severity configuration for boolean properties.</summary>
/// <param name="True">The severity when property is true.</param>
/// <param name="False">The severity when property is false.</param>
public sealed record BooleanSeverity(Severity True, Severity False);

/// <summary>History recording policy configuration.</summary>
/// <param name="Mode">The recording mode (<c>"all"</c>, <c>"periodic"</c>, <c>"deadband"</c>, <c>"none"</c>).</param>
/// <param name="Type">The aggregation type (e.g. for periodic/deadband).</param>
/// <param name="Threshold">The deadband threshold.</param>
/// <param name="Interval">The periodic sample interval.</param>
public sealed record HistoryPolicy(string Mode, string? Type = null, double? Threshold = null, TimeSpan? Interval = null);

/// <summary>Command definition of a device type (spec MOD §4.1).</summary>
/// <param name="Path">The command path (<c>&lt;capabilityId&gt;.&lt;memberId&gt;</c>).</param>
/// <param name="Parameters">The command parameter schema.</param>
/// <param name="Risk">The default risk level of the command.</param>
/// <param name="RiskByArgument">Risk overrides depending on argument name and value.</param>
/// <param name="Timeout">The command timeout duration.</param>
/// <param name="Idempotent">Whether multiple invocations with same arguments produce the same effect.</param>
/// <param name="Batchable">Whether the command can be sent to multiple devices in a batch.</param>
/// <param name="ExpectedEffect">Expected property changes following execution, or <see langword="null"/>.</param>
public sealed record CommandDefinition(
    CommandPath Path,
    ParameterSchema Parameters,
    Risk Risk,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, Risk>> RiskByArgument,
    TimeSpan Timeout,
    bool Idempotent,
    bool Batchable,
    ExpectedEffect? ExpectedEffect)
{
    /// <summary>Calculates the effective risk given invocation arguments.</summary>
    /// <param name="arguments">The parsed arguments.</param>
    /// <returns>The effective risk level.</returns>
    public Risk EffectiveRisk(IReadOnlyDictionary<string, WpValue> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var effective = Risk;
        foreach (var (argName, overrides) in RiskByArgument)
        {
            if (arguments.TryGetValue(argName, out var argVal))
            {
                var valStr = argVal.Kind == WpValueKind.Enum ? argVal.AsEnum() : argVal.ToString();
                if (overrides.TryGetValue(valStr, out var overrideRisk) && overrideRisk > effective)
                {
                    effective = overrideRisk;
                }
            }
        }

        return effective;
    }
}

/// <summary>Expected effect specification for command verification.</summary>
/// <param name="Property">The property path that changes.</param>
/// <param name="EqualsValue">The expected value to match, or <see langword="null"/>.</param>
/// <param name="In">Allowed set of values to match, or <see langword="null"/>.</param>
/// <param name="Within">The duration within which the effect is expected.</param>
public sealed record ExpectedEffect(
    PropertyPath Property,
    WpValue? EqualsValue = null,
    IReadOnlyList<WpValue>? In = null,
    TimeSpan? Within = null);

/// <summary>Event definition of a device type (spec MOD §4.1).</summary>
/// <param name="Path">The event path (<c>&lt;capabilityId&gt;.&lt;memberId&gt;</c>).</param>
/// <param name="Severity">The default severity of the event.</param>
/// <param name="Payload">The event payload schema.</param>
/// <param name="PersonalDataFields">Field names containing personal data subject to privacy policies.</param>
public sealed record EventDefinition(
    EventPath Path,
    Severity Severity,
    ParameterSchema Payload,
    IReadOnlyList<string> PersonalDataFields);
