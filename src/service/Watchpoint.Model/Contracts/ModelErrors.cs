using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;

namespace Watchpoint.Model.Contracts;

/// <summary>Standard error codes and error factories for the Model module (spec MOD §9).</summary>
public static class ModelErrors
{
    /// <summary>Type definition not found.</summary>
    public static readonly ErrorCode TypeNotFound = ErrorCode.FromTrusted("model.type.not_found");

    /// <summary>Capability definition not found.</summary>
    public static readonly ErrorCode CapabilityNotFound = ErrorCode.FromTrusted("model.capability.not_found");

    /// <summary>Document failed schema or structural validation.</summary>
    public static readonly ErrorCode DocumentInvalid = ErrorCode.FromTrusted("model.document.invalid");

    /// <summary>Conflicting document with same id and version.</summary>
    public static readonly ErrorCode DocumentConflict = ErrorCode.FromTrusted("model.document.conflict");

    /// <summary>Package defines type or capability ids outside vendor reserved namespace.</summary>
    public static readonly ErrorCode ReservedNamespace = ErrorCode.FromTrusted("model.package.reserved_namespace");

    /// <summary>Excluded enum value is a property default.</summary>
    public static readonly ErrorCode ExcludeDefault = ErrorCode.FromTrusted("model.type.exclude_default");

    /// <summary>Value kind does not match property value type.</summary>
    public static readonly ErrorCode TypeMismatch = ErrorCode.FromTrusted("model.value.type_mismatch");

    /// <summary>Unknown enum code for the property.</summary>
    public static readonly ErrorCode EnumUnknown = ErrorCode.FromTrusted("model.value.enum_unknown");

    /// <summary>Numeric or duration value is out of range.</summary>
    public static readonly ErrorCode OutOfRange = ErrorCode.FromTrusted("model.value.out_of_range");

    /// <summary>Value or string exceeded maximum length.</summary>
    public static readonly ErrorCode TooLong = ErrorCode.FromTrusted("model.value.too_long");

    /// <summary>Unit cannot be converted to the expected unit.</summary>
    public static readonly ErrorCode UnitIncompatible = ErrorCode.FromTrusted("model.value.unit_incompatible");

    /// <summary>Property path not found on device type.</summary>
    public static readonly ErrorCode PropertyNotFound = ErrorCode.FromTrusted("model.property.not_found");

    /// <summary>Command path not found on device type.</summary>
    public static readonly ErrorCode CommandNotFound = ErrorCode.FromTrusted("model.command.not_found");

    /// <summary>Event path not found on device type.</summary>
    public static readonly ErrorCode EventNotFound = ErrorCode.FromTrusted("model.event.not_found");

    /// <summary>Symbol not found in catalogue resources.</summary>
    public static readonly ErrorCode SymbolNotFound = ErrorCode.FromTrusted("model.symbol.not_found");

    /// <summary>Creates a <c>model.type.not_found</c> error.</summary>
    public static Error TypeNotFoundError(TypeId id, SemVersion version) =>
        Error.NotFound(TypeNotFound.Value, ("typeId", id.Value), ("version", version.ToString()));

    /// <summary>Creates a <c>model.type.not_found</c> error for major version.</summary>
    public static Error TypeNotFoundMajorError(TypeId id, int major) =>
        Error.NotFound(TypeNotFound.Value, ("typeId", id.Value), ("major", major));

    /// <summary>Creates a <c>model.capability.not_found</c> error.</summary>
    public static Error CapabilityNotFoundError(CapabilityId id, SemVersion version) =>
        Error.NotFound(CapabilityNotFound.Value, ("capabilityId", id.Value), ("version", version.ToString()));

    /// <summary>Creates a <c>model.document.conflict</c> error.</summary>
    public static Error ConflictError(string message, string id, string version) =>
        Error.Conflict(DocumentConflict.Value, ("id", id), ("version", version), ("message", message));

    /// <summary>Creates a <c>model.package.reserved_namespace</c> error.</summary>
    public static Error ReservedNamespaceError(string id) =>
        Error.Validation(ReservedNamespace.Value, [new FieldError("/id", ReservedNamespace, new Dictionary<string, object?> { ["id"] = id })]);

    /// <summary>Creates a <c>model.type.exclude_default</c> error.</summary>
    public static Error ExcludeDefaultError(string property, string code) =>
        Error.Validation(ExcludeDefault.Value, [new FieldError(property, ExcludeDefault, new Dictionary<string, object?> { ["property"] = property, ["code"] = code })]);

    /// <summary>Creates a <c>model.value.type_mismatch</c> error.</summary>
    public static Error TypeMismatchError(string expected, string actual, string path = "") =>
        Error.Validation(TypeMismatch.Value, [new FieldError(path, TypeMismatch, new Dictionary<string, object?> { ["expected"] = expected, ["actual"] = actual })]);

    /// <summary>Creates a <c>model.value.enum_unknown</c> error.</summary>
    public static Error EnumUnknownError(string code, IReadOnlyList<string> allowed, string path = "") =>
        Error.Validation(EnumUnknown.Value, [new FieldError(path, EnumUnknown, new Dictionary<string, object?> { ["code"] = code, ["allowed"] = allowed })]);

    /// <summary>Creates a <c>model.value.out_of_range</c> error.</summary>
    public static Error OutOfRangeError(object? min, object? max, string path = "") =>
        Error.Validation(OutOfRange.Value, [new FieldError(path, OutOfRange, new Dictionary<string, object?> { ["min"] = min, ["max"] = max })]);

    /// <summary>Creates a <c>model.value.too_long</c> error.</summary>
    public static Error TooLongError(int max, string path = "") =>
        Error.Validation(TooLong.Value, [new FieldError(path, TooLong, new Dictionary<string, object?> { ["max"] = max })]);

    /// <summary>Creates a <c>model.value.unit_incompatible</c> error.</summary>
    public static Error UnitIncompatibleError(string unit, string expected, string path = "") =>
        Error.Validation(UnitIncompatible.Value, [new FieldError(path, UnitIncompatible, new Dictionary<string, object?> { ["unit"] = unit, ["expected"] = expected })]);
}
