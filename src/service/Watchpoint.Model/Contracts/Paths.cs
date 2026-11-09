using System.Text.Json;
using System.Text.Json.Serialization;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Contracts;

/// <summary>
/// Path of a command inside a capability: <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>, e.g. <c>openable.open</c>.
/// Serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(CommandPathJsonConverter))]
public readonly record struct CommandPath
{
    /// <summary>Creates a command path from its parts.</summary>
    /// <param name="capability">The capability.</param>
    /// <param name="member">The member inside the capability.</param>
    public CommandPath(CapabilityId capability, MemberId member)
    {
        Capability = capability;
        Member = member;
    }

    /// <summary>Gets the capability.</summary>
    public CapabilityId Capability { get; }

    /// <summary>Gets the member inside the capability.</summary>
    public MemberId Member { get; }

    /// <summary>Gets the text form <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>.</summary>
    public string Value => Capability.Value + "." + Member.Value;

    /// <summary>Parses <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The path, or <c>common.value.invalid_format</c>.</returns>
    public static Result<CommandPath> Parse(string? text)
    {
        var dot = text?.LastIndexOf('.') ?? -1;
        if (dot > 0
            && CapabilityId.Parse(text![..dot]).TryGetValue(out var capability, out _)
            && MemberId.Parse(text[(dot + 1)..]).TryGetValue(out var member, out _))
        {
            return new CommandPath(capability, member);
        }

        return CommonErrors.InvalidFormat("<capabilityId>.<memberId>");
    }

    /// <summary>Splits a value at its last dot without validation; for constants.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The path.</returns>
    public static CommandPath FromTrusted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dot = value.LastIndexOf('.');
        return new(CapabilityId.FromTrusted(value[..Math.Max(dot, 0)]), MemberId.FromTrusted(value[(dot + 1)..]));
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;
}

/// <summary>JSON converter for <see cref="CommandPath"/>.</summary>
public sealed class CommandPathJsonConverter : JsonConverter<CommandPath>
{
    /// <inheritdoc />
    public override CommandPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        var result = CommandPath.Parse(text);
        if (result.TryGetValue(out var path, out var error))
        {
            return path;
        }

        throw new JsonException(error.MessageKey);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CommandPath value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// Path of an event inside a capability: <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>, e.g. <c>doorPosition.forced</c>.
/// Serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(EventPathJsonConverter))]
public readonly record struct EventPath
{
    /// <summary>Creates an event path from its parts.</summary>
    /// <param name="capability">The capability.</param>
    /// <param name="member">The member inside the capability.</param>
    public EventPath(CapabilityId capability, MemberId member)
    {
        Capability = capability;
        Member = member;
    }

    /// <summary>Gets the capability.</summary>
    public CapabilityId Capability { get; }

    /// <summary>Gets the member inside the capability.</summary>
    public MemberId Member { get; }

    /// <summary>Gets the text form <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>.</summary>
    public string Value => Capability.Value + "." + Member.Value;

    /// <summary>Parses <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The path, or <c>common.value.invalid_format</c>.</returns>
    public static Result<EventPath> Parse(string? text)
    {
        var dot = text?.LastIndexOf('.') ?? -1;
        if (dot > 0
            && CapabilityId.Parse(text![..dot]).TryGetValue(out var capability, out _)
            && MemberId.Parse(text[(dot + 1)..]).TryGetValue(out var member, out _))
        {
            return new EventPath(capability, member);
        }

        return CommonErrors.InvalidFormat("<capabilityId>.<memberId>");
    }

    /// <summary>Splits a value at its last dot without validation; for constants.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The path.</returns>
    public static EventPath FromTrusted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dot = value.LastIndexOf('.');
        return new(CapabilityId.FromTrusted(value[..Math.Max(dot, 0)]), MemberId.FromTrusted(value[(dot + 1)..]));
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;
}

/// <summary>JSON converter for <see cref="EventPath"/>.</summary>
public sealed class EventPathJsonConverter : JsonConverter<EventPath>
{
    /// <inheritdoc />
    public override EventPath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        var result = EventPath.Parse(text);
        if (result.TryGetValue(out var path, out var error))
        {
            return path;
        }

        throw new JsonException(error.MessageKey);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, EventPath value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
