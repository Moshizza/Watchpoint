using Watchpoint.Kernel.Results;

namespace Watchpoint.Storage.Contracts;

/// <summary>Error codes and factories of the storage module (spec STO §9).</summary>
public static class StorageErrors
{
    /// <summary><c>storage.unavailable</c>.</summary>
    public const string UnavailableCode = "storage.unavailable";

    /// <summary><c>storage.provider.unknown</c>.</summary>
    public const string ProviderUnknownCode = "storage.provider.unknown";

    /// <summary><c>storage.provider.unsupported_version</c>.</summary>
    public const string UnsupportedVersionCode = "storage.provider.unsupported_version";

    /// <summary><c>storage.migration.failed</c>.</summary>
    public const string MigrationFailedCode = "storage.migration.failed";

    /// <summary><c>storage.migration.lock_timeout</c>.</summary>
    public const string MigrationLockTimeoutCode = "storage.migration.lock_timeout";

    /// <summary><c>storage.concurrency.conflict</c>.</summary>
    public const string ConcurrencyConflictCode = "storage.concurrency.conflict";

    /// <summary><c>storage.settings.invalid</c>: storage settings failed validation (field errors name the keys).</summary>
    public const string SettingsInvalidCode = "storage.settings.invalid";

    /// <summary>Database of a store unreachable.</summary>
    /// <param name="store">The store (or database key).</param>
    /// <returns>The error.</returns>
    public static Error Unavailable(string store) => Error.Create(ErrorKind.Unavailable, UnavailableCode, ("store", store));

    /// <summary>Settings reference a provider that is not installed.</summary>
    /// <param name="provider">The provider id.</param>
    /// <returns>The error.</returns>
    public static Error ProviderUnknown(string provider) => Error.Create(ErrorKind.Validation, ProviderUnknownCode, ("provider", provider));

    /// <summary>Server version too old.</summary>
    /// <param name="minimum">Minimum version.</param>
    /// <param name="actual">Server version.</param>
    /// <returns>The error.</returns>
    public static Error UnsupportedVersion(string minimum, string actual) =>
        Error.Create(ErrorKind.Unprocessable, UnsupportedVersionCode, ("minimum", minimum), ("actual", actual));

    /// <summary>A migration failed.</summary>
    /// <param name="module">The module.</param>
    /// <param name="migration">The migration id (or empty when unknown).</param>
    /// <returns>The error.</returns>
    public static Error MigrationFailed(string module, string migration) =>
        Error.Create(ErrorKind.Internal, MigrationFailedCode, ("module", module), ("migration", migration));

    /// <summary>The migration lock could not be acquired in time.</summary>
    /// <param name="database">The database key.</param>
    /// <returns>The error.</returns>
    public static Error MigrationLockTimeout(string database) =>
        Error.Create(ErrorKind.Unavailable, MigrationLockTimeoutCode, ("database", database));

    /// <summary>Storage settings are invalid.</summary>
    /// <param name="fields">The invalid keys.</param>
    /// <returns>The error.</returns>
    public static Error SettingsInvalid(IReadOnlyList<FieldError> fields) => Error.Validation(SettingsInvalidCode, fields);
}

/// <summary>A fatal storage failure during start-up; the host stops with <see cref="ExitCode"/>.</summary>
public sealed class StorageStartupException : Exception
{
    /// <summary>Exit code 75 (<c>EX_TEMPFAIL</c>): a required database stayed unreachable (HST §6.1).</summary>
    public const int TempFail = 75;

    /// <summary>Exit code 70 (<c>EX_SOFTWARE</c>): start-up task failure.</summary>
    public const int Software = 70;

    /// <summary>Exit code 78 (<c>EX_CONFIG</c>): invalid settings.</summary>
    public const int Config = 78;

    /// <summary>Creates the exception.</summary>
    /// <param name="error">The error.</param>
    /// <param name="exitCode">The exit code the host must use.</param>
    /// <param name="inner">The cause.</param>
    public StorageStartupException(Error error, int exitCode, Exception? inner = null)
        : base($"{error.Code}: {error.DeveloperDetail ?? string.Join(", ", error.Args.Select(a => $"{a.Key}={a.Value}"))}", inner)
    {
        Error = error;
        ExitCode = exitCode;
    }

    /// <summary>Creates the exception (serialisation-free default).</summary>
    public StorageStartupException()
        : this(Error.Internal("storage start-up failure"), Software)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public StorageStartupException(string message)
        : this(Error.Internal(message), Software)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public StorageStartupException(string message, Exception innerException)
        : this(Error.Internal(message), Software, innerException)
    {
    }

    /// <summary>Gets the error.</summary>
    public Error Error { get; }

    /// <summary>Gets the exit code the host must use.</summary>
    public int ExitCode { get; }
}
