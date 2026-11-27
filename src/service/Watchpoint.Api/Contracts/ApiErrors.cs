using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Contracts;

/// <summary>Error codes owned by the API framework (spec API §10).</summary>
public static class ApiErrors
{
    /// <summary><c>api.client_kind.not_allowed</c> (403): the endpoint needs another client kind (args <c>required</c>).</summary>
    /// <param name="required">The required client kind, lower case.</param>
    /// <returns>The error.</returns>
    public static Error ClientKindNotAllowed(string required) => Error.Forbidden("api.client_kind.not_allowed", ("required", required));

    /// <summary><c>common.version.required</c> (428): a versioned PATCH/PUT/DELETE without <c>If-Match</c>.</summary>
    /// <returns>The error.</returns>
    public static Error VersionRequired() => Error.Create(ErrorKind.PreconditionFailed, "common.version.required");

    /// <summary><c>common.value.invalid_format</c> on the <c>If-Match</c> header.</summary>
    /// <returns>The error.</returns>
    public static Error VersionInvalid() => Error.Validation(CommonErrorCodes.ValidationFailed.Value,
        [FieldError.Create("/headers/If-Match", CommonErrorCodes.ValueInvalidFormat)]);

    /// <summary><c>api.idempotency.key_invalid</c> (400): the key is not 1–64 characters of <c>[A-Za-z0-9_-]</c>.</summary>
    /// <returns>The error.</returns>
    public static Error IdempotencyKeyInvalid() => Error.Create(ErrorKind.Validation, "api.idempotency.key_invalid");

    /// <summary><c>api.idempotency.mismatch</c> (422): the key was used with another body or route.</summary>
    /// <returns>The error.</returns>
    public static Error IdempotencyMismatch() => Error.Unprocessable("api.idempotency.mismatch");

    /// <summary><c>api.idempotency.in_progress</c> (409): the first request with this key is still running.</summary>
    /// <returns>The error.</returns>
    public static Error IdempotencyInProgress() => Error.Conflict("api.idempotency.in_progress");

    /// <summary><c>api.job.not_found</c> (404), also for jobs of other users.</summary>
    /// <param name="id">The job id.</param>
    /// <returns>The error.</returns>
    public static Error JobNotFound(string id) => Error.NotFound("api.job.not_found", ("id", id));

    /// <summary><c>api.request.invalid</c> (400): the body or a parameter could not be read (malformed JSON, depth &gt; 32, wrong type).</summary>
    /// <returns>The error.</returns>
    public static Error RequestInvalid() => Error.Create(ErrorKind.Validation, "api.request.invalid");

    /// <summary><c>api.request.too_large</c> (413): the body exceeds the endpoint's limit (args <c>max</c>).</summary>
    /// <param name="max">The limit in bytes.</param>
    /// <returns>The error.</returns>
    public static Error RequestTooLarge(long max) => Error.Create(ErrorKind.Validation, "api.request.too_large", ("max", max));
}
