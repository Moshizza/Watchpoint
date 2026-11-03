using Watchpoint.Kernel.Ids;

namespace Watchpoint.Kernel.Results;

/// <summary>Codes of the common errors (spec K §5.5).</summary>
public static class CommonErrorCodes
{
    /// <summary><c>common.validation.failed</c>: container for field errors.</summary>
    public static readonly ErrorCode ValidationFailed = ErrorCode.FromTrusted("common.validation.failed");

    /// <summary><c>common.value.required</c>: missing value.</summary>
    public static readonly ErrorCode ValueRequired = ErrorCode.FromTrusted("common.value.required");

    /// <summary><c>common.value.invalid_format</c>: bad format (argument <c>expected</c>).</summary>
    public static readonly ErrorCode ValueInvalidFormat = ErrorCode.FromTrusted("common.value.invalid_format");

    /// <summary><c>common.value.out_of_range</c>: number outside range (arguments <c>min</c>, <c>max</c>).</summary>
    public static readonly ErrorCode ValueOutOfRange = ErrorCode.FromTrusted("common.value.out_of_range");

    /// <summary><c>common.value.too_long</c>: string or list too long (argument <c>max</c>).</summary>
    public static readonly ErrorCode ValueTooLong = ErrorCode.FromTrusted("common.value.too_long");

    /// <summary><c>common.value.too_short</c>: string or list too short (argument <c>min</c>).</summary>
    public static readonly ErrorCode ValueTooShort = ErrorCode.FromTrusted("common.value.too_short");

    /// <summary><c>common.value.not_allowed</c>: value not in the allowed list (argument <c>allowed</c>).</summary>
    public static readonly ErrorCode ValueNotAllowed = ErrorCode.FromTrusted("common.value.not_allowed");

    /// <summary><c>common.value.duplicate</c>: duplicate in a list (argument <c>value</c>).</summary>
    public static readonly ErrorCode ValueDuplicate = ErrorCode.FromTrusted("common.value.duplicate");

    /// <summary><c>common.not_found</c>: generic not found (arguments <c>entity</c>, <c>id</c>).</summary>
    public static readonly ErrorCode NotFound = ErrorCode.FromTrusted("common.not_found");

    /// <summary><c>common.version.conflict</c>: ETag mismatch (arguments <c>expected</c>, <c>actual</c>).</summary>
    public static readonly ErrorCode VersionConflict = ErrorCode.FromTrusted("common.version.conflict");

    /// <summary><c>common.cursor.invalid</c>: bad paging cursor.</summary>
    public static readonly ErrorCode CursorInvalid = ErrorCode.FromTrusted("common.cursor.invalid");

    /// <summary><c>common.sort.invalid_field</c>: unknown sort field (arguments <c>field</c>, <c>allowed</c>).</summary>
    public static readonly ErrorCode SortInvalidField = ErrorCode.FromTrusted("common.sort.invalid_field");

    /// <summary><c>common.unauthenticated</c>.</summary>
    public static readonly ErrorCode Unauthenticated = ErrorCode.FromTrusted("common.unauthenticated");

    /// <summary><c>common.forbidden</c> (argument <c>permission</c>).</summary>
    public static readonly ErrorCode Forbidden = ErrorCode.FromTrusted("common.forbidden");

    /// <summary><c>common.rate_limited</c> (argument <c>retryAfterSeconds</c>).</summary>
    public static readonly ErrorCode RateLimited = ErrorCode.FromTrusted("common.rate_limited");

    /// <summary><c>common.unavailable</c>: dependency down (argument <c>dependency</c>).</summary>
    public static readonly ErrorCode Unavailable = ErrorCode.FromTrusted("common.unavailable");

    /// <summary><c>common.internal</c>: unexpected failure.</summary>
    public static readonly ErrorCode Internal = ErrorCode.FromTrusted("common.internal");
}
