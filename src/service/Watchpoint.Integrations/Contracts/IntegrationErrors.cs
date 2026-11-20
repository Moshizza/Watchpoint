using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Integrations.Contracts;

/// <summary>Error and problem codes of the integration runtime (spec IRT §12).</summary>
public static class IntegrationErrors
{
#pragma warning disable CS1591 // names are the codes
    public const string PackageNotFound = "irt.package.not_found";
    public const string PackageUnsigned = "irt.package.unsigned";
    public const string PackageSignatureInvalid = "irt.package.signature_invalid";
    public const string PackageUntrustedKey = "irt.package.untrusted_key";
    public const string PackageUnsafeArchive = "irt.package.unsafe_archive";
    public const string PackageHashMismatch = "irt.package.hash_mismatch";
    public const string PackageFileUnlisted = "irt.package.file_unlisted";
    public const string PackageContractUnsupported = "irt.package.contract_unsupported";
    public const string PackageProductTooOld = "irt.package.product_too_old";
    public const string PackagePlatformUnsupported = "irt.package.platform_unsupported";
    public const string PackageConnectorInvalid = "irt.package.connector_invalid";
    public const string PackageNotActive = "irt.package.not_active";
    public const string PackageSharedAssemblyShadowed = "irt.package.shared_assembly_shadowed";
    public const string PackageVersionConflict = "irt.package.version_conflict";
    public const string PackageUnreadable = "irt.package.unreadable";
    public const string PackageFileMissing = "irt.package.file_missing";
    public const string PackageReservedId = "irt.package.reserved_id";
    public const string PackageFileNameInvalid = "irt.package.file_name_invalid";
    public const string ConnectionNotFound = "irt.connection.not_found";
    public const string ConnectionIsolationRequired = "irt.connection.isolation_required";
    public const string HostUnavailable = "irt.host.unavailable";
    public const string ConnectionNotRunning = "irt.connection.not_running";
    public const string StoreLimit = "irt.store.limit";
    public const string InventoryTypeUnknown = "irt.inventory.type_unknown";
    public const string ServerTypeUnknown = "irt.connection.server_type_unknown";
    public const string SecretRequired = "irt.connection.secret_required";
    public const string SecretUnknown = "irt.connection.secret_unknown";
    public const string LocationUnknown = "irt.connection.location_unknown";
    public const string NotSupported = "irt.connection.not_supported";
    public const string InventoryInvalid = "irt.inventory.invalid";
#pragma warning restore CS1591

    /// <summary>Package version (or connector) not in the catalogue.</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version, when one was asked for.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(PackageId id, string? version = null) =>
        Error.NotFound(PackageNotFound, ("package", id.Value), ("version", version));

    /// <summary>The package version is not usable (invalid or removed).</summary>
    /// <param name="id">The package id.</param>
    /// <param name="version">The version.</param>
    /// <returns>The error.</returns>
    public static Error NotActive(PackageId id, string version) =>
        Error.Unprocessable(PackageNotActive, ("package", id.Value), ("version", version));

    /// <summary>The archive is refused (spec IRT §4.3 step 1).</summary>
    /// <param name="reason">Why: <c>path</c>, <c>symlink</c>, <c>tooManyFiles</c>, <c>tooLarge</c>, <c>compressionRatio</c>, <c>duplicate</c>, <c>unreadable</c>.</param>
    /// <param name="entry">The entry, when one is at fault.</param>
    /// <returns>The error.</returns>
    public static Error UnsafeArchive(string reason, string? entry = null) =>
        Error.Create(ErrorKind.Validation, PackageUnsafeArchive, ("reason", reason), ("entry", entry));

    /// <summary>The connection does not exist.</summary>
    /// <param name="id">The connection.</param>
    /// <returns>The error.</returns>
    public static Error ConnectionNotFoundError(ConnectionId id) =>
        Error.NotFound(ConnectionNotFound, ("connection", id.Value.ToString("D")));

    /// <summary>The operation needs a running connection.</summary>
    /// <param name="id">The connection.</param>
    /// <param name="state">Its state.</param>
    /// <returns>The error.</returns>
    public static Error NotRunning(ConnectionId id, string state) =>
        Error.Unprocessable(ConnectionNotRunning, ("connection", id.Value.ToString("D")), ("state", state));

    /// <summary>The host mode is not available (M1: isolated and remote).</summary>
    /// <param name="hostMode">The host mode.</param>
    /// <returns>The error.</returns>
    public static Error HostModeUnavailable(string hostMode) =>
        Error.Unprocessable(HostUnavailable, ("hostMode", hostMode));

    /// <summary>A connection store limit is exceeded.</summary>
    /// <param name="limit"><c>value</c> (per key) or <c>total</c>.</param>
    /// <param name="max">The limit in bytes.</param>
    /// <returns>The error.</returns>
    public static Error StoreLimitExceeded(string limit, int max) =>
        Error.Create(ErrorKind.Validation, StoreLimit, ("limit", limit), ("max", max));

    /// <summary>The upload file name is refused.</summary>
    /// <param name="fileName">The name.</param>
    /// <returns>The error.</returns>
    public static Error FileNameInvalid(string fileName) =>
        Error.Create(ErrorKind.Validation, PackageFileNameInvalid, ("fileName", fileName));
}
