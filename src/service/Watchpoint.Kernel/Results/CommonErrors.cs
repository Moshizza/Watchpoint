using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Results;

/// <summary>
/// Factories of the common errors (spec K §5.5). Methods ending in <c>At</c> return a <see cref="FieldError"/>
/// for a field of a validated document; the others return an <see cref="Error"/>.
/// </summary>
public static class CommonErrors
{
    /// <summary><c>common.validation.failed</c> carrying field errors.</summary>
    /// <param name="fields">The field errors.</param>
    /// <returns>The error.</returns>
    public static Error ValidationFailed(IReadOnlyList<FieldError> fields) =>
        new() { Code = CommonErrorCodes.ValidationFailed, Kind = ErrorKind.Validation, FieldErrors = fields };

    /// <summary><c>common.value.invalid_format</c> for a single value (kind <see cref="ErrorKind.Validation"/>).</summary>
    /// <param name="expected">Description of the expected format, e.g. a pattern.</param>
    /// <returns>The error.</returns>
    public static Error InvalidFormat(string expected) => Validation(CommonErrorCodes.ValueInvalidFormat, ("expected", expected));

    /// <summary><c>common.value.out_of_range</c> for a single value (kind <see cref="ErrorKind.Validation"/>).</summary>
    /// <param name="min">The minimum allowed value.</param>
    /// <param name="max">The maximum allowed value.</param>
    /// <returns>The error.</returns>
    public static Error OutOfRange(long min, long max) =>
        Validation(CommonErrorCodes.ValueOutOfRange, ("min", min), ("max", max));

    /// <summary><c>common.value.required</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <returns>The field error.</returns>
    public static FieldError RequiredAt(string path) => FieldError.Create(path, CommonErrorCodes.ValueRequired);

    /// <summary><c>common.value.invalid_format</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="expected">Description of the expected format.</param>
    /// <returns>The field error.</returns>
    public static FieldError InvalidFormatAt(string path, string expected) =>
        FieldError.Create(path, CommonErrorCodes.ValueInvalidFormat, ("expected", expected));

    /// <summary><c>common.value.out_of_range</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="min">The minimum allowed value.</param>
    /// <param name="max">The maximum allowed value.</param>
    /// <returns>The field error.</returns>
    public static FieldError OutOfRangeAt(string path, long min, long max) =>
        FieldError.Create(path, CommonErrorCodes.ValueOutOfRange, ("min", min), ("max", max));

    /// <summary><c>common.value.too_long</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="max">The maximum length.</param>
    /// <returns>The field error.</returns>
    public static FieldError TooLongAt(string path, int max) => FieldError.Create(path, CommonErrorCodes.ValueTooLong, ("max", max));

    /// <summary><c>common.value.too_short</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="min">The minimum length.</param>
    /// <returns>The field error.</returns>
    public static FieldError TooShortAt(string path, int min) => FieldError.Create(path, CommonErrorCodes.ValueTooShort, ("min", min));

    /// <summary><c>common.value.not_allowed</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="allowed">The allowed values.</param>
    /// <returns>The field error.</returns>
    public static FieldError NotAllowedAt(string path, IReadOnlyList<string> allowed) =>
        FieldError.Create(path, CommonErrorCodes.ValueNotAllowed, ("allowed", allowed));

    /// <summary><c>common.value.duplicate</c> for a field.</summary>
    /// <param name="path">JSON Pointer of the field.</param>
    /// <param name="value">The duplicated value.</param>
    /// <returns>The field error.</returns>
    public static FieldError DuplicateAt(string path, string value) =>
        FieldError.Create(path, CommonErrorCodes.ValueDuplicate, ("value", value));

    /// <summary><c>common.not_found</c>.</summary>
    /// <param name="entity">The entity name, e.g. <c>device</c>.</param>
    /// <param name="id">The id (an allowed argument type, spec K §5.4).</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string entity, object id) =>
        Error.Create(ErrorKind.NotFound, CommonErrorCodes.NotFound.Value, ("entity", entity), ("id", id));

    /// <summary><c>common.version.conflict</c> (kind <see cref="ErrorKind.PreconditionFailed"/>).</summary>
    /// <param name="expected">The expected version (ETag).</param>
    /// <param name="actual">The current version.</param>
    /// <returns>The error.</returns>
    public static Error VersionConflict(string expected, string actual) =>
        Error.Create(ErrorKind.PreconditionFailed, CommonErrorCodes.VersionConflict.Value, ("expected", expected), ("actual", actual));

    /// <summary><c>common.cursor.invalid</c>.</summary>
    /// <returns>The error.</returns>
    public static Error CursorInvalid() => Validation(CommonErrorCodes.CursorInvalid);

    /// <summary><c>common.sort.invalid_field</c>.</summary>
    /// <param name="field">The rejected field.</param>
    /// <param name="allowed">The allowed fields.</param>
    /// <returns>The error.</returns>
    public static Error SortInvalidField(string field, IReadOnlyList<string> allowed) =>
        Validation(CommonErrorCodes.SortInvalidField, ("field", field), ("allowed", allowed));

    /// <summary><c>common.time.invalid_local</c> (kind <see cref="ErrorKind.Validation"/>).</summary>
    /// <param name="local">The local date and time, ISO 8601 without offset.</param>
    /// <param name="reason"><c>gap</c> (skipped local time) or <c>overlap</c> (repeated local time).</param>
    /// <returns>The error.</returns>
    public static Error TimeInvalidLocal(string local, string reason) =>
        Validation(CommonErrorCodes.TimeInvalidLocal, ("local", local), ("reason", reason));

    /// <summary><c>common.unauthenticated</c>.</summary>
    /// <returns>The error.</returns>
    public static Error Unauthenticated() => new() { Code = CommonErrorCodes.Unauthenticated, Kind = ErrorKind.Unauthenticated };

    /// <summary><c>common.forbidden</c>.</summary>
    /// <param name="permission">The missing permission.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(PermissionCode permission) =>
        Error.Create(ErrorKind.Forbidden, CommonErrorCodes.Forbidden.Value, ("permission", permission));

    /// <summary><c>common.rate_limited</c>.</summary>
    /// <param name="retryAfterSeconds">Seconds before a retry is allowed.</param>
    /// <returns>The error.</returns>
    public static Error RateLimited(int retryAfterSeconds) =>
        Error.Create(ErrorKind.TooManyRequests, CommonErrorCodes.RateLimited.Value, ("retryAfterSeconds", retryAfterSeconds));

    /// <summary><c>common.unavailable</c>.</summary>
    /// <param name="dependency">The unavailable dependency, e.g. <c>database</c>.</param>
    /// <returns>The error.</returns>
    public static Error Unavailable(string dependency) =>
        Error.Create(ErrorKind.Unavailable, CommonErrorCodes.Unavailable.Value, ("dependency", dependency));

    /// <summary><c>common.internal</c>.</summary>
    /// <param name="developerDetail">Technical detail for logs; never sent to clients.</param>
    /// <returns>The error.</returns>
    public static Error Internal(string developerDetail) => Error.Internal(developerDetail);

    private static Error Validation(ErrorCode code, params (string Key, object? Value)[] args) =>
        Error.Create(ErrorKind.Validation, code.Value, args);
}
