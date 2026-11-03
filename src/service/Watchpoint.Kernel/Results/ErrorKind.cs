namespace Watchpoint.Kernel.Results;

/// <summary>Category of an <see cref="Error"/>; the API maps it to an HTTP status (Architecture README §6.2).</summary>
public enum ErrorKind
{
    /// <summary>The input is invalid (field errors attached).</summary>
    Validation,

    /// <summary>The caller is not authenticated.</summary>
    Unauthenticated,

    /// <summary>The caller lacks a permission.</summary>
    Forbidden,

    /// <summary>The target does not exist (or is not visible to the caller).</summary>
    NotFound,

    /// <summary>The request conflicts with the current state.</summary>
    Conflict,

    /// <summary>A precondition (e.g. version / ETag) does not hold.</summary>
    PreconditionFailed,

    /// <summary>The input is well formed but cannot be processed.</summary>
    Unprocessable,

    /// <summary>A rate limit was exceeded.</summary>
    TooManyRequests,

    /// <summary>A dependency is unavailable (database, message bus…).</summary>
    Unavailable,

    /// <summary>An unexpected failure.</summary>
    Internal,
}
