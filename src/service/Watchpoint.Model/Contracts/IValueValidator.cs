using System.Text.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Model.Contracts;

/// <summary>Validates property values, command arguments, event payloads and attributes (spec MOD §4.3).</summary>
public interface IValueValidator
{
    /// <summary>Validates and normalises a property value against its definition, optionally converting units.</summary>
    /// <param name="property">The property definition.</param>
    /// <param name="value">The reported value.</param>
    /// <param name="reportedUnit">The unit the value was reported with, or <see langword="null"/>.</param>
    /// <returns>The normalised value in the property's canonical unit, or a validation error.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec MOD §4.3.")]
    Result<WpValue> ValidateProperty(PropertyDefinition property, WpValue value, string? reportedUnit = null);

    /// <summary>Validates command arguments against the command's parameter schema, applying defaults.</summary>
    /// <param name="command">The command definition.</param>
    /// <param name="arguments">The JSON arguments.</param>
    /// <returns>Dictionary of argument values with defaults applied, or a validation error.</returns>
    Result<IReadOnlyDictionary<string, WpValue>> ValidateArguments(CommandDefinition command, JsonElement arguments);

    /// <summary>Validates event payload fields against the event payload schema.</summary>
    /// <param name="evt">The event definition.</param>
    /// <param name="payload">The event payload values.</param>
    /// <returns>The validated payload dictionary, or a validation error.</returns>
    Result<IReadOnlyDictionary<string, WpValue>> ValidatePayload(EventDefinition evt, IReadOnlyDictionary<string, WpValue> payload);

    /// <summary>Validates device attributes against the device type attribute schema.</summary>
    /// <param name="type">The device type definition.</param>
    /// <param name="attributes">The JSON attributes.</param>
    /// <returns>The validated attribute dictionary, or a validation error.</returns>
    Result<IReadOnlyDictionary<string, WpValue>> ValidateAttributes(DeviceTypeDefinition type, JsonElement attributes);
}
