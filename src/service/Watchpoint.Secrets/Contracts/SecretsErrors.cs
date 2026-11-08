using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Secrets.Contracts;

/// <summary>Error codes of the secrets module (spec SEC §10).</summary>
public static class SecretsErrors
{
    /// <summary><c>secrets.not_found</c>.</summary>
    public const string NotFoundCode = "secrets.not_found";

    /// <summary><c>secrets.access.denied</c>.</summary>
    public const string AccessDeniedCode = "secrets.access.denied";

    /// <summary><c>secrets.integrity.failed</c>.</summary>
    public const string IntegrityFailedCode = "secrets.integrity.failed";

    /// <summary><c>secrets.value.too_large</c>.</summary>
    public const string ValueTooLargeCode = "secrets.value.too_large";

    /// <summary><c>secrets.master_key.unavailable</c>.</summary>
    public const string MasterKeyUnavailableCode = "secrets.master_key.unavailable";

    /// <summary><c>secrets.ref.invalid</c>.</summary>
    public const string RefInvalidCode = "secrets.ref.invalid";

    /// <summary>Unknown secret.</summary>
    /// <param name="reference">The id or bootstrap name.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(string reference) => Error.Create(ErrorKind.NotFound, NotFoundCode, ("secret", reference));

    /// <summary>Caller is not the owner.</summary>
    /// <param name="id">The secret.</param>
    /// <returns>The error.</returns>
    public static Error AccessDenied(SecretId id) => Error.Create(ErrorKind.Forbidden, AccessDeniedCode, ("secret", id.Value.ToString("D")));

    /// <summary>Decryption authentication failed.</summary>
    /// <param name="reference">The id or bootstrap name.</param>
    /// <returns>The error.</returns>
    public static Error IntegrityFailed(string reference) => Error.Create(ErrorKind.Internal, IntegrityFailedCode, ("secret", reference));

    /// <summary>Value too large.</summary>
    /// <param name="max">Maximum bytes.</param>
    /// <returns>The error.</returns>
    public static Error ValueTooLarge(int max) => Error.Create(ErrorKind.Validation, ValueTooLargeCode, ("max", max));

    /// <summary>The master key cannot be loaded.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="reason">Why (no key material).</param>
    /// <returns>The error.</returns>
    public static Error MasterKeyUnavailable(string provider, string reason) =>
        Error.Create(ErrorKind.Unavailable, MasterKeyUnavailableCode, ("provider", provider), ("reason", reason));

    /// <summary>Bad <see cref="SecretRef"/> text.</summary>
    /// <returns>The error.</returns>
    public static Error RefInvalid() => Error.Create(ErrorKind.Validation, RefInvalidCode, ("expected", "vault:<uuid> | bootstrap:<name>"));
}

/// <summary>A fatal secrets failure (master key unavailable); the host stops.</summary>
public sealed class SecretsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="error">The error.</param>
    /// <param name="inner">The cause.</param>
    public SecretsException(Error error, Exception? inner = null)
        : base(error?.Code.Value, inner) => Error = error ?? throw new ArgumentNullException(nameof(error));

    /// <summary>Creates the exception.</summary>
    public SecretsException()
        : this(Error.Internal("secrets failure"))
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public SecretsException(string message)
        : this(Error.Internal(message))
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SecretsException(string message, Exception innerException)
        : this(Error.Internal(message), innerException)
    {
    }

    /// <summary>Gets the error.</summary>
    public Error Error { get; }
}
