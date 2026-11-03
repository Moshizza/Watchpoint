using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Ids;

// All string-based id types of Watchpoint (spec K §4.2). Every type has the same shape: keep them identical.

/// <summary>
/// Identifier of a device or server type, e.g. <c>ac.door</c>. Pattern <c>^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,4}$</c>, at most 100 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<TypeId>))]
public readonly partial record struct TypeId : IStringId<TypeId>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 100;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,4}$";

    private readonly string? _value;

    private TypeId(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<TypeId> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new TypeId(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static TypeId FromTrusted(string value) => new(value);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Identifier of a capability, e.g. <c>doorPosition</c>. Pattern <c>^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)?$</c>, at most 64 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<CapabilityId>))]
public readonly partial record struct CapabilityId : IStringId<CapabilityId>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 64;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)?$";

    private readonly string? _value;

    private CapabilityId(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<CapabilityId> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new CapabilityId(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static CapabilityId FromTrusted(string value) => new(value);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Identifier of a member (property, command or event) inside a capability, e.g. <c>position</c>. Pattern <c>^[a-z][a-zA-Z0-9]*$</c>, at most 64 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<MemberId>))]
public readonly partial record struct MemberId : IStringId<MemberId>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 64;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-zA-Z0-9]*$";

    private readonly string? _value;

    private MemberId(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<MemberId> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new MemberId(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static MemberId FromTrusted(string value) => new(value);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Identifier of an integration package, e.g. <c>watchpoint.integration.salto</c>. Pattern <c>^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,5}$</c>, at most 128 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<PackageId>))]
public readonly partial record struct PackageId : IStringId<PackageId>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 128;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,5}$";

    private readonly string? _value;

    private PackageId(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<PackageId> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new PackageId(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static PackageId FromTrusted(string value) => new(value);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9-]*){1,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Identifier of a permission, e.g. <c>alarms.acknowledge</c>. Pattern <c>^[a-z][a-zA-Z0-9]*(\.([a-z][a-zA-Z0-9]*|\*)){1,5}$</c>, at most 128 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<PermissionCode>))]
public readonly partial record struct PermissionCode : IStringId<PermissionCode>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 128;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-zA-Z0-9]*(\.([a-z][a-zA-Z0-9]*|\*)){1,5}$";

    private readonly string? _value;

    private PermissionCode(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<PermissionCode> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new PermissionCode(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static PermissionCode FromTrusted(string value) => new(value);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*(\.([a-z][a-zA-Z0-9]*|\*)){1,5}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Identifier of an error, e.g. <c>registry.device.not_found</c>. Pattern <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,4}$</c>, at most 128 characters;
/// compared ordinally; serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<ErrorCode>))]
public readonly partial record struct ErrorCode : IStringId<ErrorCode>
{
    /// <summary>The maximum length of the value.</summary>
    public const int MaxLength = 128;

    /// <summary>The pattern the value must match.</summary>
    public const string Pattern = @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,4}$";

    private readonly string? _value;

    private ErrorCode(string value) => _value = value;

    /// <summary>Gets the value (empty for <see langword="default"/>).</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Validates and wraps a value.</summary>
    /// <param name="text">The text to parse.</param>
    /// <returns>The id, or <c>common.value.invalid_format</c> (argument <c>expected</c> = the pattern).</returns>
    public static Result<ErrorCode> Parse(string? text) =>
        StringIdHelper.IsValid(text, MaxLength, Regex()) ? new ErrorCode(text) : CommonErrors.InvalidFormat(Pattern);

    /// <summary>Wraps a value without validation; for constants and values already validated.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    public static ErrorCode FromTrusted(string value) => new(value);

    /// <summary>Validates a code given by a programmer; an invalid code is a programming error.</summary>
    internal static ErrorCode Of(string code, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(code))] string? paramName = null) =>
        StringIdHelper.IsValid(code, MaxLength, Regex())
            ? new(code)
            : throw new ArgumentException($"'{code}' is not a valid error code (pattern {Pattern}).", paramName);

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Regex();
}

/// <summary>
/// Path of a member inside a capability: <c>"&lt;capabilityId&gt;.&lt;memberId&gt;"</c>, e.g. <c>doorPosition.position</c>
/// or <c>vendor.saltoDnd.active</c> (the member is after the last dot). Serialised as a JSON string.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<PropertyPath>))]
public readonly record struct PropertyPath : IStringId<PropertyPath>
{
    /// <summary>Creates a path from its parts.</summary>
    /// <param name="capability">The capability.</param>
    /// <param name="member">The member inside the capability.</param>
    public PropertyPath(CapabilityId capability, MemberId member)
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
    public static Result<PropertyPath> Parse(string? text)
    {
        var dot = text?.LastIndexOf('.') ?? -1;
        if (dot > 0
            && CapabilityId.Parse(text![..dot]).TryGetValue(out var capability, out _)
            && MemberId.Parse(text[(dot + 1)..]).TryGetValue(out var member, out _))
        {
            return new PropertyPath(capability, member);
        }

        return CommonErrors.InvalidFormat("<capabilityId>.<memberId>");
    }

    /// <summary>Splits a value at its last dot without validation; for constants.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The path.</returns>
    public static PropertyPath FromTrusted(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dot = value.LastIndexOf('.');
        return new(CapabilityId.FromTrusted(value[..Math.Max(dot, 0)]), MemberId.FromTrusted(value[(dot + 1)..]));
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    /// <returns>The value.</returns>
    public override string ToString() => Value;
}
